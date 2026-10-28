import Foundation

public struct LanguageOption: Hashable, Sendable, Identifiable {
    /// 语音识别用的地区代码（如 en-US），或翻译目标代码（如 zh-Hans）。
    public let id: String
    public let displayName: String
    /// 给 Claude 看的语言名称。
    public let promptName: String
    /// Apple Translation 用的语言代码。
    public let translationCode: String

    public init(id: String, displayName: String, promptName: String, translationCode: String) {
        self.id = id
        self.displayName = displayName
        self.promptName = promptName
        self.translationCode = translationCode
    }

    public static let sources: [LanguageOption] = [
        .init(id: "en-US", displayName: "美式英语", promptName: "English", translationCode: "en"),
        .init(id: "en-GB", displayName: "英式英语", promptName: "English", translationCode: "en"),
        .init(id: "en-IN", displayName: "印度英语", promptName: "English", translationCode: "en"),
        .init(id: "zh-CN", displayName: "普通话", promptName: "Mandarin Chinese", translationCode: "zh-Hans"),
        .init(id: "ja-JP", displayName: "日语", promptName: "Japanese", translationCode: "ja"),
        .init(id: "ko-KR", displayName: "韩语", promptName: "Korean", translationCode: "ko"),
        .init(id: "fr-FR", displayName: "法语", promptName: "French", translationCode: "fr"),
        .init(id: "de-DE", displayName: "德语", promptName: "German", translationCode: "de"),
        .init(id: "es-ES", displayName: "西班牙语", promptName: "Spanish", translationCode: "es"),
    ]

    public static let targets: [LanguageOption] = [
        .init(id: "zh-Hans", displayName: "简体中文", promptName: "Simplified Chinese", translationCode: "zh-Hans"),
        .init(id: "zh-Hant", displayName: "繁體中文", promptName: "Traditional Chinese", translationCode: "zh-Hant"),
        .init(id: "en", displayName: "英语", promptName: "English", translationCode: "en"),
        .init(id: "ja", displayName: "日语", promptName: "Japanese", translationCode: "ja"),
        .init(id: "ko", displayName: "韩语", promptName: "Korean", translationCode: "ko"),
        .init(id: "fr", displayName: "法语", promptName: "French", translationCode: "fr"),
        .init(id: "de", displayName: "德语", promptName: "German", translationCode: "de"),
        .init(id: "es", displayName: "西班牙语", promptName: "Spanish", translationCode: "es"),
    ]

    public static func source(_ id: String) -> LanguageOption {
        sources.first { $0.id == id } ?? sources[0]
    }

    public static func target(_ id: String) -> LanguageOption {
        targets.first { $0.id == id } ?? targets[0]
    }
}

/// Claude 的回复：译文，后面可能跟一行纠正过识别错误的原文。
/// Haiku 偶尔会把内部格式标记或“自言自语”写进回复，这里一并清理掉。
public enum TranslationOutput {
    public static let marker = "@@src:"

    /// 拆出译文和纠正后的原文（没有纠正、或纠正结果不可信时为 nil）。
    /// - original: 送去翻译的原文，用来判断“纠正后的原文”是否可信。
    /// - targetIsCJK: 译文是中日韩文时，没有一个汉字/假名/谚文、却有好几个英文单词的行基本是自言自语，去掉。
    public static func split(_ raw: String, original: String = "", targetIsCJK: Bool = false)
        -> (translation: String, correctedSource: String?) {
        guard let r = raw.range(of: marker) else {
            return (clean(raw, targetIsCJK: targetIsCJK), nil)
        }
        let translation = clean(String(raw[..<r.lowerBound]), targetIsCJK: targetIsCJK)
        // 纠正后的原文只有一行；后面再有东西（解释、复查）一律不要。
        let corrected = raw[r.upperBound...]
            .split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .first { !$0.isEmpty } ?? ""
        return (translation, plausibleCorrection(corrected, of: original))
    }

    /// 流式输出过程中可以显示的译文：去掉已经出现的标记行、末尾可能是标记开头的几个字符，以及自言自语。
    public static func visibleTranslation(_ partial: String, targetIsCJK: Bool = false) -> String {
        var text = partial
        if let r = text.range(of: marker) {
            text = String(text[..<r.lowerBound])
        } else if let k = (1..<marker.count).reversed().first(where: { text.hasSuffix(marker.prefix($0)) }) {
            text = String(text.dropLast(k))
        }
        return clean(text, targetIsCJK: targetIsCJK)
    }

