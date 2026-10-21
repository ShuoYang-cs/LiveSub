import Foundation
import Testing
@testable import LiveSubCore

@Suite struct AssistantPromptTests {
    let t0 = Date(timeIntervalSince1970: 1_000_000)

    @Test func questionOnly() {
        let m = AssistantPrompt.message(newLines: [], speaking: "  ", question: " 什么是记忆化？ ")
        #expect(m == "[我的问题]\n什么是记忆化？")
    }

    @Test func newLinesSpeakingAndQuestion() {
        let lines = [
            AssistantPrompt.Line(time: t0, text: "So we fill the table."),
            AssistantPrompt.Line(time: t0.addingTimeInterval(65), text: "Then we go back from the corner."),
        ]
        let m = AssistantPrompt.message(newLines: lines, speaking: "and that gives", question: "回溯怎么走？")
        let a = TranscriptFormat.timeFormatter.string(from: t0)
        let b = TranscriptFormat.timeFormatter.string(from: t0.addingTimeInterval(65))
        #expect(m == """
        [字幕 · 新增 2 句 · \(a)–\(b)]
        [\(a)] So we fill the table.
        [\(b)] Then we go back from the corner.

        [正在说（还没说完）]
        and that gives

        [我的问题]
        回溯怎么走？
        """)
    }

    @Test func systemPromptUsesTargetLanguage() {
        let p = AssistantPrompt.system(source: .source("en-US"), target: .target("zh-Hans"))
        #expect(p.contains("Answer in Simplified Chinese"))
    }

    @Test func transcriptEntryKeepsOriginalOnlyWhenDifferent() {
        let time = Date(timeIntervalSince1970: 1_000_000)
        let stamp = TranscriptFormat.timeFormatter.string(from: time)
        let corrected = TranscriptFormat.entry(time: time, sourceLabel: nil, source: "use dynamic programming.",
                                               translation: "用动态规划。", original: "use dynamic program.")
        #expect(corrected == "**[\(stamp)]** use dynamic programming.\n*识别原文：use dynamic program.*\n> 用动态规划。\n\n")
        let same = TranscriptFormat.entry(time: time, sourceLabel: nil, source: "okay.", translation: "好。", original: "okay.")
        #expect(same == "**[\(stamp)]** okay.\n> 好。\n\n")
    }
}
