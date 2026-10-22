import Foundation

/// AI 助手：一个长驻的 Claude 对话，提问时把上次提问以来新增的字幕一起带上。
/// 不在每句字幕到来时都发给 Claude——那样每句都会触发一次回答，白白消耗额度。
public enum AssistantPrompt {
    public static func system(source: LanguageOption, target: LanguageOption) -> String {
        """
        You are an assistant helping the user follow live speech: a lecture, meeting, video, talk or conversation. The user's app streams you the transcript: each message starts with the transcript lines that are new since the previous message (live speech recognition in \(source.promptName), with local times), then the user's question.

        The transcript comes from speech recognition: expect sound-alike words, broken sentences and missing punctuation. Infer what the speaker actually said.

        When answering:
        - Answer in \(target.promptName). Be short and direct (usually 2–6 sentences or a short list): the user is reading while still listening.
        - Most questions are about the last few minutes. Connect the answer to what the speaker just said and the examples used; keep key \(source.promptName) terms in parentheses.
        - If the transcript is too garbled, say briefly what you think was said, then answer the likely question.
        - Plain text with light Markdown only (bold, inline code, simple lists). No headings, no tables.
        - Never repeat the transcript back unless asked.

        Saving files: you cannot write files yourself, but the app saves them for you. When the user asks you to save, export or organize something into a file or folder, put each file at the end of your answer in this exact form, one block per file:
        \(AssistantFiles.startMarker) <short file name>
        <the complete file content in Markdown; headings, lists and tables are fine here>
        \(AssistantFiles.endMarker)
        The app saves the files into the user's LiveSub "AI 助手" folder and shows their names. Outside the blocks write at most one short sentence; do not repeat the file content, do not wrap the blocks in code fences, and never give shell commands. Only use these blocks when the user asks to save something.
        """
    }

    /// 一句字幕。
    public struct Line: Equatable, Sendable {
        public var time: Date
        public var text: String
        public init(time: Date, text: String) {
            self.time = time
            self.text = text
        }
    }

    /// 发给 Claude 的一条消息：新增字幕 + 正在说的半句 + 问题。
    public static func message(newLines: [Line], speaking: String, question: String) -> String {
        var parts: [String] = []
        if !newLines.isEmpty {
            let range = newLines.count == 1
                ? time(newLines[0].time)
                : "\(time(newLines[0].time))–\(time(newLines[newLines.count - 1].time))"
            var block = ["[字幕 · 新增 \(newLines.count) 句 · \(range)]"]
            block += newLines.map { "[\(time($0.time))] \($0.text)" }
            parts.append(block.joined(separator: "\n"))
        }
        let speaking = speaking.trimmingCharacters(in: .whitespacesAndNewlines)
        if !speaking.isEmpty {
            parts.append("[正在说（还没说完）]\n\(speaking)")
        }
        parts.append("[我的问题]\n\(question.trimmingCharacters(in: .whitespacesAndNewlines))")
        return parts.joined(separator: "\n\n")
    }

    /// 面板里的快捷问题。
    public static let quickQuestions = ["总结最近的内容", "解释相关概念", "解释上一句"]

    private static func time(_ date: Date) -> String { TranscriptFormat.timeFormatter.string(from: date) }
}

/// AI 助手把内容存成文件：模型不能自己写文件（Claude Code 以无工具模式运行，API 和本地模型也没有工具），
/// 所以让它在回答里用约定的块写出文件名和内容，由 App 代为保存，并且只写进记录文件夹下的“AI 助手”文件夹。
public enum AssistantFiles {
    public static let startMarker = "@@save:"
    public static let endMarker = "@@end"

    public struct File: Equatable, Sendable {
        public var name: String
        public var content: String
        public init(name: String, content: String) {
            self.name = name
            self.content = content
        }
    }

