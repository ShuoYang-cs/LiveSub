import AppKit
import LiveSubCore
import SwiftUI
import Translation

/// 设置窗口的界面状态（命令行工具链不能用 @State）。
@MainActor @Observable
final class SettingsViewState {
    /// 所选语言的 Apple 翻译语言包是否已下载；nil = 还没查到（或系统不支持）。
    var appleLanguagesInstalled: Bool?
    @ObservationIgnored private var downloadStorage: AnyObject?

    /// 触发系统的语言包下载窗口（TranslationSession.Configuration 需要 macOS 15）。
    @available(macOS 15, *)
    var download: LanguageDownloadState {
        if let download = downloadStorage as? LanguageDownloadState { return download }
        let download = LanguageDownloadState()
        downloadStorage = download
        return download
    }
}

@available(macOS 15, *)
@MainActor @Observable
final class LanguageDownloadState {
    var configuration: TranslationSession.Configuration?

    func request(source: LanguageOption, target: LanguageOption) {
        let config = TranslationSession.Configuration(source: Locale.Language(identifier: source.translationCode),
                                                      target: Locale.Language(identifier: target.translationCode))
        // 同一组语言再点一次：配置相同不会重新触发，要 invalidate。
        if configuration?.source == config.source, configuration?.target == config.target {
            configuration?.invalidate()
        } else {
            configuration = config
        }
    }
}

/// 系统翻译框架的下载窗口只能从界面里的翻译会话弹出（`prepareTranslation`）。
private struct LanguageDownload: ViewModifier {
    let state: SettingsViewState
    let onFinished: () async -> Void

    func body(content: Content) -> some View {
        if #available(macOS 15, *) {
            content.translationTask(state.download.configuration) { session in
                do {
                    try await session.prepareTranslation()
                } catch {
                    appLog.notice("language download: \(String(describing: error), privacy: .public)")
                }
                await onFinished()
            }
        } else {
            content
        }
    }
}

struct SettingsView: View {
    @Bindable var settings: AppSettings
    var state = SettingsViewState()
    let onTranslationSettingsChanged: () -> Void
    let onAppearanceChanged: () -> Void
    let onManageModels: () -> Void
    let onShowHistory: () -> Void
    let onShowRecordings: () -> Void
    /// 字幕大小、位置和 AI 助手位置恢复默认。
    let onResetOverlay: () -> Void

