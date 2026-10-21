import Foundation
import Testing
@testable import LiveSubCore

@Suite struct SegmenterTests {
    let t0 = Date(timeIntervalSince1970: 1_000_000)

    @Test func completeSentenceIsEmittedImmediately() {
        var s = Segmenter()
        let out = s.addFinal("Today we're going to talk about eigenvalues.", at: t0)
        #expect(out == ["Today we're going to talk about eigenvalues."])
        #expect(s.pending.isEmpty)
    }

    @Test func multipleSentencesInOneFinalAreSplit() {
        var s = Segmenter()
        let out = s.addFinal("An eigenvector only gets scaled. So A times v equals lambda v. And then", at: t0)
        #expect(out == ["An eigenvector only gets scaled.", "So A times v equals lambda v."])
        #expect(s.pending == "And then")
    }

    @Test func shortSentencesAreMergedWithFollowing() {
        var s = Segmenter()
        #expect(s.addFinal("Okay.", at: t0).isEmpty)
        let out = s.addFinal("Let's get started with today's topic.", at: t0)
        #expect(out == ["Okay. Let's get started with today's topic."])
    }

    @Test func decimalsAndAbbreviationsDoNotSplit() {
        var s = Segmenter()
        let out = s.addFinal("Pi is about 3.14, e.g. in circles, as Dr. Smith said.", at: t0)
        #expect(out == ["Pi is about 3.14, e.g. in circles, as Dr. Smith said."])
    }

    @Test func idleFlushSendsRemainder() {
        var s = Segmenter(config: .init(minUnits: 4, maxUnits: 32, idleFlush: 1.5))
        #expect(s.addFinal("so the next thing we need", at: t0).isEmpty)
        #expect(s.tick(at: t0.addingTimeInterval(1.0), speechInProgress: false).isEmpty)
        #expect(s.tick(at: t0.addingTimeInterval(1.6), speechInProgress: false) == ["so the next thing we need"])
        #expect(s.pending.isEmpty)
    }

    @Test func idleFlushWaitsLongerWhileSpeechContinues() {
        var s = Segmenter(config: .init(minUnits: 4, maxUnits: 32, idleFlush: 1.5))
        _ = s.addFinal("so the next thing we need", at: t0)
        #expect(s.tick(at: t0.addingTimeInterval(2.0), speechInProgress: true).isEmpty)
        #expect(s.tick(at: t0.addingTimeInterval(4.6), speechInProgress: true).count == 1)
    }

    @Test func longRunOnIsSplitAtComma() {
        var s = Segmenter(config: .init(minUnits: 4, maxUnits: 12, idleFlush: 1.5))
        let out = s.addFinal("we take the matrix and we subtract lambda times the identity, and then we compute the determinant and set it to zero and", at: t0)
        #expect(out.first == "we take the matrix and we subtract lambda times the identity,")
        #expect(Segmenter.units(s.pending) <= 12)
    }

    @Test func longCompleteSentenceIsSplitAtComma() {
        var s = Segmenter(config: .init(minUnits: 4, maxUnits: 20, idleFlush: 1.5))
        let out = s.addFinal("Today we are going to talk about eigenvalues and eigenvectors, an eigenvector of the matrix is a nonzero vector that only gets scaled.", at: t0)
        #expect(out == ["Today we are going to talk about eigenvalues and eigenvectors,",
                        "an eigenvector of the matrix is a nonzero vector that only gets scaled."])
        #expect(s.pending.isEmpty)
    }

    @Test func chineseSentencesSplitWithoutSpaces() {
        var s = Segmenter(config: .init(minUnits: 3, maxUnits: 40, idleFlush: 1.5))
        let out = s.addFinal("今天我们来讲特征值。特征向量只会被缩放。然后", at: t0)
        #expect(out == ["今天我们来讲特征值。", "特征向量只会被缩放。"])
        #expect(s.pending == "然后")
    }

    @Test func unitsCountsWordsAndCJK() {
        #expect(Segmenter.units("hello world") == 2)
        #expect(Segmenter.units("特征值") == 2)
        #expect(Segmenter.units("") == 0)
    }
}

@Suite struct ClaudeStreamTests {
    @Test func parsesTextDelta() {
        let line = #"{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"今天"}}}"#
        #expect(ClaudeStream.parse(line: line) == .textDelta("今天"))
    }

    @Test func parsesErrorResult() {
        let line = #"{"type":"result","subtype":"success","is_error":true,"result":"Not logged in · Please run /login"}"#
        #expect(ClaudeStream.parse(line: line) == .result(text: "Not logged in · Please run /login", isError: true))
    }

    @Test func parsesSuccessResultAndInit() {
        #expect(ClaudeStream.parse(line: #"{"type":"result","subtype":"success","is_error":false,"result":"你好"}"#) == .result(text: "你好", isError: false))
        #expect(ClaudeStream.parse(line: #"{"type":"system","subtype":"init","model":"claude-haiku-5-5"}"#) == .initialized(model: "claude-haiku-5-5"))
        #expect(ClaudeStream.parse(line: "not json") == nil)
    }

