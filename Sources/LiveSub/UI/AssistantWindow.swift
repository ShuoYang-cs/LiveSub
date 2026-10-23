import AppKit
import LiveSubCore
import SwiftUI

/// AI 助手面板：第一次打开时放在字幕旁边，按住标题栏可以单独拖到别处，可以打字提问。
/// 显示面板、点按钮、拖动都不激活应用（不把焦点从正在用的 App 抢走）；只有点输入框时才成为 key 窗口，这时激活本应用。
final class AssistantPanel: NSPanel {
    /// 为了输入而激活本应用、还没激活完。
    private var activatingForInput = false

    init() {
        super.init(contentRect: NSRect(x: 0, y: 0, width: 380, height: 420),
                   styleMask: [.borderless, .nonactivatingPanel],
                   backing: .buffered, defer: false)
        isFloatingPanel = true
        level = .statusBar
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        backgroundColor = .clear
        isOpaque = false
        hasShadow = true
        hidesOnDeactivate = false
        becomesKeyOnlyIfNeeded = true
        isReleasedWhenClosed = false
        // 激活时系统可能把键盘交给本应用上次用过的窗口（例如设置），这里交回给面板。
        NotificationCenter.default.addObserver(forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, self.activatingForInput else { return }
                self.activatingForInput = false
                if self.isVisible && !self.isKeyWindow { self.makeKey() }
            }
        }
    }

    /// 不激活的面板成为 key 窗口时，原来那个程序不知道自己失去了键盘，它的输入框还在闪光标，
    /// 两边同时闪，看不出字会打到哪里。所以点输入框时激活本应用，原来的程序就停止闪烁。
    /// 判断要不要激活不能看 `NSApp.isActive`：不激活的面板成为 key 窗口时它已经是 true，
    /// 系统的前台程序却还是原来那个（2026-10-22 真实窗口里用日志确认过），所以看系统的前台程序。
    override func becomeKey() {
        super.becomeKey()
        guard NSWorkspace.shared.frontmostApplication?.processIdentifier != ProcessInfo.processInfo.processIdentifier else { return }
        activatingForInput = true
        NSApp.activate()
        // 激活完成的通知不一定会来（见上），过一会儿就不再把键盘交回面板，免得之后抢走设置窗口的键盘。
        DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in self?.activatingForInput = false }
        appLog.notice("assistant panel: activating for input")
    }

    /// ⌘V 时先问它：剪贴板里是图片或文件就作为附件（返回 true），是文字就照常粘贴进输入框。
    var onPaste: ((NSPasteboard) -> Bool)?

    // 按键先经过 key 窗口，再到主菜单的“编辑 › 粘贴”，所以在这里能抢先处理附件。
    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        if event.modifierFlags.intersection(.deviceIndependentFlagsMask) == .command,
           event.charactersIgnoringModifiers == "v", onPaste?(.general) == true {
            return true
        }
        return super.performKeyEquivalent(with: event)
    }

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}

@MainActor
final class AssistantWindowController {
    let panel = AssistantPanel()
    private let settings: AppSettings
    private let overlay: NSWindow
    static let minSize = NSSize(width: 280, height: 220)
    private static let gap: CGFloat = 8
    private static let originKey = "assistantOrigin"
    /// 程序自己摆放面板时不记位置：用户没拖过时，每次打开都重新放到字幕旁边。
    private var placing = false