    var body: some View {
        Form {
            Section("翻译") {
                LabeledContent("语言") {
                    HStack(spacing: 6) {
                        Picker("讲话语言", selection: $settings.sourceLanguage) {
                            ForEach(LanguageOption.sources) { Text(L($0.displayName)).tag($0.id) }
                        }
                        Image(systemName: "arrow.right").foregroundStyle(.secondary)
                        Picker("字幕语言", selection: $settings.targetLanguage) {
                            ForEach(LanguageOption.targets) { Text(L($0.displayName)).tag($0.id) }
                        }
                    }
                    .labelsHidden()
                    .fixedSize()
                }
                Picker("音源", selection: $settings.audioSource) {
                    ForEach(AudioSourceMode.allCases) { Text($0.shortTitle).tag($0) }
                }
                .pickerStyle(.segmented)
                if settings.audioSource != .system {
                    Toggle("麦克风降噪", isOn: $settings.micNoiseReduction)
                        .help("使用系统语音处理降低环境噪音并消除回声。讲者距离较远时可能降低识别效果。")
                }
                Picker("翻译模型", selection: $settings.translationModel) {
                    ForEach(settings.modelGroups(for: .translation)) { group in
                        Section(group.title) {
                            ForEach(group.choices, id: \.self) { choice in
                                Text(settings.menuTitle(choice, selection: settings.translationModel)).tag(choice)
                            }
                        }
                    }
                }
                .help("实时字幕建议选择速度快的模型。每家服务用哪个具体模型，在“模型服务”里调整。Apple 本地翻译可离线使用。")
                if settings.translationModel == .apple { appleNote }
                LabeledContent("模型服务") {
                    HStack {
                        Text(configuredServices == 0 ? L("填上 API 密钥即可使用 GPT、千问等") : L("已设置 %d 个", configuredServices))
                            .foregroundStyle(.secondary)
                        Button("管理…", action: onManageModels)
                    }
                }
                Toggle("自动纠正口音", isOn: $settings.autoCorrect)
                    .help("讲者口音较重、识别不准时，自动让大模型按发音和上下文纠正识别错误。使用 Google 翻译或 Apple 本地翻译时，纠正期间临时改用 Claude Sonnet，需要本机已登录 Claude Code。")
                if settings.autoCorrect {
                    Toggle("纠正时保留识别原文", isOn: $settings.keepOriginalText)
                        .help("纠正修改了原文时，在字幕记录中同时保留识别器原来的文字。")
                        .padding(.leading, 14)
                }
            }

            Section("字幕") {
                Picker("显示", selection: $settings.displayMode) {
                    ForEach(DisplayMode.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                Picker("外观", selection: $settings.appearance) {
                    ForEach(AppearanceMode.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                .onChange(of: settings.appearance) { onAppearanceChanged() }
                slider("字号", value: fontSize, range: 14...56, text: "\(Int(settings.translationFontSize)) pt")
                slider("背景", value: $settings.backgroundOpacity, range: 0...1,
                       text: "\(Int((settings.backgroundOpacity * 100).rounded()))%")
                LabeledContent("大小") {
                    Button("恢复默认", action: onResetOverlay)
                        .help("拖动字幕边框可以调整大小。点这里让字幕恢复默认大小，移回屏幕右上角；AI 助手回到字幕旁边。")
                }
                Toggle("显示状态条", isOn: $settings.showStatusBar)
                    .help("在字幕底部显示音频输入、识别与翻译状态。")
                Toggle("锁定字幕", isOn: $settings.clickThrough)
                    .help("字幕点不到、不挡操作。鼠标移到字幕上时右上角出现锁，点它解锁。")
                    .onChange(of: settings.clickThrough) { onAppearanceChanged() }
            }

            Section("记录") {
                LabeledContent("字幕记录") {
                    Button("查看…", action: onShowHistory)
                }
                LabeledContent("录音记录") {
                    Button("查看…", action: onShowRecordings)
                }
                LabeledContent("记录文件夹") {
                    HStack {
                        Text((settings.transcriptFolder as NSString).abbreviatingWithTildeInPath)
                            .lineLimit(1).truncationMode(.middle).foregroundStyle(.secondary)
                        Button("更改…", action: chooseFolder)
                    }
                }
            }
        }
        .formStyle(.grouped)
        .modifier(WindowBackgroundDrag())
        .frame(width: 480)
        .frame(minHeight: 600)
        .onChange(of: translationKey) { onTranslationSettingsChanged() }
        .task(id: settings.sourceLanguage + settings.targetLanguage) { await checkAppleLanguages() }
        .modifier(LanguageDownload(state: state, onFinished: checkAppleLanguages))
        // 用户也可能在“系统设置”里下载语言包，回到本应用时重新检查。
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            Task { await checkAppleLanguages() }
        }
    }

    /// 选了 Apple 本地翻译时的说明：能力范围，以及语言包还没下载时提供下载。
    @ViewBuilder
    private var appleNote: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("离线翻译，速度快，不需要账号。")
                .foregroundStyle(.secondary)
            if state.appleLanguagesInstalled == false {
                HStack {
                    Label(L("尚未下载“%@ → %@”语言包", L(settings.source.displayName), L(settings.target.displayName)),
                          systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange)
                    Spacer()
                    Button("下载…") {
                        if #available(macOS 15, *) { state.download.request(source: settings.source, target: settings.target) }
                    }
                }
            }
        }
        .font(.callout)
    }

    private func checkAppleLanguages() async {
        guard #available(macOS 26, *) else { return }
        let source = settings.source, target = settings.target
        state.appleLanguagesInstalled = source.translationCode == target.translationCode
            ? true : await AppleTranslator.isInstalled(source: source, target: target)
    }

    /// 能用的模型服务：本地的，或者已经填了 API 密钥的（默认列出的那几个没填密钥不算）。
    private var configuredServices: Int {
        settings.endpoints.filter { $0.isLocal || !(KeychainStore.key(for: $0.id) ?? "").isEmpty }.count
    }


    /// 一个滑块同时调译文和原文字号（原文约为译文的 0.62 倍）。
    private var fontSize: Binding<Double> {
        Binding(
            get: { settings.translationFontSize },
            set: { size in
                settings.translationFontSize = size.rounded()
                settings.sourceFontSize = (size * 0.62).rounded()
            })
    }

    /// 任何会影响识别或翻译的设置；改了就自动生效。
    private var translationKey: String {
        [settings.sourceLanguage, settings.targetLanguage, settings.audioSource.rawValue,
         settings.displayName(settings.translationModel), settings.claudePath,
         String(settings.micNoiseReduction),
         String(settings.autoCorrect)].joined(separator: "\u{1}")
    }

    private func slider(_ title: String, value: Binding<Double>, range: ClosedRange<Double>, text: String) -> some View {
        LabeledContent(L(title)) {
            HStack(spacing: 10) {
                Slider(value: value, in: range).frame(maxWidth: 240)
                Text(text)
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
                    .frame(width: 44, alignment: .trailing)
            }
        }
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.directoryURL = URL(fileURLWithPath: (settings.transcriptFolder as NSString).expandingTildeInPath)
        FilePanel.present(panel) { [settings] url in
            if let url { settings.transcriptFolder = url.path }
        }
    }
}

/// 按住空白处或文字也能拖动窗口，不只是标题栏（macOS 15 起；更早的系统只能拖标题栏）。
/// 按钮、开关等控件先处理自己的点击，不会被当成拖动。
struct WindowBackgroundDrag: ViewModifier {
    func body(content: Content) -> some View {
        if #available(macOS 15, *) {
            content.gesture(WindowDragGesture())
        } else {
            content
        }
    }
}

