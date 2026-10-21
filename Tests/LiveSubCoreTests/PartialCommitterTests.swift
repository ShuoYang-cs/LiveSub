import Testing
@testable import LiveSubCore

@Suite struct PartialCommitterTests {
    @Test func commitsStableSentenceWhileSpeakerContinues() {
        var c = PartialCommitter()
        #expect(c.update("Good morning everyone", now: 1, audioTime: 1) == "Good morning everyone")
        _ = c.update("Good morning everyone. Today we", now: 2, audioTime: 2)
        #expect(c.commitIfStable(now: 2.5) == nil)
        _ = c.update("Good morning everyone. Today we will talk", now: 2.6, audioTime: 2.6)
        let committed = c.commitIfStable(now: 3.1)
        #expect(committed?.text == "Good morning everyone.")
        #expect(committed?.audioTime == 2)
        #expect(c.update("Good morning everyone. Today we will talk about", now: 3.2, audioTime: 3.2) == "Today we will talk about")
        #expect(c.remainder(of: "Good morning, everyone. Today we will talk about speech.") == "Today we will talk about speech.")
    }

    @Test func sentenceAtTheVeryEndIsLeftToPauseDetection() {
        var c = PartialCommitter()
        _ = c.update("Does that make sense?", now: 0, audioTime: 0)
        #expect(c.commitIfStable(now: 5) == nil)
    }

    /// 句号先出现在末尾，之后识别器把它改掉又改回来：从句号第一次出现时计时。
    @Test func timerStartsWhenBoundaryFirstAppearsAtTheEnd() {
        var c = PartialCommitter()
        _ = c.update("Recognition works on older versions of the system.", now: 7.4, audioTime: 7.4)
        _ = c.update("Recognition works on older versions of the system first", now: 8.3, audioTime: 8.3)
        #expect(c.commitIfStable(now: 9) == nil)
        _ = c.update("Recognition works on older versions of the system. First, the audio", now: 10, audioTime: 10)
        let committed = c.commitIfStable(now: 10)
        #expect(committed?.text == "Recognition works on older versions of the system.")
        #expect(committed?.audioTime == 7.4)
    }

    /// 先以为是一句话的结尾，后来发现是同一句话：改为等后面的句末标点，不会一直卡住。
    @Test func mergedSentencesMoveCandidateForward() {
        var c = PartialCommitter()
        _ = c.update("Hello there.", now: 0, audioTime: 0)
        _ = c.update("Hello there everyone. Today", now: 0.5, audioTime: 0.5)
        #expect(c.commitIfStable(now: 1.2) == nil)
        _ = c.update("Hello there everyone. Today we", now: 1.3, audioTime: 1.3)
        #expect(c.commitIfStable(now: 1.6)?.text == "Hello there everyone.")
    }

    /// 识别器在 “system. First” 和 “system first” 之间来回改：计时不重置，标点在的时候确认。
    @Test func punctuationFlipFlopKeepsTimer() {
        var c = PartialCommitter()
        _ = c.update("It works on the system. First the audio", now: 0, audioTime: 0)
        _ = c.update("It works on the system first the audio is", now: 0.6, audioTime: 0.6)
        #expect(c.commitIfStable(now: 1.2) == nil)  // 现在没有句号
        _ = c.update("It works on the system. First, the audio is split", now: 1.3, audioTime: 1.3)
        #expect(c.commitIfStable(now: 1.3)?.text == "It works on the system.")
    }

    @Test func changedWordsRestartTimer() {
        var c = PartialCommitter()
        _ = c.update("We use dynamic program. Then", now: 0, audioTime: 0)
        _ = c.update("We use dynamic programming. Then we", now: 0.8, audioTime: 0.8)
        #expect(c.commitIfStable(now: 1.2) == nil)
        #expect(c.commitIfStable(now: 1.8)?.text == "We use dynamic programming.")
    }

    /// 已确认部分后来被改写、词数变了：靠最后几个词重新对齐，不重复也不丢词。
    @Test func realignsWhenCommittedPartIsRewritten() {
        var c = PartialCommitter()
        _ = c.update("The audio is split in the short segments. Then each", now: 0, audioTime: 0)
        #expect(c.commitIfStable(now: 1)?.text == "The audio is split in the short segments.")
        #expect(c.update("The audio is split into short segments. Then each segment", now: 1.2, audioTime: 1.2)
                == "Then each segment")
        #expect(c.remainder(of: "The audio is split into short segments, then each segment is sent.")
                == "then each segment is sent.")
    }

    @Test func commitsSeveralSentencesInTurn() {
        var c = PartialCommitter()
        _ = c.update("One. Two. Three", now: 0, audioTime: 0)
        #expect(c.commitIfStable(now: 1)?.text == "One. Two.")
        _ = c.update("One. Two. Three is here. Four", now: 1.1, audioTime: 1.1)
        #expect(c.commitIfStable(now: 1.5) == nil)
        #expect(c.commitIfStable(now: 2.2)?.text == "Three is here.")
        #expect(c.remainder(of: "One. Two. Three is here. Four five.") == "Four five.")
    }
}
