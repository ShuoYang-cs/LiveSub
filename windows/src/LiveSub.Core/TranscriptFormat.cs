using System.Globalization;

namespace LiveSub.Core;

/// <summary>记录文件头里的文字：中文界面用中文，英文界面用英文（与 macOS 版一致）。</summary>
public sealed record TranscriptLabels(string Started, string Language, bool EnglishLanguageNames)
{
    public static readonly TranscriptLabels Chinese = new("开始时间", "语言", false);
    public static readonly TranscriptLabels English = new("Started", "Language", true);
}

/// <summary>字幕记录（Markdown）的格式，与 macOS 版一致。时间按本机时区显示。</summary>
public static class TranscriptFormat
{
    public static string Header(string title, DateTime started, LanguageOption source, LanguageOption target,
                                TranscriptLabels? labels = null)
    {
        var l = labels ?? TranscriptLabels.Chinese;
        string Name(LanguageOption o) => l.EnglishLanguageNames ? o.EnglishName : o.DisplayName;
        return $"# {title}\n\n- {l.Started}：{DateTimeStamp(started)}\n- {l.Language}：{Name(source)} → {Name(target)}\n\n";
    }

    /// <param name="original">自动纠正改过原文时，识别器原来给出的文字（用户打开“保留识别原文”才有）。</param>
    public static string Entry(DateTime time, string? sourceLabel, string source, string translation, string? original = null)
    {
        var label = sourceLabel is null ? "" : " " + sourceLabel;
        var output = $"**[{TimeStamp(time)}]{label}** {source}\n";
        if (original is not null && original != source)
        {
            output += $"*识别原文：{original}*\n";
        }
        if (translation.Length > 0)
        {
            output += $"> {translation.Replace("\n", "\n> ")}\n";
        }
        return output + "\n";
    }

    /// <summary>给模型生成笔记用的纯文本记录。</summary>
    public static string Plain(IEnumerable<(DateTime Time, string Source, string Translation)> items) =>
        string.Join("\n", items.Select(i => $"[{TimeStamp(i.Time)}] {i.Source}\n    {i.Translation}"));

    /// <summary>HH:mm:ss（本机时区）。</summary>
    public static string TimeStamp(DateTime time) => Local(time).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>yyyy-MM-dd HH:mm（本机时区）。</summary>
    public static string DateTimeStamp(DateTime time) => Local(time).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>文件名用的时间：yyyy-MM-dd HH.mm（Windows 文件名不能有冒号）。</summary>
    public static string FileStamp(DateTime time) => Local(time).ToString("yyyy-MM-dd HH.mm", CultureInfo.InvariantCulture);

    private static DateTime Local(DateTime time) => time.Kind == DateTimeKind.Local ? time : time.ToLocalTime();
}

/// <summary>
/// AI 助手：一个长驻的对话，提问时把上次提问以来新增的字幕一起带上。
/// 不在每句字幕到来时都发给模型——那样每句都会触发一次回答，白白消耗额度。
/// （Windows 版 v0.1 尚未提供助手面板，先移植提示词与消息格式。）
/// </summary>
public static class AssistantPrompt
{
    public static string System(LanguageOption source, LanguageOption target) => $"""
        You are an assistant helping the user follow live speech: a lecture, meeting, video, talk or conversation. The user's app streams you the transcript: each message starts with the transcript lines that are new since the previous message (live speech recognition in {source.PromptName}, with local times), then the user's question.

        The transcript comes from speech recognition: expect sound-alike words, broken sentences and missing punctuation. Infer what the speaker actually said.

        When answering:
        - Answer in {target.PromptName}. Be short and direct (usually 2–6 sentences or a short list): the user is reading while still listening.
        - Most questions are about the last few minutes. Connect the answer to what the speaker just said and the examples used; keep key {source.PromptName} terms in parentheses.
        - If the transcript is too garbled, say briefly what you think was said, then answer the likely question.
        - Plain text with light Markdown only (bold, inline code, simple lists). No headings, no tables.
        - Never repeat the transcript back unless asked.
        """;

    /// <summary>一句字幕。</summary>
    public sealed record Line(DateTime Time, string Text);

    /// <summary>发给模型的一条消息：新增字幕 + 正在说的半句 + 问题。</summary>
    public static string Message(IReadOnlyList<Line> newLines, string speaking, string question)
    {
        var parts = new List<string>();
        if (newLines.Count > 0)
        {
            var range = newLines.Count == 1
                ? TranscriptFormat.TimeStamp(newLines[0].Time)
                : $"{TranscriptFormat.TimeStamp(newLines[0].Time)}–{TranscriptFormat.TimeStamp(newLines[^1].Time)}";
            var block = new List<string> { $"[字幕 · 新增 {newLines.Count} 句 · {range}]" };
            block.AddRange(newLines.Select(l => $"[{TranscriptFormat.TimeStamp(l.Time)}] {l.Text}"));
            parts.Add(string.Join("\n", block));
        }
        speaking = speaking.Trim();
        if (speaking.Length > 0)
        {
            parts.Add($"[正在说（还没说完）]\n{speaking}");
        }
        parts.Add($"[我的问题]\n{question.Trim()}");
        return string.Join("\n\n", parts);
    }

    /// <summary>面板里的快捷问题。</summary>
    public static readonly IReadOnlyList<string> QuickQuestions = ["总结最近的内容", "解释相关概念", "解释上一句"];
}
