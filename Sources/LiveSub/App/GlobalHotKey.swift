import Carbon.HIToolbox

/// 全局快捷键（Carbon RegisterEventHotKey，不需要辅助功能权限）。
/// 菜单栏图标被刘海挡住时，也能用快捷键把字幕叫出来。
@MainActor
final class GlobalHotKey {
    private static var handlers: [UInt32: () -> Void] = [:]
    private static var nextID: UInt32 = 1
    private static var eventHandlerInstalled = false
    private var ref: EventHotKeyRef?

    /// `keyCode` 用 kVK_*，`modifiers` 用 controlKey / optionKey / cmdKey / shiftKey 组合。
    init?(keyCode: Int, modifiers: Int, action: @escaping () -> Void) {
        Self.installEventHandler()
        let id = Self.nextID
        Self.nextID += 1
        let hotKeyID = EventHotKeyID(signature: OSType(0x4C53_5542), id: id) // 'LSUB'
        guard RegisterEventHotKey(UInt32(keyCode), UInt32(modifiers), hotKeyID,
                                  GetApplicationEventTarget(), 0, &ref) == noErr else { return nil }
        Self.handlers[id] = action
    }

    private static func installEventHandler() {
        guard !eventHandlerInstalled else { return }
        eventHandlerInstalled = true
        var type = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            var hotKeyID = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                              nil, MemoryLayout<EventHotKeyID>.size, nil, &hotKeyID)
            let id = hotKeyID.id
            Task { @MainActor in GlobalHotKey.handlers[id]?() }
            return noErr
        }, 1, &type, nil, nil)
    }
}
