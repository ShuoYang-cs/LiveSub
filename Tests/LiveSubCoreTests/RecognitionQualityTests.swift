import Testing
@testable import LiveSubCore

@Suite struct RecognitionQualityTests {
    @Test func clearSpeechStaysClear() {
        var m = RecognitionQualityMonitor()
        let changed = m.add(Array(repeating: 0.93, count: 30))
        #expect(!changed)
        #expect(!m.isDifficult)
    }

    @Test func accentedSpeechTurnsOnCorrection() {
        var m = RecognitionQualityMonitor()
        let tooFew = m.add(Array(repeating: 0.72, count: 10))  // 词太少，先不判断
        #expect(!tooFew)
        let changed = m.add(Array(repeating: 0.74, count: 15))
        #expect(changed)
        #expect(m.isDifficult)
    }

    @Test func hysteresisAvoidsFlipping() {
        var m = RecognitionQualityMonitor()
        m.add(Array(repeating: 0.7, count: 40))
        #expect(m.isDifficult)
        // 0.83 介于两个阈值之间：保持难识别
        m.add(Array(repeating: 0.83, count: 40))
        #expect(m.isDifficult)
        let recovered = m.add(Array(repeating: 0.92, count: 40))
        #expect(recovered)
        #expect(!m.isDifficult)
    }

    @Test func timelineSplitsOneFinalIntoTwoSegments() {
        var t = SegmentTimeline()
        t.addFinal("aaaa bbbb", start: 10, end: 18)   // 8 个非空白字符
        let first = t.take("aaaa")
        let second = t.take("bbbb")
        #expect(first?.start == 10 && first?.end == 14)
        #expect(second?.start == 14 && second?.end == 18)
        let none = t.take("more")
        #expect(none == nil)
    }

    @Test func timelineMergesTwoFinalsIntoOneSegment() {
        var t = SegmentTimeline()
        t.addFinal("Okay.", start: 1, end: 2)
        t.addFinal("Let's start.", start: 3, end: 5)
        let merged = t.take("Okay. Let's start.")
        #expect(merged?.start == 1 && merged?.end == 5)
    }

    @Test func srtAndVttFormatting() {
        let cues = [
            SubtitleExport.Cue(start: 3661.5, end: 3663, text: "第二句"),
            SubtitleExport.Cue(start: 0.25, end: 0.5, text: "第一句\nFirst"),
            SubtitleExport.Cue(start: 5, end: 6, text: "  "),
        ]
        #expect(SubtitleExport.srt(cues) == """
        1
        00:00:00,250 --> 00:00:01,250
        第一句
        First

        2
        01:01:01,500 --> 01:01:03,000
        第二句

        """)
        #expect(SubtitleExport.vtt(cues).hasPrefix("WEBVTT\n\n00:00:00.250 --> 00:00:01.250\n第一句\nFirst\n"))
    }

    @Test func cuesDoNotOverlap() {
        let tidy = SubtitleExport.tidy([
            .init(start: 0, end: 5, text: "a"),
            .init(start: 2, end: 3, text: "b"),
        ])
        #expect(tidy[0].end == 2)
    }
}