    @Test func userMessageIsSingleLineJSON() throws {
        let line = ClaudeStream.userMessageLine("line1\nline2 \"q\"")
        #expect(line.hasSuffix("\n"))
        #expect(line.dropLast().contains("\n") == false)
        let obj = try JSONSerialization.jsonObject(with: Data(line.utf8)) as! [String: Any]
        let msg = obj["message"] as! [String: Any]
        let content = msg["content"] as! [[String: Any]]
        #expect(content[0]["text"] as? String == "line1\nline2 \"q\"")
    }

    @Test func lineBufferHandlesSplitUTF8() {
        var buf = LineBuffer()
        let bytes = Array("你好\nworld\n".utf8)
        var lines: [String] = []
        lines += buf.append(Data(bytes[0..<2])) // 切在“你”的中间
        lines += buf.append(Data(bytes[2...]))
        #expect(lines == ["你好", "world"])
    }
}

@Suite struct PromptTests {
    @Test func contextMessage() {
        #expect(Prompts.translationMessage(text: "hi", context: []) == "hi")
        let m = Prompts.translationMessage(text: "next", context: [("a", "甲")])
        #expect(m == "[Context]\na\n→ 甲\n[Translate]\nnext")
    }

    @Test func systemPromptWithoutAccent() {
        let p = Prompts.translatorSystemPrompt(source: .source("en-US"), target: .target("zh-Hans"))
        #expect(p.contains("Simplified Chinese"))
        #expect(p.contains("Reply: 欢迎大家。"))
        #expect(p.contains("Reply: 大家都能看到黑板吗？"))
        #expect(p.contains("never answer"))
        #expect(!p.contains("Recognition is unreliable"))
        // 识别清晰时不要求输出纠正行（这条规则会让 Haiku 每句都犹豫、把思考过程写出来）。
        #expect(!p.contains(TranslationOutput.marker))
    }

    @Test func systemPromptAsksForCorrectionsWhenRecognitionIsHard() {
        let p = Prompts.translatorSystemPrompt(source: .source("en-US"), target: .target("zh-Hans"), correctMishearings: true)
        #expect(p.contains("Recognition is unreliable"))
        #expect(p.contains(TranslationOutput.marker))
    }
}

@Suite struct TranslationOutputTests {
    @Test func plainTranslation() {
        let out = TranslationOutput.split("所以解决办法是用动态规划。\n")
        #expect(out.translation == "所以解决办法是用动态规划。")
        #expect(out.correctedSource == nil)
    }

    @Test func translationWithCorrectedSource() {
        let out = TranslationOutput.split("所以解决办法是使用动态规划 (dynamic programming)。\n@@src: So the solution would be to use dynamic programming.")
        #expect(out.translation == "所以解决办法是使用动态规划 (dynamic programming)。")
        #expect(out.correctedSource == "So the solution would be to use dynamic programming.")
    }

    @Test func emptyCorrectionIsIgnored() {
        #expect(TranslationOutput.split("好的。\n@@src:  ").correctedSource == nil)
    }

    @Test func formatTagsLeakingIntoTranslationAreRemoved() {
        let raw = "[Translate] 的 — the fragment is unfinished, so I translate it as is.\n\n但这是一个拓扑序……"
        #expect(TranslationOutput.split(raw, targetIsCJK: true).translation == "但这是一个拓扑序……")
    }

    @Test func thinkingOutLoudAfterMarkerIsIgnored() {
        let original = "That is called edit distance."
        let raw = "这叫编辑距离（edit distance）。\n@@src: That is called edit distance.\n\nWait—I should check: nothing needed correcting, so I should not include this line."
        let out = TranslationOutput.split(raw, original: original, targetIsCJK: true)
        #expect(out.translation == "这叫编辑距离（edit distance）。")
        #expect(out.correctedSource == nil) // 和原文一样，不算纠正
    }

    @Test func implausibleCorrectionIsRejected() {
        let original = "use dynamic program."
        #expect(TranslationOutput.split("用动态规划。\n@@src: use dynamic programming.", original: original).correctedSource
                == "use dynamic programming.")
        let rambling = "用动态规划。\n@@src: use dynamic programming, which as I explained earlier in the lecture means storing results."
        #expect(TranslationOutput.split(rambling, original: original).correctedSource == nil)
    }

    @Test func englishOnlyLinesAreKeptForNonCJKTargetsAndShortTokens() {
        // 目标语言不是中日韩文时不按“没有汉字”过滤；短的英文（网址、代码）也保留。
        #expect(TranslationOutput.split("Let me think about this sentence.", targetIsCJK: false).translation
                == "Let me think about this sentence.")
        #expect(TranslationOutput.split("sourceforge.net\n网址如上", targetIsCJK: true).translation == "sourceforge.net\n网址如上")
    }

    @Test func partialHidesMarker() {
        #expect(TranslationOutput.visibleTranslation("最小生成树") == "最小生成树")
        #expect(TranslationOutput.visibleTranslation("最小生成树。\n@") == "最小生成树。")
        #expect(TranslationOutput.visibleTranslation("最小生成树。\n@@sr") == "最小生成树。")
        #expect(TranslationOutput.visibleTranslation("最小生成树。\n@@src: minimum spa") == "最小生成树。")
        #expect(TranslationOutput.visibleTranslation("邮箱 a@b.com") == "邮箱 a@b.com")
        #expect(TranslationOutput.visibleTranslation("好的。\nWait, I should double check this", targetIsCJK: true) == "好的。")
    }
}