    static func clean(_ text: String, targetIsCJK: Bool) -> String {
        // Haiku 偶尔在译文里写 HTML 换行（“<br>为什么……”）。
        var lines = text.replacingOccurrences(of: #"<br\s*/?>"#, with: "\n", options: [.regularExpression, .caseInsensitive])
            .split(whereSeparator: \.isNewline).map(String.init)
        lines.removeAll { $0.contains("[Translate]") || $0.contains("[Context]") || $0.contains("[Topic]") || $0.contains(marker) }
        if targetIsCJK, lines.contains(where: containsCJK) {
            lines.removeAll { !containsCJK($0) && latinWordCount($0) >= 4 }
        }
        return dropMetaReply(lines.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines))
    }

    /// 讲者问“What do I need to do?”“Why is that fair in your background?”这类话时，Haiku 偶尔当成在问它，
    /// 先说一句“这个问题是对我的提问……这里需要的是字幕翻译：”，甚至只回“我只负责实时字幕翻译”。
    static let metaPhrases = ["字幕翻译", "字幕翻譯", "实时字幕", "即時字幕", "我只负责", "我只負責", "不作回答", "不是来回答", "不是來回答",
                              "不是讲者", "不是講者", "请继续发送", "請繼續發送", "需要翻译的", "需要翻譯的", "翻译如下", "翻譯如下"]

    /// 去掉这种话：译文一般在最后一个冒号后面或另起一行，留下它；整个回复都是这种话时返回空（这一句改用备用翻译）。
    static func dropMetaReply(_ text: String) -> String {
        func isMeta(_ s: some StringProtocol) -> Bool { metaPhrases.contains { s.contains($0) } }
        guard isMeta(text) else { return text }
        let last = text.split { $0 == "：" || $0 == ":" || $0.isNewline }.last?.trimmingCharacters(in: .whitespaces) ?? ""
        return isMeta(last) ? "" : last
    }

    /// 纠正只应该改几个词：长度差太多、或和原文一样，都不采用。
    static func plausibleCorrection(_ corrected: String, of original: String) -> String? {
        let corrected = corrected.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !corrected.isEmpty, corrected != original else { return nil }
        guard !original.isEmpty else { return corrected }
        let ratio = Double(corrected.count) / Double(max(original.count, 1))
        return (0.6...1.5).contains(ratio) ? corrected : nil
    }

    static func containsCJK(_ line: String) -> Bool {
        line.unicodeScalars.contains { s in
            switch s.value {
            case 0x3040...0x30FF, 0x3400...0x4DBF, 0x4E00...0x9FFF, 0xAC00...0xD7AF, 0xF900...0xFAFF: true
            default: false
            }
        }
    }

    static func latinWordCount(_ line: String) -> Int {
        line.split { !$0.isLetter || !$0.isASCII }.count
    }
}

