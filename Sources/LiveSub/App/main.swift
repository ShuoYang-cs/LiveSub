import AVFoundation
import AppKit
import LiveSubCore
import SwiftUI

// 命令行自检模式（不启动界面）：
//   LiveSub --transcribe-file <音频文件> [--locale en-US] [--guard zh-CN]（过滤中文说话声）[--realtime]（按真实速度播放）
//   LiveSub --translate "句子1" "句子2" ... [--engine claude|apple|api]
//     --engine api 时：--format openai|anthropic --base-url URL --model 模型名，密钥从环境变量 LIVESUB_API_KEY 读取 [--model haiku] [--target zh-Hans] [--correct]（按发音纠正识别错误）
let arguments = CommandLine.arguments

func option(_ name: String) -> String? {
    guard let i = arguments.firstIndex(of: name), i + 1 < arguments.count else { return nil }
    return arguments[i + 1]
}

func runTranscribeFile(_ path: String) async {
    let locale = option("--locale") ?? "en-US"
    let engine = TranscriptionEngine()
    let start = Date()
    let segmenter = SegmenterBox()
    @Sendable func stamp() -> String { String(format: "%6.2fs", Date().timeIntervalSince(start)) }
    do {
        try await engine.start(localeID: locale, guardLocaleID: option("--guard"),
                               onStatus: { message, _ in print("[status] \(message)") }, onEvent: { event in
            switch event {
            case .volatile(let text):
                print("\(stamp()) ~ \(text)")
            case .final(let result):
                let conf = result.confidences.isEmpty ? 0 : result.confidences.reduce(0, +) / Double(result.confidences.count)
                print("\(stamp()) = \(result.text)  [conf \(String(format: "%.2f", conf))]")
                for s in await segmenter.add(result.text) { print("\(stamp()) >>> SEGMENT: \(s)") }
            case .filtered(let text):
                print("\(stamp()) x FILTERED: \(text)")
            }
        })
        let file = try AVAudioFile(forReading: URL(fileURLWithPath: path))
        let format = file.processingFormat
        print("[file] \(file.length) frames @ \(format.sampleRate) Hz, \(format.channelCount) ch")
        if arguments.contains("--realtime") {
            let capture = FileCapture(url: URL(fileURLWithPath: path))
            try capture.start { engine.append($0) }
            try await Task.sleep(for: .seconds(Double(file.length) / format.sampleRate + 3))
            capture.stop()
        } else {
            while file.framePosition < file.length {
                guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 4096) else { break }
                try file.read(into: buffer)
                if buffer.frameLength == 0 { break }
                engine.append(buffer)
            }
        }
        await engine.stop()
        for s in await segmenter.flush() { print("\(stamp()) >>> SEGMENT (flush): \(s)") }
        print("[done] \(stamp())")
    } catch {
        print("[error] \(error.localizedDescription)")
        exit(1)
    }
}

actor SegmenterBox {
    var segmenter = Segmenter()
    func add(_ text: String) -> [String] { segmenter.addFinal(text) }
    func flush() -> [String] { segmenter.flushAll() }
}

func runTranslate(_ texts: [String]) async {
    let source = LanguageOption.source(option("--source") ?? "en-US")
    let target = LanguageOption.target(option("--target") ?? "zh-Hans")
    let translator: Translator
    if option("--engine") == "apple" {
        guard #available(macOS 26, *) else {
            print("[error] Apple 本地翻译需要 macOS 26")
            exit(1)
        }
        translator = AppleTranslator(source: source, target: target)
    } else if option("--engine") == "api" {
        guard let base = option("--base-url"), let model = option("--model") else {
            print("[error] --engine api 需要 --base-url 和 --model")
            exit(1)
        }
        translator = APIChatSession(config: .init(
            format: option("--format") == "anthropic" ? .anthropic : .openAI, baseURL: base,
            apiKey: ProcessInfo.processInfo.environment["LIVESUB_API_KEY"], model: model,
            systemPrompt: Prompts.translatorSystemPrompt(source: source, target: target,
                                                         correctMishearings: arguments.contains("--correct")),
            displayName: model, historyTurns: 12))
    } else {
        guard let exe = ClaudeCLI.locate(customPath: "") else {
            print("[error] \(TranslatorError.claudeNotFound.localizedDescription)")
            exit(1)
        }
        print("[claude] \(exe.path)")
        let claude = ClaudeTranslator(config: .init(
            executable: exe, model: option("--model") ?? "haiku",
            systemPrompt: Prompts.translatorSystemPrompt(source: source, target: target,
                                                         correctMishearings: arguments.contains("--correct"))))
        claude.warmUp()
        translator = claude
    }
    var history: ContextPairs = []
    for text in texts {
        let t0 = Date()
        let firstToken = FirstTokenClock()
        do {
            let raw = try await translator.translate(text, context: history) { _ in firstToken.mark() }
            let first = firstToken.elapsed(since: t0).map { String(format: "%.2fs", $0) } ?? "-"
            let cjk = ["zh", "ja", "ko"].contains { target.translationCode.hasPrefix($0) }
            let (result, corrected) = TranslationOutput.split(raw, original: text, targetIsCJK: cjk)
            print(String(format: "[%.2fs, 首字 %@] %@\n    → %@", Date().timeIntervalSince(t0), first, text, result))
            if let corrected { print("    ✎ \(corrected)") }
            history.append((corrected ?? text, result))
        } catch {
            print("[error] \(error.localizedDescription)")
            translator.shutdown()
            exit(1)
        }
    }
    translator.shutdown()
}

