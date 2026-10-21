import Foundation

/// 通过 HTTP 调用的大模型接口：OpenAI 兼容格式，或 Anthropic Messages API。
/// OpenAI 兼容格式一种实现即可覆盖 OpenAI、DeepSeek、通义千问、Gemini、OpenRouter 等云服务，
/// 以及 Ollama、LM Studio 等本地服务。
public enum ChatAPIFormat: String, Codable, CaseIterable, Sendable {
    case openAI
    case anthropic
}

public struct ChatMessage: Equatable, Sendable {
    public enum Role: String, Sendable { case user, assistant }
    public var role: Role
    public var content: String
    public init(role: Role, content: String) {
        self.role = role
        self.content = content
    }
}

/// 流式响应里的一行解析出的事件。
public enum ChatStreamEvent: Equatable, Sendable {
    case text(String)
    case done
    case error(String)
}

public enum ChatAPI {
    // MARK: - 请求

    /// 请求地址：`baseURL` 是服务商给的根地址（如 https://api.deepseek.com/v1、http://localhost:11434/v1、https://api.anthropic.com）。
    public static func endpoint(format: ChatAPIFormat, baseURL: String, path: String) -> URL? {
        var base = baseURL.trimmingCharacters(in: .whitespacesAndNewlines)
        while base.hasSuffix("/") { base.removeLast() }
        switch format {
        case .openAI:
            return URL(string: base + "/" + path)
        case .anthropic:
            // Anthropic 的根地址不带 /v1；用户填了也兼容。
            if base.hasSuffix("/v1") { base.removeLast(3) }
            return URL(string: base + "/v1/" + path)
        }
    }

    public static func headers(format: ChatAPIFormat, apiKey: String?) -> [String: String] {
        var headers = ["Content-Type": "application/json"]
        let key = apiKey?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        switch format {
        case .openAI:
            if !key.isEmpty { headers["Authorization"] = "Bearer \(key)" }
        case .anthropic:
            headers["anthropic-version"] = "2023-06-01"
            if !key.isEmpty { headers["x-api-key"] = key }
        }
        return headers
    }

    /// 流式对话请求的 JSON。
    public static func requestBody(format: ChatAPIFormat, model: String, system: String,
                                   messages: [ChatMessage], maxTokens: Int = 4096) -> Data {
        let turns = messages.map { ["role": $0.role.rawValue, "content": $0.content] }
        var body: [String: Any]
        switch format {
        case .openAI:
            body = [
                "model": model,
                "messages": [["role": "system", "content": system]] + turns,
                "stream": true,
            ]
        case .anthropic:
            body = [
                "model": model,
                "max_tokens": maxTokens,
                "system": system,
                "messages": turns,
                "stream": true,
                // 自动提示缓存：系统提示和对话历史每次都一样，按缓存价格计费。
                "cache_control": ["type": "ephemeral"],
            ]
            body.merge(anthropicLatencySettings(model: model)) { _, new in new }
        }
        return try! JSONSerialization.data(withJSONObject: body, options: [.sortedKeys, .withoutEscapingSlashes])
    }

    /// 实时字幕要快：按模型尽量少思考。
    /// - Haiku 5.5：可以关闭思考；
    /// - Sonnet 5.5：关闭会返回 400，改用 between_tools（没有工具时就等于不思考）；
    /// - Opus 5.5、Fable：思考关不掉，用最低的 effort。
    static func anthropicLatencySettings(model: String) -> [String: Any] {
        if model.hasPrefix("claude-haiku-5") {
            return ["thinking": ["type": "disabled"]]
        }
        if model.hasPrefix("claude-sonnet-5-5") {
            return ["thinking": ["type": "between_tools"]]
        }
        if model.hasPrefix("claude-opus-5") || model.hasPrefix("claude-fable") {
            return ["output_config": ["effort": "low"]]
        }
        return [:]
    }

    // MARK: - 流式响应

