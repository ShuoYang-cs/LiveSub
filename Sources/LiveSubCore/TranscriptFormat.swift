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
