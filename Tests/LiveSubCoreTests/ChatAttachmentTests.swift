import Foundation
import Testing
@testable import LiveSubCore

@Suite struct ChatAttachmentTests {
    let png = ChatAttachment.image(name: "slide.png", mediaType: "image/png", data: Data([1, 2, 3]))
    let pdf = ChatAttachment.pdf(name: "notes.pdf", data: Data([4, 5]), text: "PDF 里的文字")
    let txt = ChatAttachment.text(name: "a.md", content: "# 标题")

    func json(_ data: Data) -> [String: Any] {
        (try? JSONSerialization.jsonObject(with: data) as? [String: Any]) ?? [:]
    }

    @Test func anthropicPutsImagesAndPDFsBeforeText() {
        let blocks = ChatContent.anthropicBlocks(text: "这是什么？", attachments: [txt, png, pdf])
        #expect(blocks.map { $0["type"] as? String } == ["image", "document", "text"])
        let image = blocks[0]["source"] as? [String: String]
        #expect(image?["media_type"] == "image/png")
        #expect(image?["data"] == Data([1, 2, 3]).base64EncodedString())
        #expect(blocks[1]["title"] as? String == "notes.pdf")
        // 文字附件放进最后的文字块，问题在最后。
        let text = blocks[2]["text"] as? String ?? ""
        #expect(text.contains("[附件：a.md]\n# 标题\n[附件结束：a.md]"))
        #expect(text.hasSuffix("这是什么？"))
        #expect(!text.contains("PDF 里的文字"))  // Claude 直接读 PDF，不重复发文字
    }

    @Test func openAIKeepsPlainStringWithoutImages() {
        // 没有图片时 content 仍是字符串：本地服务不一定认数组。
        let content = ChatContent.openAIContent(text: "总结一下", attachments: [pdf, txt]) as? String ?? ""
        #expect(content.contains("[附件：notes.pdf]\nPDF 里的文字"))
        #expect(content.contains("[附件：a.md]"))
        #expect(content.hasSuffix("总结一下"))
    }

    @Test func openAIUsesDataURLsForImages() {
        let parts = ChatContent.openAIContent(text: "看图", attachments: [png]) as? [[String: Any]] ?? []
        #expect(parts.map { $0["type"] as? String } == ["text", "image_url"])
        let url = (parts[1]["image_url"] as? [String: String])?["url"]
        #expect(url == "data:image/png;base64," + Data([1, 2, 3]).base64EncodedString())
    }

    @Test func requestBodiesCarryAttachmentsOnlyWhereGiven() {
        let messages = [ChatMessage(role: .user, content: "旧问题"), ChatMessage(role: .assistant, content: "旧回答"),
                        ChatMessage(role: .user, content: "新问题", attachments: [png])]
        let anthropic = json(ChatAPI.requestBody(format: .anthropic, model: "claude-haiku-5-5", system: "S", messages: messages))
        let turns = anthropic["messages"] as? [[String: Any]] ?? []
        #expect(turns[0]["content"] as? String == "旧问题")
        #expect((turns[2]["content"] as? [[String: Any]])?.count == 2)
        let openAI = json(ChatAPI.requestBody(format: .openAI, model: "gemini", system: "S", messages: messages))
        let openAITurns = openAI["messages"] as? [[String: Any]] ?? []
        #expect(openAITurns[1]["content"] as? String == "旧问题")
        #expect((openAITurns[3]["content"] as? [[String: Any]])?.count == 2)
    }

    @Test func claudeCodeLineCarriesAttachments() throws {
        let line = ClaudeStream.userMessageLine("问题", attachments: [png, pdf])
        #expect(line.dropLast().contains("\n") == false)
        let obj = try JSONSerialization.jsonObject(with: Data(line.utf8)) as! [String: Any]
        let content = (obj["message"] as! [String: Any])["content"] as! [[String: Any]]
        #expect(content.map { $0["type"] as? String } == ["image", "document", "text"])
    }
}
