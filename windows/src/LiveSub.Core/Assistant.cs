using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LiveSub.Core.Subtitles;

namespace LiveSub.Core;

/// <summary>
/// AI 助手：一个长驻的对话，提问时把上次提问以来新增的字幕一起带上（与 macOS 版的 AssistantPrompt 相同）。
/// 不在每句字幕到来时都发给模型——那样每句都会触发一次回答，白白消耗额度。
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

        Saving files: you cannot write files yourself, but the app saves them for you. When the user asks you to save, export or organize something into a file or folder, put each file at the end of your answer in this exact form, one block per file:
        {AssistantFiles.StartMarker} <short file name>
        <the complete file content in Markdown; headings, lists and tables are fine here>
        {AssistantFiles.EndMarker}
        The app saves the files into the user's LiveSub "AI 助手" folder and shows their names. Outside the blocks write at most one short sentence; do not repeat the file content, do not wrap the blocks in code fences, and never give shell commands. Only use these blocks when the user asks to save something.
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

    /// <summary>面板里的快捷问题（发给模型的是中文原文，按钮上显示界面语言的文字）。</summary>
    public static readonly IReadOnlyList<string> QuickQuestions = ["总结最近的内容", "解释相关概念", "解释上一句"];
}

/// <summary>
/// AI 助手把内容存成文件（与 macOS 版的 AssistantFiles 相同）：模型不能自己写文件（Claude Code 以无工具模式运行，
/// 模型服务也没有工具），所以让它在回答里用约定的块写出文件名和内容，由程序代为保存，并且只写进记录文件夹下的“AI 助手”文件夹。
/// </summary>
public static class AssistantFiles
{
    public const string StartMarker = "@@save:";
    public const string EndMarker = "@@end";

    /// <summary>回答里要保存的一个文件。</summary>
    public sealed record File(string Name, string Content);

    /// <summary>拆开的回答。</summary>
    /// <param name="Text">块外的文字：显示在面板里。</param>
    /// <param name="Writing">最后一个文件还没写完（流式输出中，或者回答被截断）。</param>
    public sealed record Parsed(string Text, IReadOnlyList<File> Files, bool Writing);

    /// <summary>拆出回答里要保存的文件。没有结束标记的块算到回答末尾（回答被截断时也保存已经写出的部分）。</summary>
    public static Parsed Parse(string reply)
    {
        var text = new List<string>();
        var files = new List<File>();
        (string Name, List<string> Lines, bool Fenced)? current = null;
        // 刚结束的块是被包在代码块里的：跳过紧跟着的代码块结束标记。
        bool closingFence = false;
        foreach (var line in reply.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (current is { } file)
            {
                if (trimmed == EndMarker)
                {
                    files.Add(new File(file.Name, TrimBlankLines(file.Lines)));
                    closingFence = file.Fenced;
                    current = null;
                }
                else
                {
                    file.Lines.Add(line);
                }
            }
            else if (trimmed.StartsWith(StartMarker, StringComparison.Ordinal))
            {
                // 模型偶尔还是会把块包进代码块：去掉紧挨着的开头标记，记住要去掉对应的结束标记。
                bool fenced = text.Count > 0 && text[^1].Trim().StartsWith("```", StringComparison.Ordinal);
                if (fenced) text.RemoveAt(text.Count - 1);
                current = (trimmed[StartMarker.Length..].Trim(), new List<string>(), fenced);
            }
            else if (closingFence && trimmed.Length == 0)
            {
                continue;
            }
            else if (closingFence && trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                closingFence = false;
            }
            else
            {
                closingFence = false;
                text.Add(line);
            }
        }
        if (current is { } last) files.Add(new File(last.Name, TrimBlankLines(last.Lines)));
        var visible = string.Join("\n", text).Trim();
        while (visible.Contains("\n\n\n", StringComparison.Ordinal)) visible = visible.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        return new Parsed(visible, files, current is not null);
    }

