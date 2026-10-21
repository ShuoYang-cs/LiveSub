import AVFoundation
import LiveSubCore
import Speech

/// 一段确认的识别结果。
struct FinalResult: Sendable {
    var text: String
    /// 在音频时间轴上的起止秒数（从开始采集算起）；没有逐词时间时为 nil。
    var start: Double?
    var end: Double?
    /// 各个词的置信度，用来自动判断这位讲者好不好识别。
    var confidences: [Double]

    init(text: String, words: [TimedWord]) {
        self.text = text
        start = words.map(\.start).min()
        end = words.map(\.end).max()
        confidences = words.map(\.confidence)
    }
}


enum TranscriptEvent: Sendable {
    /// 尚未确认、会继续变化的识别文本（用于实时显示）。
    case volatile(String)
    /// 已确认的识别文本。
    case final(FinalResult)
    /// 已确认、但判断为别的语言（如旁边同学说中文）而被过滤掉的文本。
    case filtered(String)
}

enum TranscriptionError: LocalizedError {
    case unsupportedLocale(String)
    case noAudioFormat
    case speechPermissionDenied

    var errorDescription: String? {
        switch self {
        case .unsupportedLocale(let id): L("系统语音识别不支持 %@。", id)
        case .noAudioFormat: L("语音识别模型没有可用的音频格式。")
        case .speechPermissionDenied: L("没有语音识别权限。请在“系统设置 › 隐私与安全性 › 语音识别”中允许 LiveSub。")
        }
    }
}

/// 语音识别引擎的统一接口。按系统版本选择实现：
/// macOS 26 用 SpeechAnalyzer（`AnalyzerEngine`，支持中文过滤和逐词置信度），更早的系统用 SFSpeechRecognizer（`LegacySpeechEngine`）。
protocol SpeechEngine: AnyObject, Sendable {
    func start(localeID: String, guardLocaleID: String?,
               onStatus: @escaping @Sendable (_ message: String, _ isError: Bool) -> Void,
               onEvent: @escaping @Sendable @MainActor (TranscriptEvent) async -> Void) async throws
    func append(_ buffer: AVAudioPCMBuffer)
    var inputStatus: (level: Float, silentFor: TimeInterval) { get }
    func finalizePending() async
    func stop() async
}

/// 识别引擎入口：调用方不用关心系统版本。
/// 设置环境变量 LIVESUB_LEGACY_SPEECH=1 可以在 macOS 26 上强制使用旧引擎（测试用）。
final class TranscriptionEngine: @unchecked Sendable {
    private let engine: SpeechEngine

    init() {
        if #available(macOS 26, *), ProcessInfo.processInfo.environment["LIVESUB_LEGACY_SPEECH"] == nil {
            engine = AnalyzerEngine()
        } else {
            engine = LegacySpeechEngine()
        }
    }

    func start(localeID: String, guardLocaleID: String? = nil,
               onStatus: @escaping @Sendable (_ message: String, _ isError: Bool) -> Void,
               onEvent: @escaping @Sendable @MainActor (TranscriptEvent) async -> Void) async throws {
        try await engine.start(localeID: localeID, guardLocaleID: guardLocaleID, onStatus: onStatus, onEvent: onEvent)
    }

    func append(_ buffer: AVAudioPCMBuffer) { engine.append(buffer) }
    var inputStatus: (level: Float, silentFor: TimeInterval) { engine.inputStatus }
    func finalizePending() async { await engine.finalizePending() }
    func stop() async { await engine.stop() }
}

/// 一块音频第一个声道的音量（RMS，0…1），给状态条用。
func audioLevel(_ buffer: AVAudioPCMBuffer) -> Float {
    guard let data = buffer.floatChannelData?[0], buffer.frameLength > 0 else { return 0 }
    let n = Int(buffer.frameLength)
    var sum: Float = 0
    for i in stride(from: 0, to: n, by: 4) { sum += data[i] * data[i] }
    return (sum / Float((n + 3) / 4)).squareRoot()
}

