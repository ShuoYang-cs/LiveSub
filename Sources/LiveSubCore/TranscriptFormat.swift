import Foundation

public enum TranscriptFormat {
    public static func header(title: String, started: Date, source: LanguageOption, target: LanguageOption) -> String {
        """
        # \(title)

        - \(NSLocalizedString("开始时间", comment: ""))：\(dateTimeFormatter.string(from: started))
        - \(NSLocalizedString("语言", comment: ""))：\(NSLocalizedString(source.displayName, comment: "")) → \(NSLocalizedString(target.displayName, comment: ""))


        """
    }

    /// `original`：口音纠正改过原文时，识别器原来给出的文字（用户打开“保留识别原文”才有）。
    public static func entry(time: Date, sourceLabel: String?, source: String, translation: String,
                             original: String? = nil) -> String {
        let label = sourceLabel.map { " \($0)" } ?? ""
        var out = "**[\(timeFormatter.string(from: time))]\(label)** \(source)\n"
        if let original, original != source {
            out += "*" + String(format: NSLocalizedString("识别原文：%@", comment: ""), original) + "*\n"
        }
        if !translation.isEmpty {
            out += "> \(translation.replacingOccurrences(of: "\n", with: "\n> "))\n"
        }
        return out + "\n"
    }

    /// 读回记录文件里的一句（字幕记录窗口看以前的记录用）。
    public struct Entry: Equatable, Sendable {
        /// 当天的第几秒（文件里只有“时:分:秒”）。
        public var seconds: Int
        public var label: String?
        public var source: String
        public var original: String?
        public var translation: String
    }

    /// 解析 `entry` 写出的格式；认不出的行（标题、开始时间等）跳过。
    public static func parse(_ text: String) -> [Entry] {
        var entries: [Entry] = []
        // “识别原文：”按写文件时的界面语言写，两种都认。
        let originalPrefixes = Set([String(format: NSLocalizedString("识别原文：%@", comment: ""), ""), "识别原文：", "Recognized: "])
        for raw in text.split(whereSeparator: \.isNewline) {
            let line = String(raw)
            if line.hasPrefix("**["), let close = line.firstIndex(of: "]"),
               let end = line.range(of: "** ", range: close..<line.endIndex) {
                let parts = line[line.index(line.startIndex, offsetBy: 3)..<close].split(separator: ":").compactMap { Int($0) }
                guard parts.count == 3 else { continue }
                let label = line[line.index(after: close)..<end.lowerBound].trimmingCharacters(in: .whitespaces)
                entries.append(Entry(seconds: parts[0] * 3600 + parts[1] * 60 + parts[2], label: label.isEmpty ? nil : label,
                                     source: String(line[end.upperBound...]), original: nil, translation: ""))
            } else if !entries.isEmpty, line.hasPrefix("> ") || line == ">" {
                let text = line.dropFirst(line.hasPrefix("> ") ? 2 : 1)
                let last = entries.count - 1
                entries[last].translation += (entries[last].translation.isEmpty ? "" : "\n") + text
            } else if !entries.isEmpty, line.count > 2, line.hasPrefix("*"), !line.hasPrefix("**"), line.hasSuffix("*") {
                var inner = String(line.dropFirst().dropLast())
                if let prefix = originalPrefixes.first(where: { !$0.isEmpty && inner.hasPrefix($0) }) { inner.removeFirst(prefix.count) }
                entries[entries.count - 1].original = inner
            }
        }
        return entries
    }

    /// 给 Claude 生成笔记用的纯文本记录。
    public static func plain(_ items: [(time: Date, source: String, translation: String)]) -> String {
        items.map { "[\(timeFormatter.string(from: $0.time))] \($0.source)\n    \($0.translation)" }.joined(separator: "\n")
    }

    public static let timeFormatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "HH:mm:ss"
        return f
    }()

    public static let dateTimeFormatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd HH:mm"
        return f
    }()

    public static let fileStampFormatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd HH.mm"
        return f
    }()
}
