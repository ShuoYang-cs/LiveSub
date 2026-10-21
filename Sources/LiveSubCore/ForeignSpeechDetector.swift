import Foundation

/// 识别结果里的一个词（或一个汉字），时间以识别器音频时间轴的秒数计。
public struct TimedWord: Equatable, Sendable {
    public var text: String
    public var start: Double
    public var end: Double
    public var confidence: Double

    public init(text: String, start: Double, end: Double, confidence: Double = 1) {
        self.text = text
        self.start = start
        self.end = end
        self.confidence = confidence
    }

    var mid: Double { (start + end) / 2 }
    /// 纯标点没有语言信息，不参与判断。
    var hasLetters: Bool { text.unicodeScalars.contains { CharacterSet.letters.contains($0) } }
    var isCJK: Bool { text.unicodeScalars.contains(where: isCJKScalar) }
}

func isCJKScalar(_ s: Unicode.Scalar) -> Bool {
    switch s.value {
    case 0x3400...0x4DBF, 0x4E00...0x9FFF, 0xF900...0xFAFF, 0x20000...0x2FA1F: true
    default: false
    }
}

/// 判断一段声音是不是“另一种语言”（典型场景：英语课上旁边同学用中文聊天）。
///
/// 做法：在主识别器（如英语）之外，并行跑一个中文识别器当“哨兵”。实测：
/// - 中文识别器听到中文时输出汉字，听到英语（包括口音很重的英语）时输出拼凑的拉丁字母——
///   所以“哨兵在这段时间里输出的是不是汉字”能区分两种语言；
/// - 英语识别器把中文硬听成英语时置信度偏低，但老师口音重时置信度也会低，所以置信度只作辅助：
///   主识别器很有把握的词一律保留。
///
/// 只有哨兵的**确认结果**带逐词时间；临时结果是一整段文字，只用来隐藏“正在说”那一行的乱码。
public struct ForeignSpeechDetector: Sendable {
    public struct Config: Sendable {
        /// 一个词前后这段时间内，哨兵输出的汉字时长占比达到这个值，就认为这个词其实是中文。
        public var minForeignShare: Double = 0.5
        /// 判断时把词的时间范围向两边各扩这么多秒（汉字之间有空隙）。
        public var padding: Double = 0.25
        /// 一段连续“像中文”的词，平均置信度高于这个值时仍然保留（主识别器听得很清楚）。
        public var keepAboveConfidence: Double = 0.75
        /// 哨兵临时结果里汉字占比达到这个值，才等哨兵确认后再判断。
        public var waitForeignShare: Double = 0.3
        public init() {}
    }

    public var config: Config
    /// 哨兵确认结果里的词。
    public private(set) var guardWords: [TimedWord] = []
    /// 哨兵确认结果已经覆盖到的音频时刻。
    public private(set) var coveredUntil: Double = 0
    /// 哨兵最新的临时结果中汉字所占比例。
    public private(set) var volatileForeignShare: Double = 0

    public init(config: Config = Config()) {
        self.config = config
    }

    /// 哨兵识别器报告了一个结果，覆盖 `start..<end` 这段时间。
    public mutating func updateGuard(start: Double, end: Double, words: [TimedWord], isFinal: Bool) {
        guard isFinal else {
            volatileForeignShare = Self.foreignShare(ofText: words.map(\.text).joined(separator: " "))
            return
        }
        volatileForeignShare = 0
        guardWords.removeAll { $0.mid >= start && $0.mid < end }
        guardWords.append(contentsOf: words.filter(\.hasLetters))
        guardWords.sort { $0.start < $1.start }
        coveredUntil = max(coveredUntil, end)
        // 只需要保留最近一段时间。
        guardWords.removeAll { $0.end < coveredUntil - 120 }
    }

    /// 哨兵确认结果中，与 `start..<end` 重叠的部分里汉字所占的时长比例；哨兵没听到东西时返回 nil。
    public func foreignShare(start: Double, end: Double) -> Double? {
        var total = 0.0, foreign = 0.0
        for w in guardWords where w.end > start && w.start < end {
            let overlap = min(w.end, end) - max(w.start, start)
            total += overlap
            if w.isCJK { foreign += overlap }
        }
        return total > 0 ? foreign / total : nil
    }

    /// 主识别器的确认结果里，哪些词其实是另一种语言（true = 应该去掉）。
    public func foreignMask(_ words: [TimedWord]) -> [Bool] {
        let candidate = words.map { w -> Bool in
            guard w.end > w.start else { return false }
            let share = foreignShare(start: w.start - config.padding, end: w.end + config.padding) ?? 0
            return share >= config.minForeignShare
        }
        // 连续的候选词作为一段整体判断：主识别器对这段很有把握就保留。
        var mask = Array(repeating: false, count: words.count)
        var i = 0
        while i < words.count {
            guard candidate[i] else { i += 1; continue }
            var j = i
            while j < words.count, candidate[j] { j += 1 }
            let confidence = words[i..<j].map(\.confidence).reduce(0, +) / Double(j - i)
            if confidence < config.keepAboveConfidence {
                for k in i..<j { mask[k] = true }
            }
            i = j
        }
        return mask
    }

    /// 主识别器确认了一句话（说到 `time` 为止），要不要等哨兵也确认到这里再判断。等待最多 1.5 秒，
    /// 实测会拖慢连续讲话时的字幕，所以只在确实可能是中文时才等：
    /// - 这句话每个词的置信度都不低于 `keepAboveConfidence`：`foreignMask` 本来就不会删它，不用等；
    /// - 哨兵听英语时偶尔冒出一两个汉字，汉字占比不到 `waitForeignShare` 时也不等。
    public func shouldWaitForGuard(until time: Double, lowestConfidence: Double = 0) -> Bool {
        guard lowestConfidence < config.keepAboveConfidence else { return false }
        return coveredUntil < time && volatileForeignShare >= config.waitForeignShare
    }

    /// “正在说”的临时文本要不要先藏起来：哨兵的临时结果以汉字为主，且主识别器没把握。
    public func volatileLooksForeign(confidence: Double) -> Bool {
        volatileForeignShare >= 0.6 && confidence < config.keepAboveConfidence
    }

    /// 一段文字中汉字的比重：每个汉字算 1 份，每个拉丁字母单词算 1.5 份（大致按说话时长）。
    static func foreignShare(ofText text: String) -> Double {
        var cjk = 0.0, latin = 0.0
        var inWord = false
        for s in text.unicodeScalars {
            if isCJKScalar(s) {
                cjk += 1
                inWord = false
            } else if CharacterSet.letters.contains(s) {
                if !inWord { latin += 1.5 }
                inWord = true
            } else {
                inWord = false
            }
        }
        return cjk + latin > 0 ? cjk / (cjk + latin) : 0
    }
}
