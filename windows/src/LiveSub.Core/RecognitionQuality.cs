namespace LiveSub.Core;

/// <summary>
/// 根据识别器给每个词的置信度，自动判断“这位讲者是不是很难识别”（口音、语速快、噪音大）。
///
/// macOS 实测：口音较重的讲课平均置信度约 0.73，清晰的英语 0.9 以上。判断为“难识别”时让模型按发音纠正识别错误；
/// 用两个阈值（滞回）避免在临界值附近来回切换。Windows 版的置信度来自 Whisper 的逐词概率，刻度可能不同，需要实测校准。
/// 进入快、退出慢：口音重的讲者中间常有几句说得清楚（macOS 2026-10-21 实测一节课里“清晰”多半只持续二十几秒），
/// 以前一清楚就退出，一个半小时来回切换 47 次，每次都要换翻译会话。现在要连续清楚 <see cref="Config.ExitAfter"/> 才退出；
/// 纠正多开一会儿几乎没有坏处。
/// </summary>
public sealed class RecognitionQualityMonitor
{
    public sealed record Config
    {
        /// <summary>统计最近这么多个词（讲课约 25 秒的话）。一两句没认准不会马上判为难识别；换成口音重的讲者时，大约 15 秒开始纠正。</summary>
        public int Window { get; init; } = 60;
        /// <summary>至少有这么多个词才开始判断。</summary>
        public int MinWords { get; init; } = 20;
        /// <summary>平均置信度低于它：判定为难识别。</summary>
        public double EnterBelow { get; init; } = 0.80;
        /// <summary>难识别状态下平均置信度高于它：开始计时，准备恢复为清晰。</summary>
        public double ExitAbove { get; init; } = 0.86;
        /// <summary>平均置信度连续这么久都高于 <see cref="ExitAbove"/>，才恢复为清晰；中间有一次不够高就重新计时。</summary>
        public TimeSpan ExitAfter { get; init; } = TimeSpan.FromSeconds(60);
    }

    public Config Settings { get; }
    public bool IsDifficult { get; private set; }
    private readonly List<double> confidences = [];
    /// <summary>难识别状态下，从什么时候起平均置信度一直高于 ExitAbove。</summary>
    private DateTime? clearSince;

    public RecognitionQualityMonitor(Config? config = null)
    {
        Settings = config ?? new Config();
    }

    /// <summary>最近的平均置信度；词太少时为 null。</summary>
    public double? AverageConfidence =>
        confidences.Count >= Settings.MinWords ? confidences.Sum() / confidences.Count : null;

    /// <summary>加入一句确认结果里各个词的置信度。返回 true 表示“难识别”状态变了。</summary>
    public bool Add(IEnumerable<double> wordConfidences, DateTime? now = null)
    {
        var time = now ?? DateTime.UtcNow;
        confidences.AddRange(wordConfidences);
        if (confidences.Count > Settings.Window) confidences.RemoveRange(0, confidences.Count - Settings.Window);
        if (AverageConfidence is not { } average) return false;
        if (!IsDifficult)
        {
            if (average >= Settings.EnterBelow) return false;
            IsDifficult = true;
            clearSince = null;
            return true;
        }
        if (average <= Settings.ExitAbove)
        {
            clearSince = null;
            return false;
        }
        clearSince ??= time;
        if (time - clearSince.Value < Settings.ExitAfter) return false;
        IsDifficult = false;
        clearSince = null;
        return true;
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
