using System.Text;
using System.Text.RegularExpressions;

namespace LiveSub.Core;

/// <summary>
/// 每次记录一个文件夹（与 macOS 版相同，2026-11-23 用户要求）：
/// <code>
/// 记录文件夹\2026-11-23 13.07\
///     2026-11-23 13.07 字幕记录.md
///     2026-11-23 13.07 AI 助手对话.md
///     2026-11-23 13.07 录音.m4a
///     2026-11-23 13.07 笔记.md
/// </code>
/// 文件夹名是开始时间；里面的文件名保留时间前缀，拷出去也认得出是哪一次。
/// 以前的版本把这些文件平铺在记录文件夹里，<see cref="MigrationPlan"/> 把它们按时间搬进各自的文件夹。
/// </summary>
public static partial class RecordFolders
{
    /// <summary>本次记录的文件夹名：开始时间；同一分钟里已经有一次了就加序号（“2026-11-23 13.07 2”）。</summary>
    public static string FolderName(string stamp, Func<string, bool> exists)
    {
        if (!exists(stamp)) return stamp;
        int n = 2;
        while (exists($"{stamp} {n}")) n++;
        return $"{stamp} {n}";
    }

    /// <summary>文件名开头的“年-月-日 时.分”，后面要跟空格；没有时为 null。</summary>
    public static string? Stamp(string fileName) => StampPattern().Match(fileName) is { Success: true } m ? m.Value : null;

    /// <summary>一次搬动：把记录文件夹里的 <see cref="File"/> 搬进子文件夹 <see cref="Folder"/>。</summary>
    public sealed record Move(string File, string Folder);

    /// <summary>旧版平铺的文件要怎么搬。</summary>
    /// <param name="files">记录文件夹最上层的文件名（不含子文件夹）。</param>
    /// <param name="keywords">记录文件名里的词（“字幕记录”“录音”“笔记”，英文界面写的是 Transcript、Recording、Notes）。
    /// 只搬“时间开头、带这些词”的文件，用户自己放进来的别的文件不动。</param>
    /// <param name="existsInFolder">子文件夹里是不是已经有同名文件（有就不搬，不覆盖）。</param>
    public static List<Move> MigrationPlan(IEnumerable<string> files, IReadOnlyCollection<string> keywords,
                                           Func<string, string, bool> existsInFolder)
    {
        var plan = new List<Move>();
        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Stamp(file) is not { } stamp) continue;
            var rest = file[stamp.Length..];
            if (!keywords.Any(k => rest.Contains(k, StringComparison.Ordinal)) || existsInFolder(stamp, file)) continue;
            plan.Add(new Move(file, stamp));
        }
        return plan;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}\.\d{2}(?= )")]
    private static partial Regex StampPattern();
}

/// <summary>对话记录里的固定文字（按界面语言）。</summary>
public sealed record AssistantLogLabels(string NewConversation, string AttachmentsOnly, string Attachments, string Failed,
                                        string SavedFiles, string ListSeparator)
{
    public static readonly AssistantLogLabels Chinese = new("新对话", "（只发了附件）", "附件：", "回答失败：", "已保存文件：", "、");
    public static readonly AssistantLogLabels English = new("New conversation", "(attachments only)", "Attachments: ", "Answer failed: ",
                                                            "Saved files: ", ", ");
}

/// <summary>
/// AI 助手对话记录（与 macOS 版格式相同）：每答完一问追加一段，崩溃时已经答完的不丢。
/// 同一次记录里点了“新对话”，在同一个文件里用分隔线隔开。
/// </summary>
public static class AssistantLog
{
    public static string Header(string title) => $"# {title}\n";

    /// <summary>新对话的分隔：文件里已经有前一个对话时写。</summary>
    public static string Separator(DateTime time, AssistantLogLabels labels) =>
        $"\n---\n\n**{labels.NewConversation} {Time(time)}**\n";

    /// <summary>一问一答。<paramref name="answer"/> 是面板上显示的回答（要保存的文件块已经去掉）；<paramref name="files"/> 是这次存下的文件名。</summary>
    public static string Entry(DateTime time, string question, IReadOnlyList<string> attachments, string answer,
                               IReadOnlyList<string> files, bool failed, AssistantLogLabels labels)
    {
        var sb = new StringBuilder();
        sb.Append($"\n### [{Time(time)}] ");
        var oneLine = string.Join(" ", question.Split('\n', '\r').Select(l => l.Trim()).Where(l => l.Length > 0));
        sb.Append(oneLine.Length == 0 ? labels.AttachmentsOnly : oneLine).Append('\n');
        if (attachments.Count > 0) sb.Append('\n').Append(labels.Attachments).Append(string.Join(labels.ListSeparator, attachments)).Append('\n');
        var body = answer.Trim();
        if (failed) sb.Append("\n*").Append(labels.Failed).Append(body).Append("*\n");
        else if (body.Length > 0) sb.Append('\n').Append(body).Append('\n');
        if (files.Count > 0) sb.Append('\n').Append(labels.SavedFiles).Append(string.Join(labels.ListSeparator, files)).Append('\n');
        return sb.ToString();
    }

    private static string Time(DateTime time) => TranscriptFormat.TimeStamp(time);
}