    init(assistant: AssistantSession, settings: AppSettings, overlay: NSWindow, onClose: @escaping () -> Void) {
        self.settings = settings
        self.overlay = overlay
        let hosting = NSHostingView(rootView: AssistantView(assistant: assistant, settings: settings, onClose: onClose))
        hosting.sizingOptions = []
        panel.onPaste = { [weak assistant] pasteboard in assistant?.paste(from: pasteboard) ?? false }
        panel.contentView = ResizeHandleView.container(
            around: hosting, size: panel.frame.size,
            onResize: { [weak self] frame, edges in self?.userResized(to: frame, edges: edges) })
        // 用户拖动标题栏或边框后记住位置。面板和字幕窗口各自独立，字幕窗口移动、改大小时不跟着走。
        NotificationCenter.default.addObserver(forName: NSWindow.didMoveNotification, object: panel, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.savePosition() }
        }
    }

    var isVisible: Bool { panel.isVisible }

    func show() {
        place()
        panel.orderFrontRegardless()
    }

    func hide() { panel.orderOut(nil) }

    /// 忘掉用户拖到的位置，回到字幕旁边。
    func resetPosition() {
        UserDefaults.standard.removeObject(forKey: Self.originKey)
        if panel.isVisible { place() }
    }

    /// 放到用户上次拖到的位置；没拖过，或那里已经不在任何屏幕上（换了显示器），就放在字幕旁边。
    private func place() {
        let size = NSSize(width: settings.assistantWidth, height: settings.assistantHeight)
        var frame = NSRect(origin: besideOverlay(size: size), size: size)
        if let saved = UserDefaults.standard.array(forKey: Self.originKey) as? [Double], saved.count == 2 {
            let candidate = NSRect(x: saved[0], y: saved[1], width: size.width, height: size.height)
            // 标题栏要在屏幕上，才能再拖回来。
            let header = NSRect(x: candidate.minX, y: candidate.maxY - 36, width: candidate.width, height: 36)
            if NSScreen.screens.contains(where: { $0.visibleFrame.intersects(header) }) { frame = candidate }
        }
        guard panel.frame != frame else { return }
        placing = true
        panel.setFrame(frame, display: true)
        placing = false
    }

    /// 字幕右边；右边放不下就放左边。字幕在屏幕上半部分时顶边对齐，否则底边对齐。
    private func besideOverlay(size: NSSize) -> NSPoint {
        guard let screen = (overlay.screen ?? NSScreen.main)?.visibleFrame else { return overlay.frame.origin }
        let o = overlay.frame
        var x = o.maxX + Self.gap
        if x + size.width > screen.maxX {
            let left = o.minX - Self.gap - size.width
            x = left >= screen.minX ? left : screen.maxX - size.width
        }
        var y = o.midY > screen.midY ? o.maxY - size.height : o.minY
        y = min(max(y, screen.minY), screen.maxY - size.height)
        return NSPoint(x: x, y: y)
    }

    private func userResized(to proposed: NSRect, edges: ResizeHandleView.Edges) {
        let frame = ResizeHandleView.clamped(proposed, edges: edges, minSize: Self.minSize, screen: panel.screen)
        panel.setFrame(frame, display: true)
        settings.assistantWidth = Double(frame.width)
        settings.assistantHeight = Double(frame.height)
    }

    private func savePosition() {
        guard !placing else { return }
        let o = panel.frame.origin
        UserDefaults.standard.set([o.x, o.y], forKey: Self.originKey)
    }
}

/// 按住拖动就移动所在的窗口。面板无边框，没有系统标题栏，AI 助手的标题栏靠它拖动。
struct WindowDragArea: NSViewRepresentable {
    final class DragView: NSView {
        // 本应用从不成为活动程序，第一下点击就要能拖。
        override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
        override func mouseDown(with event: NSEvent) { window?.performDrag(with: event) }
    }

    func makeNSView(context: Context) -> NSView { DragView() }
    func updateNSView(_ nsView: NSView, context: Context) {}
}

/// 鼠标是否停在某个控件上（命令行工具链不能用 @State）。
@MainActor @Observable
final class HoverState {
    var isHovering = false
}

/// 鼠标停在哪个按钮上（显示它的说明）。
@MainActor @Observable
final class TipState {
    var name: String?
}

/// 和字幕快捷按钮一样：鼠标停上去立刻显示说明（2026-10-22 用户要求）。系统提示要停一会儿才出现，
/// 面板不是活动窗口时还常常不显示。说明画在按钮下方（`above` 时上方），靠面板边缘的按钮向内对齐，免得被裁掉。
private struct InstantTip: ViewModifier {
    let name: String
    let state: TipState
    let palette: OverlayPalette
    var above = false
    var alignment: HorizontalAlignment = .center