/// 封装 macOS 26 的 SpeechAnalyzer + SpeechTranscriber。
/// 识别模型由系统管理，不占用本应用内存；首次使用某种语言时会自动下载。
///
/// 可选的“哨兵”：同一路音频再喂给一个中文识别器，用 `ForeignSpeechDetector` 判断某句话其实是不是在说中文。
@available(macOS 26, *)
final class AnalyzerEngine: SpeechEngine, @unchecked Sendable {
    /// 一个识别器及其输入流。
    private final class Lane: @unchecked Sendable {
        let analyzer: SpeechAnalyzer
        let format: AVAudioFormat
        let stream: AsyncStream<AnalyzerInput>
        let continuation: AsyncStream<AnalyzerInput>.Continuation
        let converter = BufferConverter()
        var resultsTask: Task<Void, Never>?

        init(analyzer: SpeechAnalyzer, format: AVAudioFormat) {
            self.analyzer = analyzer
            self.format = format
            (stream, continuation) = AsyncStream<AnalyzerInput>.makeStream()
        }
    }

    private var lanes: [Lane] = []
    static let maxGuardWait: TimeInterval = 1.5
    /// 最近一块音频的音量（RMS，0…1）和收到的时间；只在 lock 内访问。给状态条用。
    private var level: Float = 0
    private var lastBufferAt = Date.distantPast
    private let lock = NSLock()
    /// 只在 lock 内访问。
    private var detector = ForeignSpeechDetector()

    /// 准备模型并开始识别。`onEvent` 在主线程上按顺序调用。
    /// `guardLocaleID` 不为 nil 时，额外跑一个该语言的识别器，用来过滤这种语言的说话声。
    func start(localeID: String, guardLocaleID: String?,
               onStatus: @escaping @Sendable (_ message: String, _ isError: Bool) -> Void,
               onEvent: @escaping @Sendable @MainActor (TranscriptEvent) async -> Void) async throws {
        let transcriber = try await Self.makeTranscriber(
            localeID: localeID, reportingOptions: [.volatileResults, .fastResults],
            attributeOptions: [.transcriptionConfidence, .audioTimeRange], onStatus: onStatus)
        var sentinel: SpeechTranscriber?
        if let guardLocaleID {
            do {
                sentinel = try await Self.makeTranscriber(
                    localeID: guardLocaleID, reportingOptions: [.volatileResults, .fastResults],
                    attributeOptions: [.audioTimeRange], onStatus: onStatus)
            } catch {
                // 哨兵只是锦上添花，失败了照常识别。
                onStatus(L("中文过滤不可用：%@", error.localizedDescription), true)
            }
        }
        lock.withLock { detector = ForeignSpeechDetector() }

        let primary = try await Self.makeLane(transcriber)
        primary.resultsTask = Task.detached(priority: .userInitiated) { [weak self] in
            do {
                for try await result in transcriber.results {
                    guard let self else { return }
                    let text = String(result.text.characters)
                    let words = Self.timedWords(result.text)
                    guard sentinel != nil else {
                        await onEvent(result.isFinal ? .final(FinalResult(text: text, words: words)) : .volatile(text))
                        continue
                    }
                    if result.isFinal {
                        // 等哨兵也确认完这段话（以最后一个词为准，不算句尾静音），再逐词判断是不是中文。
                        let spokenUntil = words.map(\.end).max() ?? result.range.end.seconds
                        let waitStart = Date()
                        let lowest = words.map(\.confidence).min() ?? 0
                        await self.waitForGuard(until: spokenUntil - 0.3, lowestConfidence: lowest)
                        if ProcessInfo.processInfo.environment["LIVESUB_DEBUG_GUARD"] != nil {
                            print(String(format: "   [waited %.2fs for guard, audio %.1fs]", Date().timeIntervalSince(waitStart), spokenUntil))
                        }
                        let mask = self.lock.withLock { self.detector.foreignMask(words) }
                        if !mask.contains(true) {
                            await onEvent(.final(FinalResult(text: text, words: words)))
                        } else {
                            let kept = zip(words, mask).filter { !$0.1 }.map(\.0)
                            let dropped = zip(words, mask).filter { $0.1 }.map(\.0.text)
                            await onEvent(.filtered(dropped.joined(separator: " ")))
                            if !kept.isEmpty {
                                await onEvent(.final(FinalResult(text: kept.map(\.text).joined(separator: " "), words: kept)))
                            }
                        }
                    } else {
                        // 临时结果可能不带置信度；没有时只看哨兵。
                        let confidence = Self.meanConfidence(result.text) ?? 0
                        let foreign = self.lock.withLock { self.detector.volatileLooksForeign(confidence: confidence) }
                        await onEvent(.volatile(foreign ? "" : text))
                    }
                }
            } catch {
                onStatus(L("语音识别中断：%@", error.localizedDescription), true)
            }
        }

        var lanes = [primary]
        if let sentinel, let lane = try? await Self.makeLane(sentinel) {
            lane.resultsTask = Task.detached(priority: .userInitiated) { [weak self] in
                do {
                    for try await result in sentinel.results {
                        guard let self else { return }
                        let words = Self.timedWords(result.text)
                        let range = result.range
                        self.lock.withLock {
                            self.detector.updateGuard(start: range.start.seconds, end: range.end.seconds,
                                                      words: words, isFinal: result.isFinal)
                        }
                    }
                } catch {}
            }
            lanes.append(lane)
        }
        lock.withLock { self.lanes = lanes }
        for lane in lanes {
            try await lane.analyzer.prepareToAnalyze(in: lane.format)
        }
        for lane in lanes {
            try await lane.analyzer.start(inputSequence: lane.stream)
        }
    }

