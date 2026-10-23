import Foundation

/// 旧版识别器在一段话里只给出“从这一段开始到现在”的整段临时文字，要等讲者停顿才确认。
/// 讲者连续说话时，把已经稳定下来的完整句子提前确认，免得第一句要等整段说完才翻译。
///
/// 做法：到某个句末标点为止的词连续 `stableFor` 秒没有变化（忽略大小写和标点，识别器会在
/// “system. First” 和 “system first” 之间来回改），并且最新的临时文字里这个标点还在、
/// 后面也已经有了新词（这句肯定说完了），就确认到这个标点为止的句子。标点出现在末尾时就开始计时。之后的临时文字和确认结果只取
/// 后面没确认的部分；识别器改写已确认部分导致词数变化时，用已确认部分的最后几个词重新对齐。
public struct PartialCommitter: Sendable {
    public var stableFor: Double = 1.0

    /// 已确认的词数（按最近一次临时文字计）。
    public private(set) var committedWords = 0
    private var committedTail: [String] = []
    private var candidate: Candidate?
    private var latestStart = 0

    private struct Candidate: Sendable {
        var key: [String]
        var text: String
        var since: Double
        var sinceAudio: Double
        /// 最近一次临时文字里，这句末尾还有句末标点。
        var hasBoundary = true
        /// 最近一次临时文字里，这句后面已经有词。
        var hasFollowing = false
    }

    public init() {}

    /// 新的临时文字。返回还没确认的部分（用于实时显示）。
    public mutating func update(_ text: String, now: Double, audioTime: Double) -> String {
        let words = Self.words(text)
        let start = alignedStart(words)
        latestStart = start
        let rest = Array(words[start...])
        let display = rest.joined(separator: " ")
        let keys = rest.map(Self.normalize)
        let lastBoundary = rest.indices.last(where: { Self.endsSentence(rest[$0]) })
        if let c = candidate, keys.starts(with: c.key) {
            let hasBoundary = Self.endsSentence(rest[c.key.count - 1])
            // 候选句子的词都还在：继续按原来的时间计时。句末标点被改掉时先不确认，等它改回来；
            // 后面又出现了别的句末标点，说明两句其实是一句，改为等后面这个标点。
            if hasBoundary || lastBoundary.map({ $0 < c.key.count }) ?? true {
                candidate?.text = rest.prefix(c.key.count).joined(separator: " ")
                candidate?.hasBoundary = hasBoundary
                candidate?.hasFollowing = keys.count > c.key.count
                return display
            }
        }
        guard let boundary = lastBoundary else {
            candidate = nil
            return display
        }
        let key = Array(keys[...boundary])
        let text = rest[...boundary].joined(separator: " ")
        // 候选里后一句被改掉了：前面这句从原来的时间起就是稳定的。
        let previous = candidate.flatMap { $0.key.starts(with: key) ? $0 : nil }
        candidate = Candidate(key: key, text: text, since: previous?.since ?? now,
                              sinceAudio: previous?.sinceAudio ?? audioTime,
                              hasFollowing: boundary < rest.count - 1)
        return display
    }

    /// 候选句子已经稳定足够久时确认它。返回句子文字，以及它大致说完时的音频时间。
    public mutating func commitIfStable(now: Double) -> (text: String, audioTime: Double)? {
        guard let c = candidate, c.hasBoundary, c.hasFollowing, now - c.since >= stableFor, !c.key.isEmpty else { return nil }
        candidate = nil
        committedWords = latestStart + c.key.count
        committedTail = Array((committedTail + c.key).suffix(3))
        return (c.text, c.sinceAudio)
    }

    /// 讲者换气时，把这段临时文字里还没确认的词全部确认，返回这些词；没有新词时为 nil。
    /// macOS 26 的识别器临时文字没有标点，等不到句末标点，长句只能在换气处这样提前确认（`BreathCutter`）。
    public mutating func commitAll(_ text: String) -> String? {
        let words = Self.words(text)
        let start = alignedStart(words)
        guard start < words.count else { return nil }
        let rest = Array(words[start...])
        candidate = nil
        committedWords = words.count
        committedTail = Array((committedTail + rest.map(Self.normalize)).suffix(3))
        return rest.joined(separator: " ")
    }

    /// 一段结束时，确认结果（或最后的临时文字）里还没确认的部分。
    public func remainder(of text: String) -> String {
        let words = Self.words(text)
        return words[alignedStart(words)...].joined(separator: " ")
    }

    // MARK: - 对齐与分词

    /// 新文字里，已确认部分在第几个词结束。
    private func alignedStart(_ words: [String]) -> Int {
        let expected = min(committedWords, words.count)
        guard !committedTail.isEmpty else { return expected }
        let keys = words.map(Self.normalize)
        // 在预期位置附近找已确认部分的最后几个词，离预期位置越近越优先；
        // 改写可能正好落在最后几个词里，找不到就少比一个词。
        for k in stride(from: committedTail.count, through: 1, by: -1) {
            let tail = Array(committedTail.suffix(k))
            for offset in [0, -1, 1, -2, 2, -3, 3] {
                let end = expected + offset
                guard end >= k, end <= keys.count else { continue }
                if Array(keys[(end - k)..<end]) == tail { return end }
            }
        }
        return expected
    }

    static func words(_ text: String) -> [String] {
        text.split(whereSeparator: \.isWhitespace).map(String.init)
    }

    static func normalize(_ word: String) -> String {
        String(word.lowercased().unicodeScalars.filter { CharacterSet.alphanumerics.contains($0) })
    }

    static func endsSentence(_ word: String) -> Bool {
        guard let last = word.last else { return false }
        return ".?!。？！".contains(last)
    }
}
