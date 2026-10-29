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
        var t0 = new DateTime(2026, 10, 21, 13, 0, 0, DateTimeKind.Utc);
        var m = new RecognitionQualityMonitor();
        m.Add(Repeat(0.7, 60), t0);
        Assert.True(m.IsDifficult);
        // 0.83 介于两个阈值之间：保持难识别
        m.Add(Repeat(0.83, 60), t0.AddSeconds(5));
        Assert.True(m.IsDifficult);
        // 清楚了，但要连续清楚 60 秒才恢复
        Assert.False(m.Add(Repeat(0.92, 60), t0.AddSeconds(10)));
        Assert.False(m.Add(Repeat(0.92, 12), t0.AddSeconds(40)));
        var recovered = m.Add(Repeat(0.92, 12), t0.AddSeconds(70));
        Assert.True(recovered);
        Assert.False(m.IsDifficult);
    }

    [Fact]
    public void BriefClearSpellKeepsCorrection()
    {
        // 口音重的讲者中间有二十几秒说得清楚：不要关掉纠正。每 5 秒一句，每句 12 个词。
        var t0 = new DateTime(2026, 10, 21, 13, 0, 0, DateTimeKind.Utc);
        var m = new RecognitionQualityMonitor();
        m.Add(Repeat(0.72, 60), t0);
        Assert.True(m.IsDifficult);
        int changes = 0;
        void Say(double confidence, double second)
        {
            if (m.Add(Repeat(confidence, 12), t0.AddSeconds(second))) changes++;
        }
        for (double s = 5; s <= 25; s += 5) Say(0.97, s);   // 第 15 秒起平均高于 0.86，开始计时
        Say(0.3, 30);                                          // 一句没认准：平均掉下来，重新计时
        for (double s = 35; s <= 80; s += 5) Say(0.97, s);  // 第 55 秒起重新计时
        // 不重新计时的话，第 75 秒（15 + 60）就恢复了
        Assert.Equal(0, changes);
        Assert.True(m.IsDifficult);
        for (double s = 85; s <= 115; s += 5) Say(0.97, s);
        Assert.Equal(1, changes);
        Assert.False(m.IsDifficult);
    }

    [Fact]
    public void OneUnclearSentenceDoesNotStartCorrection()
    {
        var m = new RecognitionQualityMonitor();
        m.Add(Repeat(0.92, 48));
        Assert.False(m.Add(Repeat(0.55, 12)));
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

    [Fact]
    public void SourceLabelFollowsTheTimeInBold()
    {
        // “两者”模式下每句带音源标签，和时间一起加粗（与 macOS 版相同）；只有一个音源时没有标签。
        var time = new DateTime(2026, 10, 21, 14, 3, 9, DateTimeKind.Local);
        Assert.Equal("**[14:03:09] 系统音频** next slide.\n> 下一页。\n\n", TranscriptFormat.Entry(time, "系统音频", "next slide.", "下一页。"));
        Assert.Equal("**[14:03:09] Microphone** a question.\n*识别原文：a quest in.*\n\n",
            TranscriptFormat.Entry(time, "Microphone", "a question.", "", "a quest in."));
        Assert.Equal("**[14:03:09]** next slide.\n\n", TranscriptFormat.Entry(time, null, "next slide.", ""));
    }

    // 与 macOS 版 TranscriptParseTests 相同。
    [Fact]
    public void ParseReadsBackWhatEntryWrites()
    {
        var day = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Local);
        var text = TranscriptFormat.Header("2026-10-28 13.11 字幕记录", day, LanguageOption.Source("en-US"), LanguageOption.Target("zh-Hans"))
            + TranscriptFormat.Entry(day.AddSeconds(13 * 3600 + 17 * 60 + 33), null, "Now, when", "现在，当")
            + TranscriptFormat.Entry(day.AddSeconds(13 * 3600 + 18 * 60), "麦克风", "Okay.", "第一行\n第二行", "Okay, okay.")
            + TranscriptFormat.Entry(day.AddSeconds(13 * 3600 + 18 * 60 + 5), null, "Untranslated.", "");
        Assert.Equal(
        [
            new TranscriptFormat.ParsedEntry(47853, null, "Now, when", null, "现在，当"),
            new TranscriptFormat.ParsedEntry(47880, "麦克风", "Okay.", "Okay, okay.", "第一行\n第二行"),
            new TranscriptFormat.ParsedEntry(47885, null, "Untranslated.", null, ""),
        ], TranscriptFormat.Parse(text));
    }

    [Fact]
    public void ParseReadsEnglishOriginalAndWindowsLineEndings()
    {
        var text = "# 2026-10-28 13.11 Transcript\r\n\r\n- Started: 2026-10-28 13:11\r\n\r\n**[09:00:01]** Hi.\r\n*Recognized: High.*\r\n> 你好。\r\n";
        Assert.Equal([new TranscriptFormat.ParsedEntry(32401, null, "Hi.", "High.", "你好。")], TranscriptFormat.Parse(text));
    }
}
