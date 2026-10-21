import AppKit
import SwiftUI

/// 悬浮字幕窗口：无边框、置顶、所有桌面（含全屏视频）可见、不抢键盘焦点。
final class OverlayPanel: NSPanel {
    init() {
        super.init(contentRect: NSRect(x: 0, y: 0, width: 900, height: 120),
                   styleMask: [.borderless, .nonactivatingPanel],
                   backing: .buffered, defer: false)
        isFloatingPanel = true
        level = .statusBar
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        backgroundColor = .clear
        isOpaque = false
        hasShadow = false
        hidesOnDeactivate = false
        isMovableByWindowBackground = true
        isReleasedWhenClosed = false
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// 允许在字幕背景上按住拖动窗口。
final class DraggableHostingView<Content: View>: NSHostingView<Content> {
    override var mouseDownCanMoveWindow: Bool { true }
}

/// 承载字幕内容和边缘缩放手柄；空白处同样可以拖动窗口。
final class OverlayContainerView: NSView {
    override var mouseDownCanMoveWindow: Bool { true }
}

/// 允许本应用在后台时设置鼠标指针。
/// 字幕面板不会成为活动窗口（不抢键盘焦点），而 macOS 默认只让活动的程序改指针，
/// 边框上的缩放指针因此显示不出来。这里用系统的私有接口打开“后台也能设置指针”（窗口管理类工具常用）；
/// 接口不存在时什么都不做，只是边框上仍显示普通箭头。
enum BackgroundCursor {
    static func enable() {
        let handle = dlopen(nil, RTLD_NOW)
        guard let connectionSymbol = dlsym(handle, "_CGSDefaultConnection"),
              let setSymbol = dlsym(handle, "CGSSetConnectionProperty") else { return }
        typealias DefaultConnection = @convention(c) () -> Int32
        typealias SetProperty = @convention(c) (Int32, Int32, CFString, CFTypeRef) -> Int32
        let connection = unsafeBitCast(connectionSymbol, to: DefaultConnection.self)()
        let result = unsafeBitCast(setSymbol, to: SetProperty.self)(
            connection, connection, "SetsCursorInBackground" as CFString, kCFBooleanTrue)
        if result != 0 { appLog.notice("background cursor unavailable (\(result))") }
    }
}

/// 字幕窗口边缘/四角的缩放手柄。
/// 字幕面板无边框、也不能成为 key 窗口（不抢焦点），系统自带的边框缩放用不了，所以自己处理拖动。
final class ResizeHandleView: NSView {
    struct Edges: OptionSet {
        let rawValue: Int
        static let left = Edges(rawValue: 1 << 0)
        static let right = Edges(rawValue: 1 << 1)
        static let top = Edges(rawValue: 1 << 2)
        static let bottom = Edges(rawValue: 1 << 3)
    }

    let edges: Edges
    /// 拖动中的建议窗口大小（屏幕坐标）。
    var onResize: ((NSRect, Edges) -> Void)?
    var onDoubleClick: ((Edges) -> Void)?
    private var startFrame = NSRect.zero
    private var startMouse = NSPoint.zero
    private var dragging = false

    init(edges: Edges) {
        self.edges = edges
        super.init(frame: .zero)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var mouseDownCanMoveWindow: Bool { false }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    private var cursor: NSCursor {
        if #available(macOS 15, *) {
            let position: NSCursor.FrameResizePosition = switch edges {
            case [.top, .left]: .topLeft
            case [.top, .right]: .topRight
            case [.bottom, .left]: .bottomLeft
            case [.bottom, .right]: .bottomRight
            case .left: .left
            case .right: .right
            case .top: .top
            default: .bottom
            }
            return NSCursor.frameResize(position: position, directions: .all)
        }
        // macOS 14 没有斜向的缩放光标，四个角用十字光标。
        switch edges {
        case .left, .right: return .resizeLeftRight
        case .top, .bottom: return .resizeUpDown
        default: return .crosshair
        }
    }

    // 面板不是 key 窗口，cursor rect 不生效，用 activeAlways 的 tracking area 自己换光标。
    // 只在进入时设一次不够：下面的字幕内容和其他程序会在鼠标移动时把光标换回箭头，
    // 所以在边框上每次移动、每次收到光标更新都重新设置。
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero,
                                       options: [.mouseEnteredAndExited, .mouseMoved, .cursorUpdate, .activeAlways, .inVisibleRect],
                                       owner: self))
    }

    override func mouseEntered(with event: NSEvent) { cursor.set() }
    override func mouseMoved(with event: NSEvent) { cursor.set() }
    override func cursorUpdate(with event: NSEvent) { cursor.set() }

    override func mouseExited(with event: NSEvent) {
        if !dragging { NSCursor.arrow.set() }
    }

    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 {
            onDoubleClick?(edges)
            return
        }
        guard let window else { return }
        dragging = true
        startFrame = window.frame
        startMouse = NSEvent.mouseLocation
    }

    override func mouseDragged(with event: NSEvent) {
        guard dragging else { return }
        let mouse = NSEvent.mouseLocation
        let dx = mouse.x - startMouse.x, dy = mouse.y - startMouse.y
        var frame = startFrame
        if edges.contains(.left) { frame.origin.x += dx; frame.size.width -= dx }
        if edges.contains(.right) { frame.size.width += dx }
        if edges.contains(.top) { frame.size.height += dy }
        if edges.contains(.bottom) { frame.origin.y += dy; frame.size.height -= dy }
        onResize?(frame, edges)
        cursor.set()
    }

    override func mouseUp(with event: NSEvent) {
        dragging = false
        if !bounds.contains(convert(event.locationInWindow, from: nil)) { NSCursor.arrow.set() }
    }

    /// 把拖出来的窗口大小限制在最小值和屏幕之间；被限制时保持对边不动。
    static func clamped(_ proposed: NSRect, edges: Edges, minSize: NSSize, screen: NSScreen?) -> NSRect {
        let maxSize = (screen ?? NSScreen.main)?.visibleFrame.size ?? NSSize(width: 4000, height: 3000)
        let width = min(max(proposed.width, minSize.width), maxSize.width)
        let height = min(max(proposed.height, minSize.height), maxSize.height)
        var frame = proposed
        if edges.contains(.left) { frame.origin.x = frame.maxX - width }
        if edges.contains(.bottom) { frame.origin.y = frame.maxY - height }
        frame.size = NSSize(width: width, height: height)
        return frame
    }

    /// 内容视图 + 四边四角的缩放手柄（手柄叠在内容上面，先拿到鼠标事件）。
    @MainActor
    static func container(around content: NSView, size: NSSize,
                          onResize: @escaping (NSRect, Edges) -> Void,
                          onDoubleClick: @escaping (Edges) -> Void = { _ in }) -> NSView {
        let container = OverlayContainerView(frame: NSRect(origin: .zero, size: size))
        content.frame = container.bounds
        content.autoresizingMask = [.width, .height]
        container.addSubview(content)

        let edge: CGFloat = 8, corner: CGFloat = 14
        let w = container.bounds.width, h = container.bounds.height
        let handles: [(Edges, NSRect, NSView.AutoresizingMask)] = [
            (.left, NSRect(x: 0, y: corner, width: edge, height: h - 2 * corner), [.height, .maxXMargin]),
            (.right, NSRect(x: w - edge, y: corner, width: edge, height: h - 2 * corner), [.height, .minXMargin]),
            (.bottom, NSRect(x: corner, y: 0, width: w - 2 * corner, height: edge), [.width, .maxYMargin]),
            (.top, NSRect(x: corner, y: h - edge, width: w - 2 * corner, height: edge), [.width, .minYMargin]),
            ([.bottom, .left], NSRect(x: 0, y: 0, width: corner, height: corner), [.maxXMargin, .maxYMargin]),
            ([.bottom, .right], NSRect(x: w - corner, y: 0, width: corner, height: corner), [.minXMargin, .maxYMargin]),
            ([.top, .left], NSRect(x: 0, y: h - corner, width: corner, height: corner), [.maxXMargin, .minYMargin]),
            ([.top, .right], NSRect(x: w - corner, y: h - corner, width: corner, height: corner), [.minXMargin, .minYMargin]),
        ]
        for (edges, frame, mask) in handles {
            let handle = ResizeHandleView(edges: edges)
            handle.frame = frame
            handle.autoresizingMask = mask
            handle.onResize = onResize
            handle.onDoubleClick = onDoubleClick
            container.addSubview(handle)
        }
        return container
    }
}

