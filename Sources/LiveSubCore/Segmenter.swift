import Foundation

/// 把语音识别的“确认结果”切成适合翻译的句子片段。
///
/// 规则：
/// - 完整句子（以 . ? ! 。？！ 等结尾）且长度达到 `minUnits` 立即送出；过短的句子与后续合并，减少请求次数。
/// - 未完成的句子超过 `maxUnits` 时，在最后一个逗号/分号处切开（找不到就硬切），避免一句话等太久。
/// - 一段时间没有新的确认结果（老师停顿）时，把剩余内容全部送出。
public struct Segmenter: Sendable {
    public struct Config: Sendable {
        /// 句子最少多少“单位”才单独送出（英文按单词，中日韩按每 2 个字算 1 个单位）。
        public var minUnits: Int
        /// 未完成句子的最大长度，超过就强制切分。
        public var maxUnits: Int
        /// 距离上一次确认结果多久后，把剩余内容送出。
        public var idleFlush: TimeInterval

        public init(minUnits: Int = 4, maxUnits: Int = 24, idleFlush: TimeInterval = 1.6) {
            self.minUnits = minUnits
            self.maxUnits = maxUnits
            self.idleFlush = idleFlush
        }
    }

    public var config: Config
    public private(set) var pending: String = ""
    private var lastFinalAt: Date?

    public init(config: Config = Config()) {
        self.config = config
    }