    /// <summary>
    /// 保存用的文件名：只留最后一段（不能带路径），去掉系统不允许的字符，限制长度，统一用 .md；
    /// 名字不以日期开头时加上“年-月-日 时.分”前缀，文件夹里按时间排序。
    /// </summary>
    public static string FileName(string name, string stamp, string fallback)
    {
        var kept = string.Concat(name.Split('/', '\\')[^1].EnumerateRunes()
            .Where(r => !(r.IsAscii && ":*?\"<>|".Contains((char)r.Value))
                        && Rune.GetUnicodeCategory(r) is not (UnicodeCategory.Control or UnicodeCategory.Format))
            .Select(r => r.ToString()));
        var stem = string.Join(" ", kept.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        foreach (var ext in new[] { ".md", ".markdown", ".txt" })
        {
            if (stem.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) stem = stem[..^ext.Length];
        }
        stem = stem.Trim(' ', '.');
        // 按字（与 macOS 的字符数相同）截断，不把表情等拆成两半。
        var info = new StringInfo(stem);
        if (info.LengthInTextElements > 80) stem = info.SubstringByTextElements(0, 80).Trim(' ', '.');
        if (stem.Length == 0) stem = fallback;
        if (!Regex.IsMatch(stem, @"^\d{4}-\d{2}-\d{2}")) stem = $"{stamp} {stem}";
        return stem + ".md";
    }

    /// <summary>不覆盖已有文件：重名时在扩展名前加“ 2”“ 3”……</summary>
    public static string UniqueName(string name, Func<string, bool> exists)
    {
        if (!exists(name)) return name;
        var stem = name.EndsWith(".md", StringComparison.Ordinal) ? name[..^3] : name;
        int n = 2;
        while (exists($"{stem} {n}.md")) n++;
        return $"{stem} {n}.md";
    }

    private static string TrimBlankLines(List<string> lines)
    {
        int start = 0, end = lines.Count;
        while (start < end && lines[start].Trim().Length == 0) start++;
        while (end > start && lines[end - 1].Trim().Length == 0) end--;
        return string.Join("\n", lines.GetRange(start, end - start));
    }
}

public enum AssistantRole { User, Assistant }

/// <summary>替用户保存的文件：成功时有位置（<paramref name="Path"/>），失败时有原因（<paramref name="Error"/>）。</summary>
public sealed record AssistantSavedFile(string Name, string? Path = null, string? Error = null);

/// <summary>面板里的一条消息。<see cref="Failed"/> 的回答显示为橙色的错误说明。</summary>
public sealed class AssistantMessage(AssistantRole role, string text, bool failed = false)
{
    public Guid Id { get; } = Guid.NewGuid();
    public AssistantRole Role { get; } = role;
    /// <summary>显示的文字：回答里要保存成文件的部分不显示，只显示文件名。</summary>
    public string Text { get; internal set; } = text;
    public bool Failed { get; internal set; } = failed;
    /// <summary>模型的原始回答（流式输出只会变长）。</summary>
    public string Raw { get; internal set; } = "";
    /// <summary>回答里正在写的文件还没写完，或者回答完了、文件还在保存（显示“正在保存文件…”）。</summary>
    public bool WritingFile { get; internal set; }
    public IReadOnlyList<AssistantSavedFile> Files { get; internal set; } = [];
}

/// <summary>
/// 一次提问：会话是新开的（第一次提问、换了模型、进程重启）时发 <see cref="NewSessionMessage"/>（本次对话以来的全部字幕），
/// 否则发 <see cref="OngoingMessage"/>（只带还没发过的字幕）。回答成功后 <see cref="SettledIds"/> 才算发过。
/// </summary>
public sealed record AssistantQuestion(int Conversation, Guid AnswerId, string NewSessionMessage, string OngoingMessage,
                                       IReadOnlyList<Guid> SettledIds);

/// <summary>
/// AI 助手的对话状态（与 macOS 版 AssistantSession 的逻辑相同）：问答列表、哪些字幕已经发给模型、是否正在回答。
/// - 只带本次对话开始以后的字幕；
/// - 已经开始翻译的句子算“已确定”；还在排队的句子可能会和后面的句子合并，和正在说的半句一起放进“正在说”；
/// - 回答成功后这些句子才算发过，失败时下次提问重新带上。
/// 只在界面线程上使用。
/// </summary>
public sealed class AssistantConversation(DateTime start)
{
    private readonly List<AssistantMessage> messages = [];
    private readonly HashSet<Guid> sent = [];
    /// <summary>每次 <see cref="Reset"/> 加一：旧对话里还没回答完的提问，结果到来时不再写进新对话。</summary>
    private int conversation;
    private Guid? answering;

