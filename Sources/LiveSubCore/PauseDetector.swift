import Foundation

/// 旧版识别器（SFSpeechRecognizer）不会逐句给出确认结果，要靠这个判断讲者停顿了，再结束当前这一段。
///
/// 两个信号满足任意一个就算停顿：
/// - 声音：连续 `silence` 秒音量都在背景噪声附近。背景噪声随环境自动估计；
/// - 文字：识别出的字数 `stall` 秒没有增长。说完以后识别器还会反复改写整段文字
///   （例如在 “system. First” 和 “system first” 之间来回改），只看文字有没有变化，永远等不到停顿。
///
/// 时间都用秒：`audioTime` 是已送入识别器的音频时长，`now` 是挂钟时间。
public struct PauseDetector: Sendable {
    public var silence: Double = 1.0
    public var stall: Double = 1.5
    /// 低于这个音量一律算安静（数字静音时背景噪声为 0）。
    public var minimumLevel: Float = 0.003
    /// 音量超过背景噪声的这个倍数才算有人说话。
    public var voiceRatio: Float = 3

    private var noiseFloor: Float?
    private var firstVoice: Double?
    private var lastVoice: Double?
    private var maxLength = 0
    private var lastGrowth: Double = 0

    public init() {}

    /// 开始新的一段。背景噪声的估计保留下来。
    public mutating func reset(now: Double) {
        firstVoice = nil
        lastVoice = nil
        maxLength = 0
        lastGrowth = now
    }

    /// 送入一段音频的音量（均方根），`audioTime` 是这段音频结束时的音频时间。
    public mutating func addAudio(level: Float, audioTime: Double) {
        let floor = noiseFloor ?? level
        // 变安静时立刻跟上，变吵时慢慢上升：持续的背景声会逐渐被当成噪声，说话中的字间停顿会把它拉回来。
        noiseFloor = level < floor ? level : floor + (level - floor) * 0.005
        if level > max(floor * voiceRatio, minimumLevel) {
            if firstVoice == nil { firstVoice = audioTime }
            lastVoice = audioTime
        }
    }

    /// 识别器给出新的临时结果。
    public mutating func updateText(_ text: String, now: Double) {
        let length = text.unicodeScalars.filter { CharacterSet.alphanumerics.contains($0) }.count
        if length > maxLength {
            maxLength = length
            lastGrowth = now
        }
    }

    /// 这一段已经有文字，并且讲者停下来了。
    public func isPaused(audioTime: Double, now: Double) -> Bool {
        guard maxLength > 0 else { return false }
        if let lastVoice, audioTime - lastVoice > silence { return true }
        return now - lastGrowth > stall
    }

    /// 这一段里有人说话的音频时间范围，用来给没有逐词时间的确认结果定起止时间。
    public var speechRange: (start: Double, end: Double)? {
        guard let firstVoice, let lastVoice else { return nil }
        return (firstVoice, lastVoice)
    }
}
