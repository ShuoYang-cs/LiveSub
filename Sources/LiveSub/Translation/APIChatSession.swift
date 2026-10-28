import Foundation
import LiveSubCore

/// 一问一答式的大模型会话：翻译、AI 助手、笔记共用。Claude Code 命令行和 HTTP 接口都实现它。
protocol ChatSession: Translator {
    /// 这节课的主题和术语（纠正用）。
    var topicNote: TopicNote { get }
    /// 会话是新开的（第一次，或轮数用完换了新会话）时发 `newSessionMessage`，否则发 `ongoingMessage`。
    /// `attachments`：随这次提问发送的图片和文件（AI 助手用）。
    func request(newSessionMessage: String, ongoingMessage: String, attachments: [ChatAttachment],
                 onPartial: @escaping @Sendable (String) -> Void) async throws -> String
    /// 提前准备好，第一次提问不用等。
    func warmUp()
}

extension ChatSession {
    func request(newSessionMessage: String, ongoingMessage: String,
                 onPartial: @escaping @Sendable (String) -> Void) async throws -> String {
        try await request(newSessionMessage: newSessionMessage, ongoingMessage: ongoingMessage, attachments: [], onPartial: onPartial)
    }
}

extension ClaudeTranslator: ChatSession {}

/// 通过 HTTP 调用 OpenAI 兼容接口或 Anthropic Messages API 的会话（用户自己的 API 密钥，或 Ollama 等本地服务）。
/// 对话历史保存在本机内存里，每次请求都要带上历史。翻译只保留最近 `historyTurns` 轮作为上下文，
/// 控制每次请求的 token 数；AI 助手需要完整的字幕记录，保留全部历史，超过 `maxTurns` 轮才开新会话。
final class APIChatSession: ChatSession, @unchecked Sendable {
    struct Config {
        var format: ChatAPIFormat
        var baseURL: String
        var apiKey: String?
        var model: String
        var systemPrompt: String
        var displayName: String
        var maxTurns = 40
        /// 只保留最近这么多轮对话；nil 表示保留全部。
        var historyTurns: Int?
        var timeout: TimeInterval = 30
    }

    let name: String
    let topicNote = TopicNote()
    private let config: Config
    private let lock = NSLock()
    // 以下只在 lock 内访问。
    private var history: [ChatMessage] = []
    private var current: Task<String, Error>?

    init(config: Config) {
        self.config = config
        name = config.displayName
    }

    func warmUp() {}

    func translate(_ text: String, context: ContextPairs, onPartial: @escaping @Sendable (String) -> Void) async throws -> String {
        do {
            return try await request(newSessionMessage: Prompts.translationMessage(text: text, context: context, topic: topicNote.current),
                                     ongoingMessage: topicNote.ongoingMessage(text), onPartial: onPartial)
        } catch {
            topicNote.resend()
            throw error
        }
    }

    func request(newSessionMessage: String, ongoingMessage: String, attachments: [ChatAttachment],
                 onPartial: @escaping @Sendable (String) -> Void) async throws -> String {
        let messages: [ChatMessage] = lock.withLock {
            if let keep = config.historyTurns, history.count > keep * 2 {
                history.removeFirst(history.count - keep * 2)
            } else if history.count >= config.maxTurns * 2 {
                history = []
            }
            let text = history.isEmpty ? newSessionMessage : ongoingMessage
            return ChatAttachmentBudget.compact(history + [ChatMessage(role: .user, content: text, attachments: attachments)])
        }
        let task = Task { try await self.stream(messages, onPartial: onPartial) }
        lock.withLock { current = task }
        let reply = try await task.value
        lock.withLock {
            history = messages + [ChatMessage(role: .assistant, content: reply)]
            current = nil
        }
        return reply
    }

    func shutdown() {
        lock.withLock {
            current?.cancel()
            current = nil
            history = []
        }
    }

