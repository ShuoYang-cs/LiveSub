import Foundation
import Testing
@testable import LiveSubCore

@Suite struct ChatAPITests {
    func json(_ data: Data) -> [String: Any] {
        (try? JSONSerialization.jsonObject(with: data) as? [String: Any]) ?? [:]
    }

    @Test func endpointURLs() {
        #expect(ChatAPI.endpoint(format: .openAI, baseURL: "https://api.deepseek.com/v1/", path: "chat/completions")?.absoluteString
                == "https://api.deepseek.com/v1/chat/completions")
        #expect(ChatAPI.endpoint(format: .anthropic, baseURL: "https://api.anthropic.com", path: "messages")?.absoluteString
                == "https://api.anthropic.com/v1/messages")
        #expect(ChatAPI.endpoint(format: .anthropic, baseURL: "https://api.anthropic.com/v1", path: "models")?.absoluteString
                == "https://api.anthropic.com/v1/models")
    }

    @Test func headers() {
        #expect(ChatAPI.headers(format: .openAI, apiKey: "sk-1")["Authorization"] == "Bearer sk-1")
        #expect(ChatAPI.headers(format: .openAI, apiKey: "")["Authorization"] == nil)  // 本地服务不需要密钥
        let a = ChatAPI.headers(format: .anthropic, apiKey: "k")
        #expect(a["x-api-key"] == "k")
        #expect(a["anthropic-version"] == "2023-06-01")
    }

    @Test func openAIBodyPutsSystemFirst() {
        let body = json(ChatAPI.requestBody(format: .openAI, model: "deepseek-chat", system: "S",
                                            messages: [.init(role: .user, content: "hi")]))
        let messages = body["messages"] as? [[String: String]]
        #expect(messages?.first?["role"] == "system")
        #expect(messages?.last?["content"] == "hi")
        #expect(body["stream"] as? Bool == true)
    }

    @Test func anthropicBodyUsesTopLevelSystemAndLowLatencySettings() {
        let haiku = json(ChatAPI.requestBody(format: .anthropic, model: "claude-haiku-5-5", system: "S",
                                             messages: [.init(role: .user, content: "hi")]))
        #expect(haiku["system"] as? String == "S")
        #expect((haiku["messages"] as? [[String: String]])?.count == 1)
        #expect((haiku["thinking"] as? [String: String])?["type"] == "disabled")
        #expect((haiku["cache_control"] as? [String: String])?["type"] == "ephemeral")
        let sonnet = json(ChatAPI.requestBody(format: .anthropic, model: "claude-sonnet-5-5", system: "S", messages: []))
        #expect((sonnet["thinking"] as? [String: String])?["type"] == "between_tools")
        let opus = json(ChatAPI.requestBody(format: .anthropic, model: "claude-opus-5-5", system: "S", messages: []))
        #expect(opus["thinking"] == nil)
        #expect((opus["output_config"] as? [String: String])?["effort"] == "low")
        let other = json(ChatAPI.requestBody(format: .anthropic, model: "claude-haiku-4-5", system: "S", messages: []))
        #expect(other["thinking"] == nil && other["output_config"] == nil)
    }

    @Test func parseOpenAIStream() {
        #expect(ChatAPI.parse(line: #"data: {"choices":[{"delta":{"content":"你好"}}]}"#, format: .openAI) == .text("你好"))
        #expect(ChatAPI.parse(line: #"data: {"choices":[{"delta":{"role":"assistant"}}]}"#, format: .openAI) == nil)
        #expect(ChatAPI.parse(line: "data: [DONE]", format: .openAI) == .done)
        #expect(ChatAPI.parse(line: ": keep-alive", format: .openAI) == nil)
        #expect(ChatAPI.parse(line: #"data: {"error":{"message":"Invalid API key"}}"#, format: .openAI) == .error("Invalid API key"))
    }

    @Test func parseAnthropicStream() {
        #expect(ChatAPI.parse(line: #"data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}"#,
                              format: .anthropic) == .text("Hello"))
        #expect(ChatAPI.parse(line: #"data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"x"}}"#,
                              format: .anthropic) == nil)
        #expect(ChatAPI.parse(line: "event: message_stop", format: .anthropic) == nil)
        #expect(ChatAPI.parse(line: #"data: {"type":"message_stop"}"#, format: .anthropic) == .done)
        #expect(ChatAPI.parse(line: #"data: {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"#,
                              format: .anthropic) == .error("Overloaded"))
    }

    @Test func errorBodiesAndModelLists() {
        #expect(ChatAPI.errorMessage(fromBody: Data(#"{"error":{"message":"bad key"}}"#.utf8), status: 401) == "bad key")
        #expect(ChatAPI.errorMessage(fromBody: Data(), status: 502) == "HTTP 502")
        #expect(ChatAPI.modelIDs(from: Data(#"{"data":[{"id":"b"},{"id":"a"}]}"#.utf8)) == ["a", "b"])
    }

    @Test func localEndpoints() {
        #expect(ChatEndpointPreset.isLocal(baseURL: "http://localhost:11434/v1"))
        #expect(ChatEndpointPreset.isLocal(baseURL: "http://127.0.0.1:1234/v1"))
        #expect(!ChatEndpointPreset.isLocal(baseURL: "https://api.deepseek.com/v1"))
    }

    @Test func thinkingIsTurnedOffOnlyWhereSupported() {
        func body(_ model: String) -> [String: Any] {
            json(ChatAPI.requestBody(format: .openAI, model: model, system: "S", messages: [.init(role: .user, content: "hi")]))
        }
        #expect((body("deepseek-flash")["thinking"] as? [String: String])?["type"] == "disabled")
        #expect((body("kimi-k2.6")["thinking"] as? [String: String])?["type"] == "disabled")
        #expect(body("qwen-flash")["enable_thinking"] as? Bool == false)
        // OpenAI、Grok 收到不认识的参数会报错，不能带。
        for model in ["gpt-5.4-mini", "grok-4.20-0309-non-reasoning", "kimi-k3"] {
            #expect(body(model)["thinking"] == nil && body(model)["enable_thinking"] == nil)
        }
    }

    @Test func builtInServicesHaveStableIDsAndModels() {
        let presets = ChatEndpointPreset.builtIn
        #expect(presets.map(\.name) == ["OpenAI", "通义千问", "DeepSeek", "Kimi", "Grok"])
        let ids = presets.compactMap { $0.builtInID.flatMap(UUID.init(uuidString:)) }
        #expect(Set(ids).count == presets.count)
        #expect(presets.allSatisfy { $0.needsKey && !$0.exampleModels.isEmpty && $0.format == .openAI })
        // 添加菜单里也有它们，而且不重复。
        #expect(Set(ChatEndpointPreset.all.map(\.name)).count == ChatEndpointPreset.all.count)
    }
}
