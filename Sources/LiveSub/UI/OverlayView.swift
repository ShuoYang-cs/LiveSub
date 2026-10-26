import AppKit
import LiveSubCore
import SwiftUI

/// 悬浮字幕上的快捷按钮回调（由 AppDelegate 提供）。
struct OverlayActions {
    var toggleRunning: () -> Void
    /// 锁定字幕：点击穿透，不挡下面的程序（解锁点右上角的锁）。
    var lock: () -> Void
    /// 录音按钮：把听到的声音存成音频文件。
    var toggleRecording: () -> Void
    var showSettings: () -> Void
    var quit: () -> Void
    var toggleAssistant: () -> Void
    /// 在字幕上改了翻译模型或音源。
    var settingsChanged: () -> Void
    var manageModels: () -> Void
}

/// 字幕上的快捷按钮（记录鼠标停在哪个上面；名字会随状态变，不能拿名字当标识）。
enum OverlayTool: String {
    case run, record, lock, source, assistant, settings, quit
}

@Observable
final class OverlayState {
    var isHovering = false
    /// 快捷按钮里的菜单打开着：鼠标移到菜单上时快捷按钮不能消失。
    var menuOpen = false
    /// 滚动区域是否停在最底部（最新字幕）。停在底部时新字幕自动跟随，往上翻看时不打扰。
    var atBottom = true
    /// 用户正在用触控板或滚轮滚动字幕。
    var userScrolling = false
    /// 鼠标停在哪个快捷按钮上（按钮的功能名）：按钮变亮放大，工具栏下面显示功能名。
    var hoveredTool: OverlayTool?
    /// 鼠标停在“开始翻译”大按钮上。
    var startHovered = false
    /// 鼠标停在状态条的翻译模型上：和 AI 助手的模型菜单一样变蓝（2026-10-22 用户要求）。
    var modelMenuHovered = false
    @ObservationIgnored private var followStorage: AnyObject?

    /// 滚动位置控制（ScrollPosition 需要 macOS 15）。
    @available(macOS 15, *)
    var follow: ScrollFollowState {
        if let follow = followStorage as? ScrollFollowState { return follow }
        let follow = ScrollFollowState()
        followStorage = follow
        return follow
    }
}

@available(macOS 15, *)
@Observable
final class ScrollFollowState {
    var position = ScrollPosition(edge: .bottom)
    /// 位置条用的滚动几何。只在位置条显示时更新，跟随最新字幕时不引起额外重绘。
    var geometry = ScrollFollow()
    /// 最近一次的滚动位置离底部多远（压力测试检查贴底跟随用）。
    @ObservationIgnored var distanceFromBottom: CGFloat = 0
}

/// 停在底部时跟随新字幕、往上翻看时不打扰（macOS 15 的滚动接口）。
@available(macOS 15, *)
private struct FollowBottom: ViewModifier {
    let state: OverlayState
    @Bindable var follow: ScrollFollowState

