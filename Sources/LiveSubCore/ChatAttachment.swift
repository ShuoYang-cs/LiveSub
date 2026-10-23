import Foundation

/// 随问题一起发给模型的附件（AI 助手用）。文件由 App 读好、图片缩小编码好之后放在这里。
public enum ChatAttachment: Equatable, Sendable {
    /// 图片：JPEG 或 PNG 数据。
    case image(name: String, mediaType: String, data: Data)
    /// PDF：Claude（Claude Code、Anthropic 格式）直接读原文件，连图表一起看；
    /// OpenAI 兼容的服务大多不收 PDF，改为发提取出的文字。
    case pdf(name: String, data: Data, text: String)
    /// 文本文件、Word 文档等提取出的文字。
    case text(name: String, content: String)

    public var name: String {
        switch self {
        case .image(let name, _, _), .pdf(let name, _, _), .text(let name, _): name
        }
    }

    public var isImage: Bool {
        if case .image = self { true } else { false }
    }
}

/// 把问题和附件组装成两种接口格式的消息内容。
public enum ChatContent {
    /// Anthropic Messages API 和 Claude Code stream-json 的 content 块。
    /// 图片、PDF 放在文字前面（Anthropic 建议的顺序），文字附件和问题放在最后。
    public static func anthropicBlocks(text: String, attachments: [ChatAttachment]) -> [[String: Any]] {
        var blocks: [[String: Any]] = []
        var documents: [ChatAttachment] = []
        for attachment in attachments {
            switch attachment {
            case .image(_, let mediaType, let data):
                blocks.append(["type": "image",
                               "source": ["type": "base64", "media_type": mediaType, "data": data.base64EncodedString()]])
            case .pdf(let name, let data, _):
                blocks.append(["type": "document", "title": name,
                               "source": ["type": "base64", "media_type": "application/pdf", "data": data.base64EncodedString()]])
            case .text:
                documents.append(attachment)
            }
        }
        blocks.append(["type": "text", "text": textWithDocuments(text, documents)])
        return blocks
    }

    /// OpenAI 兼容格式的 content：没有图片时是一段字符串（本地服务也都认），有图片时是数组，图片用 data URL。
    /// PDF 发提取出的文字。
    public static func openAIContent(text: String, attachments: [ChatAttachment]) -> Any {
        let documents = attachments.filter { !$0.isImage }
        let combined = textWithDocuments(text, documents)
        let images = attachments.compactMap { attachment -> [String: Any]? in
            guard case .image(_, let mediaType, let data) = attachment else { return nil }
            return ["type": "image_url", "image_url": ["url": "data:\(mediaType);base64,\(data.base64EncodedString())"]]
        }
        guard !images.isEmpty else { return combined }
        return [["type": "text", "text": combined]] + images
    }

    /// 文字附件（以及按文字发送的 PDF）放在问题前面，每个用文件名标出起止。
    static func textWithDocuments(_ text: String, _ documents: [ChatAttachment]) -> String {
        let parts: [String] = documents.compactMap { attachment in
            switch attachment {
            case .text(let name, let content):
                "[附件：\(name)]\n\(content)\n[附件结束：\(name)]"
            case .pdf(let name, _, let content):
                "[附件：\(name)]\n\(content)\n[附件结束：\(name)]"
            case .image:
                nil
            }
        }
        return (parts + [text]).joined(separator: "\n\n")
    }
}

/// 附件的总大小上限：Claude（Claude Code、Anthropic 接口）一次请求最多 32 MB，图片和 PDF 按 base64 发送要大三分之一。
/// 一个 20 MB 的 PDF 加几张图就会超过，请求直接失败（HTTP 413）；HTTP 接口每次请求还要带上整段对话，
/// 前面问过的附件每问都重发一遍，所以：
/// - 这一问的附件太大时，PDF 从大到小改成只发提取出的文字（`fit`）；
/// - 整段对话超过上限时，从最早的提问开始，把附件换成文字（`compact`）。
public enum ChatAttachmentBudget {
    /// 原始字节数的上限：base64 后约 24 MB，给问题、字幕记录和回答留出余量。
    public static let maxBinaryBytes = 18_000_000

    public static func binaryBytes(_ attachment: ChatAttachment) -> Int {
        switch attachment {
        case .image(_, _, let data), .pdf(_, let data, _): data.count
        case .text: 0
        }
    }

    public static func binaryBytes(_ attachments: [ChatAttachment]) -> Int {
        attachments.reduce(0) { $0 + binaryBytes($1) }
    }

    /// 只留文字：PDF 换成提取出的文字，图片换成一句说明。
    public static func textOnly(_ attachment: ChatAttachment) -> ChatAttachment {
        switch attachment {
        case .image(let name, _, _): .text(name: name, content: "[An image sent earlier in this conversation; it is no longer attached.]")
        case .pdf(let name, _, let text): .text(name: name, content: text)
        case .text: attachment
        }
    }

    /// 这一问的附件超过上限时，PDF 从大到小改成只发文字（图片最大 3.5 MB，不用动）。返回改过的附件和改成文字的文件名。
    public static func fit(_ attachments: [ChatAttachment], maxBytes: Int = maxBinaryBytes) -> (attachments: [ChatAttachment], textOnly: [String]) {
        var result = attachments
        var changed: [String] = []
        let pdfs = result.indices.filter { if case .pdf = result[$0] { true } else { false } }
            .sorted { binaryBytes(result[$0]) > binaryBytes(result[$1]) }
        for i in pdfs where binaryBytes(result) > maxBytes {
            changed.append(result[i].name)
            result[i] = textOnly(result[i])
        }
        return (result, changed)
    }

    /// 整段对话里的附件超过上限时，从最早的消息开始把附件换成文字，最新一条（这次的提问）不动。
    public static func compact(_ messages: [ChatMessage], maxBytes: Int = maxBinaryBytes) -> [ChatMessage] {
        var result = messages
        var total = result.reduce(0) { $0 + binaryBytes($1.attachments) }
        for i in result.indices.dropLast() where total > maxBytes {
            let before = binaryBytes(result[i].attachments)
            guard before > 0 else { continue }
            result[i].attachments = result[i].attachments.map(textOnly)
            total -= before
        }
        return result
    }
}