    func body(content: Content) -> some View {
        content
            .onHover { inside in
                if inside {
                    state.name = name
                } else if state.name == name {
                    state.name = nil
                }
            }
            .overlay(alignment: Alignment(horizontal: alignment, vertical: .center)) {
                if state.name == name {
                    Text(name)
                        .font(.system(size: 11, weight: .medium))
                        .foregroundStyle(palette.fg)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 3)
                        .background(Capsule().fill(palette.bg.opacity(0.92)).strokeBorder(palette.fg.opacity(0.12)))
                        .fixedSize()
                        // 按钮约 20–26 点高、说明约 19 点高：从按钮中心挪 26 点，正好在按钮外面留出几点空隙。
                        .offset(y: above ? -26 : 26)
                        .allowsHitTesting(false)
                }
            }
            // 说明要画在旁边的按钮和下面的内容上面。
            .zIndex(state.name == name ? 1 : 0)
    }
}

struct AssistantView: View {
    @Environment(\.colorScheme) private var scheme
    private var palette: OverlayPalette { OverlayPalette(scheme) }
    @Bindable var assistant: AssistantSession
    @Bindable var settings: AppSettings
    let onClose: () -> Void
    /// 截图自检（--render-assistant --hover）时从外面传入。
    var modelMenuHover = HoverState()
    /// 有文件拖到面板上方。
    var dropTarget = HoverState()
    /// 截图自检（--render-assistant --tip 新对话）时从外面传入。
    var tip = TipState()

    var body: some View {
        VStack(spacing: 0) {
            header.zIndex(1)
            Divider().overlay(palette.fg.opacity(0.12))
            messagesView
            quickQuestions
            attachmentsBar
            inputBar.zIndex(1)
        }
        .background(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(palette.bg.opacity(max(settings.backgroundOpacity, 0.8)))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .strokeBorder(Color.blue, lineWidth: 2)
                .opacity(dropTarget.isHovering ? 1 : 0)
        )
        .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
        // 菜单打开时收起说明（菜单期间收不到鼠标移出）。
        .onReceive(NotificationCenter.default.publisher(for: NSMenu.didBeginTrackingNotification)) { _ in tip.name = nil }
        // 文件、截图、网页上的图片拖进面板任何地方都可以。
        .onDrop(of: [.fileURL, .image], isTargeted: Binding(get: { dropTarget.isHovering }, set: { dropTarget.isHovering = $0 })) {
            assistant.drop($0)
        }
    }

