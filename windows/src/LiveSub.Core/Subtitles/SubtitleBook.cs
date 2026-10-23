namespace LiveSub.Core.Subtitles;

public enum LineState { Waiting, Translating, Done, Failed }

public enum DisplayMode { Bilingual, TranslationOnly, SourceOnly }

public enum SubtitleFileFormat { Srt, Vtt }

public sealed class SubtitleLine
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime Time { get; init; }
    public string? SourceLabel { get; init; }
    public string Source { get; set; } = "";
    public string Translation { get; set; } = "";
    public LineState State { get; set; } = LineState.Waiting;
    /// <summary>附注，例如“已切换备用模型”。</summary>
    public string? Note { get; set; }
    /// <summary>自动纠正改过原文时，识别器原来给出的文字（打开“保留识别原文”时才保存）。</summary>
    public string? OriginalSource { get; set; }
    /// <summary>这句话在音频里的起止时刻（导出 SRT/VTT 用）；没有时间信息时为 null。</summary>
    public DateTime? AudioStart { get; set; }
    public DateTime? AudioEnd { get; set; }
    /// <summary>延迟分析用：开始翻译、出第一个字、完成的时刻。</summary>
    public DateTime? TranslateStartedAt { get; set; }
    public DateTime? FirstTokenAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

/// <summary>
/// 一次记录里的全部字幕和翻译队列（与 macOS 版 SubtitleController 的队列逻辑一致）。
/// 只在界面线程上使用。
/// </summary>
public sealed class SubtitleBook
{
    /// <summary>积压合并时，合并后的一句最长多少单位。</summary>
    public const int MaxMergedUnits = 70;
    /// <summary>新会话第一句带上最近几句作为上下文。</summary>
    public const int ContextLines = 6;

    private readonly List<SubtitleLine> lines = [];
    private readonly List<Guid> queue = [];
    /// <summary>
    /// 记录文件已经写到第几句（与 macOS 的 persistedCount 相同）。两句同时翻译时后一句常常先翻完，
    /// 按字幕顺序写：前面的都翻完（或失败）了才写后面的。积压合并只去掉还在排队的句子（都在游标后面），
    /// 所以已经写过的句子位置不变。
    /// </summary>
    private int persistedCount;

    public IReadOnlyList<SubtitleLine> Lines => lines;

    /// <summary>等待翻译的句数。</summary>
    public int Backlog => queue.Count;

    /// <summary>
    /// 把分句后的片段加入字幕和翻译队列。
    /// <paramref name="timeline"/> 把片段对回音频时间（相对 <paramref name="captureStart"/> 的秒数）。
    /// </summary>
    public List<SubtitleLine> Enqueue(IEnumerable<string> segments, string? label, SegmentTimeline? timeline,
                                      DateTime captureStart, DateTime now)
    {
        var added = new List<SubtitleLine>();
        foreach (var text in segments)
        {
            var line = new SubtitleLine { Time = now, SourceLabel = label, Source = text };
            if (timeline?.Take(text) is { } t)
            {
                line.AudioStart = captureStart.AddSeconds(t.Start);
                line.AudioEnd = captureStart.AddSeconds(t.End);
            }
            lines.Add(line);
            queue.Add(line.Id);
            added.Add(line);
        }
        return added;
    }

    /// <summary>
    /// 取出下一句。如果积压了 2 句以上（翻译跟不上语速），把同一音源的积压句合并成一句一起翻译，
    /// 让字幕延迟不会越积越长。
    /// </summary>
    public SubtitleLine? DequeueMerged()
    {
        if (queue.Count == 0) return null;
        var first = queue[0];
        queue.RemoveAt(0);
        var firstIndex = IndexOf(first);
        if (firstIndex is null) return DequeueMerged();
        var target = lines[firstIndex.Value];
        if (queue.Count < 2) return target;

        var merged = new HashSet<Guid>();
        foreach (var id in queue)
        {
            if (IndexOf(id) is not { } i || lines[i].SourceLabel != target.SourceLabel
                || Segmenter.Units(target.Source) + Segmenter.Units(lines[i].Source) > MaxMergedUnits)
            {
                break;
            }
            target.Source = Segmenter.Join(target.Source, lines[i].Source);
            if (lines[i].AudioEnd is { } end) target.AudioEnd = end;
            merged.Add(id);
        }
        queue.RemoveAll(merged.Contains);
        lines.RemoveAll(l => merged.Contains(l.Id));
        return target;
    }

