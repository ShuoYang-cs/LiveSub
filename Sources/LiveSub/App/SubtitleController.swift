import AVFoundation
import AppKit
import LiveSubCore
import Observation
import os

/// 运行日志：`log show --last 1h --predicate 'subsystem == "com.livesub.app"'`。
/// 出问题（比如卡住、没字幕）时用来看停在哪一步。
let appLog = Logger(subsystem: "com.livesub.app", category: "pipeline")

struct SubtitleLine: Identifiable, Equatable {
    enum State: Equatable { case waiting, translating, done, failed }

    let id = UUID()
    let time: Date
    let sourceLabel: String?
    var source: String
    var translation = ""
    var state: State = .waiting
    /// 例如 “Apple 兜底”，表示这一句不是用首选引擎翻译的。
    var note: String?
    /// 自动纠正改过原文时，识别器原来给出的文字（打开“保留识别原文”时才保存）。
    var originalSource: String?
    /// 这句话在音频里的起止时刻（导出 SRT/VTT 用）；没有逐词时间时为 nil。
    var audioStart: Date?
    var audioEnd: Date?
    /// 延迟分析用（LIVESUB_DEBUG_LATENCY）：开始翻译、出第一个字的时刻。
    var translateStartedAt: Date?
    var firstTokenAt: Date?
}

enum SubtitleFileFormat: String, CaseIterable {
    case srt, vtt
    var menuTitle: String { self == .srt ? "SRT…" : "WebVTT…" }
}

/// 一个音源的完整链路：采集 → 识别 → 分句。
@MainActor
final class SourcePipeline {
    let capture: AudioCapture
    let label: String?
    let engine = TranscriptionEngine()
    var segmenter = Segmenter()
    var volatileText = ""
    /// 把分句后的片段对回音频时间。
    var timeline = SegmentTimeline()
    /// 识别器音频时间轴的 0 秒对应的时刻（开始采集时）。
    var captureStart = Date()
    /// 已经因为“没有新音频”请求过确认当前这段临时文本了（新结果到来时清掉）。
    var finalizeRequested = false

    init(capture: AudioCapture, label: String?) {
        self.capture = capture
        self.label = label
    }

    var liveSource: String { Segmenter.join(segmenter.pending, volatileText) }
}

@MainActor @Observable
final class SubtitleController {
    private(set) var isRunning = false
    private(set) var isStarting = false
    private(set) var status = L("未开始")
    private(set) var lines: [SubtitleLine] = []
    /// 各音源正在说、还没分好句的文字（键是音源标签，只有一个音源时为空字符串）。
    private(set) var live: [String: String] = [:]
    private(set) var notesInProgress = false
    /// 状态条：最近的输入音量（0…1）、多久没有声音、翻译积压句数、识别是否困难、最近的错误。
    private(set) var inputLevel: Float = 0
    private(set) var silentFor: TimeInterval = 0
    private(set) var backlog = 0
    private(set) var recognitionDifficult = false
    private(set) var alert: String?
    /// 首选引擎（Claude）用不了（例如未登录），正在用 Apple 兜底。
    private(set) var usingFallback = false

    @ObservationIgnored let settings: AppSettings
    /// AI 助手；每次用户点“开始”时新开一个对话。
    @ObservationIgnored let assistant: AssistantSession
    @ObservationIgnored private var pipelines: [SourcePipeline] = []
    @ObservationIgnored private var ticker: Timer?
    @ObservationIgnored private var translator: Translator?
    @ObservationIgnored private var fallback: Translator?
    @ObservationIgnored private var queue: [UUID] = []
    @ObservationIgnored private var workerRunning = false
    @ObservationIgnored private var store: TranscriptStore?
    @ObservationIgnored private var listeningStatus = ""
    /// 当前识别链路是按哪些设置启动的；这些设置变了才需要重启识别。
    @ObservationIgnored private var startedRecognitionKey = ""
    @ObservationIgnored private var applyTask: Task<Void, Never>?
    /// 运行期间告诉系统这是实时任务：不进 App Nap、不因闲置而睡眠（一节课不碰电脑也不会断）。
    @ObservationIgnored private var activity: NSObjectProtocol?
    @ObservationIgnored private var quality = RecognitionQualityMonitor()
    /// 当前翻译会话是按“讲者难识别”的提示词建立的吗（重新开始时状态清零，要和它对得上）。
    @ObservationIgnored private var translatorBuiltDifficult = false
    @ObservationIgnored private var alertAt = Date.distantPast
    /// 这份记录对应的录音从什么时候开始（导出字幕的时间零点）。
    @ObservationIgnored private var sessionStart: Date?
    @ObservationIgnored private var filteredCount = 0

