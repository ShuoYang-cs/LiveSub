import Foundation
import Testing
@testable import LiveSubCore

@Suite struct RecordFoldersTests {
    private let keywords = ["字幕记录", "录音", "笔记", "Transcript", "Record", "Notes"]

    @Test func folderNameIsStartTime() {
        #expect(RecordFolders.folderName(stamp: "2026-11-23 13.07") { _ in false } == "2026-11-23 13.07")
    }

    @Test func sameMinuteGetsNumber() {
        let taken: Set = ["2026-11-23 13.07", "2026-11-23 13.07 2"]
        #expect(RecordFolders.folderName(stamp: "2026-11-23 13.07") { taken.contains($0) } == "2026-11-23 13.07 3")
    }

    @Test func stampNeedsSpaceAfterIt() {
        #expect(RecordFolders.stamp(of: "2026-11-18 13.07 字幕记录.md") == "2026-11-18 13.07")
        #expect(RecordFolders.stamp(of: "2026-11-18 13.07.md") == nil)
        #expect(RecordFolders.stamp(of: "课程总结 2026-11-18 13.07 笔记.md") == nil)
    }

    @Test func groupsOldFilesByStartTime() {
        let files = [
            "2026-10-22 13.35 字幕记录.md",
            "2026-10-22 13.35 录音.m4a",
            "2026-10-22 13.35 录音（麦克风） 2.m4a",
            "2026-10-22 13.35 笔记.md",
            "2026-10-28 09.00 Transcript.md",
            "2026-10-28 09.00 Transcript.srt",
        ]
        let plan = RecordFolders.migrationPlan(files: files, keywords: keywords) { _, _ in false }
        #expect(plan.count == 6)
        #expect(Set(plan.filter { $0.folder == "2026-10-22 13.35" }.map(\.file)) == Set(files.prefix(4)))
        #expect(Set(plan.filter { $0.folder == "2026-10-28 09.00" }.map(\.file)) == Set(files.suffix(2)))
    }

    @Test func leavesOtherFilesAlone() {
        let files = ["我的课表.pdf", "2026-10-22 13.35 课件.pdf", ".DS_Store", "2026-10-22 字幕记录.md"]
        #expect(RecordFolders.migrationPlan(files: files, keywords: keywords) { _, _ in false }.isEmpty)
    }

    @Test func neverOverwritesFileInFolder() {
        let files = ["2026-10-22 13.35 字幕记录.md", "2026-10-22 13.35 笔记.md"]
        let plan = RecordFolders.migrationPlan(files: files, keywords: keywords) { folder, file in
            folder == "2026-10-22 13.35" && file == "2026-10-22 13.35 字幕记录.md"
        }
        #expect(plan == [RecordFolders.Move(file: "2026-10-22 13.35 笔记.md", folder: "2026-10-22 13.35")])
    }
}

@Suite struct AssistantLogTests {
    private let time = DateComponents(calendar: .current, year: 2026, month: 11, day: 23, hour: 13, minute: 8, second: 5).date!

    @Test func entryHasTimeQuestionAndAnswer() {
        let text = AssistantLog.entry(time: time, question: "老师刚才说的 pop 是什么？", attachments: [],
                                      answer: "  `list.pop()` 按位置删除并返回元素。\n")
        #expect(text == "\n### [13:08:05] 老师刚才说的 pop 是什么？\n\n`list.pop()` 按位置删除并返回元素。\n")
    }

    @Test func multiLineQuestionStaysInHeading() {
        let text = AssistantLog.entry(time: time, question: "第一行\n\n第二行", attachments: [], answer: "好")
        #expect(text.hasPrefix("\n### [13:08:05] 第一行 第二行\n"))
    }

    @Test func listsAttachmentsAndSavedFiles() {
        let text = AssistantLog.entry(time: time, question: "", attachments: ["slide.png", "讲义.pdf"],
                                      answer: "已整理成文件。", files: ["2026-11-23 13.08 总结.md"])
        #expect(text.contains("（只发了附件）"))
        #expect(text.contains("附件：slide.png、讲义.pdf"))
        #expect(text.contains("已保存文件：2026-11-23 13.08 总结.md"))
    }

    @Test func failedAnswerIsMarked() {
        let text = AssistantLog.entry(time: time, question: "？", attachments: [], answer: "Claude 没有响应", failed: true)
        #expect(text.contains("*回答失败：Claude 没有响应*"))
    }

    @Test func separatorShowsNewConversationTime() {
        #expect(AssistantLog.separator(time: time) == "\n---\n\n**新对话 13:08:05**\n")
    }
}