    /// <summary>这一句之前最近几句已完成的原文与译文。</summary>
    public List<(string Source, string Translation)> RecentContext(Guid id)
    {
        if (IndexOf(id) is not { } i) return [];
        return lines.Take(i).Where(l => l.State == LineState.Done)
            .TakeLast(ContextLines)
            .Select(l => (l.Source, l.Translation))
            .ToList();
    }

    public SubtitleLine? Find(Guid id) => IndexOf(id) is { } i ? lines[i] : null;

    public void MarkTranslating(Guid id, DateTime now)
    {
        if (Find(id) is not { } line) return;
        line.State = LineState.Translating;
        line.TranslateStartedAt = now;
    }

    /// <summary>流式译文：只在变长时更新（避免标记清理后短暂变短造成闪烁）。返回是否有变化。</summary>
    public bool UpdatePartial(Guid id, string partial, DateTime now)
    {
        if (Find(id) is not { } line) return false;
        if (line.FirstTokenAt is null && partial.Length > 0) line.FirstTokenAt = now;
        if (line.State != LineState.Translating || partial.Length < line.Translation.Length) return false;
        if (line.Translation == partial) return false;
        line.Translation = partial;
        return true;
    }

    /// <summary>
    /// 翻译完成。模型纠正了识别错误（口音、近音词）时，字幕和记录里都换成纠正后的原文；
    /// <paramref name="keepOriginal"/> 为 true 时同时保留识别器原来的文字。
    /// </summary>
    public SubtitleLine? Finish(Guid id, string translation, string? note, string? correctedSource, bool keepOriginal, DateTime now)
    {
        if (Find(id) is not { } line) return null;
        if (correctedSource is not null)
        {
            if (keepOriginal) line.OriginalSource = line.Source;
            line.Source = correctedSource;
        }
        line.Translation = translation;
        line.State = LineState.Done;
        line.Note = note;
        line.FinishedAt = now;
        return line;
    }

    public SubtitleLine? Fail(Guid id, DateTime now)
    {
        if (Find(id) is not { } line) return null;
        line.State = LineState.Failed;
        line.FinishedAt = now;
        return line;
    }

    /// <summary>按字幕顺序取出可以写进记录的句子：从游标开始，遇到还没翻完的就停。</summary>
    public List<SubtitleLine> TakePersistable()
    {
        var ready = new List<SubtitleLine>();
        while (persistedCount < lines.Count && lines[persistedCount].State is LineState.Done or LineState.Failed)
        {
            ready.Add(lines[persistedCount]);
            persistedCount += 1;
        }
        return ready;
    }

    /// <summary>
    /// 退出、开始新的记录前：剩下的句子不管翻完没有都按顺序取出（没翻完的由调用方只写原文），
    /// 之后再翻完的不会重复写。
    /// </summary>
    public List<SubtitleLine> TakeRemaining()
    {
        var rest = lines.Skip(persistedCount).ToList();
        persistedCount = lines.Count;
        return rest;
    }

    public void Clear()
    {
        lines.Clear();
        queue.Clear();
        persistedCount = 0;
    }

    /// <summary>字幕文件内容：时间相对这份记录开始录音的时刻；文字按“显示内容”（双语/仅译文/仅原文）。</summary>
    public string SubtitleFile(SubtitleFileFormat format, DisplayMode mode, DateTime? sessionStart)
    {
        DateTime reference;
        if (sessionStart is { } s)
        {
            reference = s;
        }
        else
        {
            var starts = lines.Where(l => l.AudioStart is not null).Select(l => l.AudioStart!.Value).ToList();
            reference = starts.Count > 0 ? starts.Min() : lines.FirstOrDefault()?.Time ?? DateTime.UtcNow;
        }
        var cues = lines.Select(line =>
        {
            var text = mode switch
            {
                DisplayMode.Bilingual => line.Translation.Length == 0 ? line.Source : $"{line.Source}\n{line.Translation}",
                DisplayMode.TranslationOnly => line.Translation.Length == 0 ? line.Source : line.Translation,
                _ => line.Source,
            };
            // 没有时间信息的句子按入队时间往前估 3 秒。
            var start = line.AudioStart ?? line.Time.AddSeconds(-3);
            var end = line.AudioEnd ?? line.Time;
            return new SubtitleExport.Cue((start - reference).TotalSeconds, (end - reference).TotalSeconds, text);
        });
        return format == SubtitleFileFormat.Srt ? SubtitleExport.Srt(cues) : SubtitleExport.Vtt(cues);
    }

    private int? IndexOf(Guid id)
    {
        // 新句子总在末尾，从后往前找更快。
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].Id == id) return i;
        }
        return null;
    }
}