    public IReadOnlyList<AssistantMessage> Messages => messages;
    public bool IsAnswering => answering is not null;
    /// <summary>对话开始的时刻（与字幕的 <see cref="SubtitleLine.Time"/> 同一种时间）。</summary>
    public DateTime Start { get; private set; } = start;

    /// <summary>新对话：清空问答，之后只带上从 <paramref name="now"/> 开始的字幕。</summary>
    public void Reset(DateTime now)
    {
        conversation++;
        messages.Clear();
        sent.Clear();
        answering = null;
        Start = now;
    }

    /// <summary>
    /// 开始回答一个问题：加上提问和空的回答，返回要发给模型的消息。正在回答上一个问题时返回 null。
    /// </summary>
    /// <param name="speaking">正在说、还没确认的文字。</param>
    public AssistantQuestion? Begin(string question, IEnumerable<SubtitleLine> lines, string speaking)
    {
        if (IsAnswering) return null;
        var current = lines.Where(l => l.Time >= Start).ToList();
        var settled = current.Where(l => l.State != LineState.Waiting && l.Source.Length > 0).ToList();
        var pending = current.Where(l => l.State == LineState.Waiting).Select(l => l.Source);
        var speakingNow = string.Join(" ", pending.Append(speaking).Where(s => s.Length > 0));
        var all = settled.Select(l => new AssistantPrompt.Line(l.Time, l.Source)).ToList();
        var fresh = settled.Where(l => !sent.Contains(l.Id)).Select(l => new AssistantPrompt.Line(l.Time, l.Source)).ToList();

        messages.Add(new AssistantMessage(AssistantRole.User, question));
        var answer = new AssistantMessage(AssistantRole.Assistant, "");
        messages.Add(answer);
        answering = answer.Id;
        return new AssistantQuestion(conversation, answer.Id,
            AssistantPrompt.Message(all, speakingNow, question),
            AssistantPrompt.Message(fresh, speakingNow, question),
            settled.Select(l => l.Id).ToList());
    }

    /// <summary>
    /// 流式输出：回答只会变长（晚到的较短片段不显示）。要保存成文件的部分不显示（见 <see cref="AssistantFiles.Parse"/>）。
    /// 回答已结束或对话已清空时忽略。返回是否有变化。
    /// </summary>
    public bool UpdateAnswer(AssistantQuestion question, string partial)
    {
        if (!IsCurrent(question) || Find(question.AnswerId) is not { } answer || partial.Length <= answer.Raw.Length) return false;
        Show(answer, partial);
        return true;
    }

    /// <summary>
    /// 回答完成：这次带上的字幕算发过。返回是否还是当前对话的提问。
    /// 回答里有要保存的文件时，保存完（<see cref="FilesSaved"/>）之前一直显示“正在保存文件…”。
    /// </summary>
    public bool Complete(AssistantQuestion question, string reply)
    {
        if (!IsCurrent(question)) return false;
        if (Find(question.AnswerId) is { } answer)
        {
            var parsed = Show(answer, reply.Trim().Length > 0 ? reply.Trim() : answer.Raw);
            answer.WritingFile = parsed.Files.Count > 0;
        }
        sent.UnionWith(question.SettledIds);
        answering = null;
        return true;
    }

    /// <summary>回答完成后要保存的文件（由程序写进“AI 助手”文件夹）。还在回答、回答失败或对话已清空时为空。</summary>
    public IReadOnlyList<AssistantFiles.File> FilesToSave(AssistantQuestion question) =>
        question.Conversation == conversation && answering != question.AnswerId && Find(question.AnswerId) is { Failed: false } answer
            ? AssistantFiles.Parse(answer.Raw).Files
            : [];

    /// <summary>文件保存完：显示保存到了哪里或失败的原因。对话已经清空时忽略，返回 false。</summary>
    public bool FilesSaved(AssistantQuestion question, IReadOnlyList<AssistantSavedFile> files)
    {
        if (question.Conversation != conversation || Find(question.AnswerId) is not { } answer) return false;
        answer.Files = files;
        answer.WritingFile = false;
        return true;
    }

