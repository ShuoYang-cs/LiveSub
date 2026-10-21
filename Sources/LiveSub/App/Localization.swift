import Foundation

/// 界面文字：代码里写中文，英文对照在 Resources/en.lproj/Localizable.strings。
/// SwiftUI 里直接写的文字（Text("…")、Button("…") 等）会自动查表；运行时拼出来的文字用 `L`。
func L(_ key: String) -> String {
    NSLocalizedString(key, comment: "")
}

/// 带参数的界面文字，例如 `L("已添加 %d 个", count)`。
func L(_ key: String, _ arguments: CVarArg...) -> String {
    String(format: NSLocalizedString(key, comment: ""), arguments: arguments)
}
