using LiveSub.Core;

namespace LiveSub.Core.Tests;

// 与 macOS 版 Tests/LiveSubCoreTests/RecognitionQualityTests.swift 相同的输入与期望输出。

public class RecognitionQualityTests
{
    private static double[] Repeat(double v, int n) => Enumerable.Repeat(v, n).ToArray();

    [Fact]
    public void ClearSpeechStaysClear()
    {
        var m = new RecognitionQualityMonitor();
        var changed = m.Add(Repeat(0.93, 30));
        Assert.False(changed);
        Assert.False(m.IsDifficult);
    }

    [Fact]
    public void AccentedSpeechTurnsOnCorrection()
    {
        var m = new RecognitionQualityMonitor();
        var tooFew = m.Add(Repeat(0.72, 10)); // 词太少，先不判断
        Assert.False(tooFew);
        var changed = m.Add(Repeat(0.74, 15));
        Assert.True(changed);
        Assert.True(m.IsDifficult);
    }

    [Fact]
    public void HysteresisAvoidsFlipping()
    {
        var m = new RecognitionQualityMonitor();
        m.Add(Repeat(0.7, 40));
        Assert.True(m.IsDifficult);
        // 0.83 介于两个阈值之间：保持难识别
        m.Add(Repeat(0.83, 40));
        Assert.True(m.IsDifficult);
        var recovered = m.Add(Repeat(0.92, 40));
        Assert.True(recovered);
        Assert.False(m.IsDifficult);
    }

    [Fact]
    public void TimelineSplitsOneFinalIntoTwoSegments()
    {
        var t = new SegmentTimeline();
        t.AddFinal("aaaa bbbb", 10, 18); // 8 个非空白字符
        var first = t.Take("aaaa");
        var second = t.Take("bbbb");
        Assert.Equal((10.0, 14.0), first);
        Assert.Equal((14.0, 18.0), second);
        Assert.Null(t.Take("more"));
    }

    [Fact]
    public void TimelineMergesTwoFinalsIntoOneSegment()
    {
        var t = new SegmentTimeline();
        t.AddFinal("Okay.", 1, 2);
        t.AddFinal("Let's start.", 3, 5);
        Assert.Equal((1.0, 5.0), t.Take("Okay. Let's start."));
    }

    [Fact]
    public void SrtAndVttFormatting()
    {
        SubtitleExport.Cue[] cues =
        [
            new(3661.5, 3663, "第二句"),
            new(0.25, 0.5, "第一句\nFirst"),
            new(5, 6, "  "),
        ];
        Assert.Equal("""
            1
            00:00:00,250 --> 00:00:01,250
            第一句
            First

            2
            01:01:01,500 --> 01:01:03,000
            第二句

            """, SubtitleExport.Srt(cues).Replace("\r\n", "\n"));
        Assert.StartsWith("WEBVTT\n\n00:00:00.250 --> 00:00:01.250\n第一句\nFirst\n", SubtitleExport.Vtt(cues));
    }

    [Fact]
    public void CuesDoNotOverlap()
    {
        var tidy = SubtitleExport.Tidy([new(0, 5, "a"), new(2, 3, "b")]);
        Assert.Equal(2, tidy[0].End);
    }
}

public class AssistantPromptTests
{
    private static readonly DateTime T0 = DateTime.UnixEpoch.AddSeconds(1_000_000);

    [Fact]
    public void QuestionOnly()
    {
        var m = AssistantPrompt.Message([], "  ", " 什么是记忆化？ ");
        Assert.Equal("[我的问题]\n什么是记忆化？", m);
    }

    [Fact]
    public void NewLinesSpeakingAndQuestion()
    {
        AssistantPrompt.Line[] lines =
        [
            new(T0, "So we fill the table."),
            new(T0.AddSeconds(65), "Then we go back from the corner."),
        ];
        var m = AssistantPrompt.Message(lines, "and that gives", "回溯怎么走？");
        var a = TranscriptFormat.TimeStamp(T0);
        var b = TranscriptFormat.TimeStamp(T0.AddSeconds(65));
        Assert.Equal($"""
            [字幕 · 新增 2 句 · {a}–{b}]
            [{a}] So we fill the table.
            [{b}] Then we go back from the corner.

            [正在说（还没说完）]
            and that gives

            [我的问题]
            回溯怎么走？
            """, m);
    }

    [Fact]
    public void SystemPromptUsesTargetLanguage()
    {
        var p = AssistantPrompt.System(LanguageOption.Source("en-US"), LanguageOption.Target("zh-Hans"));
        Assert.Contains("Answer in Simplified Chinese", p);
    }

    [Fact]
    public void TranscriptEntryKeepsOriginalOnlyWhenDifferent()
    {
        var time = DateTime.UnixEpoch.AddSeconds(1_000_000);
        var stamp = TranscriptFormat.TimeStamp(time);
        var corrected = TranscriptFormat.Entry(time, null, "use dynamic programming.", "用动态规划。", "use dynamic program.");
        Assert.Equal($"**[{stamp}]** use dynamic programming.\n*识别原文：use dynamic program.*\n> 用动态规划。\n\n", corrected);
        var same = TranscriptFormat.Entry(time, null, "okay.", "好。", "okay.");
        Assert.Equal($"**[{stamp}]** okay.\n> 好。\n\n", same);
    }

    // Windows 版补充：记录文件头与多行译文。

    [Fact]
    public void HeaderAndMultilineTranslation()
    {
        var started = new DateTime(2026, 10, 19, 9, 5, 0, DateTimeKind.Local);
        var header = TranscriptFormat.Header("2026-10-19 09.05 字幕记录", started, LanguageOption.Source("en-US"), LanguageOption.Target("zh-Hans"));
        Assert.Equal("# 2026-10-19 09.05 字幕记录\n\n- 开始时间：2026-10-19 09:05\n- 语言：美式英语 → 简体中文\n\n", header);
        Assert.Equal("2026-10-19 09.05", TranscriptFormat.FileStamp(started));
        var english = TranscriptFormat.Header("2026-10-19 09.05 Transcript", started, LanguageOption.Source("en-US"), LanguageOption.Target("zh-Hans"), TranscriptLabels.English);
        Assert.Equal("# 2026-10-19 09.05 Transcript\n\n- Started：2026-10-19 09:05\n- Language：American English → Simplified Chinese\n\n", english);
        var entry = TranscriptFormat.Entry(started, "麦克风", "a", "一\n二");
        Assert.Equal("**[09:05:00] 麦克风** a\n> 一\n> 二\n\n", entry);
    }
}