final class FirstTokenClock: @unchecked Sendable {
    private let lock = NSLock()
    private var at: Date?
    func mark() { lock.withLock { if at == nil { at = Date() } } }
    func elapsed(since t0: Date) -> TimeInterval? { lock.withLock { at.map { $0.timeIntervalSince(t0) } } }
}

// 用真实的字幕窗口（含滚动区域、缩放手柄）离屏截图：--render-window out.png [--hover]（显示快捷按钮）
// 固定高度模式先 `defaults write LiveSub overlayHeight -float 200` 再运行。
if let out = option("--render-window") {
    MainActor.assumeIsolated {
        let controller = SubtitleController(settings: AppSettings.shared)
        controller.loadPreview()
        let overlay = OverlayWindowController(
            controller: controller, settings: AppSettings.shared,
            actions: OverlayActions(toggleRunning: {}, lock: {}, showHistory: {}, showSettings: {}, minimize: {}, toggleAssistant: {}, modelChanged: {}, manageModels: {}))
        overlay.state.isHovering = arguments.contains("--hover")
        overlay.panel.setFrameOrigin(NSPoint(x: -20000, y: -20000))
        overlay.show()
        RunLoop.main.run(until: Date().addingTimeInterval(1.5))
        guard let view = overlay.panel.contentView,
              let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { exit(1) }
        view.cacheDisplay(in: view.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: out))
        print("saved \(out) \(view.bounds.size)")
    }
    exit(0)
}

// 字幕窗口压力测试（离屏）：快速追加字幕、不停更新“正在说”，报告主线程最长卡顿、窗口高度有没有变，
// 以及最后是否还停在最新一句。加 --streaming：句子先“翻译中”再逐字出译文（检查贴底跟随）。
// 用户拖出来的高度先 `defaults write LiveSub overlayHeight -float 130`。
if arguments.contains("--stress-overlay") {
    MainActor.assumeIsolated {
        let controller = SubtitleController(settings: AppSettings.shared)
        let overlay = OverlayWindowController(
            controller: controller, settings: AppSettings.shared,
            actions: OverlayActions(toggleRunning: {}, lock: {}, showHistory: {}, showSettings: {}, minimize: {}, toggleAssistant: {}, modelChanged: {}, manageModels: {}))
        overlay.panel.setFrameOrigin(NSPoint(x: -20000, y: -20000))
        overlay.show()
        var maxGap = 0.0, last = Date(), n = 0, ticks = 0
        var minHeight = CGFloat.infinity, maxHeight: CGFloat = 0
        let started = Date()
        let streaming = arguments.contains("--streaming")
        let timer = Timer.scheduledTimer(withTimeInterval: 0.02, repeats: true) { _ in
            let now = Date()
            maxGap = max(maxGap, now.timeIntervalSince(last))
            last = now
            ticks += 1
            MainActor.assumeIsolated {
                minHeight = min(minHeight, overlay.panel.frame.height)
                maxHeight = max(maxHeight, overlay.panel.frame.height)
                if ticks % 3 == 0 {
                    controller.debugUpdateLive(String(repeating: "word ", count: ticks % 25 + 1))
                }
                if streaming {
                    // 像真实翻译一样：句子先进入“翻译中”，译文每 40 毫秒多出两个字。
                    if ticks % 2 == 0 { controller.debugAdvanceStreaming(by: 2) }
                    if ticks % 60 == 0, n < 400 {
                        n += 1
                        controller.debugAppendStreaming(
                            source: "Line \(n): so the longest common subsequence " + String(repeating: "and more ", count: n % 9),
                            translation: "第 \(n) 句 " + String(repeating: "最长公共子序列", count: n % 6 + 2))
                    }
                } else if ticks % 4 == 0, n < 400 {
                    n += 1
                    controller.debugAppend(
                        source: "Line \(n): so the longest common subsequence " + String(repeating: "and more ", count: n % 9),
                        translation: "第 \(n) 句 " + String(repeating: "最长公共子序列", count: n % 6 + 1),
                        live: "")
                }
            }
        }
        RunLoop.main.run(until: Date().addingTimeInterval(20))
        let elapsed = Date().timeIntervalSince(started)
        timer.invalidate()
        // 停止追加后等滚动动画结束，再看是否停在最新一句。
        RunLoop.main.run(until: Date().addingTimeInterval(1))
        var distance = "-"
        if #available(macOS 15, *) { distance = String(format: "%.0f", overlay.state.follow.distanceFromBottom) }
        print(String(format: "lines %d, ticks %d in %.1fs, max main-thread gap %.3fs, window height %.0f–%.0f pt, following latest: %@, distance from bottom: %@ pt",
                     n, ticks, elapsed, maxGap, minHeight, maxHeight, overlay.state.atBottom ? "yes" : "NO", distance))
    }
    exit(0)
}

