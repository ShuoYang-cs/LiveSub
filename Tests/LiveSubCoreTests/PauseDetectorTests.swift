import Testing
@testable import LiveSubCore

@Suite struct PauseDetectorTests {
    /// 每 0.1 秒送一段音频。
    func feed(_ detector: inout PauseDetector, level: Float, from start: Double, seconds: Double) {
        var t = start
        while t < start + seconds - 1e-9 {
            t += 0.1
            detector.addAudio(level: level, audioTime: t)
        }
    }

    @Test func silenceAfterSpeechIsAPause() {
        var d = PauseDetector()
        d.reset(now: 0)
        feed(&d, level: 0.002, from: 0, seconds: 1)   // 背景噪声
        feed(&d, level: 0.08, from: 1, seconds: 3)    // 说话
        d.updateText("Good morning everyone", now: 4)
        #expect(!d.isPaused(audioTime: 4, now: 4))
        feed(&d, level: 0.002, from: 4, seconds: 0.8)
        #expect(!d.isPaused(audioTime: 4.8, now: 4.8))
        feed(&d, level: 0.002, from: 4.8, seconds: 0.5)
        #expect(d.isPaused(audioTime: 5.3, now: 5.3))
        let range = d.speechRange
        #expect(range.map { abs($0.start - 1.1) < 1e-6 && abs($0.end - 4.0) < 1e-6 } == true)
    }

    @Test func noTextMeansNoPause() {
        var d = PauseDetector()
        d.reset(now: 0)
        feed(&d, level: 0.08, from: 0, seconds: 1)
        feed(&d, level: 0, from: 1, seconds: 3)
        #expect(!d.isPaused(audioTime: 4, now: 4))
    }

    /// 说完后识别器还在改写，字数不再增长：按“文字停滞”算停顿。
    @Test func rewritesWithoutGrowthCountAsStall() {
        var d = PauseDetector()
        d.reset(now: 0)
        d.updateText("on older versions of the system first, the audio", now: 1)
        d.updateText("on older versions of the system. First, the audio", now: 1.6)
        d.updateText("on older versions of the system first the audio", now: 2.2)
        #expect(!d.isPaused(audioTime: 2.4, now: 2.4))
        #expect(d.isPaused(audioTime: 2.6, now: 2.6))
        d.updateText("on older versions of the system first the audio is", now: 2.7)
        #expect(!d.isPaused(audioTime: 2.8, now: 2.8))
    }

    /// 持续的背景声（例如空调、背景音乐）会被当成噪声，不会一直算作有人说话。
    @Test func steadyBackgroundBecomesNoiseFloor() {
        var d = PauseDetector()
        d.reset(now: 0)
        feed(&d, level: 0.02, from: 0, seconds: 1)
        feed(&d, level: 0.1, from: 1, seconds: 2)
        d.updateText("hello there", now: 3)
        feed(&d, level: 0.02, from: 3, seconds: 1.2)
        #expect(d.isPaused(audioTime: 4.2, now: 3.5))
    }

    @Test func resetKeepsNoiseFloorButClearsSpeech() {
        var d = PauseDetector()
        d.reset(now: 0)
        feed(&d, level: 0.08, from: 0, seconds: 1)
        d.updateText("hello", now: 1)
        d.reset(now: 1)
        #expect(d.speechRange == nil)
        #expect(!d.isPaused(audioTime: 5, now: 5))
    }
}