    private static func makeTranscriber(localeID: String, reportingOptions: Set<SpeechTranscriber.ReportingOption>,
                                        attributeOptions: Set<SpeechTranscriber.ResultAttributeOption>,
                                        onStatus: @escaping @Sendable (String, Bool) -> Void) async throws -> SpeechTranscriber {
        guard let locale = await SpeechTranscriber.supportedLocale(equivalentTo: Locale(identifier: localeID)) else {
            throw TranscriptionError.unsupportedLocale(localeID)
        }
        let transcriber = SpeechTranscriber(locale: locale, transcriptionOptions: [],
                                            reportingOptions: reportingOptions, attributeOptions: attributeOptions)
        if await AssetInventory.status(forModules: [transcriber]) != .installed,
           let request = try await AssetInventory.assetInstallationRequest(supporting: [transcriber]) {
            onStatus(L("正在下载语音识别模型…"), false)
            try await request.downloadAndInstall()
        }
        return transcriber
    }

    private static func makeLane(_ transcriber: SpeechTranscriber) async throws -> Lane {
        let analyzer = SpeechAnalyzer(modules: [transcriber],
                                      options: .init(priority: .userInitiated, modelRetention: .processLifetime))
        guard let format = await SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith: [transcriber]) else {
            throw TranscriptionError.noAudioFormat
        }
        return Lane(analyzer: analyzer, format: format)
    }

    private static func meanConfidence(_ text: AttributedString) -> Double? {
        let values = text.runs.compactMap { $0[AttributeScopes.SpeechAttributes.ConfidenceAttribute.self] }
        return values.isEmpty ? nil : values.reduce(0, +) / Double(values.count)
    }

    /// 带时间和置信度的词。没有请求置信度的识别器，置信度记为 1。
    private static func timedWords(_ text: AttributedString) -> [TimedWord] {
        text.runs.compactMap { run in
            guard let range = run[AttributeScopes.SpeechAttributes.TimeRangeAttribute.self] else { return nil }
            let word = String(text[run.range].characters).trimmingCharacters(in: .whitespaces)
            guard !word.isEmpty else { return nil }
            return TimedWord(text: word, start: range.start.seconds, end: range.end.seconds,
                             confidence: run[AttributeScopes.SpeechAttributes.ConfidenceAttribute.self] ?? 1)
        }
    }

    /// 哨兵听到疑似中文时，等它确认到 `time` 这一刻，最多等 `maxGuardWait` 秒（哨兵卡住时不能拖慢字幕）。
    private func waitForGuard(until time: Double, lowestConfidence: Double) async {
        let deadline = Date().addingTimeInterval(Self.maxGuardWait)
        while Date() < deadline, lock.withLock({ detector.shouldWaitForGuard(until: time, lowestConfidence: lowestConfidence) }) {
            try? await Task.sleep(for: .milliseconds(80))
        }
    }

    /// 由采集线程同步调用：立即把音频转换成识别器需要的格式（同时完成拷贝）。
    func append(_ buffer: AVAudioPCMBuffer) {
        let rms = audioLevel(buffer)
        let lanes = lock.withLock {
            level = rms
            lastBufferAt = Date()
            return self.lanes
        }
        var shared: (AVAudioFormat, AVAudioPCMBuffer)?
        for lane in lanes {
            // 格式相同的 lane 共用一份转换结果。
            if let (format, converted) = shared, format == lane.format {
                lane.continuation.yield(AnalyzerInput(buffer: converted))
                continue
            }
            guard let converted = try? lane.converter.convert(buffer, to: lane.format) else { continue }
            shared = (lane.format, converted)
            lane.continuation.yield(AnalyzerInput(buffer: converted))
        }
    }

    /// 把已经听到、但还没确认的内容立即确认（不结束识别）。
    /// 电脑声音停下来时（视频暂停、播完）系统不再送音频，识别器等不到句尾的静音，最后半句会一直停在“正在说”。
    func finalizePending() async {
        let lanes = lock.withLock { self.lanes }
        for lane in lanes { try? await lane.analyzer.finalize(through: nil) }
    }

    /// 状态条用：最近的音量，以及多久没收到音频了。
    var inputStatus: (level: Float, silentFor: TimeInterval) {
        lock.withLock { (level, Date().timeIntervalSince(lastBufferAt)) }
    }

    func stop() async {
        let lanes = lock.withLock {
            let current = self.lanes
            self.lanes = []
            return current
        }
        for lane in lanes { lane.continuation.finish() }
        for lane in lanes { try? await lane.analyzer.finalizeAndFinishThroughEndOfInput() }
        // 让最后的确认结果送达后再结束结果流。
        for lane in lanes { _ = await lane.resultsTask?.value }
    }
}

/// 把任意格式的 PCM 转成目标格式。输出总是新分配的 buffer。
final class BufferConverter {
    private var converter: AVAudioConverter?

    func convert(_ buffer: AVAudioPCMBuffer, to format: AVAudioFormat) throws -> AVAudioPCMBuffer {
        let inputFormat = buffer.format
        if converter == nil || converter!.inputFormat != inputFormat || converter!.outputFormat != format {
            converter = AVAudioConverter(from: inputFormat, to: format)
            converter?.primeMethod = .none
        }
        guard let converter else { throw TranscriptionError.noAudioFormat }

        let ratio = format.sampleRate / inputFormat.sampleRate
        let capacity = AVAudioFrameCount((Double(buffer.frameLength) * ratio).rounded(.up)) + 32
        guard let output = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: capacity) else {
            throw TranscriptionError.noAudioFormat
        }
        var consumed = false
        var error: NSError?
        let status = converter.convert(to: output, error: &error) { _, inputStatus in
            if consumed {
                inputStatus.pointee = .noDataNow
                return nil
            }
            consumed = true
            inputStatus.pointee = .haveData
            return buffer
        }
        if status == .error, let error { throw error }
        return output
    }
}