struct HistoryView: View {
    let controller: SubtitleController

    var body: some View {
        VStack(spacing: 0) {
            ScrollViewReader { proxy in
                List(controller.lines) { line in
                    VStack(alignment: .leading, spacing: 3) {
                        HStack(spacing: 6) {
                            Text(line.time, format: .dateTime.hour().minute().second())
                                .font(.caption.monospacedDigit())
                                .foregroundStyle(.secondary)
                            if let label = line.sourceLabel {
                                Text(label).font(.caption).foregroundStyle(.secondary)
                            }
                            if let note = line.note {
                                Text(note).font(.caption).foregroundStyle(.orange)
                            }
                        }
                        Text(line.source).textSelection(.enabled)
                        if let original = line.originalSource, original != line.source {
                            Text(L("识别原文：%@", original)).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                        }
                        if !line.translation.isEmpty {
                            Text(line.translation).font(.body.weight(.medium)).textSelection(.enabled)
                        }
                    }
                    .padding(.vertical, 2)
                    .id(line.id)
                }
                .onChange(of: controller.lines.count) {
                    if let last = controller.lines.last { proxy.scrollTo(last.id, anchor: .bottom) }
                }
            }
            Divider()
            HStack {
                Text(controller.status).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                Spacer()
                // 菜单栏图标去掉后（2026-10-22），记录相关的操作都在这里。
                Button("在 Finder 中显示") { openTranscriptFolder() }
                Menu("导出字幕") {
                    ForEach(SubtitleFileFormat.allCases, id: \.self) { format in
                        Button(format.menuTitle) { controller.exportSubtitles(format: format) }
                    }
                }
                .fixedSize()
                .disabled(controller.lines.isEmpty)
                Button("开始新的记录") { controller.newSession() }
                    .disabled(controller.lines.isEmpty)
                Button(controller.notesInProgress ? L("正在生成…") : L("生成笔记")) { controller.generateNotes() }
                    .disabled(controller.notesInProgress || controller.lines.isEmpty)
            }
            .padding(10)
        }
        .frame(minWidth: 520, minHeight: 420)
    }

    /// 有本次的记录文件就在 Finder 里选中它，否则打开记录文件夹。
    private func openTranscriptFolder() {
        if let url = controller.transcriptURL {
            NSWorkspace.shared.activateFileViewerSelecting([url])
        } else {
            let folder = URL(fileURLWithPath: (controller.settings.transcriptFolder as NSString).expandingTildeInPath)
            try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            NSWorkspace.shared.open(folder)
        }
    }
}