    init(settings: AppSettings) {
        self.settings = settings
        assistant = AssistantSession(settings: settings)
        assistant.transcript = { [weak self] in
            guard let self else { return ([], "") }
            return (self.lines, self.live.keys.sorted().compactMap { self.live[$0] }.joined(separator: " "))
        }
    }

    var transcriptURL: URL? { store?.fileURL }

    // MARK: - 开始 / 停止

    /// 用户点“开始/停止”。开始时 AI 助手新开一个对话（设置变化引起的自动重启不会清空对话）。
    func toggle() {
        Task {
            if isRunning {
                await stop()
            } else {
                assistant.newConversation()
                if settings.assistantVisible { assistant.warmUp() }
                // 换了讲者/场景：重新判断好不好识别。
                quality = RecognitionQualityMonitor()
                recognitionDifficult = false
                // 上次的翻译会话是按“难识别”建立的：换回普通提示词。
                if translatorBuiltDifficult { resetTranslators() }
                await start()
            }
        }
    }

    func start() async {
        guard !isRunning, !isStarting else { return }
        isStarting = true
        defer { isStarting = false }
        status = L("准备中…")
        appLog.notice("start: source=\(self.settings.audioSource.rawValue, privacy: .public) lang=\(self.settings.sourceLanguage, privacy: .public) model=\(self.settings.displayName(self.settings.translationModel), privacy: .public)")

        let mode = settings.audioSource
        let testing = ProcessInfo.processInfo.environment["LIVESUB_TEST_AUDIO_FILE"] != nil
        if mode != .system, !testing {
            guard await MicrophoneCapture.requestPermission() else {
                status = CaptureError.permissionDenied(L("麦克风")).localizedDescription
                return
            }
        }

        await prepareTranslators()
        appLog.notice("start: translators ready (\(self.translator?.name ?? "none", privacy: .public))")
        if store == nil {
            store = TranscriptStore(folderPath: settings.transcriptFolder, started: Date(),
                                    source: settings.source, target: settings.target)
        }

        var captures: [AudioCapture] = []
        if let testFile = ProcessInfo.processInfo.environment["LIVESUB_TEST_AUDIO_FILE"] {
            captures.append(FileCapture(url: URL(fileURLWithPath: testFile)))
        } else {
            if mode != .system { captures.append(MicrophoneCapture(noiseReduction: settings.micNoiseReduction)) }
            if mode != .microphone { captures.append(SystemAudioCapture()) }
        }

        var started: [SourcePipeline] = []
        do {
            for capture in captures {
                let pipeline = SourcePipeline(capture: capture, label: mode == .both ? capture.label : nil)
                appLog.notice("start: recognizer for \(String(describing: type(of: capture)), privacy: .public)")
                try await pipeline.engine.start(
                    localeID: settings.sourceLanguage, guardLocaleID: settings.guardLocale,
                    onStatus: { [weak self] message, isError in
                        Task { @MainActor in
                            self?.status = message
                            if isError { self?.showAlert(message) }
                        }
                    },
                    onEvent: { [weak self, weak pipeline] event in
                        guard let self, let pipeline else { return }
                        self.handle(event, from: pipeline)
                    })
                let engine = pipeline.engine
                appLog.notice("start: recognizer ready, starting audio capture")
                // 在后台线程启动采集：第一次录系统声音时，Core Audio 会一直等到用户回答权限弹窗才返回，
                // 放在主线程上整个界面（字幕、菜单）都会冻住。
                let box = UncheckedSendable(capture)
                pipeline.captureStart = Date()
                try await Task.detached(priority: .userInitiated) {
                    try box.value.start { buffer in engine.append(buffer) }
                }.value
                if sessionStart == nil { sessionStart = pipeline.captureStart }
                appLog.notice("start: audio capture running")
                started.append(pipeline)
            }
        } catch {
            for p in started { p.capture.stop(); await p.engine.stop() }
            status = L("启动失败：%@", error.localizedDescription)
            appLog.error("start failed: \(error.localizedDescription, privacy: .public)")
            return
        }

        pipelines = started
        isRunning = true
        activity = ProcessInfo.processInfo.beginActivity(options: [.userInitiated, .latencyCritical], reason: "LiveSub 实时字幕")
        startedRecognitionKey = recognitionKey
        filteredCount = 0
        updateListeningStatus()
        ticker = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
    }

