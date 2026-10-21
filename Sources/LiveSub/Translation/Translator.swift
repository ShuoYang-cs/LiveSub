import Foundation
import LiveSubCore
import Translation

typealias ContextPairs = [(source: String, translation: String)]

protocol Translator: AnyObject, Sendable {
    var name: String { get }
    /// 翻译一个片段。`onPartial` 会收到逐步变长的译文（如果引擎支持流式输出）。
    func translate(_ text: String, context: ContextPairs, onPartial: @escaping @Sendable (String) -> Void) async throws -> String
    func shutdown()
}

extension Translator {
    func shutdown() {}
}

enum TranslatorError: LocalizedError {
    case claudeNotFound
    case claudeFailed(String)
    case timeout
    case processExited(String)
    case appleUnavailable(String, String)
    /// 通过 API 调用模型出错。`persistent`：密钥错误、模型不存在等，重试没有意义。
    case apiFailed(String, persistent: Bool)

    var errorDescription: String? {
        switch self {
        case .claudeNotFound:
            L("找不到 claude 命令。请先安装 Claude Code，或在设置中填写 Claude 路径。")
        case .claudeFailed(let message):
            message.localizedCaseInsensitiveContains("not logged in")
                ? L("Claude Code 未登录：请在终端运行 claude，输入 /login 登录一次。")
                : L("Claude 翻译出错：%@", message)
        case .timeout:
            L("Claude 翻译超时。")
        case .processExited(let stderr):
            L("Claude 进程退出：%@", stderr.isEmpty ? L("无输出") : stderr)
        case .apiFailed(let message, _):
            L("模型服务出错：%@", message)
        case .appleUnavailable(let pair, _):
            L("Apple 本地翻译不可用：%@。请在“系统设置 › 通用 › 语言与地区 › 翻译语言”中下载对应语言。", pair)
        }
    }

    /// 这类错误重试没有意义，应直接切换到兜底引擎并提示用户。
    var isPersistent: Bool {
        switch self {
        case .claudeNotFound: true
        case .claudeFailed(let m): m.localizedCaseInsensitiveContains("not logged in")
        case .apiFailed(_, let persistent): persistent
        default: false
        }
    }
}

/// Apple 系统自带的离线翻译（Translation 框架）。模型由系统管理，不占用本应用内存。
/// 不依赖界面直接创建翻译会话需要 macOS 26；更早的系统不提供这个选项。
@available(macOS 26, *)
final class AppleTranslator: Translator, @unchecked Sendable {
    let name = L("Apple 本地翻译")
    private let session: TranslationSession
    private let pair: String

    init(source: LanguageOption, target: LanguageOption) {
        session = TranslationSession(installedSource: Locale.Language(identifier: source.translationCode),
                                     target: Locale.Language(identifier: target.translationCode))
        pair = "\(L(source.displayName)) → \(L(target.displayName))"
    }

    /// 语言包是否已经下载到本机。
    static func isInstalled(source: LanguageOption, target: LanguageOption) async -> Bool {
        let status = await LanguageAvailability().status(from: Locale.Language(identifier: source.translationCode),
                                                         to: Locale.Language(identifier: target.translationCode))
        return status == .installed
    }

    func translate(_ text: String, context: ContextPairs, onPartial: @escaping @Sendable (String) -> Void) async throws -> String {
        let session = self.session
        do {
            return try await withThrowingTaskGroup(of: String.self) { group in
                group.addTask { try await session.translate(text).targetText }
                group.addTask {
                    try await Task.sleep(for: .seconds(8))
                    throw TranslatorError.timeout
                }
                defer { group.cancelAll() }
                return try await group.next()!
            }
        } catch {
            throw TranslatorError.appleUnavailable(pair, String(describing: error))
        }
    }
}
