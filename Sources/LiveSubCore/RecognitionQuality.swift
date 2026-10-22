import Foundation

/// 根据识别器给每个词的置信度，自动判断“这位讲者是不是很难识别”（口音、语速快、噪音大）。
///
/// 实测：口音较重的讲课平均置信度约 0.73，清晰的英语 0.9 以上。判断为“难识别”时让 Claude 按发音纠正识别错误；
/// 用两个阈值（滞回）避免在临界值附近来回切换。
/// 进入快、退出慢：口音重的讲者中间常有几句说得清楚（2026-10-21 实测一节课里“清晰”多半只持续二十几秒），
/// 以前一清楚就退出，一个半小时来回切换 47 次，每次都要换翻译进程。现在要连续清楚 `exitAfter` 秒才退出；
/// 纠正多开一会儿几乎没有坏处。
/// 系统没有识别“哪种口音”的能力（需要专门的模型），而纠正识别错误也不需要知道口音名称。
public struct RecognitionQualityMonitor: Sendable {
    public struct Config: Sendable {
        /// 统计最近这么多个词（讲课约 25 秒的话）。一两句没认准不会马上判为难识别；
        /// 换成口音重的讲者时，大约 15 秒开始纠正。
        public var window = 60
        /// 至少有这么多个词才开始判断。
        public var minWords = 20
        /// 平均置信度低于它：判定为难识别。
        public var enterBelow = 0.80
        /// 难识别状态下平均置信度高于它：开始计时，准备恢复为清晰。
        public var exitAbove = 0.86
        /// 平均置信度连续这么多秒都高于 `exitAbove`，才恢复为清晰；中间有一次不够高就重新计时。
        public var exitAfter: TimeInterval = 60
        public init() {}
    }

    public var config: Config
    public private(set) var isDifficult = false
    private var confidences: [Double] = []
    /// 难识别状态下，从什么时候起平均置信度一直高于 `exitAbove`。
    private var clearSince: Date?

    public init(config: Config = Config()) {
        self.config = config
    }

    /// 最近的平均置信度；词太少时为 nil。
    public var averageConfidence: Double? {
        guard confidences.count >= config.minWords else { return nil }
        return confidences.reduce(0, +) / Double(confidences.count)
    }

    /// 加入一句确认结果里各个词的置信度。返回 true 表示“难识别”状态变了。
    @discardableResult
    public mutating func add(_ wordConfidences: [Double], at now: Date = Date()) -> Bool {
        confidences.append(contentsOf: wordConfidences)
        if confidences.count > config.window { confidences.removeFirst(confidences.count - config.window) }
        guard let average = averageConfidence else { return false }
        if !isDifficult {
            guard average < config.enterBelow else { return false }
            isDifficult = true
            clearSince = nil
            return true
        }
        guard average > config.exitAbove else {
            clearSince = nil
            return false
        }
        let since = clearSince ?? now
        clearSince = since
        guard now.timeIntervalSince(since) >= config.exitAfter else { return false }
        isDifficult = false
        clearSince = nil
        return true
    }
}

/// 把分句后的字幕片段对回音频时间。
///
/// 识别器给的每段确认结果有起止时间；分句器会把它们合并、切开。这里按“非空白字符数”记账：
/// 每段确认结果的字符均匀分布在它的时间范围里，分出来的片段按顺序消耗字符，换算出起止时间。
public struct SegmentTimeline: Sendable {
    private struct Span: Sendable {
        var chars: Int
        var start: Double
        var end: Double
    }

    private var spans: [Span] = []
    /// 第一段里已经被消耗的字符数。
    private var consumed = 0

    public init() {}

    public mutating func addFinal(_ text: String, start: Double, end: Double) {
        let chars = Self.count(text)
        guard chars > 0 else { return }
        spans.append(Span(chars: chars, start: start, end: max(end, start)))
    }

    /// 取出一个片段的起止时间（音频时间轴上的秒数）；没有时间信息时返回 nil。
    public mutating func take(_ segment: String) -> (start: Double, end: Double)? {
        var remaining = Self.count(segment)
        guard remaining > 0, !spans.isEmpty else { return nil }
        let start = time(atOffset: consumed, in: spans[0])
        var end = start
        while remaining > 0, !spans.isEmpty {
            let available = spans[0].chars - consumed
            let used = min(available, remaining)
            consumed += used
            remaining -= used
            end = time(atOffset: consumed, in: spans[0])
            if consumed >= spans[0].chars {
                spans.removeFirst()
                consumed = 0
            }
        }
        return (start, end)
    }

    private func time(atOffset offset: Int, in span: Span) -> Double {
        span.start + (span.end - span.start) * Double(offset) / Double(span.chars)
    }

    private static func count(_ text: String) -> Int {
        text.unicodeScalars.filter { !CharacterSet.whitespacesAndNewlines.contains($0) }.count
    }
}

/// 导出 SRT / WebVTT 字幕文件。时间是相对录音开始的秒数。
public enum SubtitleExport {
    public struct Cue: Equatable, Sendable {
        public var start: Double
        public var end: Double
        public var text: String
        public init(start: Double, end: Double, text: String) {
            self.start = start
            self.end = end
            self.text = text
        }
    }

    public static func srt(_ cues: [Cue]) -> String {
        tidy(cues).enumerated().map { i, cue in
            "\(i + 1)\n\(timestamp(cue.start, separator: ",")) --> \(timestamp(cue.end, separator: ","))\n\(cue.text)\n"
        }.joined(separator: "\n")
    }

    public static func vtt(_ cues: [Cue]) -> String {
        "WEBVTT\n\n" + tidy(cues).map { cue in
            "\(timestamp(cue.start, separator: ".")) --> \(timestamp(cue.end, separator: "."))\n\(cue.text)\n"
        }.joined(separator: "\n")
    }

    /// 按时间排序；每条至少显示 1 秒，但不和下一条重叠；去掉空字幕。
    static func tidy(_ cues: [Cue]) -> [Cue] {
        var sorted = cues.filter { !$0.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            .map { Cue(start: max(0, $0.start), end: max(0, $0.end), text: $0.text) }
            .sorted { $0.start < $1.start }
        for i in sorted.indices {
            var end = max(sorted[i].end, sorted[i].start + 1)
            if i + 1 < sorted.count { end = min(end, sorted[i + 1].start) }
            sorted[i].end = max(end, sorted[i].start + 0.1)
        }
        return sorted
    }

    static func timestamp(_ seconds: Double, separator: String) -> String {
        let ms = Int((seconds * 1000).rounded())
        let h = ms / 3_600_000, m = ms / 60_000 % 60, s = ms / 1000 % 60, f = ms % 1000
        return String(format: "%02d:%02d:%02d%@%03d", h, m, s, separator, f)
    }
}