    /// 送入一段确认的识别文本，返回可以送去翻译的片段。
    public mutating func addFinal(_ text: String, at now: Date = Date()) -> [String] {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return [] }
        pending = Self.join(pending, trimmed)
        lastFinalAt = now
        return drainCompleteSentences()
    }

    /// 定时调用；`speechInProgress` 为 true 表示当前还有未确认的语音，此时不做超时送出。
    public mutating func tick(at now: Date = Date(), speechInProgress: Bool) -> [String] {
        guard !pending.isEmpty, let last = lastFinalAt else { return [] }
        let idle = now.timeIntervalSince(last)
        // 有语音仍在识别时多等一会儿，但不能无限等（老师一直讲不停）。
        let limit = speechInProgress ? config.idleFlush * 3 : config.idleFlush
        guard idle >= limit else { return [] }
        return flushAll()
    }

    /// 全部送出；太长时同样在从句边界切成几条，字幕不会一条占满整个窗口。
    public mutating func flushAll() -> [String] {
        var rest = pending.trimmingCharacters(in: .whitespacesAndNewlines)
        pending = ""
        lastFinalAt = nil
        var output: [String] = []
        while Self.units(rest) > config.maxUnits {
            let (head, tail) = Self.splitLong(rest, maxUnits: config.maxUnits)
            guard !head.isEmpty, !tail.isEmpty else { break }
            output.append(head)
            rest = tail
        }
        if !rest.isEmpty { output.append(rest) }
        return output
    }

    // MARK: - 内部

    private mutating func drainCompleteSentences() -> [String] {
        var output: [String] = []
        var sentences = Self.splitSentences(pending)
        // 最后一个片段如果没有句末标点，则保留为未完成部分。
        var tail = ""
        if let last = sentences.last, !Self.endsSentence(last) {
            tail = last
            sentences.removeLast()
        }

        var carry = ""
        for sentence in sentences {
            carry = Self.join(carry, sentence)
            if Self.units(carry) >= config.minUnits {
                // 识别器常把好几句用逗号连成一个长句，字幕太长不好读，在逗号处再切。
                while Self.units(carry) > config.maxUnits {
                    let (head, rest) = Self.splitLong(carry, maxUnits: config.maxUnits)
                    guard !head.isEmpty, !rest.isEmpty else { break }
                    output.append(head)
                    carry = rest
                }
                output.append(carry)
                carry = ""
            }
        }
        // 过短的完整句并入未完成部分，等后续内容一起送出。
        tail = Self.join(carry, tail)

        while Self.units(tail) > config.maxUnits {
            let (head, rest) = Self.splitLong(tail, maxUnits: config.maxUnits)
            output.append(head)
            tail = rest
        }
        pending = tail
        return output
    }

    public static func join(_ a: String, _ b: String) -> String {
        if a.isEmpty { return b }
        if b.isEmpty { return a }
        // 中日韩文字之间不加空格。
        if let l = a.unicodeScalars.last, let f = b.unicodeScalars.first, isCJK(l) || isCJK(f) {
            return a + b
        }
        return a + " " + b
    }

    static let terminators: Set<Character> = [".", "?", "!", "。", "？", "！", "…"]
    static let closers: Set<Character> = ["\"", "'", "”", "’", ")", "）", "」", "』"]
    static let abbreviations: Set<String> = [
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc", "e.g", "i.e", "fig", "eq", "no", "approx", "inc", "ltd", "co", "u.s", "u.k", "a.m", "p.m",
    ]

    static func endsSentence(_ s: String) -> Bool {
        var t = s.trimmingCharacters(in: .whitespaces)
        while let last = t.last, closers.contains(last) { t.removeLast() }
        guard let last = t.last, terminators.contains(last) else { return false }
        return !(last == "." && endsWithAbbreviation(t))
    }

    static func endsWithAbbreviation(_ t: String) -> Bool {
        // t 以 "." 结尾；取最后一个词（去掉末尾句点）判断。
        let word = t.dropLast().split(whereSeparator: { $0 == " " }).last.map(String.init)?.lowercased() ?? ""
        // 不把单个字母当缩写：课堂上 "equals lambda v." 这种以变量结尾的句子远比人名缩写常见，
        // 而误切出的 "J." 太短会被并入下一句，代价很小。
        return abbreviations.contains(word)
    }

    /// 按句末标点切分，标点保留在句子末尾。小数点（3.14）和缩写不切。
    static func splitSentences(_ text: String) -> [String] {
        var result: [String] = []
        var current = ""
        let chars = Array(text)
        var i = 0
        while i < chars.count {
            let c = chars[i]
            current.append(c)
            if terminators.contains(c) {
                // 吞掉连续标点和右引号/括号
                while i + 1 < chars.count, terminators.contains(chars[i + 1]) || closers.contains(chars[i + 1]) {
                    i += 1
                    current.append(chars[i])
                }
                let next: Character? = i + 1 < chars.count ? chars[i + 1] : nil
                let isCJKStop = c == "。" || c == "？" || c == "！"
                let boundary = next == nil || next!.isWhitespace || isCJKStop
                if boundary && !(c == "." && endsWithAbbreviation(current.trimmingCharacters(in: .whitespaces))) {
                    let s = current.trimmingCharacters(in: .whitespaces)
                    if !s.isEmpty { result.append(s) }
                    current = ""
                }
            }
            i += 1
        }
        let rest = current.trimmingCharacters(in: .whitespaces)
        if !rest.isEmpty { result.append(rest) }
        return result
    }

    /// 把过长的未完成句在 maxUnits 之内的最后一个从句边界处切开。
    static func splitLong(_ text: String, maxUnits: Int) -> (String, String) {
        let chars = Array(text)
        var unitsSoFar = 0.0
        var lastBreak: Int?
        var hardCut = chars.count
        var inWord = false
        for (idx, c) in chars.enumerated() {
            if c.unicodeScalars.contains(where: isCJK) {
                unitsSoFar += 0.5
                inWord = false
            } else if c.isWhitespace {
                inWord = false
            } else if !inWord {
                inWord = true
                unitsSoFar += 1
            }
            if Int(unitsSoFar) > maxUnits {
                hardCut = idx
                break
            }
            if [",", ";", ":", "，", "；", "：", "、"].contains(c), Double(maxUnits) * 0.3 < unitsSoFar {
                lastBreak = idx + 1
            }
        }
        var cut = lastBreak ?? hardCut
        if lastBreak == nil {
            // 硬切时退回到上一个空格，避免切断单词。
            var j = cut
            while j > 0, !chars[j - 1].isWhitespace, !chars[j - 1].unicodeScalars.contains(where: isCJK) { j -= 1 }
            if j > 0 { cut = j }
        }
        let head = String(chars[..<cut]).trimmingCharacters(in: .whitespaces)
        let rest = String(chars[cut...]).trimmingCharacters(in: .whitespaces)
        return (head, rest)
    }

    /// 长度单位：英文等按单词计；中日韩每 2 个字计 1 个单位。
    public static func units(_ text: String) -> Int {
        var cjk = 0
        var words = 0
        var inWord = false
        for scalar in text.unicodeScalars {
            if isCJK(scalar) {
                cjk += 1
                inWord = false
            } else if CharacterSet.alphanumerics.contains(scalar) {
                if !inWord { words += 1 }
                inWord = true
            } else {
                inWord = false
            }
        }
        return words + (cjk + 1) / 2
    }

    static func isCJK(_ s: Unicode.Scalar) -> Bool {
        switch s.value {
        case 0x3040...0x30FF, 0x3400...0x4DBF, 0x4E00...0x9FFF, 0xAC00...0xD7AF, 0xF900...0xFAFF, 0xFF01...0xFF60:
            return true
        default:
            return false
        }
    }
}