    /// 解析服务器推送的一行（SSE）。不相关的行（event:、注释、心跳、思考内容）返回 nil。
    public static func parse(line: String, format: ChatAPIFormat) -> ChatStreamEvent? {
        let trimmed = line.trimmingCharacters(in: .whitespaces)
        guard trimmed.hasPrefix("data:") else { return nil }
        let payload = trimmed.dropFirst(5).trimmingCharacters(in: .whitespaces)
        if payload == "[DONE]" { return .done }
        guard let data = payload.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return nil }
        if let message = errorMessage(in: obj) { return .error(message) }
        switch format {
        case .openAI:
            guard let choices = obj["choices"] as? [[String: Any]], let first = choices.first else { return nil }
            if let delta = first["delta"] as? [String: Any], let text = delta["content"] as? String, !text.isEmpty {
                return .text(text)
            }
            return nil
        case .anthropic:
            switch obj["type"] as? String {
            case "content_block_delta":
                guard let delta = obj["delta"] as? [String: Any], delta["type"] as? String == "text_delta",
                      let text = delta["text"] as? String
                else { return nil }
                return .text(text)
            case "message_stop":
                return .done
            default:
                return nil
            }
        }
    }

    /// 出错时服务商返回的说明（两种格式都是 {"error": {"message": ...}}，有的直接是字符串）。
    public static func errorMessage(in obj: [String: Any]) -> String? {
        if let error = obj["error"] as? [String: Any] {
            return error["message"] as? String ?? NSLocalizedString("未知错误", comment: "")
        }
        if let error = obj["error"] as? String { return error }
        return nil
    }

    public static func errorMessage(fromBody data: Data, status: Int) -> String {
        if let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any], let message = errorMessage(in: obj) {
            return message
        }
        let text = String(decoding: data.prefix(300), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        return text.isEmpty ? "HTTP \(status)" : "HTTP \(status)：\(text)"
    }

    /// 模型列表（两种格式都是 {"data": [{"id": ...}]}）。
    public static func modelIDs(from data: Data) -> [String] {
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let items = obj["data"] as? [[String: Any]]
        else { return [] }
        return items.compactMap { $0["id"] as? String }.sorted()
    }
}

/// 常用服务的模板：填好接口格式和地址，用户只需要填密钥、选模型。
/// 模型名只是示例，以服务商当前文档或“获取模型列表”为准。
public struct ChatEndpointPreset: Sendable, Identifiable {
    public var id: String { name }
    public let name: String
    public let format: ChatAPIFormat
    public let baseURL: String
    public let exampleModels: [String]
    public let needsKey: Bool

    public static let all: [ChatEndpointPreset] = [
        .init(name: "Anthropic", format: .anthropic, baseURL: "https://api.anthropic.com",
              exampleModels: ["claude-haiku-5-5", "claude-sonnet-5-5"], needsKey: true),
        .init(name: "OpenAI", format: .openAI, baseURL: "https://api.openai.com/v1",
              exampleModels: [], needsKey: true),
        .init(name: "DeepSeek", format: .openAI, baseURL: "https://api.deepseek.com/v1",
              exampleModels: ["deepseek-chat"], needsKey: true),
        .init(name: "通义千问", format: .openAI, baseURL: "https://dashscope.aliyuncs.com/compatible-mode/v1",
              exampleModels: [], needsKey: true),
        .init(name: "Gemini", format: .openAI, baseURL: "https://generativelanguage.googleapis.com/v1beta/openai",
              exampleModels: [], needsKey: true),
        .init(name: "OpenRouter", format: .openAI, baseURL: "https://openrouter.ai/api/v1",
              exampleModels: [], needsKey: true),
        .init(name: "Ollama", format: .openAI, baseURL: "http://localhost:11434/v1",
              exampleModels: [], needsKey: false),
        .init(name: "LM Studio", format: .openAI, baseURL: "http://localhost:1234/v1",
              exampleModels: [], needsKey: false),
    ]

    /// 本机运行的服务（Ollama、LM Studio 等）归为“本地模型”。
    public static func isLocal(baseURL: String) -> Bool {
        guard let host = URL(string: baseURL.trimmingCharacters(in: .whitespaces))?.host?.lowercased() else { return false }
        return host == "localhost" || host == "127.0.0.1" || host == "::1" || host.hasSuffix(".local")
    }
}
