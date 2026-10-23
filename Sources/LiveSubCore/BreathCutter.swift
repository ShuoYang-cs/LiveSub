import Foundation

/// 讲者一口气说很长时，识别器要等停顿才确认整段话，长句的前半段要等十几秒才能翻译
/// （2026-10-22 实测一句 45 个词的长句，说完前半句到进入翻译 10.5 秒）。
///
/// 这里从音量里找讲者换气时的短暂回落：当前这段已经说了 `minWords` 个词、超过 `minDuration` 秒还没确认，
/// 下一次回落就返回切分时间，识别引擎把那一刻之前的临时文字提前确认，前半句先去翻译。换气多在从句之间，不会切断单词。
///
/// 时间都是音频时间（秒，从开始送音频算起）。
public struct BreathCutter: Sendable {
    public struct Config: Sendable {
        /// 这一段临时文字至少这么多个词才考虑切分。
        public var minWords = 12
        /// 这一段至少说了这么久才考虑切分。
        public var minDuration: Double = 3
        /// 音量回落至少持续这么久才算换气（字与字之间的间隙更短）。
        public var dipDuration: Double = 0.15
        /// 低于最近说话音量的这个比例算回落。
        public var dipRatio: Float = 0.3
        /// 低于这个音量一律算安静（数字静音时为 0）。
        public var minimumLevel: Float = 0.003
        /// 切分后等识别器给出确认结果；超过这么久没给就允许再切。
        public var pendingTimeout: Double = 3
        public init() {}
    }

    public var config: Config
    /// 最近有人说话时的音量（只在说话时更新）。
    private var speechLevel: Float = 0
    private var quietStart: Double?
    private var words = 0
    private var utteranceStart: Double?
    private var cutAt: Double?

    public init(config: Config = Config()) {
        self.config = config
    }

    /// 识别器给出新的临时文字：`start` 是这段还没确认的音频从哪里开始。
    public mutating func updateVolatile(words: Int, start: Double) {
        self.words = words
        utteranceStart = start
    }

    /// 识别器给出确认结果：开始新的一段。
    public mutating func finalReceived() {
        words = 0
        utteranceStart = nil
        cutAt = nil
    }

    /// 送入一块音频的音量（均方根）；`end` 是这块音频结束时的音频时间。返回要切分的音频时间，不切时为 nil。
    public mutating func addAudio(level: Float, duration: Double, end: Double) -> Double? {
        let threshold = max(speechLevel * config.dipRatio, config.minimumLevel)
        guard level < threshold else {
            // 说话：慢慢跟随音量变化，单个响亮的词不会把门槛一下抬太高。
            speechLevel = speechLevel == 0 ? level : speechLevel + (level - speechLevel) * 0.1
            quietStart = nil
            return nil
        }
        let start = quietStart ?? end - duration
        quietStart = start
        guard end - start >= config.dipDuration else { return nil }
        if let cut = cutAt, end - cut < config.pendingTimeout { return nil }
        guard words >= config.minWords, let utteranceStart, start - utteranceStart >= config.minDuration else { return nil }
        // 切在回落刚开始不久：前一个词已经说完，下一个词还没开始。
        let cut = start + config.dipDuration / 2
        cutAt = cut
        return cut
    }

    /// 换气时要提前确认的那部分临时文字：到最后一个从句标点为止。
    ///
    /// 识别器约每 0.9 秒处理一块音频，换气后的一两个词常常也在覆盖换气时刻的那块里（“…edges, and why”），
    /// 只认“最后一个字是标点”时这种换气全被放过（2026-10-22 实测）。标点后面最多留 `maxTrailingWords` 个词给下一段；
    /// 没有标点时不确认：最后一个词可能还没说完（“respon”），多半是句末，识别器自己马上会确认整句。
    /// 返回确认的部分，以及标点后面还有没有词。
    public static func clause(in text: String, maxTrailingWords: Int = 4) -> (text: String, hasTrailing: Bool)? {
        guard let end = text.lastIndex(where: { ",.?!;:".contains($0) }) else { return nil }
        let head = String(text[...end])
        let trailing = text[text.index(after: end)...].split(whereSeparator: \.isWhitespace).count
        guard trailing <= maxTrailingWords, !head.trimmingCharacters(in: .whitespaces).isEmpty else { return nil }
        return (head, trailing > 0)
    }
}
