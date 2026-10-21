import Testing
@testable import LiveSubCore

@Suite struct ForeignSpeechDetectorTests {
    /// 把一串词按每个 `step` 秒排在时间轴上。
    func words(_ texts: [String], from start: Double, step: Double = 0.3, confidence: Double = 0.9) -> [TimedWord] {
        texts.enumerated().map { i, t in
            TimedWord(text: t, start: start + Double(i) * step, end: start + Double(i + 1) * step, confidence: confidence)
        }
    }

    @Test func chineseChatterIsFiltered() {
        var d = ForeignSpeechDetector()
        // 中文哨兵：12–15 秒听到汉字
        d.updateGuard(start: 12, end: 15, words: words(["你", "听", "懂", "了", "吗", "我", "觉", "得"], from: 12.2), isFinal: true)
        // 英语识别器把它硬听成英语，置信度很低
        let primary = words(["I", "mean,", "to", "tell", "them", "out."], from: 12.3, step: 0.4, confidence: 0.38)
        #expect(d.foreignMask(primary) == Array(repeating: true, count: 6))
    }

    @Test func onlyTheChinesePartOfAMixedResultIsDropped() {
        var d = ForeignSpeechDetector()
        d.updateGuard(start: 0, end: 6, words: words(["爱", "你", "听", "懂", "了", "吗"], from: 0, step: 0.4)
            + words(["So", "I", "share", "this", "lame"], from: 3, step: 0.5), isFinal: true)
        let primary = words(["you", "have", "sugar."], from: 0.2, step: 0.6, confidence: 0.4)
            + words(["So", "I", "will", "share", "this", "link."], from: 3, step: 0.4, confidence: 0.85)
        #expect(d.foreignMask(primary) == [true, true, true, false, false, false, false, false, false])
    }

    @Test func accentedEnglishIsKeptWhenGuardSeesLatinLetters() {
        var d = ForeignSpeechDetector()
        // 中文识别器听口音重的英语时输出拼凑的拉丁字母
        d.updateGuard(start: 0, end: 6, words: words(["OKay", "so", "theday", "win", "are", "going"], from: 0.1, step: 0.8), isFinal: true)
        let primary = words(["Okay,", "so", "today", "we", "are", "going"], from: 0.1, step: 0.8, confidence: 0.4)
        #expect(!d.foreignMask(primary).contains(true))
    }

    @Test func confidentEnglishIsAlwaysKept() {
        var d = ForeignSpeechDetector()
        // 即使哨兵误把老师的英语听成汉字，主识别器很有把握时也保留
        d.updateGuard(start: 0, end: 3, words: words(["哦", "给", "所", "以"], from: 0), isFinal: true)
        let primary = words(["Okay,", "so", "now"], from: 0, step: 0.4, confidence: 0.9)
        #expect(!d.foreignMask(primary).contains(true))
    }

    @Test func nothingFromGuardMeansKeep() {
        let d = ForeignSpeechDetector()
        let primary = words(["uh", "so"], from: 5, confidence: 0.3)
        #expect(!d.foreignMask(primary).contains(true))
    }

    @Test func volatileGuardResultsDoNotTouchTheTimeline() {
        var d = ForeignSpeechDetector()
        // 临时结果是一整段没有逐词时间的文字，不能拿来逐词判断
        d.updateGuard(start: 0, end: 80, words: [TimedWord(text: "爱你听懂了吗", start: 0, end: 80)], isFinal: false)
        #expect(d.foreignShare(start: 0, end: 80) == nil)
        #expect(d.coveredUntil == 0)
        #expect(d.volatileLooksForeign(confidence: 0.4))
        #expect(!d.volatileLooksForeign(confidence: 0.9))
        // 确认结果到来后，临时结果的判断清零
        d.updateGuard(start: 0, end: 2, words: words(["okay", "so"], from: 0, step: 1), isFinal: true)
        #expect(!d.volatileLooksForeign(confidence: 0.4))
        #expect(d.foreignShare(start: 0, end: 2) == 0)
        #expect(d.coveredUntil == 2)
    }

    @Test func waitsForGuardOnlyWhenChineseIsAround() {
        var d = ForeignSpeechDetector()
        d.updateGuard(start: 0, end: 5, words: [TimedWord(text: "So the solution", start: 0, end: 5)], isFinal: false)
        #expect(!d.shouldWaitForGuard(until: 4))
        d.updateGuard(start: 0, end: 5, words: [TimedWord(text: "So 这个题目", start: 0, end: 5)], isFinal: false)
        #expect(d.shouldWaitForGuard(until: 4))
        d.updateGuard(start: 0, end: 5, words: words(["So", "the", "这", "个"], from: 0, step: 1), isFinal: true)
        #expect(!d.shouldWaitForGuard(until: 4))
    }

    @Test func strayCharactersOrConfidentEnglishDoNotWait() {
        var d = ForeignSpeechDetector()
        // 哨兵听英语时冒出一个汉字：占比太低，不等
        d.updateGuard(start: 0, end: 5, words: [TimedWord(text: "we keep a table of answers 的", start: 0, end: 5)], isFinal: false)
        #expect(!d.shouldWaitForGuard(until: 4))
        // 汉字很多，但这句英语每个词都很有把握：过滤本来就不会删，不等
        d.updateGuard(start: 0, end: 5, words: [TimedWord(text: "这个题目我没看懂", start: 0, end: 5)], isFinal: false)
        #expect(!d.shouldWaitForGuard(until: 4, lowestConfidence: 0.9))
        #expect(d.shouldWaitForGuard(until: 4, lowestConfidence: 0.5))
    }

    @Test func punctuationIsIgnored() {
        var d = ForeignSpeechDetector()
        d.updateGuard(start: 0, end: 2, words: words(["好", "，", "？", "。"], from: 0, step: 0.5), isFinal: true)
        #expect(d.foreignShare(start: 0, end: 2) == 1)
    }

    @Test func textShareCountsCharactersAgainstWords() {
        #expect(ForeignSpeechDetector.foreignShare(ofText: "这个题目") == 1)
        #expect(ForeignSpeechDetector.foreignShare(ofText: "OKay so theday") == 0)
        #expect(ForeignSpeechDetector.foreignShare(ofText: "") == 0)
    }
}
