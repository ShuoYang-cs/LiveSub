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
