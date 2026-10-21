namespace LiveSub.Core;

/// <summary>
/// 根据识别器给每个词的置信度，自动判断“这位讲者是不是很难识别”（口音、语速快、噪音大）。
///
/// macOS 实测：口音较重的讲课平均置信度约 0.73，清晰的英语 0.9 以上。判断为“难识别”时让模型按发音纠正识别错误；
/// 用两个阈值（滞回）避免在临界值附近来回切换。Windows 版的置信度来自 Whisper 的逐词概率，刻度可能不同，需要实测校准。
/// </summary>
public sealed class RecognitionQualityMonitor
{
    public sealed record Config
    {
        /// <summary>统计最近这么多个词。</summary>
        public int Window { get; init; } = 40;
        /// <summary>至少有这么多个词才开始判断。</summary>
        public int MinWords { get; init; } = 20;
        /// <summary>平均置信度低于它：判定为难识别。</summary>
        public double EnterBelow { get; init; } = 0.80;
        /// <summary>难识别状态下平均置信度高于它：恢复为清晰。</summary>
        public double ExitAbove { get; init; } = 0.86;
    }

    public Config Settings { get; }
    public bool IsDifficult { get; private set; }
    private readonly List<double> confidences = [];

    public RecognitionQualityMonitor(Config? config = null)
    {
        Settings = config ?? new Config();
    }

    /// <summary>最近的平均置信度；词太少时为 null。</summary>
    public double? AverageConfidence =>
        confidences.Count >= Settings.MinWords ? confidences.Sum() / confidences.Count : null;

    /// <summary>加入一句确认结果里各个词的置信度。返回 true 表示“难识别”状态变了。</summary>
    public bool Add(IEnumerable<double> wordConfidences)
    {
        confidences.AddRange(wordConfidences);
        if (confidences.Count > Settings.Window) confidences.RemoveRange(0, confidences.Count - Settings.Window);
        if (AverageConfidence is not { } average) return false;
        bool difficult = IsDifficult ? average <= Settings.ExitAbove : average < Settings.EnterBelow;
        bool changed = difficult != IsDifficult;
        IsDifficult = difficult;
        return changed;
    }
}

/// <summary>
/// 把分句后的字幕片段对回音频时间。
///
/// 识别器给的每段确认结果有起止时间；分句器会把它们合并、切开。这里按“非空白字符数”记账：
/// 每段确认结果的字符均匀分布在它的时间范围里，分出来的片段按顺序消耗字符，换算出起止时间。
/// </summary>
public sealed class SegmentTimeline
{
    private sealed record Span(int Chars, double Start, double End);

    private readonly List<Span> spans = [];
    /// <summary>第一段里已经被消耗的字符数。</summary>
    private int consumed;

    public void AddFinal(string text, double start, double end)
    {
        int chars = Count(text);
        if (chars <= 0) return;
        spans.Add(new Span(chars, start, Math.Max(end, start)));
    }

    /// <summary>取出一个片段的起止时间（音频时间轴上的秒数）；没有时间信息时返回 null。</summary>
    public (double Start, double End)? Take(string segment)
    {
        int remaining = Count(segment);
        if (remaining <= 0 || spans.Count == 0) return null;
        double start = TimeAt(consumed, spans[0]);
        double end = start;
        while (remaining > 0 && spans.Count > 0)
        {
            int available = spans[0].Chars - consumed;
            int used = Math.Min(available, remaining);
            consumed += used;
            remaining -= used;
            end = TimeAt(consumed, spans[0]);
            if (consumed >= spans[0].Chars)
            {
                spans.RemoveAt(0);
                consumed = 0;
            }
        }
        return (start, end);
    }

    private static double TimeAt(int offset, Span span) =>
        span.Start + (span.End - span.Start) * offset / span.Chars;

    private static int Count(string text) => text.EnumerateRunes().Count(r => !System.Text.Rune.IsWhiteSpace(r));
}

/// <summary>导出 SRT / WebVTT 字幕文件。时间是相对录音开始的秒数。</summary>
public static class SubtitleExport
{
    public sealed record Cue(double Start, double End, string Text);

    public static string Srt(IEnumerable<Cue> cues) =>
        string.Join("\n", Tidy(cues).Select((cue, i) =>
            $"{i + 1}\n{Timestamp(cue.Start, ",")} --> {Timestamp(cue.End, ",")}\n{cue.Text}\n"));

    public static string Vtt(IEnumerable<Cue> cues) =>
        "WEBVTT\n\n" + string.Join("\n", Tidy(cues).Select(cue =>
            $"{Timestamp(cue.Start, ".")} --> {Timestamp(cue.End, ".")}\n{cue.Text}\n"));

    /// <summary>按时间排序；每条至少显示 1 秒，但不和下一条重叠；去掉空字幕。</summary>
    internal static List<Cue> Tidy(IEnumerable<Cue> cues)
    {
        var sorted = cues.Where(c => c.Text.Trim().Length > 0)
            .Select(c => new Cue(Math.Max(0, c.Start), Math.Max(0, c.End), c.Text))
            .OrderBy(c => c.Start)
            .ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            double end = Math.Max(sorted[i].End, sorted[i].Start + 1);
            if (i + 1 < sorted.Count) end = Math.Min(end, sorted[i + 1].Start);
            sorted[i] = sorted[i] with { End = Math.Max(end, sorted[i].Start + 0.1) };
        }
        return sorted;
    }

    internal static string Timestamp(double seconds, string separator)
    {
        long ms = (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
        long h = ms / 3_600_000, m = ms / 60_000 % 60, s = ms / 1000 % 60, f = ms % 1000;
        return $"{h:00}:{m:00}:{s:00}{separator}{f:000}";
    }
}
