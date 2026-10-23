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

    /// <summary>
    /// 笔记文件：和字幕记录放在同一个文件夹，文件名里的“字幕记录”换成“笔记”（英文界面 Transcript → Notes，与 macOS 版相同）。
    /// 记录是在另一种界面语言下建的、文件名里没有 <paramref name="transcriptName"/> 时，在后面加上 <paramref name="notesName"/>，
    /// 免得笔记把记录文件覆盖掉。
    /// </summary>
    public static string NotesPath(string transcriptPath, string transcriptName, string notesName)
    {
        var folder = Path.GetDirectoryName(transcriptPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(transcriptPath);
        name = name.Contains(transcriptName, StringComparison.Ordinal)
            ? name.Replace(transcriptName, notesName, StringComparison.Ordinal)
            : $"{name} {notesName}";
        return Path.Combine(folder, name + ".md");
    }

    private static DateTime Local(DateTime time) => time.Kind == DateTimeKind.Local ? time : time.ToLocalTime();
}