// 主线程监视自检：故意卡住主线程 8 秒，应在 ~/Library/Logs/LiveSub/ 生成 hang-*.txt。
if arguments.contains("--test-watchdog") {
    MainThreadWatchdog.shared.start()
    Thread.sleep(forTimeInterval: 1.5)
    DispatchQueue.main.async {
        Thread.sleep(forTimeInterval: 8)
        print("main thread resumed")
        exit(0)
    }
    dispatchMain()
}

// AI 助手端到端自检（真的调用 Claude）：--assistant-test <字幕记录.md> [--ask "问题"]
// 先只给前一半字幕问一次，再给全部字幕问一次（验证“新会话带全部字幕 / 之后只带新增字幕”）。
if let path = option("--assistant-test") {
    MainActor.assumeIsolated {
        let text = (try? String(contentsOfFile: path, encoding: .utf8)) ?? ""
        let calendar = Calendar.current
        let today = calendar.startOfDay(for: Date())
        // 记录格式：**[13:41:05]** 原文
        let entries: [SubtitleLine] = text.split(separator: "\n").compactMap { raw in
            guard raw.hasPrefix("**["), let close = raw.firstIndex(of: "]"),
                  let body = raw.range(of: "** ", range: close..<raw.endIndex) else { return nil }
            let parts = raw[raw.index(raw.startIndex, offsetBy: 3)..<close].split(separator: ":").compactMap { Int($0) }
            guard parts.count == 3 else { return nil }
            let seconds = Double(parts[0] * 3600 + parts[1] * 60 + parts[2])
            var line = SubtitleLine(time: today.addingTimeInterval(seconds), sourceLabel: nil, source: String(raw[body.upperBound...]))
            line.state = .done
            return line
        }
        let assistant = AssistantSession(settings: AppSettings.shared)
        assistant.newConversation()
        // newConversation 只带“开始”之后的字幕；自检时把开始时间放到最早一句之前。
        var visible = Array(entries.prefix(entries.count / 2))
        assistant.transcript = { (visible, "") }
        assistant.debugBackdate(to: today)
        @MainActor func ask(_ q: String) {
            let t0 = Date()
            assistant.ask(q)
            while assistant.isAnswering { RunLoop.main.run(until: Date().addingTimeInterval(0.1)) }
            print(String(format: "[%.1fs] Q: %@\nA: %@\n", Date().timeIntervalSince(t0), q, assistant.messages.last?.text ?? ""))
        }
        if let question = option("--ask") {
            // 自定义问题（例如让它把总结存成文件），带上全部字幕。
            visible = entries
            ask(question)
            for file in assistant.messages.last?.files ?? [] {
                print("[file] \(file.url?.path ?? "-") \(file.error ?? "")")
            }
            assistant.shutdown()
            exit(0)
        }
        print("[lines] first half \(visible.count) / all \(entries.count)")
        ask("在讲哪个算法？一句话。")
        visible = entries
        ask("最后几分钟讲到哪一步了？一句话。")
        assistant.shutdown()
    }
    exit(0)
}

