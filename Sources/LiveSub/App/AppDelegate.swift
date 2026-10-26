import AppKit
import Carbon.HIToolbox
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let settings = AppSettings.shared
    private lazy var controller = SubtitleController(settings: settings)
    private var overlay: OverlayWindowController!
    private var assistantWindow: AssistantWindowController!
    private var settingsWindow: NSWindow?
    private var historyWindow: NSWindow?
    private var recordingsWindow: NSWindow?
    private var modelServicesWindow: NSWindow?
    private let modelServicesState = ModelServicesState()
    private var showHotKey: GlobalHotKey?

    /// 没有菜单栏图标（2026-10-22 用户决定和 ICT 一样去掉）：所有操作都在字幕的快捷按钮和各个窗口里，
    /// 收起的字幕用 ⌃⌥L 或再打开一次 App 叫回来。
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.appearance = settings.appearance.nsAppearance
        BackgroundCursor.enable()
        Task { await LocalServiceDetector.run(settings: settings) }
        UpdateManager.shared.start()
        // 提前读好终端里的登录环境变量，第一次翻译时不用等。
        DispatchQueue.global(qos: .utility).async { _ = ClaudeCLI.shellEnvironment() }
        overlay = OverlayWindowController(controller: controller, settings: settings, actions: OverlayActions(
            toggleRunning: { [weak self] in self?.controller.toggle() },
            lock: { [weak self] in self?.lockSubtitles() },
            toggleRecording: { [weak self] in self?.controller.toggleRecording() },
            showSettings: { [weak self] in self?.showSettings() },
            quit: { NSApp.terminate(nil) },
            toggleAssistant: { [weak self] in self?.toggleAssistant() },
            settingsChanged: { [weak self] in self?.controller.settingsChanged() },
            manageModels: { [weak self] in self?.showModelServices() }
        ))
        assistantWindow = AssistantWindowController(
            assistant: controller.assistant, settings: settings, overlay: overlay.panel,
            onClose: { [weak self] in self?.toggleAssistant() })
        // 选了 Apple 本地翻译、语言包还没下载：打开设置，那里有“下载…”按钮。
        controller.onAppleLanguagesMissing = { [weak self] in self?.showSettings() }
        // 选了还没填 API 密钥的服务、还没下载的混元翻译：打开“模型服务”，滚到这一栏。
        settings.onModelNeedsSetup = { [weak self] id in
            self?.modelServicesState.focus = id
            self?.showModelServices()
        }
        showSubtitles()
        MainThreadWatchdog.shared.start()

        setUpEditMenu()
        observeActivation()
        // ⌃⌥L：显示/收起字幕。
        showHotKey = GlobalHotKey(keyCode: kVK_ANSI_L, modifiers: controlKey | optionKey) { [weak self] in
            self?.toggleOverlay()
        }
        if ProcessInfo.processInfo.environment["LIVESUB_AUTOSTART"] == "1" {
            controller.toggle()
        }
    }

    /// 菜单栏程序不显示菜单栏，但 ⌘C、⌘V、⌘A、⌘Z 要靠主菜单里的“编辑”菜单才会送到输入框；
    /// 没有它时，AI 助手、设置、模型服务里的输入框只能右键粘贴（2026-10-22 用测试程序确认过）。
    private func setUpEditMenu() {
        let edit = NSMenu(title: L("编辑"))
        edit.addItem(withTitle: L("撤销"), action: Selector(("undo:")), keyEquivalent: "z")
        edit.addItem(withTitle: L("重做"), action: Selector(("redo:")), keyEquivalent: "Z")
        edit.addItem(.separator())
        edit.addItem(withTitle: L("剪切"), action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: L("拷贝"), action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: L("粘贴"), action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: L("全选"), action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        let main = NSMenu()
        // 第一项是应用程序菜单，“编辑”放在第二项。
        main.addItem(NSMenuItem())
        let editItem = NSMenuItem()
        editItem.submenu = edit
        main.addItem(editItem)
        NSApp.mainMenu = main
    }

    /// 应用已经在运行时再次打开它（Spotlight、Finder 双击）：把字幕叫出来。
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showSubtitles()
        return false
    }

    func applicationWillTerminate(_ notification: Notification) {
        appLog.notice("terminate")
        controller.shutdown()
        LocalModelServer.shared.stop()
    }

    // MARK: - 动作

    private func toggleOverlay() {
        overlay.isVisible ? hideSubtitles() : showSubtitles()
    }

    /// 字幕和（打开着的）AI 助手一起显示 / 收起。
    private func showSubtitles() {
        overlay.show()
        if settings.assistantVisible { showAssistant() }
    }

    /// 打开 AI 助手面板；这时才启动它的 Claude 进程（不用就不占资源）。
    private func showAssistant() {
        assistantWindow.show()
        controller.assistant.warmUp()
    }

    private func hideSubtitles() {
        assistantWindow.hide()
        overlay.hide()
    }

    private func toggleAssistant() {
        settings.assistantVisible.toggle()
        if settings.assistantVisible, overlay.isVisible {
            showAssistant()
        } else {
            assistantWindow.hide()
        }
    }

    private func lockSubtitles() {
        settings.clickThrough = true
        overlay.applyClickThrough()
    }

    /// 设置里的“恢复默认”：字幕大小和位置、AI 助手位置都回到默认。
    private func resetOverlay() {
        settings.resetOverlaySize()
        overlay.resetPosition()
        assistantWindow.resetPosition()
        showSubtitles()
    }

    private func showSettings() {
        if settingsWindow == nil {
            let view = SettingsView(settings: settings,
                                    onTranslationSettingsChanged: { [weak self] in self?.controller.settingsChanged() },
                                    onAppearanceChanged: { [weak self] in self?.applyAppearance() },
                                    onManageModels: { [weak self] in self?.showModelServices() },
                                    onShowHistory: { [weak self] in self?.showHistory() },
                                    onShowRecordings: { [weak self] in self?.showRecordings() },
                                    onResetOverlay: { [weak self] in self?.resetOverlay() })
            settingsWindow = makeWindow(title: L("LiveSub 设置"), view: view, settingsLike: true)
        }
        present(settingsWindow!)
    }

    private func showModelServices() {
        if modelServicesWindow == nil {
            let view = ModelServicesView(settings: settings, state: modelServicesState,
                                         onChanged: { [weak self] in self?.controller.settingsChanged() })
            modelServicesWindow = makeWindow(title: L("模型服务"), view: view, settingsLike: true)
        }
        present(modelServicesWindow!)
    }

    private func showHistory() {
        if historyWindow == nil {
            historyWindow = makeWindow(title: L("LiveSub 字幕记录"), view: HistoryView(controller: controller))
            historyWindow?.setContentSize(NSSize(width: 640, height: 560))
        }
        present(historyWindow!)
    }

    /// 设置、模型服务、字幕记录窗口：用 LiveSub 时放在字幕和 AI 助手（.statusBar 层）上面，打开设置不用再把它拖开
    /// （2026-10-22 用户提出）；切到别的程序时回到普通层，不挡别的程序的窗口。
    private static let raisedLevel = NSWindow.Level(rawValue: NSWindow.Level.statusBar.rawValue + 1)

    private func observeActivation() {
        let center = NotificationCenter.default
        center.addObserver(forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.setWindowLevels(raised: true) }
        }
        center.addObserver(forName: NSApplication.didResignActiveNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.setWindowLevels(raised: false) }
        }
    }

    private func setWindowLevels(raised: Bool) {
        for window in [settingsWindow, modelServicesWindow, historyWindow, recordingsWindow].compactMap({ $0 }) {
            window.level = raised ? Self.raisedLevel : .normal
        }
    }

    private func showRecordings() {
        if recordingsWindow == nil {
            recordingsWindow = makeWindow(title: L("LiveSub 录音记录"), view: RecordingsView(model: RecordingsModel(settings: settings)))
            recordingsWindow?.setContentSize(NSSize(width: 620, height: 420))
        }
        present(recordingsWindow!)
    }

    /// `settingsLike`：设置类窗口（设置、模型服务）只留关闭按钮，和一般应用的设置窗口一样（2026-10-22 用户要求）；
    /// 仍然可以拖边缘调高度（设置页比窗口长）。字幕记录、录音记录是内容窗口，保留最小化和缩放。
    private func makeWindow(title: String, view: some View, settingsLike: Bool = false) -> NSWindow {
        let window = NSWindow(contentViewController: NSHostingController(rootView: view))
        window.title = title
        window.styleMask = settingsLike ? [.titled, .closable, .resizable] : [.titled, .closable, .miniaturizable, .resizable]
        if settingsLike {
            window.standardWindowButton(.miniaturizeButton)?.isHidden = true
            window.standardWindowButton(.zoomButton)?.isHidden = true
        }
        window.isReleasedWhenClosed = false
        window.center()
        return window
    }

    private func present(_ window: NSWindow) {
        NSApp.activate()
        window.level = Self.raisedLevel
        window.makeKeyAndOrderFront(nil)
    }

    /// 锁定、外观在设置里改了：字幕窗口和各个窗口跟着变。
    private func applyAppearance() {
        NSApp.appearance = settings.appearance.nsAppearance
        overlay.applyClickThrough()
    }
}
