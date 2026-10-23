import AVFoundation
import LiveSubCore
import Speech

/// macOS 26 之前的语音识别：SFSpeechRecognizer。支持本机离线识别时只在本机识别，需要“语音识别”权限。
///
/// 和 SpeechAnalyzer 的区别：
/// - 识别结果是“从这一段开始到现在”的整段文字，不会按句子给出确认结果。`PauseDetector` 判断讲者停顿了、
///   或者一段超过 50 秒时，结束这一段拿到确认结果，并立即开始下一段；讲者连续说话时，
///   `PartialCommitter` 把已经稳定的完整句子提前确认；
/// - 本机识别给出的确认结果常常是空的，这时用这一段最后的临时结果，起止时间按声音估计；
/// - 不支持中文说话声过滤；逐词时间和置信度只在非空的确认结果里有。
final class LegacySpeechEngine: SpeechEngine, @unchecked Sendable {
    static let maxSegment: TimeInterval = 50

    /// 一段识别：从开始到拿到确认结果（或出错）为止。
    private struct Segment {
        var task: SFSpeechRecognitionTask?
        /// 这一段在音频时间轴上从第几秒开始（逐词时间要换算到整条音频时间轴上）。
        var start: Double
        var partial = ""
        /// 结束时记下的说话范围。
        var speech: (start: Double, end: Double)?
        var committer = PartialCommitter()
        /// 提前确认过句子时，下一句从第几秒开始。
        var nextStart: Double?
    }

