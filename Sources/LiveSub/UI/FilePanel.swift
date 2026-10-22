import AppKit

/// 选择文件夹、导出字幕用的系统对话框。
/// 字幕和 AI 助手是总在最上层的面板（.statusBar），比系统对话框高，会挡住对话框的按钮；
/// 用 runModal 打开时整个程序进入模态，挡住按钮的面板也拖不开，看起来像卡死了（2026-10-21 发生过）。
/// 所以对话框放在它们上面一层，并且不用模态打开：开着对话框时字幕照常更新。
enum FilePanel {
    @MainActor
    static func present(_ panel: NSSavePanel, completion: @escaping @MainActor (URL?) -> Void) {
        panel.level = NSWindow.Level(rawValue: NSWindow.Level.statusBar.rawValue + 1)
        NSApp.activate()
        panel.begin { response in
            MainActor.assumeIsolated { completion(response == .OK ? panel.url : nil) }
        }
    }
}
