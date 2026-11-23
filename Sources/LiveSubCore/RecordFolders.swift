import Foundation

/// 每次记录一个文件夹（2026-11-23 用户要求）：
///
///     记录文件夹/2026-11-23 13.07/
///         2026-11-23 13.07 字幕记录.md
///         2026-11-23 13.07 AI 助手对话.md
///         2026-11-23 13.07 录音.m4a
///         2026-11-23 13.07 笔记.md
///
/// 文件夹名是开始时间；里面的文件名保留时间前缀，拷出去也认得出是哪一次。
/// 以前的版本把这些文件平铺在记录文件夹里，`migrationPlan` 把它们按时间搬进各自的文件夹。
public enum RecordFolders {
    /// 本次记录的文件夹名：开始时间；同一分钟里已经有一次了就加序号（“2026-11-23 13.07 2”）。
    public static func folderName(stamp: String, exists: (String) -> Bool) -> String {
        guard exists(stamp) else { return stamp }
        var n = 2
        while exists("\(stamp) \(n)") { n += 1 }
        return "\(stamp) \(n)"
    }

    /// 文件名开头的“年-月-日 时.分”，后面要跟空格。
    public static func stamp(of fileName: String) -> String? {
        guard let range = fileName.range(of: #"^\d{4}-\d{2}-\d{2} \d{2}\.\d{2}(?= )"#, options: .regularExpression)
        else { return nil }
        return String(fileName[range])
    }

    /// 一次搬动：把记录文件夹里的 `file` 搬进子文件夹 `folder`。
    public struct Move: Equatable, Sendable {
        public var file: String
        public var folder: String
        public init(file: String, folder: String) {
            self.file = file
            self.folder = folder
        }
    }

    /// 旧版平铺的文件要怎么搬。
    ///
    /// - `files`：记录文件夹最上层的文件名（不含子文件夹）。
    /// - `keywords`：记录文件名里的词（“字幕记录”“录音”“笔记”，英文界面写的是 Transcript、Record、Notes）。
    ///   只搬“时间开头、带这些词”的文件，用户自己放进来的别的文件不动。
    /// - `existsInFolder`：子文件夹里是不是已经有同名文件（有就不搬，不覆盖）。
    ///
    /// 同一时间开头的文件进同一个文件夹；没有字幕记录的（比如只剩录音）也照样搬。
    public static func migrationPlan(files: [String], keywords: [String],
                                     existsInFolder: (_ folder: String, _ file: String) -> Bool) -> [Move] {
        files.sorted().compactMap { file in
            guard let stamp = stamp(of: file) else { return nil }
            let rest = file.dropFirst(stamp.count)
            guard keywords.contains(where: { rest.contains($0) }), !existsInFolder(stamp, file) else { return nil }
            return Move(file: file, folder: stamp)
        }
    }
}

/// AI 助手对话记录：每答完一问追加一段，崩溃时已经答完的不丢。
/// 同一次记录里点了“新对话”，在同一个文件里用分隔线隔开。
public enum AssistantLog {
    public static func header(title: String) -> String {
        "# \(title)\n"
    }

    /// 新对话的分隔：文件里已经有前一个对话时写。
    public static func separator(time: Date) -> String {
        "\n---\n\n" + String(format: NSLocalizedString("**新对话 %@**", comment: ""), timeFormatter.string(from: time)) + "\n"
    }

    /// 一问一答。`answer` 是界面上显示的回答（要保存的文件块已经去掉）；`files` 是这次存下的文件名。
    public static func entry(time: Date, question: String, attachments: [String], answer: String,
                             files: [String] = [], failed: Bool = false) -> String {
        var out = "\n### [\(timeFormatter.string(from: time))] "
        out += question.isEmpty ? NSLocalizedString("（只发了附件）", comment: "") : quoteFree(question)
        out += "\n"
        if !attachments.isEmpty {
            out += "\n" + String(format: NSLocalizedString("附件：%@", comment: ""),
                                 attachments.joined(separator: NSLocalizedString("、", comment: ""))) + "\n"
        }
        let body = answer.trimmingCharacters(in: .whitespacesAndNewlines)
        if failed {
            out += "\n*" + String(format: NSLocalizedString("回答失败：%@", comment: ""), body) + "*\n"
        } else if !body.isEmpty {
            out += "\n" + body + "\n"
        }
        if !files.isEmpty {
            out += "\n" + String(format: NSLocalizedString("已保存文件：%@", comment: ""),
                                 files.joined(separator: NSLocalizedString("、", comment: ""))) + "\n"
        }
        return out
    }

    /// 问题写在标题行里：多行问题合成一行，免得后面的行被当成正文。
    private static func quoteFree(_ question: String) -> String {
        question.split(whereSeparator: \.isNewline).map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }.joined(separator: " ")
    }

    private static let timeFormatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "HH:mm:ss"
        return f
    }()
}