    private var header: some View {
        HStack(spacing: 8) {
            // 图标和标题不接收点击，按在上面也是拖动面板。
            Group {
                Image(systemName: "sparkles").foregroundStyle(.yellow)
                Text("AI 助手").font(.system(size: 13, weight: .semibold))
            }
            .allowsHitTesting(false)
            Spacer()
            modelMenu
            iconButton("square.and.pencil", "新对话", alignment: .trailing) { assistant.newConversation() }
            iconButton("xmark", "收起", alignment: .trailing) { onClose() }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(WindowDragArea())
    }

    /// 系统的弹出菜单由 AppKit 绘制，改不了悬停样式；自己画标签，鼠标移上去时变蓝。
    private var modelMenu: some View {
        Menu {
            // 用 Picker 让系统画对勾：自己用“按钮 + checkmark 图标”时，菜单宽度不算对勾，
            // 带对勾的那项会被截断（2026-10-22 真实窗口里“Claude Sonnet”显示成“Claud…onnet”）。
            Picker("模型", selection: $settings.assistantModel) {
                ForEach(settings.modelGroups(for: .assistant)) { group in
                    Section(group.title) {
                        ForEach(group.choices, id: \.self) { choice in
                            Text(settings.serviceName(choice)).tag(choice)
                        }
                    }
                }
            }
            .pickerStyle(.inline)
            .labelsHidden()
        } label: {
            HStack(spacing: 3) {
                Text(settings.serviceName(settings.assistantModel))
                Image(systemName: "chevron.down").font(.system(size: 8, weight: .bold))
            }
            .font(.system(size: 12))
        }
        // 和字幕上的“⋯”菜单一样：按钮样式才保留自己设的颜色。
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .fixedSize()
        .foregroundStyle(modelMenuHover.isHovering ? Color.blue : palette.fg.opacity(0.85))
        .padding(.horizontal, 7)
        .padding(.vertical, 3)
        .background(Capsule().fill(palette.fg.opacity(modelMenuHover.isHovering ? 0.12 : 0)))
        .contentShape(Capsule())
        .onHover { modelMenuHover.isHovering = $0 }
        // 菜单打开期间收不到鼠标移出，关掉菜单时复原，免得一直是蓝色。
        .onReceive(NotificationCenter.default.publisher(for: NSMenu.didEndTrackingNotification)) { _ in
            modelMenuHover.isHovering = false
        }
        .modifier(InstantTip(name: L("回答使用的模型"), state: tip, palette: palette))
    }

    private var messagesView: some View {
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    if assistant.messages.isEmpty {
                        Text("AI 助手可以读取本次开始以来的全部字幕，可随时就其中内容提问。")
                            .font(.system(size: 13))
                            .foregroundStyle(palette.fg.opacity(0.55))
                    }
                    ForEach(assistant.messages) { message in
                        bubble(message).id(message.id)
                    }
                    Color.clear.frame(height: 1).id("bottom")
                }
                .padding(12)
            }
            .scrollIndicators(.automatic)
            .onChange(of: assistant.messages.last?.text) { proxy.scrollTo("bottom", anchor: .bottom) }
            .onChange(of: assistant.messages.count) { proxy.scrollTo("bottom", anchor: .bottom) }
        }
    }

    @ViewBuilder
    private func bubble(_ message: AssistantSession.Message) -> some View {
        switch message.role {
        case .user:
            VStack(alignment: .trailing, spacing: 4) {
                if !message.text.isEmpty {
                    HStack {
                        Spacer(minLength: 40)
                        Text(message.text)
                            .font(.system(size: 13))
                            .foregroundStyle(.white)
                            .padding(.horizontal, 10)
                            .padding(.vertical, 7)
                            .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.blue.opacity(0.75)))
                            .textSelection(.enabled)
                    }
                }
                ForEach(Array(message.attachments.enumerated()), id: \.offset) { _, attachment in
                    attachmentLabel(attachment).foregroundStyle(palette.fg.opacity(0.7))
                }
            }
            .frame(maxWidth: .infinity, alignment: .trailing)
        case .assistant:
            VStack(alignment: .leading, spacing: 6) {
                if message.text.isEmpty && !message.writingFile && message.files.isEmpty {
                    progress("正在回答…")
                } else if !message.text.isEmpty {
                    Text(Self.markdown(message.text))
                        .font(.system(size: 13.5))
                        .lineSpacing(3)
                        .foregroundStyle(message.failed ? Color.orange : palette.fg)
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                if message.writingFile { progress("正在保存文件…") }
                ForEach(message.files, id: \.name) { file in savedFile(file) }
            }
        }
    }

    private func progress(_ text: String) -> some View {
        HStack(spacing: 6) {
            ProgressView().controlSize(.small)
            Text(L(text)).font(.system(size: 13)).foregroundStyle(palette.fg.opacity(0.6))
        }
    }

    /// 替用户保存的文件：点一下在 Finder 里选中它。
    @ViewBuilder
    private func savedFile(_ file: AssistantSession.SavedFile) -> some View {
        if let url = file.url {
            Button { NSWorkspace.shared.activateFileViewerSelecting([url]) } label: {
                Label(L("已保存：%@", file.name), systemImage: "doc.text")
                    .font(.system(size: 12.5))
                    .foregroundStyle(Color.accentColor)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .modifier(InstantTip(name: L("在 Finder 中显示"), state: tip, palette: palette, alignment: .leading))
        } else {
            Label(L("保存失败：%@", file.error ?? file.name), systemImage: "exclamationmark.triangle")
                .font(.system(size: 12.5))
                .foregroundStyle(.orange)
        }
    }

    private var quickQuestions: some View {
        ScrollView(.horizontal) {
            HStack(spacing: 6) {
                ForEach(AssistantPrompt.quickQuestions, id: \.self) { question in
                    Button { assistant.ask(question) } label: {
                        Text(L(question))
                            .font(.system(size: 11.5))
                            .foregroundStyle(palette.fg.opacity(0.85))
                            .padding(.horizontal, 9)
                            .padding(.vertical, 4)
                            .background(Capsule().fill(palette.fg.opacity(0.1)))
                    }
                    .buttonStyle(.plain)
                    .disabled(assistant.isAnswering || assistant.loadingAttachments > 0)
                }
            }
            .padding(.horizontal, 10)
        }
        .scrollIndicators(.never)
        .padding(.vertical, 6)
    }

    /// 等着随下一个问题发送的附件，每个可以点 × 去掉；读不了的文件在这里提示。
    @ViewBuilder
    private var attachmentsBar: some View {
        if !assistant.attachments.isEmpty || assistant.loadingAttachments > 0 || assistant.attachmentNotice != nil {
            VStack(alignment: .leading, spacing: 4) {
                ScrollView(.horizontal) {
                    HStack(spacing: 6) {
                        ForEach(assistant.attachments) { attachment in
                            HStack(spacing: 4) {
                                attachmentLabel(attachment.label)
                                Button { assistant.removeAttachment(attachment.id) } label: {
                                    Image(systemName: "xmark").font(.system(size: 8, weight: .bold)).contentShape(Rectangle())
                                }
                                .buttonStyle(.plain)
                                .modifier(InstantTip(name: L("去掉"), state: tip, palette: palette, above: true))
                            }
                            .foregroundStyle(palette.fg.opacity(0.85))
                            .padding(.horizontal, 8)
                            .padding(.vertical, 4)
                            .background(Capsule().fill(palette.fg.opacity(0.1)))
                        }
                        if assistant.loadingAttachments > 0 { ProgressView().controlSize(.small) }
                    }
                }
                .scrollIndicators(.never)
                if let notice = assistant.attachmentNotice {
                    Text(notice).font(.system(size: 11.5)).foregroundStyle(.orange)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .padding(.horizontal, 10)
            .padding(.bottom, 6)
        }
    }

    private func attachmentLabel(_ attachment: AssistantSession.AttachmentLabel) -> some View {
        Label(attachment.name, systemImage: attachment.isImage ? "photo" : "doc.text")
            .font(.system(size: 11.5))
            .lineLimit(1)
            .truncationMode(.middle)
            .frame(maxWidth: 180, alignment: .leading)
            .fixedSize(horizontal: true, vertical: false)
    }

    private func chooseFiles() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        panel.message = L("选择要发给 AI 助手的图片或文件")
        FilePanel.present(panel) { [assistant] url in
            guard url != nil else { return }  // 取消
            assistant.addFiles(panel.urls)
        }
    }

    private var inputBar: some View {
        HStack(spacing: 6) {
            iconButton("paperclip", "添加图片或文件", above: true, alignment: .leading, chooseFiles)
            TextField("输入问题", text: $assistant.draft)
                .textFieldStyle(.plain)
                .font(.system(size: 13))
                .onSubmit { assistant.send() }
                .padding(.horizontal, 10)
                .padding(.vertical, 7)
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(palette.fg.opacity(0.08)))
            Button { assistant.send() } label: {
                Image(systemName: "arrow.up.circle.fill")
                    .font(.system(size: 22))
                    .foregroundStyle(canSend ? Color.blue : palette.fg.opacity(0.3))
            }
            .buttonStyle(.plain)
            .disabled(!canSend)
            .modifier(InstantTip(name: L("发送"), state: tip, palette: palette, above: true, alignment: .trailing))
        }
        .padding(.horizontal, 10)
        .padding(.bottom, 10)
    }

    private var canSend: Bool { assistant.canSend }

    private func iconButton(_ symbol: String, _ help: String, above: Bool = false, alignment: HorizontalAlignment = .center,
                            _ action: @escaping () -> Void) -> some View {
        let hovered = tip.name == L(help)
        return Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(palette.fg.opacity(hovered ? 1 : 0.8))
                .frame(width: 22, height: 20)
                .background(Circle().fill(palette.fg.opacity(hovered ? 0.14 : 0)).frame(width: 24, height: 24))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .modifier(InstantTip(name: L(help), state: tip, palette: palette, above: above, alignment: alignment))
    }

    /// 回答里的粗体、行内代码等（保留换行，列表按原样显示）。
    static func markdown(_ text: String) -> AttributedString {
        (try? AttributedString(markdown: text, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)))
            ?? AttributedString(text)
    }
}
