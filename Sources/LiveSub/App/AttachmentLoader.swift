import AppKit
import ImageIO
import LiveSubCore
import PDFKit
import UniformTypeIdentifiers

/// 把 AI 助手的附件（拖进来、粘贴、选择的图片和文件）读成可以发给模型的内容。
/// 读文件、缩小图片、提取 PDF 文字可能要一会儿，不要在主线程上调用。
enum AttachmentLoader {
    enum Failure: LocalizedError {
        case unsupported(String)
        case tooLarge(String)
        case unreadable(String)

        var errorDescription: String? {
            switch self {
            case .unsupported(let name): L("暂不支持这种文件：%@。可以存成 PDF 或图片再添加。", name)
            case .tooLarge(let name): L("文件太大：%@", name)
            case .unreadable(let name): L("读不了这个文件：%@", name)
            }
        }
    }

    /// 图片长边缩到这么多像素：Claude 建议不超过约 1568，幻灯片上的字仍然看得清。
    static let maxImagePixels = 1568
    /// PNG 超过这么大就改存 JPEG（Claude 单张图片上限 5 MB）。
    static let maxPNGBytes = 3_500_000
    /// Claude 读 PDF 原文件的上限是 100 页、32 MB；超过时只发提取出的文字。
    static let maxPDFPages = 100
    static let maxPDFBytes = 20_000_000
    static let maxFileBytes = 50_000_000
    static let maxTextCharacters = 200_000

    /// Word、RTF 等用系统的文本框架读出文字。HTML 不在这里：系统读 HTML 要用主线程，按纯文本发给模型即可。
    private static let richTextTypes: [UTType] = [
        .rtf, .rtfd,
        UTType("org.openxmlformats.wordprocessingml.document"),
        UTType("com.microsoft.word.doc"),
        UTType("org.oasis-open.opendocument.text"),
    ].compactMap { $0 }

    static func load(_ url: URL) throws -> ChatAttachment {
        let name = url.lastPathComponent
        let values = try? url.resourceValues(forKeys: [.contentTypeKey, .fileSizeKey, .isDirectoryKey])
        if values?.isDirectory == true { throw Failure.unsupported(name) }
        if let size = values?.fileSize, size > maxFileBytes { throw Failure.tooLarge(name) }
        let type = values?.contentType ?? UTType(filenameExtension: url.pathExtension)

        if let type, type.conforms(to: .image) {
            guard let data = try? Data(contentsOf: url) else { throw Failure.unreadable(name) }
            return try image(data, name: name)
        }
        if let type, type.conforms(to: .pdf) {
            return try pdf(url, name: name)
        }
        if let type, richTextTypes.contains(where: { type.conforms(to: $0) }) {
            guard let text = try? NSAttributedString(url: url, options: [:], documentAttributes: nil).string else {
                throw Failure.unreadable(name)
            }
            return .text(name: name, content: truncated(text))
        }
        // 纯文本、代码、字幕文件等：能按文本读出来就当文本，不管扩展名系统认不认识。
        if let text = plainText(url) {
            return .text(name: name, content: truncated(text))
        }
        throw Failure.unsupported(name)
    }

    /// 缩小并重新编码：有透明部分的存 PNG，其余存 JPEG。
    static func image(_ data: Data, name: String) throws -> ChatAttachment {
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxImagePixels,
        ]
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
        else { throw Failure.unreadable(name) }
        let hasAlpha = ![.none, .noneSkipFirst, .noneSkipLast].contains(image.alphaInfo)
        if hasAlpha, let png = encode(image, as: .png), png.count <= maxPNGBytes {
            return .image(name: name, mediaType: "image/png", data: png)
        }
        guard let jpeg = encode(image, as: .jpeg, quality: 0.85) else { throw Failure.unreadable(name) }
        return .image(name: name, mediaType: "image/jpeg", data: jpeg)
    }

    private static func encode(_ image: CGImage, as type: UTType, quality: Double? = nil) -> Data? {
        let out = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(out, type.identifier as CFString, 1, nil) else { return nil }
        let properties = quality.map { [kCGImageDestinationLossyCompressionQuality: $0] as CFDictionary }
        CGImageDestinationAddImage(destination, image, properties)
        return CGImageDestinationFinalize(destination) ? out as Data : nil
    }

    private static func pdf(_ url: URL, name: String) throws -> ChatAttachment {
        guard let data = try? Data(contentsOf: url), let document = PDFDocument(data: data) else { throw Failure.unreadable(name) }
        var text = document.string?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if text.isEmpty { text = L("（这个 PDF 里没有可读取的文字，可能是扫描件。）") }
        guard data.count <= maxPDFBytes, document.pageCount <= maxPDFPages else {
            return .text(name: name, content: truncated(text))
        }
        return .pdf(name: name, data: data, text: truncated(text))
    }

    /// 按 UTF-8 或其他常见编码读出文字；含有 NUL 字节的当作二进制文件。
    private static func plainText(_ url: URL) -> String? {
        guard let data = try? Data(contentsOf: url), !data.prefix(8192).contains(0) else { return nil }
        if let text = String(data: data, encoding: .utf8) { return text }
        var encoding = String.Encoding.utf8
        return try? String(contentsOf: url, usedEncoding: &encoding)
    }

    private static func truncated(_ text: String) -> String {
        guard text.count > maxTextCharacters else { return text }
        return String(text.prefix(maxTextCharacters)) + "\n" + L("[……后面还有 %d 字没有附上]", text.count - maxTextCharacters)
    }
}