    private func stream(_ messages: [ChatMessage], onPartial: @escaping @Sendable (String) -> Void) async throws -> String {
        let path = config.format == .anthropic ? "messages" : "chat/completions"
        guard let url = ChatAPI.endpoint(format: config.format, baseURL: config.baseURL, path: path) else {
            throw TranslatorError.apiFailed(L("接口地址无效：%@", config.baseURL), persistent: true)
        }
        var request = URLRequest(url: url, timeoutInterval: config.timeout)
        request.httpMethod = "POST"
        for (key, value) in ChatAPI.headers(format: config.format, apiKey: config.apiKey) {
            request.setValue(value, forHTTPHeaderField: key)
        }
        request.httpBody = ChatAPI.requestBody(format: config.format, model: config.model,
                                               system: config.systemPrompt, messages: messages)

        let (bytes, response) = try await URLSession.shared.bytes(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 200 else {
            var body = Data()
            for try await byte in bytes.prefix(4096) { body.append(byte) }
            // 密钥错误、没有权限、模型不存在：重试没有意义，改用备用引擎。
            throw TranslatorError.apiFailed(ChatAPI.errorMessage(fromBody: body, status: status),
                                            persistent: [400, 401, 403, 404].contains(status))
        }
        var text = ""
        for try await line in bytes.lines {
            try Task.checkCancellation()
            switch ChatAPI.parse(line: line, format: config.format) {
            case .text(let delta):
                text += delta
                onPartial(text)
            case .done:
                return text.trimmingCharacters(in: .whitespacesAndNewlines)
            case .error(let message):
                throw TranslatorError.apiFailed(message, persistent: false)
            case nil:
                continue
            }
        }
        return text.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// 读取服务提供的模型列表（“获取模型列表”按钮用）。
    static func fetchModels(format: ChatAPIFormat, baseURL: String, apiKey: String?) async throws -> [String] {
        guard let url = ChatAPI.endpoint(format: format, baseURL: baseURL, path: "models") else {
            throw TranslatorError.apiFailed(L("接口地址无效：%@", baseURL), persistent: true)
        }
        var request = URLRequest(url: url, timeoutInterval: 15)
        for (key, value) in ChatAPI.headers(format: format, apiKey: apiKey) {
            request.setValue(value, forHTTPHeaderField: key)
        }
        let (data, response) = try await URLSession.shared.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 200 else {
            throw TranslatorError.apiFailed(ChatAPI.errorMessage(fromBody: data, status: status), persistent: true)
        }
        return ChatAPI.modelIDs(from: data)
    }
}

/// API 密钥保存在系统钥匙串里，不写入设置文件，也不出现在日志里。
enum KeychainStore {
    private static let service = "com.livesub.api-key"

    static func key(for id: UUID) -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: id.uuidString,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func setKey(_ key: String, for id: UUID) {
        let base: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: id.uuidString,
        ]
        SecItemDelete(base as CFDictionary)
        let trimmed = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        var item = base
        item[kSecValueData as String] = Data(trimmed.utf8)
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        SecItemAdd(item as CFDictionary, nil)
    }
}

/// 按模型选择创建会话：Claude Code 命令行，或某个模型服务。Apple 本地翻译不是大模型，返回 nil。
enum ChatSessionFactory {
    enum Failure: LocalizedError {
        case claudeNotFound
        case endpointMissing
        case notLanguageModel
        case missingKey(String)

        var errorDescription: String? {
            switch self {
            case .claudeNotFound: TranslatorError.claudeNotFound.localizedDescription
            case .endpointMissing: L("找不到所选的模型服务，请在“模型服务”中重新选择。")
            case .notLanguageModel: L("这个功能需要大模型：请选择 Claude Code，或在“模型服务”中添加一个模型。")
            case .missingKey(let name): L("还没有填写 %@ 的 API 密钥：请在“模型服务”中填写。", name)
            }
        }
    }

    @MainActor
    /// - historyTurns: HTTP 接口只保留最近这么多轮作为上下文（翻译用）；nil 表示保留全部（AI 助手用）。
    static func make(_ choice: ModelChoice, settings: AppSettings, systemPrompt: String,
                     maxTurns: Int, historyTurns: Int? = nil, timeout: TimeInterval) throws -> ChatSession {
        switch choice {
        case .apple, .google, .localModel:
            throw Failure.notLanguageModel
        case .claudeCode(let model):
            guard let exe = ClaudeCLI.locate(customPath: settings.claudePath) else { throw Failure.claudeNotFound }
            return ClaudeTranslator(config: .init(executable: exe, model: model, systemPrompt: systemPrompt,
                                                  maxTurnsPerSession: maxTurns, timeout: timeout))
        case .endpoint(let id, let model):
            guard let endpoint = settings.endpoint(id) else { throw Failure.endpointMissing }
            if settings.endpointMissingKey(choice) != nil { throw Failure.missingKey(endpoint.name) }
            return APIChatSession(config: .init(
                format: endpoint.format, baseURL: endpoint.baseURL, apiKey: KeychainStore.key(for: id),
                model: model, systemPrompt: systemPrompt, displayName: settings.displayName(choice),
                maxTurns: maxTurns, historyTurns: historyTurns, timeout: timeout))
        }
    }
}
