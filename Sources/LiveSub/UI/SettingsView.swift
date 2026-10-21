import AppKit
import LiveSubCore
import SwiftUI

/// 设置窗口的界面状态（命令行工具链不能用 @State）。
@MainActor @Observable
final class SettingsViewState {
    var showMore = false
}

struct SettingsView: View {
    @Bindable var settings: AppSettings
    var state = SettingsViewState()
    let onTranslationSettingsChanged: () -> Void
    let onAppearanceChanged: () -> Void
    let onManageModels: () -> Void

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
                    ForEach(settings.modelGroups(includeApple: true)) { group in
                        Section(group.title) {
                            ForEach(group.choices, id: \.self) { choice in
                                Text(settings.displayName(choice)).tag(choice)
                            }
                        }
                    }
                }
                .help("实时字幕建议选择速度快的模型，例如 Claude Haiku。Apple 本地翻译可离线使用。")
                LabeledContent("模型服务") {
                    HStack {
                        Text(settings.endpoints.isEmpty ? L("可添加自己的 API 或本地模型") : L("已添加 %d 个", settings.endpoints.count))
                            .foregroundStyle(.secondary)
                        Button("管理…", action: onManageModels)
                    }
                }
                if !settings.sourceLanguage.hasPrefix("zh") {
                    Toggle("过滤中文说话声", isOn: $settings.filterChinese)
                        .help("识别并忽略中文说话声，例如周围的交谈。")
                }
            }

            Section("字幕") {
                Picker("显示", selection: $settings.displayMode) {
                    ForEach(DisplayMode.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                slider("字号", value: fontSize, range: 14...56)
                slider("背景", value: $settings.backgroundOpacity, range: 0...1)
                if !settings.fixedOverlayHeight {
                    Stepper(L("显示最近 %d 句", settings.visibleLines), value: $settings.visibleLines, in: 1...6)
                }
                LabeledContent("大小") {
                    HStack {
                        Text("拖动字幕边框调整").foregroundStyle(.secondary)
                        Button("恢复默认") {
                            settings.overlayWidth = 900
                            settings.overlayHeight = 0
                        }
                    }
                }
            }

            // “更多”用普通的分组行（和上面两组一样的行高和分隔线），点标题展开或收起。
            Section {
                if state.showMore {
                    Toggle("显示状态条", isOn: $settings.showStatusBar)
                        .help("在字幕底部显示音频输入、识别与翻译状态。")
                    Toggle("自动纠正口音", isOn: $settings.autoCorrect)
                        .help("讲者口音较重、识别不准时，自动让大模型按发音和上下文纠正识别错误。使用 Apple 本地翻译时不可用。")
                    if settings.autoCorrect {
                        Toggle("纠正时保留识别原文", isOn: $settings.keepOriginalText)
                            .help("纠正修改了原文时，在字幕记录中同时保留识别器原来的文字。")
                            .padding(.leading, 14)
                    }
                    Toggle("启用 Opus 模型", isOn: $settings.opusEnabled)
                        .help("Opus 准确度最高但速度较慢。启用后可在翻译引擎与 AI 助手中选择，所选模型会被记住。")
                    Toggle("锁定字幕", isOn: $settings.clickThrough)
                        .onChange(of: settings.clickThrough) { onAppearanceChanged() }
                    LabeledContent("记录文件夹") {
                        HStack {
                            Text((settings.transcriptFolder as NSString).abbreviatingWithTildeInPath)
                                .lineLimit(1).truncationMode(.middle).foregroundStyle(.secondary)
                            Button("更改…", action: chooseFolder)
                        }
                    }
                    TextField("Claude 路径", text: $settings.claudePath, prompt: Text("自动查找"))
                }
            } header: {
                Button {
                    withAnimation(.easeInOut(duration: 0.2)) { state.showMore.toggle() }
                } label: {
                    HStack(spacing: 4) {
                        Image(systemName: "chevron.right")
                            .font(.caption.weight(.semibold))
                            .rotationEffect(.degrees(state.showMore ? 90 : 0))
                        Text("更多")
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
        .formStyle(.grouped)
        .frame(width: 480)
        .frame(minHeight: 600)
        .onChange(of: translationKey) { onTranslationSettingsChanged() }
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
         String(settings.filterChinese), String(settings.opusEnabled), String(settings.micNoiseReduction),
         String(settings.autoCorrect)].joined(separator: "\u{1}")
    }

    private func slider(_ title: String, value: Binding<Double>, range: ClosedRange<Double>) -> some View {
        LabeledContent(L(title)) {
            Slider(value: value, in: range).frame(maxWidth: 240)
        }
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.directoryURL = URL(fileURLWithPath: (settings.transcriptFolder as NSString).expandingTildeInPath)
        if panel.runModal() == .OK, let url = panel.url {
            settings.transcriptFolder = url.path
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
                if let url = controller.transcriptURL {
                    Button("在 Finder 中显示记录") { NSWorkspace.shared.activateFileViewerSelecting([url]) }
                }
                Button(controller.notesInProgress ? L("正在生成…") : L("生成笔记")) { controller.generateNotes() }
                    .disabled(controller.notesInProgress || controller.lines.isEmpty)
            }
            .padding(10)
        }
        .frame(minWidth: 520, minHeight: 420)
    }
}