    public struct Parsed: Equatable, Sendable {
        /// 块外的文字：显示在面板里。
        public var text: String
        public var files: [File]
        /// 最后一个文件还没写完（流式输出中，或者回答被截断）。
        public var writing: Bool
    }

    /// 拆出回答里要保存的文件。没有结束标记的块算到回答末尾（回答被截断时也保存已经写出的部分）。
    public static func parse(_ reply: String) -> Parsed {
        var text: [String] = []
        var files: [File] = []
        var current: (name: String, lines: [String], fenced: Bool)?
        /// 刚结束的块是被包在代码块里的：跳过紧跟着的代码块结束标记。
        var closingFence = false
        for line in reply.components(separatedBy: "\n") {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if var file = current {
                if trimmed == endMarker {
                    files.append(File(name: file.name, content: trimmedBlankLines(file.lines)))
                    closingFence = file.fenced
                    current = nil
                } else {
                    file.lines.append(line)
                    current = file
                }
            } else if trimmed.hasPrefix(startMarker) {
                // 模型偶尔还是会把块包进代码块：去掉紧挨着的开头标记，记住要去掉对应的结束标记。
                let fenced = text.last?.trimmingCharacters(in: .whitespaces).hasPrefix("```") == true
                if fenced { text.removeLast() }
                current = (String(trimmed.dropFirst(startMarker.count)).trimmingCharacters(in: .whitespaces), [], fenced)
            } else if closingFence, trimmed.isEmpty {
                continue
            } else if closingFence, trimmed.hasPrefix("```") {
                closingFence = false
            } else {
                closingFence = false
                text.append(line)
            }
        }
        if let file = current {
            files.append(File(name: file.name, content: trimmedBlankLines(file.lines)))
        }
        var visible = text.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
        while visible.contains("\n\n\n") { visible = visible.replacingOccurrences(of: "\n\n\n", with: "\n\n") }
        return Parsed(text: visible, files: files, writing: current != nil)
    }

    /// 保存用的文件名：只留最后一段（不能带路径），去掉系统不允许的字符，限制长度，统一用 .md；
    /// 名字不以日期开头时加上“年-月-日 时.分”前缀，文件夹里按时间排序。
    public static func fileName(for name: String, stamp: String, fallback: String) -> String {
        var base = name.components(separatedBy: CharacterSet(charactersIn: "/\\")).last ?? ""
        base = String(base.unicodeScalars.filter { !CharacterSet(charactersIn: ":*?\"<>|").contains($0) && !CharacterSet.controlCharacters.contains($0) })
        base = base.split(whereSeparator: \.isWhitespace).joined(separator: " ")
        for ext in [".md", ".markdown", ".txt"] where base.lowercased().hasSuffix(ext) {
            base = String(base.dropLast(ext.count))
        }
        base = base.trimmingCharacters(in: CharacterSet(charactersIn: " ."))
        if base.count > 80 { base = String(base.prefix(80)).trimmingCharacters(in: CharacterSet(charactersIn: " .")) }
        if base.isEmpty { base = fallback }
        if base.range(of: #"^\d{4}-\d{2}-\d{2}"#, options: .regularExpression) == nil { base = "\(stamp) \(base)" }
        return base + ".md"
    }

    /// 不覆盖已有文件：重名时在扩展名前加“ 2”“ 3”……
    public static func uniqueName(_ name: String, exists: (String) -> Bool) -> String {
        guard exists(name) else { return name }
        let stem = name.hasSuffix(".md") ? String(name.dropLast(3)) : name
        var n = 2
        while exists("\(stem) \(n).md") { n += 1 }
        return "\(stem) \(n).md"
    }

    private static func trimmedBlankLines(_ lines: [String]) -> String {
        var lines = lines
        while let first = lines.first, first.trimmingCharacters(in: .whitespaces).isEmpty { lines.removeFirst() }
        while let last = lines.last, last.trimmingCharacters(in: .whitespaces).isEmpty { lines.removeLast() }
        return lines.joined(separator: "\n")
    }
}