    private let lock = NSLock()
    private let converter = BufferConverter()
    private let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 1, interleaved: false)!
    // 以下只在 lock 内访问。
    private var recognizer: SFSpeechRecognizer?
    private var request: SFSpeechAudioBufferRecognitionRequest?
    /// 还没拿到确认结果的段（必须持有任务，否则会被释放）。
    private var segments: [ObjectIdentifier: Segment] = [:]
    private var running = false
    /// 已经送入的音频秒数。
    private var audioTime: Double = 0
    private var segmentStartedAt = Date()
    private var lastText = ""
    private var pause = PauseDetector()
    private var level: Float = 0
    private var lastBufferAt = Date.distantPast
    private var timer: DispatchSourceTimer?
    private var events: AsyncStream<TranscriptEvent>.Continuation?
    private var consumer: Task<Void, Never>?

    func start(localeID: String, guardLocaleID: String?,
               onStatus: @escaping @Sendable (_ message: String, _ isError: Bool) -> Void,
               onEvent: @escaping @Sendable @MainActor (TranscriptEvent) async -> Void) async throws {
        guard await Self.requestAuthorization() else { throw TranscriptionError.speechPermissionDenied }
        guard let recognizer = SFSpeechRecognizer(locale: Locale(identifier: localeID)), recognizer.isAvailable else {
            throw TranscriptionError.unsupportedLocale(localeID)
        }
        appLog.notice("legacy speech: locale=\(localeID, privacy: .public) onDevice=\(recognizer.supportsOnDeviceRecognition)")
        if guardLocaleID != nil {
            onStatus(L("过滤中文交谈需要 macOS 26 或更新版本。"), false)
        }
        // 识别回调在系统线程上，按顺序交给主线程处理。
        let (stream, continuation) = AsyncStream<TranscriptEvent>.makeStream()
        consumer = Task {
            for await event in stream { await onEvent(event) }
        }
        lock.withLock {
            self.recognizer = recognizer
            events = continuation
            running = true
            beginSegmentLocked()
        }
        let timer = DispatchSource.makeTimerSource(queue: .global(qos: .userInitiated))
        timer.schedule(deadline: .now() + 0.2, repeating: 0.2)
        timer.setEventHandler { [weak self] in self?.tick() }
        timer.resume()
        lock.withLock { self.timer = timer }
    }

    func append(_ buffer: AVAudioPCMBuffer) {
        let rms = audioLevel(buffer)
        guard let converted = try? converter.convert(buffer, to: format) else { return }
        lock.withLock {
            level = rms
            lastBufferAt = Date()
            audioTime += Double(converted.frameLength) / format.sampleRate
            pause.addAudio(level: rms, audioTime: audioTime)
            request?.append(converted)
        }
    }

    var inputStatus: (level: Float, silentFor: TimeInterval) {
        lock.withLock { (level, Date().timeIntervalSince(lastBufferAt)) }
    }

    func finalizePending() async {
        lock.withLock {
            if !lastText.isEmpty { endSegmentLocked() }
        }
    }

    func stop() async {
        lock.withLock {
            running = false
            timer?.cancel()
            timer = nil
            if !lastText.isEmpty {
                endSegmentLocked()
            } else if let request {
                request.endAudio()
                segments[ObjectIdentifier(request)]?.task?.cancel()
                segments[ObjectIdentifier(request)] = nil
                self.request = nil
            }
        }
        // 等最后一段的确认结果送达（最多 2 秒）；还没送达的段直接用临时结果，不丢文字。
        let deadline = Date().addingTimeInterval(2)
        while Date() < deadline, lock.withLock({ !segments.isEmpty }) {
            try? await Task.sleep(for: .milliseconds(100))
        }
        lock.withLock {
            for segment in segments.values {
                emitFinalLocked(segment, result: nil)
                segment.task?.cancel()
            }
            segments = [:]
            events?.finish()
            events = nil
        }
        await consumer?.value
    }

    // MARK: - 分段

    private func beginSegmentLocked() {
        guard running, let recognizer else { return }
        let request = SFSpeechAudioBufferRecognitionRequest()
        request.shouldReportPartialResults = true
        request.addsPunctuation = true
        request.taskHint = .dictation
        if recognizer.supportsOnDeviceRecognition { request.requiresOnDeviceRecognition = true }
        segmentStartedAt = Date()
        lastText = ""
        pause.reset(now: Date().timeIntervalSinceReferenceDate)
        self.request = request
        let id = ObjectIdentifier(request)
        segments[id] = Segment(start: audioTime)
        let task = recognizer.recognitionTask(with: request) { [weak self] result, error in
            self?.handle(result, error: error, request: request)
        }
        segments[id]?.task = task
    }

    /// 结束当前段（等它的确认结果），并立即开始下一段，后续音频送进新的一段。
    private func endSegmentLocked() {
        guard let request else { return }
        let id = ObjectIdentifier(request)
        segments[id]?.speech = pause.speechRange ?? segments[id].map { ($0.start, audioTime) }
        request.endAudio()
        self.request = nil
        beginSegmentLocked()
    }

    private func tick() {
        lock.withLock {
            guard running, let request else { return }
            let id = ObjectIdentifier(request)
            if var segment = segments[id], commitStableLocked(&segment, now: Date().timeIntervalSinceReferenceDate) {
                segments[id] = segment
                events?.yield(.volatile(segment.committer.remainder(of: lastText)))
            }
            let paused = !lastText.isEmpty && pause.isPaused(audioTime: audioTime, now: Date().timeIntervalSinceReferenceDate)
            let tooLong = Date().timeIntervalSince(segmentStartedAt) > Self.maxSegment
            if paused || tooLong { endSegmentLocked() }
        }
    }

    private func handle(_ result: SFSpeechRecognitionResult?, error: Error?, request: SFSpeechAudioBufferRecognitionRequest) {
        lock.withLock {
            let id = ObjectIdentifier(request)
            // 已经送出确认结果的段，之后的回调（取消、“没有检测到语音”等）不用管。
            guard var segment = segments[id] else { return }
            let isCurrent = request === self.request
            if let result, !result.isFinal {
                let text = result.bestTranscription.formattedString
                guard !text.isEmpty else { return }
                segment.partial = text
                if isCurrent, text != lastText {
                    let now = Date().timeIntervalSinceReferenceDate
                    lastText = text
                    pause.updateText(text, now: now)
                    _ = segment.committer.update(text, now: now, audioTime: audioTime)
                    commitStableLocked(&segment, now: now)
                    events?.yield(.volatile(segment.committer.remainder(of: text)))
                }
                segments[id] = segment
                return
            }
            if let error = error as NSError?, !Self.isRoutine(error) {
                appLog.notice("legacy speech: \(error.localizedDescription, privacy: .public) (\(error.domain, privacy: .public) \(error.code))")
            }
            // 确认结果，或者出错：这一段结束。
            if isCurrent, segment.speech == nil {
                segment.speech = pause.speechRange ?? (segment.start, audioTime)
            }
            segments[id] = nil
            emitFinalLocked(segment, result: result)
            if isCurrent {
                self.request = nil
                beginSegmentLocked()
            }
        }
    }

    /// 当前段里有稳定的完整句子时提前确认它。
    @discardableResult
    private func commitStableLocked(_ segment: inout Segment, now: Double) -> Bool {
        guard let sentence = segment.committer.commitIfStable(now: now) else { return false }
        var final = FinalResult(text: sentence.text, words: [])
        let start = segment.nextStart ?? pause.speechRange?.start ?? segment.start
        final.start = start
        final.end = max(start, sentence.audioTime)
        segment.nextStart = final.end
        events?.yield(.final(final))
        return true
    }

    /// 送出一段的确认文字（去掉已经提前确认的句子）：确认结果非空就用它，否则用最后的临时结果。
    /// 没有提前确认过句子时，确认结果的逐词时间和置信度也一起送出。
    private func emitFinalLocked(_ segment: Segment, result: SFSpeechRecognitionResult?) {
        let transcription = result?.bestTranscription
        let finalText = transcription?.formattedString ?? ""
        let full = finalText.isEmpty ? segment.partial : finalText
        let committedEarly = segment.committer.committedWords > 0
        let text = committedEarly ? segment.committer.remainder(of: full) : full
        guard !text.isEmpty else { return }
        let timed = finalText.isEmpty || committedEarly ? [] : (transcription?.segments ?? []).filter { $0.duration > 0 }
        var final = FinalResult(text: text, words: timed.map { word in
            TimedWord(text: word.substring, start: segment.start + word.timestamp,
                      end: segment.start + word.timestamp + word.duration, confidence: Double(word.confidence))
        })
        if timed.isEmpty, let speech = segment.speech {
            let start = segment.nextStart ?? speech.start
            final.start = start
            final.end = max(start, speech.end)
        }
        // 置信度为 0 表示识别器没有给出，不能当成“很难识别”。
        final.confidences = final.confidences.filter { $0 > 0 }
        events?.yield(.final(final))
    }

    /// 正常结束时也会出现的错误：取消（301）、没有检测到语音（1110）。
    private static func isRoutine(_ error: NSError) -> Bool {
        error.domain == "kAFAssistantErrorDomain" && (error.code == 301 || error.code == 1110)
    }

    private static func requestAuthorization() async -> Bool {
        await withCheckedContinuation { continuation in
            SFSpeechRecognizer.requestAuthorization { continuation.resume(returning: $0 == .authorized) }
        }
    }
}
