using LiveSub.Core;
using LiveSub.Core.Subtitles;

namespace LiveSub.Core.Tests;

public class SubtitleBookTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static SubtitleBook BookWith(params string[] sources)
    {
        var book = new SubtitleBook();
        book.Enqueue(sources, null, null, T0, T0);
        return book;
    }

    [Fact]
    public void PersistsInSubtitleOrderEvenWhenALaterLineFinishesFirst()
    {
        // 与 macOS 的 persist() 相同：两句同时翻译、后一句先翻完时，等前一句翻完了再一起按顺序写。
        var book = BookWith("one.", "two.");
        var first = book.DequeueMerged()!;
        var second = book.DequeueMerged()!;
        book.MarkTranslating(first.Id, T0);
        book.MarkTranslating(second.Id, T0);
        book.Finish(second.Id, "二", null, null, false, T0);
        Assert.Empty(book.TakePersistable());
        book.Fail(first.Id, T0);
        Assert.Equal(["one.", "two."], book.TakePersistable().Select(l => l.Source));
        Assert.Empty(book.TakePersistable());
    }

    [Fact]
    public void TakeRemainingWritesTheRestOnceAndClearResetsTheCursor()
    {
        var book = BookWith("one.", "two.");
        var first = book.DequeueMerged()!;
        book.Finish(first.Id, "一", null, null, false, T0);
        Assert.Single(book.TakePersistable());
        Assert.Equal(["two."], book.TakeRemaining().Select(l => l.Source));
        // 退出前已经只写了原文的句子，之后翻完了也不再写一遍。
        var second = book.DequeueMerged()!;
        book.Finish(second.Id, "二", null, null, false, T0);
        Assert.Empty(book.TakePersistable());

        book.Clear();
        book.Enqueue(["three."], null, null, T0, T0);
        var third = book.DequeueMerged()!;
        book.Finish(third.Id, "三", null, null, false, T0);
        Assert.Equal(["three."], book.TakePersistable().Select(l => l.Source));
    }

    [Fact]
    public void SingleQueuedLinesAreNotMerged()
    {
        var book = BookWith("one two three four.", "five six seven eight.");
        var first = book.DequeueMerged()!;
        Assert.Equal("one two three four.", first.Source);
        Assert.Equal(1, book.Backlog);
        Assert.Equal(2, book.Lines.Count);
    }

    [Fact]
    public void BacklogOfTwoOrMoreIsMergedIntoTheFirst()
    {
        var book = BookWith("a b c d.", "e f g h.", "i j k l.");
        var first = book.DequeueMerged()!;
        Assert.Equal("a b c d. e f g h. i j k l.", first.Source);
        Assert.Equal(0, book.Backlog);
        Assert.Single(book.Lines);
    }

    [Fact]
    public void MergeStopsAt70UnitsAndAtADifferentSource()
    {
        var long40 = string.Join(" ", Enumerable.Repeat("word", 40));
        var book = BookWith(long40, long40, "short one here.", "and more.");
        var first = book.DequeueMerged()!;
        Assert.Equal(long40, first.Source); // 40 + 40 > 70，不合并
        Assert.Equal(3, book.Backlog);

        var mixed = new SubtitleBook();
        mixed.Enqueue(["a b c d."], "麦克风", null, T0, T0);
        mixed.Enqueue(["e f g h.", "i j k l."], "系统音频", null, T0, T0);
        Assert.Equal("a b c d.", mixed.DequeueMerged()!.Source);
    }

    [Fact]
    public void MergedLineTakesTheLastAudioEnd()
    {
        var timeline = new SegmentTimeline();
        timeline.AddFinal("a b c d.", 0, 2);
        timeline.AddFinal("e f g h.", 2, 4);
        timeline.AddFinal("i j k l.", 4, 6);
        var book = new SubtitleBook();
        book.Enqueue(["a b c d.", "e f g h.", "i j k l."], null, timeline, T0, T0);
        var first = book.DequeueMerged()!;
        Assert.Equal(T0, first.AudioStart);
        Assert.Equal(T0.AddSeconds(6), first.AudioEnd);
    }

    [Fact]
    public void RecentContextUsesLastSixFinishedLines()
    {
        var book = BookWith(Enumerable.Range(0, 9).Select(i => $"s{i}").ToArray());
        for (int i = 0; i < 8; i++)
        {
            var line = book.Lines[i];
            if (i == 3) continue; // 这一句没翻完，不算上下文
            book.Finish(line.Id, $"t{i}", null, null, false, T0);
        }
        var context = book.RecentContext(book.Lines[8].Id);
        Assert.Equal(["s1", "s2", "s4", "s5", "s6", "s7"], context.Select(c => c.Source));
        Assert.Equal("t7", context[^1].Translation);
    }

    [Fact]
    public void PartialOnlyGrowsWhileTranslating()
    {
        var book = BookWith("hello.");
        var line = book.DequeueMerged()!;
        Assert.False(book.UpdatePartial(line.Id, "你", T0)); // 还没开始翻译
        book.MarkTranslating(line.Id, T0);
        Assert.True(book.UpdatePartial(line.Id, "你好", T0));
        Assert.False(book.UpdatePartial(line.Id, "你", T0));
        Assert.Equal("你好", line.Translation);
        Assert.Equal(T0, line.FirstTokenAt);
    }

    [Fact]
    public void FinishWithCorrectionKeepsOriginalWhenAsked()
    {
        var book = BookWith("use dynamic program.");
        var line = book.Lines[0];
        book.Finish(line.Id, "用动态规划。", null, "use dynamic programming.", keepOriginal: true, T0);
        Assert.Equal("use dynamic programming.", line.Source);
        Assert.Equal("use dynamic program.", line.OriginalSource);
        Assert.Equal(LineState.Done, line.State);
    }

    [Fact]
    public void SubtitleFileFollowsDisplayMode()
    {
        var timeline = new SegmentTimeline();
        timeline.AddFinal("Hello there.", 1, 2.5);
        var book = new SubtitleBook();
        book.Enqueue(["Hello there."], null, timeline, T0, T0.AddSeconds(3));
        book.Finish(book.Lines[0].Id, "你好。", null, null, false, T0);

        Assert.Equal("1\n00:00:01,000 --> 00:00:02,500\nHello there.\n你好。\n", book.SubtitleFile(SubtitleFileFormat.Srt, DisplayMode.Bilingual, T0));
        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:02.500\n你好。\n", book.SubtitleFile(SubtitleFileFormat.Vtt, DisplayMode.TranslationOnly, T0));
        Assert.Contains("Hello there.\n", book.SubtitleFile(SubtitleFileFormat.Srt, DisplayMode.SourceOnly, T0));
    }
}