    /// <summary>回答失败：回答换成错误说明（写了一半的文件不保存）；字幕不算发过，下次提问重新带上。</summary>
    public bool Fail(AssistantQuestion question, string message)
    {
        if (!IsCurrent(question)) return false;
        if (Find(question.AnswerId) is { } answer)
        {
            answer.Text = message;
            answer.Failed = true;
            answer.WritingFile = false;
        }
        answering = null;
        return true;
    }

    /// <summary>还没发出去就失败了（例如找不到模型服务）：显示提问和错误说明。</summary>
    public void AddFailure(string question, string message)
    {
        messages.Add(new AssistantMessage(AssistantRole.User, question));
        messages.Add(new AssistantMessage(AssistantRole.Assistant, message, failed: true));
    }

    /// <summary>这句字幕已经发给模型了吗（测试用）。</summary>
    internal bool WasSent(Guid lineId) => sent.Contains(lineId);

    private static AssistantFiles.Parsed Show(AssistantMessage answer, string raw)
    {
        var parsed = AssistantFiles.Parse(raw);
        answer.Raw = raw;
        answer.Text = parsed.Text;
        answer.WritingFile = parsed.Writing;
        return parsed;
    }

    private bool IsCurrent(AssistantQuestion question) => question.Conversation == conversation && answering == question.AnswerId;

    private AssistantMessage? Find(Guid id) => messages.FirstOrDefault(m => m.Id == id);
}

[Flags]
public enum InlineStyle { None = 0, Bold = 1, Italic = 2, Code = 4 }

/// <summary>一段同样格式的文字。</summary>
public sealed record InlineRun(string Text, InlineStyle Style);

/// <summary>
/// 回答里的行内 Markdown：**粗体**、*斜体*、`代码`，与 macOS 版按“只解析行内格式、保留换行”显示相同。
/// 换行、列表符号原样保留；没配对的符号原样显示（流式输出时后半个符号可能还没到）；\* 等转义显示符号本身。
/// 不处理 __粗体__，免得 Python 的 __init__ 之类被吃掉下划线。
/// </summary>
public static class InlineMarkdown
{
    public static List<InlineRun> Parse(string text)
    {
        var runs = new List<InlineRun>();
        Parse(text, InlineStyle.None, runs);
        return runs;
    }

    private static void Parse(string text, InlineStyle style, List<InlineRun> runs)
    {
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length == 0) return;
            runs.Add(new InlineRun(plain.ToString(), style));
            plain.Clear();
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            int lineEnd = text.IndexOf('\n', i);
            if (lineEnd < 0) lineEnd = text.Length;
            if (c == '\\' && i + 1 < text.Length && text[i + 1] is '*' or '`' or '\\')
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }
            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1, lineEnd - i - 1);
                if (end > i + 1)
                {
                    Flush();
                    runs.Add(new InlineRun(text[(i + 1)..end], style | InlineStyle.Code));
                    i = end + 1;
                    continue;
                }
            }
            else if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = i + 2 < lineEnd ? text.IndexOf("**", i + 2, lineEnd - i - 2, StringComparison.Ordinal) : -1;
                if (end > i + 2 && !char.IsWhiteSpace(text[i + 2]) && !char.IsWhiteSpace(text[end - 1]))
                {
                    Flush();
                    Parse(text[(i + 2)..end], style | InlineStyle.Bold, runs);
                    i = end + 2;
                    continue;
                }
            }
            else if (c == '*' && i + 1 < lineEnd && !char.IsWhiteSpace(text[i + 1]))
            {
                int end = SingleStar(text, i + 1, lineEnd);
                if (end > i + 1 && !char.IsWhiteSpace(text[end - 1]))
                {
                    Flush();
                    Parse(text[(i + 1)..end], style | InlineStyle.Italic, runs);
                    i = end + 1;
                    continue;
                }
            }
            plain.Append(c);
            i++;
        }
        Flush();
    }

    /// <summary>同一行里下一个单独的 *（跳过 ** ），没有时返回 -1。</summary>
    private static int SingleStar(string text, int from, int lineEnd)
    {
        for (int j = from; j < lineEnd; j++)
        {
            if (text[j] != '*') continue;
            if (j + 1 < lineEnd && text[j + 1] == '*')
            {
                j++;
                continue;
            }
            return j;
        }
        return -1;
    }
}
