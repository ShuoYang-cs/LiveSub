import AppKit
import Carbon.HIToolbox
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let settings = AppSettings.shared
    private lazy var controller = SubtitleController(settings: settings)
    private var overlay: OverlayWindowController!
    private var assistantWindow: AssistantWindowController!
    private var statusItem: NSStatusItem!
    private var settingsWindow: NSWindow?
    private var historyWindow: NSWindow?
    private var modelServicesWindow: NSWindow?
    private let modelServicesState = ModelServicesState()
    private var iconObserver: Task<Void, Never>?
    private var statusMenu: NSMenu!
    private var showHotKey: GlobalHotKey?
    /// 菜单栏图标的名字：系统按它记住图标位置（⌘-拖动后的位置也会记住）。
    private static let statusItemName = "LiveSub"

    func applicationDidFinishLaunching(_ notification: Notification) {
        BackgroundCursor.enable()
        // 提前读好终端里的登录环境变量，第一次翻译时不用等。
        DispatchQueue.global(qos: .utility).async { _ = ClaudeCLI.shellEnvironment() }
        overlay = OverlayWindowController(controller: controller, settings: settings, actions: OverlayActions(
            toggleRunning: { [weak self] in self?.controller.toggle() },
            lock: { [weak self] in self?.setClickThrough(true) },
            showSettings: { [weak self] in self?.showSettings() },
            minimize: { [weak self] in self?.hideSubtitles() },
            toggleAssistant: { [weak self] in self?.toggleAssistant() },
            modelChanged: { [weak self] in self?.controller.settingsChanged() },
            manageModels: { [weak self] in self?.showModelServices() }
        ))
        assistantWindow = AssistantWindowController(
            assistant: controller.assistant, settings: settings, overlay: overlay.panel,
            onClose: { [weak self] in self?.toggleAssistant() })
        showSubtitles()
        MainThreadWatchdog.shared.start()

        setUpEditMenu()
        setUpStatusItem()
        // ⌃⌥L：显示/收起字幕。
        showHotKey = GlobalHotKey(keyCode: kVK_ANSI_L, modifiers: controlKey | optionKey) { [weak self] in
            self?.toggleOverlay()
        }
        observeRunningState()
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
    }

    // MARK: - 菜单栏

    // MARK: - 菜单栏图标

    private func setUpStatusItem() {
        // 第一次运行时把图标放在 Wi-Fi 旁边（离屏幕右边约 300 点）：新加的图标默认排在最左边，
        // 菜单栏图标多的时候会被刘海挡住。之后用户 ⌘-拖动的位置系统会自己记住。
        let positionKey = "NSStatusItem Preferred Position \(Self.statusItemName)"
        if UserDefaults.standard.object(forKey: positionKey) == nil {
            UserDefaults.standard.set(300, forKey: positionKey)
        }
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.autosaveName = Self.statusItemName
        statusMenu = NSMenu()
        statusMenu.delegate = self
        statusItem.button?.target = self
        statusItem.button?.action = #selector(statusItemClicked)
        statusItem.button?.sendAction(on: [.leftMouseUp, .rightMouseUp])
        updateIcon()
        if ProcessInfo.processInfo.environment["LIVESUB_DEBUG_STATUS"] != nil {
            DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in
                let frame = self?.statusItem.button?.window?.frame ?? .zero
                FileHandle.standardError.write(Data("status item frame \(frame)\n".utf8))
            }
        }
    }

    /// 左键：字幕收起时打开字幕，否则弹出菜单；右键（或按住 ⌃）：总是弹出菜单。
    @objc private func statusItemClicked() {
        let event = NSApp.currentEvent
        let wantsMenu = event?.type == .rightMouseUp || event?.modifierFlags.contains(.control) == true
        if !wantsMenu, !overlay.isVisible {
            showSubtitles()
            return
        }
        statusItem.menu = statusMenu
        statusItem.button?.performClick(nil)
    }

    func menuDidClose(_ menu: NSMenu) {
        // 菜单只在需要时挂上，平时让左键点击走 statusItemClicked。
        statusItem.menu = nil
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let status = NSMenuItem(title: controller.status, action: nil, keyEquivalent: "")
        status.isEnabled = false
        menu.addItem(status)
        // 字幕锁定时点不到字幕下方提示里的按钮，菜单里也放一个。
        if controller.systemAudioBlocked {
            menu.addItem(item("打开系统录音设置", #selector(openSystemAudioSettings)))
        }
        menu.addItem(.separator())

        menu.addItem(item(controller.isRunning ? "停止字幕" : "开始字幕", #selector(toggleRunning), key: "s"))

        let sourceMenu = NSMenu()
        for mode in AudioSourceMode.allCases {
            let i = item(mode.title, #selector(selectSource(_:)))
            i.representedObject = mode.rawValue
            i.state = settings.audioSource == mode ? .on : .off
            sourceMenu.addItem(i)
        }
        menu.addItem(submenu("音源", sourceMenu))

        let displayMenu = NSMenu()
        for mode in DisplayMode.allCases {
            let i = item(mode.title, #selector(selectDisplay(_:)))
            i.representedObject = mode.rawValue
            i.state = settings.displayMode == mode ? .on : .off
            displayMenu.addItem(i)
        }
        menu.addItem(submenu("显示", displayMenu))

        let modelMenu = NSMenu()
        for group in settings.modelGroups(includeApple: true) {
            modelMenu.addItem(.sectionHeader(title: group.title))
            for choice in group.choices {
                let i = item(settings.displayName(choice), #selector(selectModel(_:)))
                i.representedObject = choice
                i.state = settings.translationModel == choice ? .on : .off
                modelMenu.addItem(i)
            }
        }
        modelMenu.addItem(.separator())
        modelMenu.addItem(item("管理模型服务…", #selector(showModelServices)))
        menu.addItem(submenu("翻译模型", modelMenu))
        menu.addItem(.separator())

        let show = item(overlay.isVisible ? "收起字幕到菜单栏" : "显示字幕", #selector(toggleOverlay))
        show.keyEquivalentModifierMask = [.control, .option]
        show.keyEquivalent = "l"
        menu.addItem(show)
        let assistantItem = item("AI 助手", #selector(toggleAssistantFromMenu))
        assistantItem.state = settings.assistantVisible ? .on : .off
        menu.addItem(assistantItem)
        let lock = item("锁定字幕", #selector(toggleClickThrough), key: "l")
        lock.state = settings.clickThrough ? .on : .off
        menu.addItem(lock)
        menu.addItem(item("字幕移回屏幕底部", #selector(resetOverlayPosition)))
        menu.addItem(.separator())

        menu.addItem(item("字幕记录…", #selector(showHistory), key: "h"))
        let notes = item(controller.notesInProgress ? "正在生成笔记…" : "生成笔记", #selector(generateNotes))
        notes.isEnabled = !controller.notesInProgress && !controller.lines.isEmpty
        menu.addItem(notes)
        let exportMenu = NSMenu()
        for format in SubtitleFileFormat.allCases {
            let i = item(format.menuTitle, #selector(exportSubtitles(_:)))
            i.representedObject = format.rawValue
            exportMenu.addItem(i)
        }
        let export = submenu("导出字幕", exportMenu)
        export.isEnabled = !controller.lines.isEmpty
        menu.addItem(export)
        menu.addItem(item("开始新的记录", #selector(newSession)))
        menu.addItem(item("打开记录文件夹", #selector(openTranscriptFolder)))
        menu.addItem(.separator())
        menu.addItem(item("设置…", #selector(showSettings), key: ","))
        menu.addItem(item("退出 LiveSub", #selector(quit), key: "q"))
    }

    private func item(_ title: String, _ action: Selector, key: String = "") -> NSMenuItem {
        let i = NSMenuItem(title: L(title), action: action, keyEquivalent: key)
        i.target = self
        return i
    }

    private func submenu(_ title: String, _ menu: NSMenu) -> NSMenuItem {
        let i = NSMenuItem(title: L(title), action: nil, keyEquivalent: "")
        i.submenu = menu
        return i
    }

    private func updateIcon() {
        let symbol = controller.isRunning ? "captions.bubble.fill" : "captions.bubble"
        statusItem.button?.image = NSImage(systemSymbolName: symbol, accessibilityDescription: "LiveSub")
    }

    private func observeRunningState() {
        withObservationTracking {
            _ = controller.isRunning
        } onChange: { [weak self] in
            Task { @MainActor in
                self?.updateIcon()
                self?.observeRunningState()
            }
        }
    }

    // MARK: - 动作

    @objc private func toggleRunning() {
        if !controller.isRunning { showSubtitles() }
        controller.toggle()
    }

    @objc private func selectSource(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let mode = AudioSourceMode(rawValue: raw) else { return }
        settings.audioSource = mode
        controller.settingsChanged()
    }

    @objc private func selectDisplay(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let mode = DisplayMode(rawValue: raw) else { return }
        settings.displayMode = mode
    }

    @objc private func selectModel(_ sender: NSMenuItem) {
        guard let choice = sender.representedObject as? ModelChoice else { return }
        settings.translationModel = choice
        controller.settingsChanged()
    }

    @objc private func toggleOverlay() {
        overlay.isVisible ? hideSubtitles() : showSubtitles()
    }

    /// 字幕和（打开着的）AI 助手一起显示 / 收起到菜单栏。
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

    @objc private func toggleAssistantFromMenu() {
        if !overlay.isVisible { overlay.show() }
        toggleAssistant()
    }

    @objc private func exportSubtitles(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let format = SubtitleFileFormat(rawValue: raw) else { return }
        controller.exportSubtitles(format: format)
    }

    @objc private func toggleClickThrough() { setClickThrough(!settings.clickThrough) }

    private func setClickThrough(_ on: Bool) {
        settings.clickThrough = on
        overlay.applyClickThrough()
    }

    @objc private func resetOverlayPosition() {
        overlay.resetPosition()
        assistantWindow.resetPosition()
        showSubtitles()
    }

    @objc private func generateNotes() { controller.generateNotes() }
    @objc private func newSession() { controller.newSession() }

    @objc private func openSystemAudioSettings() { SystemAudioCapture.openPrivacySettings() }

    @objc private func openTranscriptFolder() {
        if let url = controller.transcriptURL {
            NSWorkspace.shared.activateFileViewerSelecting([url])
        } else {
            let folder = URL(fileURLWithPath: (settings.transcriptFolder as NSString).expandingTildeInPath)
            try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            NSWorkspace.shared.open(folder)
        }
    }

    @objc private func showSettings() {
        if settingsWindow == nil {
            let view = SettingsView(settings: settings,
                                    onTranslationSettingsChanged: { [weak self] in self?.controller.settingsChanged() },
                                    onAppearanceChanged: { [weak self] in self?.overlay.applyClickThrough() },
                                    onManageModels: { [weak self] in self?.showModelServices() },
                                    onShowHistory: { [weak self] in self?.showHistory() })
            settingsWindow = makeWindow(title: L("LiveSub 设置"), view: view)
        }
        present(settingsWindow!)
    }

    @objc private func showModelServices() {
        if modelServicesWindow == nil {
            let view = ModelServicesView(settings: settings, state: modelServicesState,
                                         onChanged: { [weak self] in self?.controller.settingsChanged() })
            modelServicesWindow = makeWindow(title: L("模型服务"), view: view)
        }
        present(modelServicesWindow!)
    }

    @objc private func showHistory() {
        if historyWindow == nil {
            historyWindow = makeWindow(title: L("LiveSub 字幕记录"), view: HistoryView(controller: controller))
            historyWindow?.setContentSize(NSSize(width: 640, height: 560))
        }
        present(historyWindow!)
    }

    private func makeWindow(title: String, view: some View) -> NSWindow {
        let window = NSWindow(contentViewController: NSHostingController(rootView: view))
        window.title = title
        window.styleMask = [.titled, .closable, .miniaturizable, .resizable]
        window.isReleasedWhenClosed = false
        window.center()
        return window
    }

    private func present(_ window: NSWindow) {
        NSApp.activate()
        window.makeKeyAndOrderFront(nil)
    }

    @objc private func quit() {
        controller.shutdown()
        NSApp.terminate(nil)
    }
}