    func body(content: Content) -> some View {
        content
            .scrollPosition($follow.position)
            // 系统滚动条在程序每次自动滚到底时都会闪一下；改用自己画的位置条，只在手动滚动或往回看时显示。
            .scrollIndicators(.hidden)
            // 内容不满一屏时贴底显示；内容变高时是否跟随由下面自己决定（翻看历史时不要被拽走）。
            .defaultScrollAnchor(.bottom, for: .initialOffset)
            .defaultScrollAnchor(.bottom, for: .alignment)
            .onScrollGeometryChange(for: ScrollFollow.self) { geo in
                ScrollFollow(geo)
            } action: { old, new in
                follow.distanceFromBottom = new.distanceFromBottom
                if showIndicator { follow.geometry = new }
                // 新字幕或拖动窗口大小：原来停在最新一句就继续贴底。已经在底部时不再滚动，避免和布局互相触发。
                // 用户正在滚动时不跟随，否则会和手势互相抢，停下来时位置不上不下。
                guard new.contentHeight != old.contentHeight || new.containerHeight != old.containerHeight else { return }
                if state.atBottom, !state.userScrolling, new.distanceFromBottom > 1 {
                    withAnimation(.easeInOut(duration: 0.35)) { follow.position.scrollTo(edge: .bottom) }
                }
            }
            // “是否停在最新一句”只看用户自己的滚动：手指放上触控板（或开始滚动）到停下来为止。
            // 程序自己滚到底的动画结束时也会回到 idle，那时什么都不做——在那里再滚一次会和
            // “滚到底 → 结束 → 再滚”形成循环，把主线程卡死（2026-10-20 实际发生过）。
            .onScrollPhaseChange { _, phase, context in
                switch phase {
                case .tracking, .interacting, .decelerating:
                    if !state.userScrolling {
                        follow.geometry = ScrollFollow(context.geometry)
                        state.userScrolling = true
                    }
                case .idle:
                    guard state.userScrolling else { return }
                    state.userScrolling = false
                    let distance = ScrollFollow(context.geometry).distanceFromBottom
                    let atBottom = distance < 40
                    if state.atBottom != atBottom {
                        appLog.notice("overlay: \(atBottom ? "back to" : "left", privacy: .public) latest line")
                    }
                    state.atBottom = atBottom
                    // 停在最新一句附近：对齐到底（每次手动滚动结束只做这一次）。
                    if atBottom, distance > 1 {
                        withAnimation(.easeOut(duration: 0.2)) { follow.position.scrollTo(edge: .bottom) }
                    }
                default:
                    break
                }
            }
            .overlay {
                ScrollIndicator(follow: follow)
                    .allowsHitTesting(false)
                    .opacity(showIndicator ? 1 : 0)
                    .animation(.easeOut(duration: 0.2), value: showIndicator)
            }
    }

    private var showIndicator: Bool { state.userScrolling || !state.atBottom }
}

/// 细的滚动位置条：只在手动滚动或往回看时出现，不占布局宽度。
@available(macOS 15, *)
private struct ScrollIndicator: View {
    @Environment(\.colorScheme) private var scheme
    private var palette: OverlayPalette { OverlayPalette(scheme) }
    let follow: ScrollFollowState

    var body: some View {
        let g = follow.geometry
        if g.containerHeight > 0, g.contentHeight > g.containerHeight + 1 {
            let track = g.containerHeight - 8
            let thumb = max(24, track * g.containerHeight / g.contentHeight)
            let progress = min(1, max(0, g.offset / (g.contentHeight - g.containerHeight)))
            Capsule()
                .fill(palette.fg.opacity(0.45))
                .frame(width: 4, height: thumb)
                .offset(y: 4 + (track - thumb) * progress)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topTrailing)
                .padding(.trailing, 4)
        }
    }
}

struct ScrollFollow: Equatable {
    var contentHeight: CGFloat = 0
    var containerHeight: CGFloat = 0
    /// 可见区域顶边的位置。
    var offset: CGFloat = 0
    /// 可见区域底边离内容底部还有多远。
    var distanceFromBottom: CGFloat = 0
}

@available(macOS 15, *)
extension ScrollFollow {
    /// 离底部的距离按“最大可滚动位置 − 当前位置”算，包括系统加的内容边距，
    /// 停在最底部时正好是 0（只用内容高度算会差几个点，误以为还没到底）。
    init(_ geo: ScrollGeometry) {
        let maxOffset = geo.contentSize.height + geo.contentInsets.bottom - geo.containerSize.height
        self.init(contentHeight: geo.contentSize.height, containerHeight: geo.containerSize.height,
                  offset: geo.contentOffset.y + geo.contentInsets.top,
                  distanceFromBottom: max(0, maxOffset - geo.contentOffset.y))
    }
}

/// 悬浮窗口（字幕、AI 助手）的颜色：深色外观黑底白字，浅色外观白底黑字。外观跟随设置（默认跟随系统）。
struct OverlayPalette {
    let fg: Color
    let bg: Color
    /// 文字的投影：深色外观压暗，浅色外观提亮，叠在复杂背景上也看得清。
    let shadow: Color