    func stop() async {
        guard isRunning else { return }
        appLog.notice("stop")
        if let activity { ProcessInfo.processInfo.endActivity(activity) }
        activity = nil
        isRunning = false
        ticker?.invalidate()
        ticker = nil
        let current = pipelines
        pipelines = []
        for p in current { p.capture.stop() }
        for p in current {
            await p.engine.stop()
            p.volatileText = ""
            enqueue(p.segmenter.flushAll(), from: p)
        }
        live = [:]
        status = L("已停止")
    }

    /// 结束当前记录文件，下次开始时新建一个。
    func newSession() {
        Task {
            await stop()
            lines = []
            queue = []
            backlog = 0
            store = nil
            sessionStart = nil
            translator?.shutdown()
            translator = nil
            assistant.newConversation()
            status = L("已开始新的记录")
        }
    }

    /// 设置改变后自动生效：识别相关的设置变了就重启识别，否则只重建翻译器。
    /// 稍等一下再应用，避免在文本框里打字时每个字都重来一次。
    func settingsChanged() {
        applyTask?.cancel()
        applyTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(800))
            guard let self, !Task.isCancelled else { return }
            await self.applySettings()
        }
    }

    private var recognitionKey: String {
        [settings.sourceLanguage, settings.audioSource.rawValue, settings.guardLocale ?? "", String(settings.micNoiseReduction)]
            .joined(separator: "\u{1}")
    }

    private func applySettings() async {
        appLog.notice("apply settings (running: \(self.isRunning))")
        while isStarting { try? await Task.sleep(for: .milliseconds(100)) }
        resetTranslators()
        guard isRunning else { return }
        if recognitionKey != startedRecognitionKey {
            await stop()
            await start()
        } else {
            await prepareTranslators()
            updateListeningStatus()
        }
    }

    private func resetTranslators() {
        translator?.shutdown()
        translator = nil
        fallback = nil
        usingFallback = false
    }

    private func updateListeningStatus() {
        listeningStatus = L("正在听：%@ · %@ → %@ · %@", settings.audioSource.title, L(settings.source.displayName),
                            L(settings.target.displayName), translator?.name ?? "")
        status = filteredCount > 0 ? L("%@ · 已过滤中文说话 %d 段", listeningStatus, filteredCount) : listeningStatus
    }

    func shutdown() {
        translator?.shutdown()
        assistant.shutdown()
    }

    // MARK: - 翻译器

    private func prepareTranslators() async {
        // 上次 Claude 不可用（例如未登录）的话，每次“开始”都重新试一次：用户可能已经登录了。
        if usingFallback {
            translator?.shutdown()
            translator = nil
            usingFallback = false
            ClaudeCLI.reloadShellEnvironment()
        }
        let source = settings.source, target = settings.target
        // Apple 本地翻译（所选模型出错时的备用翻译）需要 macOS 26。
        fallback = nil
        if #available(macOS 26, *), source.translationCode != target.translationCode,
           await AppleTranslator.isInstalled(source: source, target: target) {
            fallback = AppleTranslator(source: source, target: target)
        }

        guard translator == nil else { return }
        guard settings.translationModel != .apple else {
            if #available(macOS 26, *) {
                translator = fallback ?? AppleTranslator(source: source, target: target)
            } else {
                status = L("Apple 本地翻译需要 macOS 26 或更新版本，请选择其他翻译模型。")
                showAlert(status)
            }
            return
        }
        do {
            let session = try ChatSessionFactory.make(
                settings.translationModel, settings: settings,
                systemPrompt: Prompts.translatorSystemPrompt(source: source, target: target,
                                                             correctMishearings: correcting),
                maxTurns: 40, historyTurns: 12, timeout: 25)
            session.warmUp()
            translator = session
            translatorBuiltDifficult = correcting
        } catch {
            // 所选模型用不了（没装 Claude Code、服务被删除）：先用 Apple 本地翻译顶上。
            translator = fallback
            usingFallback = fallback != nil
            status = error.localizedDescription
            showAlert(error.localizedDescription)
        }
    }

    // MARK: - 识别事件

    private func handle(_ event: TranscriptEvent, from pipeline: SourcePipeline) {
        switch event {
        case .volatile(let text):
            pipeline.volatileText = text
            pipeline.finalizeRequested = false
        case .final(let result):
            if lines.isEmpty { appLog.notice("first final result") }
            pipeline.volatileText = ""
            pipeline.finalizeRequested = false
            if let start = result.start, let end = result.end {
                pipeline.timeline.addFinal(result.text, start: start, end: end)
            }
            updateQuality(result.confidences)
            enqueue(pipeline.segmenter.addFinal(result.text), from: pipeline)
        case .filtered:
            pipeline.volatileText = ""
            filteredCount += 1
            status = L("%@ · 已过滤中文说话 %d 段", listeningStatus, filteredCount)
        }
        updateLive(for: pipeline)
    }

    private func tick() {
        updateInputStatus()
        if alert != nil, Date().timeIntervalSince(alertAt) > 10 { alert = nil }
        for p in pipelines {
            // 声音停了（1.2 秒没收到音频）但还有没确认的半句：让识别器立即确认，好送去翻译。
            if !p.volatileText.isEmpty, !p.finalizeRequested, p.engine.inputStatus.silentFor > 1.2 {
                p.finalizeRequested = true
                let engine = p.engine
                Task { await engine.finalizePending() }
            }
            let segments = p.segmenter.tick(speechInProgress: !p.volatileText.isEmpty)
            if !segments.isEmpty {
                enqueue(segments, from: p)
                updateLive(for: p)
            }
        }
    }

    private func updateLive(for pipeline: SourcePipeline) {
        let key = pipeline.label ?? ""
        let text = pipeline.liveSource.isEmpty ? nil : pipeline.liveSource
        if live[key] != text { live[key] = text }
    }

    // MARK: - 识别质量（自动判断是否需要按发音纠正）

    /// 翻译时是否按发音纠正识别错误：打开了“自动纠正口音”，并且最近识别困难。
    var correcting: Bool { settings.autoCorrect && recognitionDifficult }

    private func updateQuality(_ confidences: [Double]) {
        guard quality.add(confidences) else { return }
        recognitionDifficult = quality.isDifficult
        let average = quality.averageConfidence.map { String(format: "%.2f", $0) } ?? "-"
        appLog.notice("recognition \(self.recognitionDifficult ? "difficult" : "clear", privacy: .public) (avg confidence \(average, privacy: .public))")
        // 没打开自动纠正时只在状态条上提示，不换翻译进程。
        guard settings.autoCorrect, settings.translationModel.isLanguageModel else { return }
        // 换一个带/不带“按发音纠正”的翻译进程；旧进程等正在翻译的句子完成后再关。
        let old = translator
        translator = nil
        Task {
            await prepareTranslators()
            updateListeningStatus()
            try? await Task.sleep(for: .seconds(30))
            old?.shutdown()
        }
    }

    // MARK: - 状态条

    private func updateInputStatus() {
        var level: Float = 0
        var silent = TimeInterval.infinity
        for p in pipelines {
            let s = p.engine.inputStatus
            level = max(level, s.level)
            silent = min(silent, s.silentFor)
        }
        // 只在明显变化时更新，避免界面每 0.25 秒重绘。
        let quantized = (min(level * 8, 1) * 10).rounded() / 10
        if quantized != inputLevel { inputLevel = quantized }
        let silentRounded = silent.isFinite ? silent.rounded() : 999
        if silentRounded != silentFor { silentFor = silentRounded }
    }

    private func showAlert(_ message: String) {
        alert = message
        alertAt = Date()
    }

    // MARK: - 导出字幕

    /// 字幕文件内容：时间相对这份记录开始录音的时刻；文字按当前“显示内容”（双语/仅译文/仅原文）。
    func subtitleFileContents(format: SubtitleFileFormat) -> String {
        let reference = sessionStart ?? lines.compactMap(\.audioStart).min() ?? lines.first?.time ?? Date()
        let mode = settings.displayMode
        let cues: [SubtitleExport.Cue] = lines.map { line in
            let text: String
            switch mode {
            case .bilingual: text = line.translation.isEmpty ? line.source : "\(line.source)\n\(line.translation)"
            case .translationOnly: text = line.translation.isEmpty ? line.source : line.translation
            case .sourceOnly: text = line.source
            }
            // 没有逐词时间的句子按入队时间往前估 3 秒。
            let start = line.audioStart ?? line.time.addingTimeInterval(-3)
            let end = line.audioEnd ?? line.time
            return SubtitleExport.Cue(start: start.timeIntervalSince(reference), end: end.timeIntervalSince(reference), text: text)
        }
        return format == .srt ? SubtitleExport.srt(cues) : SubtitleExport.vtt(cues)
    }

    func exportSubtitles(format: SubtitleFileFormat) {
        let content = subtitleFileContents(format: format)
        let panel = NSSavePanel()
        panel.nameFieldStringValue = (store?.fileURL.deletingPathExtension().lastPathComponent ?? L("LiveSub 字幕")) + "." + format.rawValue
        panel.directoryURL = store?.fileURL.deletingLastPathComponent()
        NSApp.activate()
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try content.write(to: url, atomically: true, encoding: .utf8)
            status = L("字幕已导出：%@", url.lastPathComponent)
        } catch {
            showAlert(L("导出失败：%@", error.localizedDescription))
        }
    }

    // MARK: - 翻译队列

    private func enqueue(_ segments: [String], from pipeline: SourcePipeline) {
        for text in segments {
            var line = SubtitleLine(time: Date(), sourceLabel: pipeline.label, source: text)
            if let t = pipeline.timeline.take(text) {
                line.audioStart = pipeline.captureStart.addingTimeInterval(t.start)
                line.audioEnd = pipeline.captureStart.addingTimeInterval(t.end)
            }
            lines.append(line)
            queue.append(line.id)
        }
        backlog = queue.count
        if !segments.isEmpty { runWorker() }
    }

    private func runWorker() {
        guard !workerRunning else { return }
        workerRunning = true
        Task {
            while let id = dequeueMerged() {
                await translateLine(id)
            }
            workerRunning = false
        }
    }

    /// 取出下一句。如果积压了 2 句以上（翻译跟不上语速），把同一音源的积压句合并成一句一起翻译，
    /// 让字幕延迟不会越积越长。
    private func dequeueMerged() -> UUID? {
        guard !queue.isEmpty else { return nil }
        let first = queue.removeFirst()
        backlog = queue.count
        guard queue.count >= 2, let firstIndex = index(of: first) else { return first }
        let label = lines[firstIndex].sourceLabel
        var merged: [UUID] = []
        for id in queue {
            guard let i = index(of: id), lines[i].sourceLabel == label,
                  Segmenter.units(lines[firstIndex].source) + Segmenter.units(lines[i].source) <= 70
            else { break }
            lines[firstIndex].source = Segmenter.join(lines[firstIndex].source, lines[i].source)
            if let end = lines[i].audioEnd { lines[firstIndex].audioEnd = end }
            merged.append(id)
        }
        queue.removeAll { merged.contains($0) }
        lines.removeAll { merged.contains($0.id) }
        backlog = queue.count
        return first
    }

    private func translateLine(_ id: UUID) async {
        guard let i = index(of: id) else { return }
        let source = lines[i].source
        lines[i].state = .translating
        lines[i].translateStartedAt = Date()
        let context = recentContext(before: id)

        if settings.source.translationCode == settings.target.translationCode {
            finishLine(id, translation: "", note: nil)
            return
        }

        let primary = usingFallback ? nil : translator
        if let primary {
            do {
                let cjk = settings.targetIsCJK
                let result = try await primary.translate(source, context: context) { [weak self] partial in
                    let visible = TranslationOutput.visibleTranslation(partial, targetIsCJK: cjk)
                    Task { @MainActor in self?.updatePartial(id, visible) }
                }
                let output = TranslationOutput.split(result, original: source, targetIsCJK: cjk)
                finishLine(id, translation: output.translation, note: nil, correctedSource: output.correctedSource)
                return
            } catch {
                let err = error as? TranslatorError
                if err?.isPersistent == true { usingFallback = true }
                status = error.localizedDescription
                showAlert(error.localizedDescription)
                appLog.error("translate failed: \(error.localizedDescription, privacy: .public)")
            }
        }
        if let fallback, fallback !== primary,
           let result = try? await fallback.translate(source, context: context, onPartial: { _ in }) {
            finishLine(id, translation: result, note: L("Apple 备用"))
        } else {
            if let j = index(of: id) { lines[j].state = .failed }
            persist(id)
        }
    }

    private func updatePartial(_ id: UUID, _ partial: String) {
        if let i = index(of: id), lines[i].firstTokenAt == nil, !partial.isEmpty { lines[i].firstTokenAt = Date() }
        guard let i = index(of: id), lines[i].state == .translating,
              partial.count >= lines[i].translation.count else { return }
        lines[i].translation = partial
    }

    private func finishLine(_ id: UUID, translation: String, note: String?, correctedSource: String? = nil) {
        guard let i = index(of: id) else { return }
        if ProcessInfo.processInfo.environment["LIVESUB_DEBUG_LATENCY"] != nil {
            let l = lines[i], now = Date()
            func gap(_ a: Date?, _ b: Date?) -> String {
                guard let a, let b else { return "  -  " }
                return String(format: "%5.2f", b.timeIntervalSince(a))
            }
            FileHandle.standardError.write(Data(("[latency] 说完→入队 \(gap(l.audioEnd, l.time))  入队→开始 \(gap(l.time, l.translateStartedAt))"
                + "  开始→首字 \(gap(l.translateStartedAt, l.firstTokenAt))  首字→完成 \(gap(l.firstTokenAt, now))"
                + "  总计 \(gap(l.audioEnd, now))  | \(l.source.prefix(50))\n").utf8))
        }
        // Claude 纠正了识别错误（口音、近音词）时，字幕和记录里都换成纠正后的原文。
        if let correctedSource {
            if settings.keepOriginalText { lines[i].originalSource = lines[i].source }
            lines[i].source = correctedSource
        }
        lines[i].translation = translation
        lines[i].state = .done
        lines[i].note = note
        persist(id)
    }

    private func persist(_ id: UUID) {
        guard let line = lines.first(where: { $0.id == id }) else { return }
        store?.append(TranscriptFormat.entry(time: line.time, sourceLabel: line.sourceLabel,
                                             source: line.source, translation: line.translation,
                                             original: line.originalSource))
    }

    private func recentContext(before id: UUID) -> ContextPairs {
        guard let i = index(of: id) else { return [] }
        return lines[..<i].filter { $0.state == .done }.suffix(6).map { ($0.source, $0.translation) }
    }

    private func index(of id: UUID) -> Int? {
        // 新句子总在末尾，从后往前找更快。
        lines.lastIndex { $0.id == id }
    }

    /// 仅用于 `--stress-overlay` 自检：直接追加一句字幕、更新“正在说”。
    func debugAppend(source: String, translation: String, live liveText: String) {
        var line = SubtitleLine(time: Date(), sourceLabel: nil, source: source)
        line.translation = translation
        line.state = .done
        lines.append(line)
        live = ["": liveText]
        isRunning = true
    }

    /// 压力测试：新句子先进入“翻译中”，之后由 `debugAdvanceStreaming` 逐字补全译文。
    func debugAppendStreaming(source: String, translation: String) {
        var line = SubtitleLine(time: Date(), sourceLabel: nil, source: source)
        line.state = .translating
        debugPendingTranslation = (line.id, translation)
        lines.append(line)
        isRunning = true
    }

    func debugAdvanceStreaming(by count: Int) {
        guard let (id, full) = debugPendingTranslation, let i = lines.firstIndex(where: { $0.id == id }) else { return }
        lines[i].translation = String(full.prefix(lines[i].translation.count + count))
        if lines[i].translation.count >= full.count {
            lines[i].state = .done
            debugPendingTranslation = nil
        }
    }

    @ObservationIgnored private var debugPendingTranslation: (UUID, String)?

    func debugUpdateLive(_ text: String) {
        live = ["": text]
    }

    /// 仅用于 `--render-window` 自检：填入示例字幕。
    func loadPreview() {
        var a = SubtitleLine(time: Date(), sourceLabel: nil, source: "Today we're going to look at eigenvalues and eigenvectors, which are probably the most important idea in this course.")
        a.translation = "今天我们来看特征值 (eigenvalue) 和特征向量 (eigenvector)，这可能是本课程最重要的概念。"
        a.state = .done
        var b = SubtitleLine(time: Date(), sourceLabel: nil, source: "An eigenvector of a matrix A is a nonzero vector v such that A times v equals lambda times v.")
        b.translation = "矩阵 A 的特征向量是一个非零向量 v，使得 A 乘以 v 等于 λ 乘以 v。"
        b.state = .translating
        lines = [a, b]
        live = ["": "In other words, the matrix only stretches"]
        isRunning = true
    }

    // MARK: - 笔记

    func generateNotes() {
        guard !notesInProgress else { return }
        let done = lines.filter { !$0.source.isEmpty }
        guard !done.isEmpty else {
            status = L("还没有字幕记录，无法生成笔记。")
            return
        }
        // 笔记用 AI 助手的模型。
        let session: ChatSession
        do {
            session = try ChatSessionFactory.make(settings.assistantModel, settings: settings,
                                                  systemPrompt: Prompts.notesPrompt(target: settings.target),
                                                  maxTurns: 1, timeout: 600)
        } catch {
            status = error.localizedDescription
            return
        }
        notesInProgress = true
        status = L("正在用 %@ 生成笔记…", settings.displayName(settings.assistantModel))
        let transcript = TranscriptFormat.plain(done.map { ($0.time, $0.source, $0.translation) })
        let notesURL = store?.notesURL
            ?? FileManager.default.temporaryDirectory.appendingPathComponent(L("LiveSub 笔记") + ".md")
        Task {
            defer { notesInProgress = false }
            do {
                let notes = try await session.request(newSessionMessage: transcript, ongoingMessage: transcript,
                                                      onPartial: { _ in })
                session.shutdown()
                try notes.write(to: notesURL, atomically: true, encoding: .utf8)
                status = L("笔记已保存：%@", notesURL.lastPathComponent)
                NSWorkspace.shared.open(notesURL)
            } catch {
                status = L("生成笔记失败：%@", error.localizedDescription)
            }
        }
    }
}