@MainActor
final class OverlayWindowController {
    let panel = OverlayPanel()
    /// 字幕的界面状态（离屏截图时用来模拟鼠标悬停）。
    let state = OverlayState()
    private let settings: AppSettings
    private static let originKey = "overlayOrigin"

    init(controller: SubtitleController, settings: AppSettings, actions: OverlayActions) {
        self.settings = settings
        let state = state
        let root = OverlayView(controller: controller, settings: settings, actions: actions, onSize: { [weak self] size in
            self?.fit(to: size)
        }, state: state)
        let hosting = DraggableHostingView(rootView: root)
        hosting.sizingOptions = []
        panel.contentView = makeContainer(hosting: hosting)
        restorePosition()
        applyClickThrough()
        NotificationCenter.default.addObserver(forName: NSWindow.didMoveNotification, object: panel, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.savePosition() }
        }
    }

    var isVisible: Bool { panel.isVisible }

    func show() { panel.orderFrontRegardless() }
    func hide() { panel.orderOut(nil) }

    func applyClickThrough() {
        panel.ignoresMouseEvents = settings.clickThrough
    }

    func resetPosition() {
        UserDefaults.standard.removeObject(forKey: Self.originKey)
        restorePosition()
    }

    static let minSize = NSSize(width: 320, height: 70)

    private func makeContainer(hosting: NSView) -> NSView {
        ResizeHandleView.container(
            around: hosting, size: panel.frame.size,
            onResize: { [weak self] frame, edges in self?.userResized(to: frame, edges: edges) },
            onDoubleClick: { [weak self] edges in
                // 双击上/下边框：恢复按“显示最近 N 句”算的高度。
                if !edges.isDisjoint(with: [.top, .bottom]) { self?.settings.overlayHeight = 0 }
            })
    }

    /// 用户拖动边框：直接改窗口大小，并记到设置里（宽度；拖了上下边框时还有固定高度）。
    private func userResized(to proposed: NSRect, edges: ResizeHandleView.Edges) {
        var frame = proposed
        if edges.isDisjoint(with: [.top, .bottom]) {
            // 只拖左右时高度不变（仍按“显示最近 N 句”算）。
            frame.origin.y = panel.frame.origin.y
            frame.size.height = panel.frame.height
        }
        frame = ResizeHandleView.clamped(frame, edges: edges, minSize: Self.minSize, screen: panel.screen)
        panel.setFrame(frame, display: true)
        settings.overlayWidth = Double(frame.width)
        if !edges.isDisjoint(with: [.top, .bottom]) { settings.overlayHeight = Double(frame.height) }
    }

    /// 改了字号、句数、宽度等设置时调整窗口：底边固定，向上生长。
    private func fit(to size: CGSize) {
        guard size.width > 0, size.height > 0 else { return }
        var frame = panel.frame
        if abs(frame.width - size.width) < 0.5, abs(frame.height - size.height) < 0.5 { return }
        let centerX = frame.midX
        frame.size = size
        frame.origin.x = centerX - size.width / 2
        panel.setFrame(frame, display: true)
    }

    private func restorePosition() {
        if let saved = UserDefaults.standard.array(forKey: Self.originKey) as? [Double], saved.count == 2 {
            let point = NSPoint(x: saved[0], y: saved[1])
            if NSScreen.screens.contains(where: { $0.frame.insetBy(dx: -50, dy: -50).contains(point) }) {
                panel.setFrameOrigin(point)
                return
            }
        }
        guard let screen = NSScreen.main ?? NSScreen.screens.first else { return }
        let visible = screen.visibleFrame
        let width = settings.overlayWidth
        panel.setFrame(NSRect(x: visible.midX - width / 2, y: visible.minY + 70, width: width, height: 120), display: true)
    }

    private func savePosition() {
        let o = panel.frame.origin
        UserDefaults.standard.set([o.x, o.y], forKey: Self.originKey)
    }
}