    init(_ scheme: ColorScheme) {
        fg = scheme == .dark ? .white : .black
        bg = scheme == .dark ? .black : .white
        shadow = scheme == .dark ? .black.opacity(0.6) : .white.opacity(0.8)
    }
}

struct OverlayView: View {
    @Environment(\.colorScheme) private var scheme
    private var palette: OverlayPalette { OverlayPalette(scheme) }
    let controller: SubtitleController
    @Bindable var settings: AppSettings
    let actions: OverlayActions
    let onSize: (CGSize) -> Void
    /// 命令行工具链没有 SwiftUI 宏插件，不能用 @State，用一个 Observable 小对象代替。
    /// 由窗口控制器持有。
    @Bindable var state: OverlayState

    private var mode: DisplayMode { settings.displayMode }

    /// 窗口高度：用户拖动上下边框设定的高度，没设过的旧设置（overlayHeight 为 0）按存下的“显示最近 N 句”算。
    /// 只随设置变化，不随字幕内容变化：字幕生成时窗口不会忽高忽低，旁边的 AI 助手也不受影响。
    /// 状态条算在高度以内（否则拖动上下边框时窗口会越拖越高）。
    private var height: CGFloat { settings.overlayHeight > 0 ? settings.overlayHeight : autoHeight }

    /// 正好放下最近 N 句（每句按一行原文、一行译文算），加上“正在说”一行和状态条。
    /// 句子折成多行时放不下的部分往上滚出去，在字幕上往上滚动可以看到。
    private var autoHeight: CGFloat {
        let source = mode == .translationOnly ? 0 : settings.sourceFontSize * 1.25
        let translation = mode == .sourceOnly ? 0 : settings.translationFontSize * 1.4
        let sentence = source + translation + (mode == .bilingual ? 2 : 0)
        let count = CGFloat(settings.visibleLines)
        let lines = Self.linesTopPadding + count * sentence + (count - 1) * Self.lineSpacing + Self.linesBottomPadding
        return ceil(lines + liveFooterHeight + (settings.showStatusBar ? Self.statusBarHeight : 0))
    }

