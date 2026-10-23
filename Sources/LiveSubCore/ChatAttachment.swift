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