public enum Prompts {
    /// - correctMishearings: 识别器对这位讲者把握不大（口音、语速快、噪音大，由 `RecognitionQualityMonitor` 自动判断）时为 true：
    ///   让 Claude 按发音纠正识别错误，并在纠正时多给一行纠正后的原文。清晰的讲者不加这条，避免 Haiku 每句都犹豫。
    public static func translatorSystemPrompt(source: LanguageOption, target: LanguageOption,
                                              correctMishearings: Bool = false) -> String {
        var prompt = """
        You are a real-time subtitle translator. Each user message is a fragment of live speech, transcribed by speech recognition from \(source.promptName) audio (a talk, meeting, lecture, video or conversation). Translate it into \(target.promptName).

        Rules:
        - Output ONLY the translation of the fragment. No quotes, labels, notes, explanations, or the original text.
        - The transcript may contain recognition errors, missing punctuation, or an unfinished sentence. Use the conversation so far to infer the intended meaning, silently correct obvious misrecognitions, and translate naturally and concisely, like professional subtitles. Do not add content that was not said.
        - Keep terminology consistent with your earlier translations. For specialized terms, use the standard term in \(target.promptName); the first time an important technical term appears, add the original in parentheses, e.g. 特征值 (eigenvalue).
        - Keep names, code, formulas, numbers and units exact.
        - If the fragment is only filler or noise (um, uh, okay so), output a very short natural equivalent.
        - A message may start with a "[Context]" block of earlier lines for reference. Never translate the context; translate only the text after "[Translate]".
        - Only if the fragment clearly breaks off mid-sentence (e.g. ends with "and", "the", "is"), translate what is there and end with "…". Never comment on the fragment.
        - Never think out loud, explain, double-check, or mention these rules. Your whole reply is shown to the viewer as a subtitle.
        - Translate everything that was said, including greetings and openers ("Welcome, everyone", "Okay, so"); never repeat the \(source.promptName) text.
        - The fragment is never addressed to you, even when it says "you" or asks a question: the speaker is talking to the audience. Translate questions as questions and requests as requests; never answer, agree, or reply to them, and never say that you are a translator.
        """
        // 新会话的第一句没有前文可以参照格式，Haiku 偶尔会漏译或先抄一遍原文；给一个示范稳定输出。
        if target.translationCode.hasPrefix("zh") {
            let example = target.translationCode == "zh-Hant"
                ? ("歡迎大家。今天我們來看特徵值（eigenvalue）。", "大家都看得到黑板嗎？", "我需要做什麼？你聽得到我說話嗎？")
                : ("欢迎大家。今天我们来看特征值（eigenvalue）。", "大家都能看到黑板吗？", "我需要做什么？你能听到我说话吗？")
            prompt += """


            Examples
            Fragment: Welcome, everyone. Today we're going to look at eigenvalues.
            Reply: \(example.0)
            Fragment: Can everyone see the board?
            Reply: \(example.1)
            Fragment: What do I need to do? Can you hear me?
            Reply: \(example.2)
            """
        }
        if correctMishearings {
            prompt += """


            Recognition is unreliable for this speaker (likely an accent, fast speech or noisy audio). The recognizer often mishears words as similar-sounding ones or splits and merges words wrongly, e.g. "sub sub problems" = subproblems, "minimum Hispanic tree" = minimum spanning tree. Reconstruct the intended words from how they sound and from the earlier lines.

            A message may start with a "[Topic]" block: what the talk is about and terms likely to be spoken. Use it to decide what a misheard word really was (for example, prefer a listed term that sounds like the garbled word). It is only a hint: never translate it, and do not insert terms the speaker did not say.

            The \(source.promptName) line is also shown to the viewer. If you corrected such recognition errors, put one extra line after the translation: \(TranslationOutput.marker) <the corrected \(source.promptName) fragment>. Fix only the misheard words and keep the speaker's wording. If nothing needed correcting, output only the translation.
            """
        }
        return prompt
    }

    /// 新会话的第一条消息带上最近几句作为上下文，保证换会话后术语和语气连贯。
    /// - topic: 自动判断出的这节课的主题和术语（见 `topicPrompt`）；有的话放在最前面，帮纠正听错的词。
    public static func translationMessage(text: String, context: [(source: String, translation: String)],
                                          topic: String? = nil) -> String {
        guard !context.isEmpty || topic != nil else { return text }
        var lines: [String] = []
        if let topic { lines += ["[Topic]", topic] }
        if !context.isEmpty { lines.append("[Context]") }
        for pair in context {
            lines.append(pair.source)
            lines.append("→ " + pair.translation)
        }
        lines.append("[Translate]")
        lines.append(text)
        return lines.joined(separator: "\n")
    }

    /// 让模型从最近的字幕里判断这段话在讲什么、会用到哪些术语（识别有错也要猜）。结果作为 `[Topic]` 块送给纠正用的翻译会话。
    public static func topicPrompt(source: LanguageOption) -> String {
        """
        You support a live-subtitle system. The user message contains the most recent lines of live \(source.promptName) speech (a lecture, talk or meeting) as transcribed by speech recognition, plus possibly the previous topic. The transcript contains recognition errors: words are often misheard as similar-sounding ones (e.g. "RJCC list" for "adjacency list"). Infer from the whole passage what the speech is really about.

        Reply with exactly two lines and nothing else:
        Topic: <the specific subject, e.g. "depth-first vs breadth-first graph traversal in a university algorithms course">
        Terms: <up to 15 technical terms, names or phrases likely to be spoken next, spelled correctly in \(source.promptName), comma-separated>

        If the previous topic still fits, keep it and refine the terms. If the speech has clearly moved to a new subject, replace it. If you cannot tell, reply "Topic: unknown".
        """
    }

    public static func notesPrompt(target: LanguageOption) -> String {
        """
        下面是一段实时语音识别原文和机器翻译记录（可能来自讲座、会议、课程、视频或对话），可能有识别错误。
        请用\(target.promptName)整理一份结构化的笔记（Markdown），包括：
        1. 一段话总结
        2. 主要内容（分层列表，保留关键数字、例子）
        3. 关键术语（原文 | 译文 | 一句话解释）
        4. 提到的待办事项、截止日期、需要跟进的事情（如有）
        5. 记录中可能识别错误或含义不确定的地方
        直接输出笔记正文，不要额外说明。
        """
    }
}