    private static let lineSpacing: CGFloat = 10
    private static let linesTopPadding: CGFloat = 12
    private static let linesBottomPadding: CGFloat = 8
    private static let statusBarHeight: CGFloat = 23
    private var showStatus: Bool { settings.showStatusBar && controller.isRunning }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            // 上面滚动区放已经分好句的字幕（翻译中的句子原地变成正式字幕），
            // “正在说”放在下面固定高度的一小块里，它的变化不会把上面的字幕往上推。
            scrollingLines
            liveFooter
            if controller.systemAudioBlocked { systemAudioNotice }
            if showStatus { statusBar }
        }
        .frame(height: height)
        .frame(width: settings.overlayWidth, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(palette.bg.opacity(settings.backgroundOpacity))
                // 浅色外观下白底叠在白色网页上看不出边缘，加一圈很淡的描边。
                .strokeBorder(palette.fg.opacity(0.08))
        )
        .overlay {
            // 没开始时：醒目的开始按钮放在正中间（在快捷按钮下面一层，不挡住它们）。
            if !controller.isRunning { startButton }
        }
        .overlay(alignment: .topTrailing) {
            // 锁定时右上角的锁不画在这里：字幕窗口点击穿透，锁要能点，放在单独的小窗口里（LockBadgePanel）。
            if (state.isHovering || state.menuOpen) && !settings.clickThrough {
                VStack(alignment: .trailing, spacing: 4) {
                    toolbar
                    if let tool = state.hoveredTool { toolName(name(of: tool)) }
                }
                .padding(6)
                .animation(.easeOut(duration: 0.12), value: state.hoveredTool)
            }
        }
        .overlay(alignment: .bottomTrailing) {
            if !state.atBottom { backToLiveButton.padding(10) }
        }

        .onHover { inside in
            state.isHovering = inside
            if !inside { state.hoveredTool = nil }
        }
        .onReceive(NotificationCenter.default.publisher(for: NSMenu.didBeginTrackingNotification)) { _ in
            if state.isHovering { state.menuOpen = true }
        }
        .onReceive(NotificationCenter.default.publisher(for: NSMenu.didEndTrackingNotification)) { _ in
            state.menuOpen = false
        }
        .fixedSize(horizontal: false, vertical: true)
        .onGeometryChange(for: CGSize.self) { $0.size } action: { onSize($0) }
    }

    /// 写明在听哪里的声音：新用户默认听的是系统声音，对着麦克风说话不会出字幕。
    private var listeningText: String {
        switch settings.audioSource {
        case .system: L("正在听系统声音…")
        case .microphone: L("正在听麦克风…")
        case .both: L("正在听麦克风和系统声音…")
        }
    }

    /// 系统录音权限被拒绝时的提示，不受“显示状态条”影响。
    private var systemAudioNotice: some View {
        HStack(spacing: 10) {
            Text("没有系统录音权限，收不到电脑播放的声音。请在“系统设置 › 隐私与安全性 › 屏幕与系统录音”中允许“实时翻译助手”，打开后字幕会自动继续。")
                .font(.system(size: 12))
                .foregroundStyle(.orange)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
            Button { SystemAudioCapture.openPrivacySettings() } label: {
                Text("打开系统设置")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(palette.fg)
                    .padding(.horizontal, 10)
                    .padding(.vertical, 4)
                    .background(Capsule().fill(palette.fg.opacity(0.18)))
                    .contentShape(Capsule())
            }
            .buttonStyle(.plain)
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 6)
    }

    // MARK: - 状态条

    /// 一小行：有没有收到声音、识别是否清晰、翻译引擎、积压、最近的错误。
    private var statusBar: some View {
        HStack(spacing: 6) {
            Image(systemName: hasInput ? "waveform" : "waveform.slash")
            levelMeter
            Text(soundText).foregroundStyle(hasInput ? palette.fg.opacity(0.6) : Color.orange)
            separator
            Text(controller.correcting ? L("识别较难 · 已自动纠正")
                 : controller.recognitionDifficult ? L("识别较难") : L("识别清晰"))
                .help(controller.correcting ? L("识别置信度较低，已自动按发音纠正识别错误。")
                      : controller.recognitionDifficult
                        ? (settings.autoCorrect ? L("识别置信度较低。自动纠正需要本机已登录 Claude Code。")
                           : L("识别置信度较低。可以在设置中打开“自动纠正口音”。"))
                      : L("识别置信度正常。"))
            if controller.isRecording {
                separator
                Label("录音", systemImage: "record.circle.fill").foregroundStyle(.red)
            }
            separator
            modelMenu
            if controller.backlog > 0 {
                separator
                Text(L("待翻译 %d 句", controller.backlog)).foregroundStyle(.orange)
            }
            Spacer(minLength: 0)
            if let alert = controller.alert {
                Text(alert).foregroundStyle(.orange).lineLimit(1).truncationMode(.tail)
            }
        }
        .font(.system(size: 11))
        .foregroundStyle(palette.fg.opacity(0.6))
        .lineLimit(1)
        .padding(.horizontal, 18)
        .padding(.top, 2)
        .padding(.bottom, 7)
        .frame(height: Self.statusBarHeight)
    }

    /// 超过 3 秒没收到任何音频数据：采集断了（和“安静”不同）。
    private var hasInput: Bool { controller.silentFor < 3 }

    private var soundText: String {
        if !hasInput { return L("无音频输入") }
        return controller.inputLevel > 0 ? L("收音中") : L("无声音")
    }

    private var engineText: String {
        let basic = !settings.translationModel.isLanguageModel
        if controller.usingFallback {
            return basic ? settings.serviceName(settings.translationModel) : L("Apple 本地翻译 · 备用")
        }
        // Google、Apple 在纠正期间实际用的是 Claude。
        if basic, controller.correcting, let model = controller.correctionModel {
            return L("%@ · 纠正中", settings.serviceName(model))
        }
        return settings.serviceName(settings.translationModel)
    }

    /// 状态条上的翻译模型：点开可以直接切换到其他模型。
    private var modelMenu: some View {
        Menu {
            // 用 Picker 让系统画对勾：自己用“按钮 + checkmark 图标”时，菜单宽度不算对勾，带对勾的那项会被截断。
            Picker("翻译模型", selection: Binding(get: { settings.translationModel }, set: {
                settings.translationModel = $0
                actions.settingsChanged()
            })) {
                ForEach(settings.modelGroups(for: .translation)) { group in
                    Section(group.title) {
                        ForEach(group.choices, id: \.self) { choice in
                            Text(settings.menuTitle(choice, selection: settings.translationModel)).tag(choice)
                        }
                    }
                }
            }
            .pickerStyle(.inline)
            .labelsHidden()
            Divider()
            Button("管理模型服务…", action: actions.manageModels)
        } label: {
            HStack(spacing: 3) {
                Text(engineText)
                Image(systemName: "chevron.down").font(.system(size: 8, weight: .semibold))
            }
        }
        // .borderlessButton 会按系统颜色重画标签（浅色模式下是黑色）；按钮样式才保留这里的白色。
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .fixedSize()
        // 颜色和底色加在 Menu 外层（加在标签里会被菜单按钮丢掉，见 AI 助手的模型菜单）。
        .foregroundStyle(state.modelMenuHovered ? Color.blue : palette.fg.opacity(0.6))
        .padding(.horizontal, 6)
        .padding(.vertical, 1)
        .background(Capsule().fill(palette.fg.opacity(state.modelMenuHovered ? 0.12 : 0)))
        .contentShape(Capsule())
        .onHover { state.modelMenuHovered = $0 }
        // 菜单打开期间收不到鼠标移出，关掉菜单时复原，免得一直是蓝色。
        .onReceive(NotificationCenter.default.publisher(for: NSMenu.didEndTrackingNotification)) { _ in
            state.modelMenuHovered = false
        }
        .help("切换翻译模型")
    }

    private var levelMeter: some View {
        HStack(alignment: .bottom, spacing: 1.5) {
            ForEach(0..<5, id: \.self) { i in
                Capsule()
                    .fill(Double(i) < Double(controller.inputLevel) * 5 ? Color.green.opacity(0.85) : palette.fg.opacity(0.2))
                    .frame(width: 2.5, height: 4 + CGFloat(i) * 1.6)
            }
        }
        .frame(height: 11, alignment: .bottom)
    }

    private var separator: some View {
        Text("·").foregroundStyle(palette.fg.opacity(0.35))
    }

    // MARK: - 部件

    /// 有文字的“正在说”。噪音有时被识别成一个 “.”，不显示。
    private var visibleLiveKeys: [String] {
        controller.live.keys.sorted().filter { key in
            controller.live[key]?.contains(where: { $0.isLetter || $0.isNumber }) == true
        }
    }

    /// “正在说”区域的高度：一行原文。
    /// 高度固定，窗口拖得再小也能留出上面完整显示最新一句的空间。
    private var liveFooterHeight: CGFloat {
        settings.sourceFontSize * 1.3 + 6
    }

    /// 只放正在说、还没分好句的文字，每项最多一行，太长时只显示最新的部分。
    private var liveFooter: some View {
        VStack(alignment: .leading, spacing: 4) {
            ForEach(visibleLiveKeys, id: \.self) { key in
                if let text = controller.live[key] {
                    liveView(source: text, label: key.isEmpty ? nil : key)
                }
            }
        }
        .padding(.horizontal, 18)
        .frame(maxWidth: .infinity, minHeight: liveFooterHeight, alignment: .topLeading)
    }

    /// 悬浮字幕里最多能往回翻这么多句（完整记录在“字幕记录”窗口）。
    static let scrollbackLines = 300

    /// 最近的字幕放进固定高度的滚动区域，默认停在最新一句。
    /// 用 VStack 而不是 LazyVStack：懒加载会在滚动时反复修正行高估算，和“贴底跟随”互相触发。
    @ViewBuilder
    private var scrollingLines: some View {
        let scroll = ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: Self.lineSpacing) {
                if controller.lines.isEmpty && visibleLiveKeys.isEmpty {
                    placeholder
                }
                ForEach(controller.lines.suffix(Self.scrollbackLines)) { line in
                    lineView(line)
                        .transition(.opacity.combined(with: .move(edge: .bottom)))
                }
            }
            .padding(.horizontal, 18)
            .padding(.top, Self.linesTopPadding)
            .padding(.bottom, Self.linesBottomPadding)
        }
        // 最上面被窗口边缘截断的那句渐隐，不生硬地切成半截。
        .mask {
            VStack(spacing: 0) {
                LinearGradient(colors: [.clear, .black], startPoint: .top, endPoint: .bottom).frame(height: 8)
                Rectangle()
            }
        }
        if #available(macOS 15, *) {
            scroll.modifier(FollowBottom(state: state, follow: state.follow)).frame(maxHeight: .infinity)
        } else {
            // macOS 14 没有滚动位置和滚动阶段的接口：始终贴底显示最新字幕，用系统滚动条。
            scroll.defaultScrollAnchor(.bottom).scrollIndicators(.automatic).frame(maxHeight: .infinity)
        }
    }

    private var backToLiveButton: some View {
        Button {
            if #available(macOS 15, *) { state.follow.position.scrollTo(edge: .bottom) }
            state.atBottom = true
        } label: {
            Label("返回实时", systemImage: "arrow.down.to.line")
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(palette.fg)
                .padding(.horizontal, 10)
                .padding(.vertical, 5)
                .background(Capsule().fill(palette.bg.opacity(0.7)))
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .padding(.trailing, 12)
    }

    @ViewBuilder
    private var placeholder: some View {
        // 没开始时中间是开始按钮，这里不再写一遍。
        if controller.isRunning {
            Text(listeningText)
                .font(.system(size: settings.sourceFontSize))
                .foregroundStyle(palette.fg.opacity(0.7))
        }
    }

    private var startButton: some View {
        VStack(spacing: 8) {
            Button(action: actions.toggleRunning) {
                HStack(spacing: 8) {
                    if controller.isStarting {
                        ProgressView().controlSize(.small)
                        Text("准备中…")
                    } else {
                        Image(systemName: "play.fill")
                        Text("开始翻译")
                    }
                }
                .font(.system(size: 16, weight: .semibold))
                .foregroundStyle(palette.bg)
                .padding(.horizontal, 22)
                .padding(.vertical, 10)
                .background(Capsule().fill(palette.fg.opacity(state.startHovered ? 1 : 0.92)))
                .shadow(color: .black.opacity(state.startHovered ? 0.5 : 0.35), radius: state.startHovered ? 12 : 8, y: 2)
                .scaleEffect(state.startHovered && !controller.isStarting ? 1.06 : 1)
                .contentShape(Capsule())
                .animation(.easeOut(duration: 0.15), value: state.startHovered)
            }
            .buttonStyle(.plain)
            .disabled(controller.isStarting)
            .onHover { state.startHovered = $0 }
            // 上次出错（没有权限、模型不可用）时显示原因，否则显示音源和语言。
            Group {
                if let alert = controller.alert {
                    Text(alert).foregroundStyle(.orange)
                } else {
                    Text(L("%@ · %@ → %@", settings.audioSource.title, L(settings.source.displayName), L(settings.target.displayName)))
                        .foregroundStyle(palette.fg.opacity(0.65))
                }
            }
            .font(.system(size: 11))
            .lineLimit(1)
            .padding(.horizontal, 18)
            // 第一次使用：基础翻译够用，更准的翻译、AI 助手和纠正口音要换模型。
            if !settings.hasStartedOnce, controller.alert == nil, !settings.translationModel.isLanguageModel {
                Text("现在用基础翻译，开箱即用。想要更准的翻译、AI 助手和自动纠正口音，到设置里选 Claude 或自己的模型。")
                    .font(.system(size: 11))
                    .foregroundStyle(palette.fg.opacity(0.65))
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.horizontal, 24)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background {
            // 上次的字幕压暗，开始按钮更醒目；不拦点击，背景照样可以拖动窗口。
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(palette.bg.opacity(controller.lines.isEmpty ? 0 : 0.5))
                .allowsHitTesting(false)
        }
    }

    @ViewBuilder
    private func lineView(_ line: SubtitleLine) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            if mode != .translationOnly {
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    if let label = line.sourceLabel { tag(label) }
                    Text(line.source)
                        .font(.system(size: settings.sourceFontSize))
                        .foregroundStyle(palette.fg.opacity(mode == .sourceOnly ? 1 : 0.72))
                }
            }
            if mode != .sourceOnly {
                translationText(line)
            }
        }
        .shadow(color: palette.shadow, radius: 2, y: 1)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    @ViewBuilder
    private func translationText(_ line: SubtitleLine) -> some View {
        let size = settings.translationFontSize
        switch line.state {
        case .waiting:
            Text("…").font(.system(size: size)).foregroundStyle(palette.fg.opacity(0.4))
        case .translating:
            Text(line.translation.isEmpty ? "…" : line.translation)
                .font(.system(size: size, weight: .semibold))
                .foregroundStyle(palette.fg.opacity(0.85))
        case .done:
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(line.translation)
                    .font(.system(size: size, weight: .semibold))
                    .foregroundStyle(palette.fg)
                if let note = line.note { tag(note) }
            }
        case .failed:
            Text("翻译失败").font(.system(size: size * 0.7)).foregroundStyle(.orange.opacity(0.9))
        }
    }

    /// 正在说、还没分好句的原文：最多一行，太长时省略前面、显示最新的词。
    private func liveView(source: String, label: String?) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            if let label { tag(label) }
            Text(source)
                .font(.system(size: settings.sourceFontSize))
                .italic()
                .foregroundStyle(palette.fg.opacity(0.55))
                .lineLimit(1)
                .truncationMode(.head)
        }
        .shadow(color: palette.shadow, radius: 2, y: 1)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func tag(_ text: String) -> some View {
        Text(text)
            .font(.system(size: 10, weight: .medium))
            .padding(.horizontal, 5)
            .padding(.vertical, 1)
            .background(Capsule().fill(palette.fg.opacity(0.18)))
            .foregroundStyle(palette.fg.opacity(0.8))
    }

    private var toolbar: some View {
        HStack(spacing: 4) {
            toolButton(.run, controller.isRunning ? "pause.fill" : "play.fill", actions.toggleRunning)
            // 录音：打开后，字幕运行期间把听到的声音存进记录文件夹（和字幕记录同名）。
            toolButton(.record, settings.recordAudio ? "record.circle.fill" : "record.circle",
                       tint: settings.recordAudio ? .red : nil, actions.toggleRecording)
            // 字幕总在最上面，不再有“置顶”开关；这里放锁定（2026-10-22 用户同意，锁定比置顶常用）。
            toolButton(.lock, "lock.open", actions.lock)
            audioSourceMenu
            // 字幕记录在设置 › 记录里（2026-10-22 用户决定不放在快捷按钮上）。
            toolButton(.assistant, "sparkles",
                       tint: settings.assistantVisible ? (scheme == .dark ? .yellow : .orange) : nil, actions.toggleAssistant)
            toolButton(.settings, "gearshape", actions.showSettings)
            toolButton(.quit, "xmark", actions.quit)
        }
        .padding(4)
        .background(Capsule().fill(palette.bg.opacity(0.8)).strokeBorder(palette.fg.opacity(0.1)))
    }

    /// 音源：麦克风、系统音频、两者。
    private var audioSourceMenu: some View {
        Menu {
            // 用 Picker 让系统画对勾（自己画的对勾会让菜单项被截断，见模型菜单）。
            Picker("音源", selection: Binding(get: { settings.audioSource }, set: {
                settings.audioSource = $0
                actions.settingsChanged()
            })) {
                ForEach(AudioSourceMode.allCases) { Text($0.title).tag($0) }
            }
            .pickerStyle(.inline)
            .labelsHidden()
        } label: {
            toolIcon(.source, settings.audioSource.symbol, tint: nil)
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .fixedSize()
        .onHover { hoverTool(.source, $0) }
        .help(name(of: .source))
    }

    /// 按钮的功能名，按当前状态现算：点了之后（开始→停止、录音→停止录音）下面的名字跟着变。
    private func name(of tool: OverlayTool) -> String {
        switch tool {
        case .run: L(controller.isRunning ? "停止" : "开始")
        case .record: L(settings.recordAudio ? "停止录音" : "录音")
        case .lock: L("锁定字幕")
        case .source: L("音源：%@", settings.audioSource.title)
        case .assistant: L(settings.assistantVisible ? "隐藏 AI 助手" : "显示 AI 助手")
        case .settings: L("设置")
        case .quit: L("退出 LiveSub")
        }
    }

    private func toolButton(_ tool: OverlayTool, _ symbol: String, tint: Color? = nil,
                            _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            toolIcon(tool, symbol, tint: tint)
        }
        .buttonStyle(.plain)
        .onHover { hoverTool(tool, $0) }
        .help(name(of: tool))
    }

    /// 快捷按钮的图标：鼠标停在上面时底下出现浅色圆形、图标略微放大。
    private func toolIcon(_ tool: OverlayTool, _ symbol: String, tint: Color?) -> some View {
        let hovered = state.hoveredTool == tool
        return Image(systemName: symbol)
            .font(.system(size: 12, weight: .semibold))
            .foregroundStyle(tint ?? palette.fg)
            .scaleEffect(hovered ? 1.15 : 1)
            .frame(width: 26, height: 24)
            .background(Circle().fill(palette.fg.opacity(hovered ? 0.16 : 0)))
            .contentShape(Rectangle())
            .animation(.easeOut(duration: 0.12), value: hovered)
    }

    private func hoverTool(_ tool: OverlayTool, _ inside: Bool) {
        if inside {
            state.hoveredTool = tool
        } else if state.hoveredTool == tool {
            state.hoveredTool = nil
        }
    }

    /// 工具栏下面的功能名。系统的提示要停一会儿才出现，字幕面板不是活动窗口时还常常不显示，所以自己画。
    private func toolName(_ name: String) -> some View {
        Text(name)
            .font(.system(size: 11, weight: .medium))
            .foregroundStyle(palette.fg)
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(Capsule().fill(palette.bg.opacity(0.85)).strokeBorder(palette.fg.opacity(0.1)))
            .transition(.opacity)
            .allowsHitTesting(false)
    }
}

/// 锁定后鼠标移到字幕上时右上角的锁：和快捷按钮同样的位置和样式，点一下解锁。
struct LockBadge: View {
    @Environment(\.colorScheme) private var scheme
    private var palette: OverlayPalette { OverlayPalette(scheme) }
    let onUnlock: () -> Void

    var body: some View {
        Button(action: onUnlock) {
            Image(systemName: "lock.fill")
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(palette.fg)
                .frame(width: 24, height: 22)
                .padding(4)
                .background(Capsule().fill(palette.bg.opacity(0.8)))
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .help(L("解锁字幕"))
    }

    static let size = NSSize(width: 32, height: 30)
}
