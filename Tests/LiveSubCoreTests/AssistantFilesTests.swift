import Testing
@testable import LiveSubCore

@Suite struct AssistantFilesTests {
    @Test func plainAnswerHasNoFiles() {
        let parsed = AssistantFiles.parse("Jerry 说的是图文冲突时模型更信文字。")
        #expect(parsed.text == "Jerry 说的是图文冲突时模型更信文字。")
        #expect(parsed.files.isEmpty)
        #expect(!parsed.writing)
    }

    @Test func savesBlockAndKeepsShortSentence() {
        let reply = """
        已整理成文件。

        @@save: 组会总结
        # 组会总结

        - 图文冲突时模型更信文字
        @@end
        """
        let parsed = AssistantFiles.parse(reply)
        #expect(parsed.text == "已整理成文件。")
        #expect(parsed.files == [AssistantFiles.File(name: "组会总结", content: "# 组会总结\n\n- 图文冲突时模型更信文字")])
        #expect(!parsed.writing)
    }

    @Test func multipleFiles() {
        let reply = "@@save: a\nA\n@@end\n@@save: b\nB\n@@end"
        let parsed = AssistantFiles.parse(reply)
        #expect(parsed.files.map(\.name) == ["a", "b"])
        #expect(parsed.text.isEmpty)
    }

    @Test func unfinishedBlockWhileStreaming() {
        let parsed = AssistantFiles.parse("好的。\n@@save: 笔记\n# 标题\n第一")
        #expect(parsed.text == "好的。")
        #expect(parsed.writing)
        #expect(parsed.files == [AssistantFiles.File(name: "笔记", content: "# 标题\n第一")])
    }

    @Test func codeFencesAroundBlockAreDropped() {
        let reply = "已保存。\n```markdown\n@@save: x\n内容\n@@end\n```\n"
        let parsed = AssistantFiles.parse(reply)
        #expect(parsed.text == "已保存。")
        #expect(parsed.files == [AssistantFiles.File(name: "x", content: "内容")])
    }

    @Test func codeInsideFileIsKept() {
        let reply = "@@save: code\n```swift\nlet a = 1\n```\n@@end\n后面还有一句。"
        let parsed = AssistantFiles.parse(reply)
        #expect(parsed.files.first?.content == "```swift\nlet a = 1\n```")
        #expect(parsed.text == "后面还有一句。")
    }

    @Test func fileNameIsSafeAndDated() {
        #expect(AssistantFiles.fileName(for: "组会总结", stamp: "2026-10-21 21.32", fallback: "AI 助手") == "2026-10-21 21.32 组会总结.md")
        // 不能带路径，去掉系统不允许的字符
        #expect(AssistantFiles.fileName(for: "../../etc/pass:wd?.txt", stamp: "S", fallback: "F") == "S passwd.md")
        #expect(AssistantFiles.fileName(for: "~/Desktop/a\\b.md", stamp: "S", fallback: "F") == "S b.md")
        // 已经以日期开头时不再加前缀
        #expect(AssistantFiles.fileName(for: "2026-10-21 组会", stamp: "S", fallback: "F") == "2026-10-21 组会.md")
        #expect(AssistantFiles.fileName(for: "  ..  ", stamp: "S", fallback: "AI 助手") == "S AI 助手.md")
        let long = String(repeating: "长", count: 200)
        #expect(AssistantFiles.fileName(for: long, stamp: "S", fallback: "F").count == "S ".count + 80 + ".md".count)
    }

    @Test func uniqueNameDoesNotOverwrite() {
        let existing: Set = ["a.md", "a 2.md"]
        #expect(AssistantFiles.uniqueName("a.md") { existing.contains($0) } == "a 3.md")
        #expect(AssistantFiles.uniqueName("b.md") { existing.contains($0) } == "b.md")
    }

    @Test func systemPromptExplainsBlocks() {
        let prompt = AssistantPrompt.system(source: .source("en-US"), target: .target("zh-Hans"))
        #expect(prompt.contains(AssistantFiles.startMarker))
        #expect(prompt.contains(AssistantFiles.endMarker))
    }
}
