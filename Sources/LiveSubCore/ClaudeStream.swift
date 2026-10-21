import Foundation

/// `claude -p --output-format stream-json` 输出的一行事件。
public enum ClaudeStreamEvent: Equatable, Sendable {
    case initialized(model: String)
    /// 流式文本增量（需要 `--include-partial-messages`）。
    case textDelta(String)
    /// 完整的助手消息文本。
    case assistantText(String)
    /// 一轮结束。`isError` 为 true 时 `text` 是错误说明（例如未登录）。
    case result(text: String, isError: Bool)
    case other
}

public enum ClaudeStream {
    public static func parse(line: String) -> ClaudeStreamEvent? {
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, let data = trimmed.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let type = obj["type"] as? String
        else { return nil }

        switch type {
        case "system":
            if obj["subtype"] as? String == "init" {
                return .initialized(model: obj["model"] as? String ?? "")
            }
            return .other
        case "stream_event":
            guard let event = obj["event"] as? [String: Any],
                  event["type"] as? String == "content_block_delta",
                  let delta = event["delta"] as? [String: Any],
                  delta["type"] as? String == "text_delta",
                  let text = delta["text"] as? String
            else { return .other }
            return .textDelta(text)
        case "assistant":
            guard let message = obj["message"] as? [String: Any],
                  let content = message["content"] as? [[String: Any]]
            else { return .other }
            let text = content.compactMap { block -> String? in
                block["type"] as? String == "text" ? block["text"] as? String : nil
            }.joined()
            return .assistantText(text)
        case "result":
            let isError = (obj["is_error"] as? Bool ?? false) || (obj["subtype"] as? String ?? "success") != "success"
            return .result(text: obj["result"] as? String ?? "", isError: isError)
        default:
            return .other
        }
    }

    /// 一条 stream-json 输入消息（一行 JSON）。
    public static func userMessageLine(_ text: String) -> String {
        let payload: [String: Any] = [
            "type": "user",
            "message": [
                "role": "user",
                "content": [["type": "text", "text": text]],
            ],
        ]
        let data = try! JSONSerialization.data(withJSONObject: payload, options: [.withoutEscapingSlashes])
        return String(decoding: data, as: UTF8.self) + "\n"
    }
}

/// 把字节流切成行。stdout 的数据块可能在任意位置断开（包括 UTF-8 多字节字符中间），所以按字节缓冲。
public struct LineBuffer: Sendable {
    private var buffer = Data()
    public init() {}

    public mutating func append(_ data: Data) -> [String] {
        buffer.append(data)
        var lines: [String] = []
        while let idx = buffer.firstIndex(of: 0x0A) {
            let lineData = buffer[buffer.startIndex..<idx]
            lines.append(String(decoding: lineData, as: UTF8.self))
            buffer.removeSubrange(buffer.startIndex...idx)
        }
        return lines
    }
}