// AI 助手面板离屏截图：--render-assistant out.png
if let out = option("--render-assistant") {
    MainActor.assumeIsolated {
        let assistant = AssistantSession(settings: AppSettings.shared)
        assistant.debugLoad([
            ("在讲哪个算法？", "在讲用动态规划（**bottom-up**）求**最长公共子序列**（LCS）：用二维表 `c[i][j]` 填 X=53142、Y=23145，先把第 0 行和第 0 列初始化为 0。"),
            ("最后几分钟讲到哪一步了？", "开始填非基础格子了：两个末尾字符不相等时取 `max(c[i-1][j], c[i][j-1])`。"),
        ])
        let window = NSWindow(contentRect: NSRect(x: -20000, y: -20000, width: 380, height: 420), styleMask: [.borderless],
                              backing: .buffered, defer: false)
        window.contentView = NSHostingView(rootView: AssistantView(assistant: assistant, settings: AppSettings.shared, onClose: {}))
        window.orderFrontRegardless()
        RunLoop.main.run(until: Date().addingTimeInterval(1.5))
        guard let view = window.contentView,
              let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { exit(1) }
        view.cacheDisplay(in: view.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: out))
        print("saved \(out) \(view.bounds.size)")
    }
    exit(0)
}

// 整条链路 + 字幕导出自检：--export-test <音频文件> <输出.srt>
// 按真实速度“播放”音频文件，走识别、分句、翻译，结束后把 SRT 写到输出文件。
if let path = option("--export-test"), let i = arguments.firstIndex(of: "--export-test"), i + 2 < arguments.count {
    let out = arguments[i + 2]
    setenv("LIVESUB_TEST_AUDIO_FILE", path, 1)
    let duration = (try? AVAudioFile(forReading: URL(fileURLWithPath: path))).map { Double($0.length) / $0.processingFormat.sampleRate } ?? 0
    Task { @MainActor in
        let controller = SubtitleController(settings: AppSettings.shared)
        await controller.start()
        try? await Task.sleep(for: .seconds(duration + 4))
        await controller.stop()
        // 等翻译做完（最多 30 秒）。
        for _ in 0..<300 where controller.backlog > 0 || controller.lines.contains(where: { $0.state == .translating || $0.state == .waiting }) {
            try? await Task.sleep(for: .milliseconds(100))
        }
        let srt = controller.subtitleFileContents(format: .srt)
        try? srt.write(toFile: out, atomically: true, encoding: .utf8)
        print(srt)
        controller.shutdown()
        exit(0)
    }
    dispatchMain()
}

// 设置窗口离屏截图：--render-settings out.png [--more]（展开“更多”） [--height 1100]
if let out = option("--render-settings") {
    MainActor.assumeIsolated {
        let state = SettingsViewState()
        state.showMore = arguments.contains("--more")
        let window = NSWindow(contentViewController: NSHostingController(rootView: SettingsView(
            settings: AppSettings.shared, state: state, onTranslationSettingsChanged: {}, onAppearanceChanged: {}, onManageModels: {})))
        if let height = option("--height").flatMap(Double.init) { window.setContentSize(NSSize(width: 480, height: height)) }
        window.setFrameOrigin(NSPoint(x: -20000, y: -20000))
        window.orderFrontRegardless()
        RunLoop.main.run(until: Date().addingTimeInterval(1.5))
        guard let view = window.contentView,
              let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { exit(1) }
        view.cacheDisplay(in: view.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: out))
        print("saved \(out) \(view.bounds.size)")
    }
    exit(0)
}

// 自检模式不能阻塞主线程（Translation 框架需要主线程的 run loop），所以用 dispatchMain。
if let path = option("--transcribe-file") {
    Task.detached { await runTranscribeFile(path); exit(0) }
    dispatchMain()
} else if let i = arguments.firstIndex(of: "--translate") {
    let texts = arguments[(i + 1)...].prefix { !$0.hasPrefix("--") }
    Task.detached { await runTranslate(Array(texts)); exit(0) }
    dispatchMain()
}

MainActor.assumeIsolated {
    let app = NSApplication.shared
    let delegate = AppDelegate()
    app.delegate = delegate
    app.setActivationPolicy(.accessory)
    withExtendedLifetime(delegate) { app.run() }
}
