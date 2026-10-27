using System.Text;
using System.Text.Json;
using LiveSub.Core;

namespace LiveSub.Core.Tests;

// 与 macOS 版 Tests/LiveSubCoreTests/SegmenterTests.swift 相同的输入与期望输出。

public class SegmenterTests
{
    private static readonly DateTime T0 = DateTime.UnixEpoch.AddSeconds(1_000_000);

    [Fact]
    public void CompleteSentenceIsEmittedImmediately()
    {
        var s = new Segmenter();
        var output = s.AddFinal("Today we're going to talk about eigenvalues.", T0);
        Assert.Equal(["Today we're going to talk about eigenvalues."], output);
        Assert.Empty(s.Pending);
    }

    [Fact]
    public void MultipleSentencesInOneFinalAreSplit()
    {
        var s = new Segmenter();
        var output = s.AddFinal("An eigenvector only gets scaled. So A times v equals lambda v. And then", T0);
        Assert.Equal(["An eigenvector only gets scaled.", "So A times v equals lambda v."], output);
        Assert.Equal("And then", s.Pending);
    }

    [Fact]
    public void ShortSentencesAreMergedWithFollowing()
    {
        var s = new Segmenter();
        Assert.Empty(s.AddFinal("Okay.", T0));
        var output = s.AddFinal("Let's get started with today's topic.", T0);
        Assert.Equal(["Okay. Let's get started with today's topic."], output);
    }

    [Fact]
    public void DecimalsAndAbbreviationsDoNotSplit()
    {
        var s = new Segmenter();
        var output = s.AddFinal("Pi is about 3.14, e.g. in circles, as Dr. Smith said.", T0);
        Assert.Equal(["Pi is about 3.14, e.g. in circles, as Dr. Smith said."], output);
    }

    [Fact]
    public void IdleFlushSendsRemainder()
    {
        var s = new Segmenter(new Segmenter.Config(4, 32, 1.5));
        Assert.Empty(s.AddFinal("so the next thing we need", T0));
        Assert.Empty(s.Tick(T0.AddSeconds(1.0), speechInProgress: false));
        Assert.Equal(["so the next thing we need"], s.Tick(T0.AddSeconds(1.6), speechInProgress: false));
        Assert.Empty(s.Pending);
    }

    [Fact]
    public void IdleFlushWaitsLongerWhileSpeechContinues()
    {
        var s = new Segmenter(new Segmenter.Config(4, 32, 1.5));
        _ = s.AddFinal("so the next thing we need", T0);
        Assert.Empty(s.Tick(T0.AddSeconds(2.0), speechInProgress: true));
        Assert.Single(s.Tick(T0.AddSeconds(4.6), speechInProgress: true));
    }

    [Fact]
    public void LongRunOnIsSplitAtComma()
    {
        var s = new Segmenter(new Segmenter.Config(4, 12, 1.5));
        var output = s.AddFinal("we take the matrix and we subtract lambda times the identity, and then we compute the determinant and set it to zero and", T0);
        Assert.Equal("we take the matrix and we subtract lambda times the identity,", output.First());
        Assert.True(Segmenter.Units(s.Pending) <= 12);
    }

    [Fact]
    public void LongCompleteSentenceIsSplitAtComma()
    {
        var s = new Segmenter(new Segmenter.Config(4, 20, 1.5));
        var output = s.AddFinal("Today we are going to talk about eigenvalues and eigenvectors, an eigenvector of the matrix is a nonzero vector that only gets scaled.", T0);
        Assert.Equal(["Today we are going to talk about eigenvalues and eigenvectors,",
                      "an eigenvector of the matrix is a nonzero vector that only gets scaled."], output);
        Assert.Empty(s.Pending);
    }

    [Fact]
    public void ChineseSentencesSplitWithoutSpaces()
    {
        var s = new Segmenter(new Segmenter.Config(3, 40, 1.5));
        var output = s.AddFinal("今天我们来讲特征值。特征向量只会被缩放。然后", T0);
        Assert.Equal(["今天我们来讲特征值。", "特征向量只会被缩放。"], output);
        Assert.Equal("然后", s.Pending);
    }

    [Fact]
    public void UnitsCountsWordsAndCJK()
    {
        Assert.Equal(2, Segmenter.Units("hello world"));
        Assert.Equal(2, Segmenter.Units("特征值"));
        Assert.Equal(0, Segmenter.Units(""));
    }

    // 以下为 Windows 版补充的用例。

    [Fact]
    public void JoinAddsSpaceOnlyBetweenNonCJK()
    {
        Assert.Equal("a b", Segmenter.Join("a", "b"));
        Assert.Equal("你好world", Segmenter.Join("你好", "world"));
        Assert.Equal("x", Segmenter.Join("", "x"));
    }

    [Fact]
    public void FlushAllReturnsTrimmedRemainder()
    {
        var s = new Segmenter();
        _ = s.AddFinal("  and then  ", T0);
        Assert.Equal(["and then"], s.FlushAll());
        Assert.Empty(s.FlushAll());
    }

    [Fact]
    public void DefaultMaxUnitsMatchesMacOS()
    {
        // 2026-10-22 macOS 从 32 降到 24：一条字幕不要太长。
        Assert.Equal(24, new Segmenter.Config().MaxUnits);
    }

    [Fact]
    public void FlushAllSplitsLongTextAtClauseBoundaries()
    {
        // 与 macOS 的 flushAll 相同：太长时同样在从句边界切成几条，字幕不会一条占满整个窗口。
        // 平时送入确认结果时就切好了，这里先用较大的上限攒下一长段，再换成小的上限送出。
        var s = new Segmenter(new Segmenter.Config(4, 40, 1.5));
        Assert.Empty(s.AddFinal("we take the matrix and we subtract lambda times the identity, and then we compute the determinant and set it to zero and", T0));
        s.Settings = new Segmenter.Config(4, 12, 1.5);
        var output = s.FlushAll();
        Assert.True(output.Count >= 2);
        Assert.Equal("we take the matrix and we subtract lambda times the identity,", output[0]);
        Assert.All(output, o => Assert.True(Segmenter.Units(o) <= 12));
        Assert.Equal("we take the matrix and we subtract lambda times the identity, and then we compute the determinant and set it to zero and",
                     string.Join(" ", output));
        Assert.Empty(s.Pending);
    }
}

public class ClaudeStreamTests
{
    [Fact]
    public void ParsesTextDelta()
    {
        var line = """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"今天"}}}""";
        Assert.Equal(new ClaudeStreamEvent.TextDelta("今天"), ClaudeStream.Parse(line));
    }

    [Fact]
    public void ParsesErrorResult()
    {
        var line = """{"type":"result","subtype":"success","is_error":true,"result":"Not logged in · Please run /login"}""";
        Assert.Equal(new ClaudeStreamEvent.Result("Not logged in · Please run /login", true), ClaudeStream.Parse(line));
    }

    [Fact]
    public void ParsesSuccessResultAndInit()
    {
        Assert.Equal(new ClaudeStreamEvent.Result("你好", false),
            ClaudeStream.Parse("""{"type":"result","subtype":"success","is_error":false,"result":"你好"}"""));
        Assert.Equal(new ClaudeStreamEvent.Initialized("claude-haiku-5-5"),
            ClaudeStream.Parse("""{"type":"system","subtype":"init","model":"claude-haiku-5-5"}"""));
        Assert.Null(ClaudeStream.Parse("not json"));
    }

    [Fact]
    public void UserMessageIsSingleLineJSON()
    {
        var line = ClaudeStream.UserMessageLine("line1\nline2 \"q\"");
        Assert.EndsWith("\n", line);
        Assert.DoesNotContain("\n", line[..^1]);
        using var doc = JsonDocument.Parse(line);
        var text = doc.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Equal("line1\nline2 \"q\"", text);
    }

    [Fact]
    public void LineBufferHandlesSplitUTF8()
    {
        var buf = new LineBuffer();
        var bytes = Encoding.UTF8.GetBytes("你好\nworld\n");
        var lines = new List<string>();
        lines.AddRange(buf.Append(bytes.AsSpan(0, 2))); // 切在“你”的中间
        lines.AddRange(buf.Append(bytes.AsSpan(2)));
        Assert.Equal(["你好", "world"], lines);
    }

    // Windows 版补充：assistant 消息与非成功的 subtype。

    [Fact]
    public void ParsesAssistantTextAndErrorSubtype()
    {
        Assert.Equal(new ClaudeStreamEvent.AssistantText("ab"),
            ClaudeStream.Parse("""{"type":"assistant","message":{"content":[{"type":"text","text":"a"},{"type":"tool_use"},{"type":"text","text":"b"}]}}"""));
        Assert.Equal(new ClaudeStreamEvent.Result("", true),
            ClaudeStream.Parse("""{"type":"result","subtype":"error_max_turns"}"""));
        Assert.Equal(new ClaudeStreamEvent.Other(), ClaudeStream.Parse("""{"type":"rate_limit_event"}"""));
    }
}

public class PromptTests
{
    [Fact]
    public void ContextMessage()
    {
        Assert.Equal("hi", Prompts.TranslationMessage("hi", []));
        var m = Prompts.TranslationMessage("next", [("a", "甲")]);
        Assert.Equal("[Context]\na\n→ 甲\n[Translate]\nnext", m);
    }

    [Fact]
    public void SystemPromptWithoutAccent()
    {
        var p = Prompts.TranslatorSystemPrompt(LanguageOption.Source("en-US"), LanguageOption.Target("zh-Hans"));
        Assert.Contains("Simplified Chinese", p);
        Assert.Contains("Reply: 欢迎大家。", p);
        Assert.Contains("Reply: 大家都能看到黑板吗？", p);
        Assert.Contains("never answer", p);
        Assert.DoesNotContain("Recognition is unreliable", p);
        // 识别清晰时不要求输出纠正行（这条规则会让模型每句都犹豫、把思考过程写出来）。
        Assert.DoesNotContain(TranslationOutput.Marker, p);
    }

    [Fact]
    public void SystemPromptAsksForCorrectionsWhenRecognitionIsHard()
    {
        var p = Prompts.TranslatorSystemPrompt(LanguageOption.Source("en-US"), LanguageOption.Target("zh-Hans"), correctMishearings: true);
        Assert.Contains("Recognition is unreliable", p);
        Assert.Contains(TranslationOutput.Marker, p);
    }

    // Windows 版补充：提示词的整体结构与 macOS 版逐字一致。

    [Fact]
    public void SystemPromptLayoutMatchesMacOS()
    {
        var p = Prompts.TranslatorSystemPrompt(LanguageOption.Source("en-US"), LanguageOption.Target("zh-Hans"), correctMishearings: true);
        Assert.StartsWith("You are a real-time subtitle translator. Each user message is a fragment of live speech, transcribed by speech recognition from English audio", p);
        Assert.Contains("never answer, agree, or reply to them.\n\nExamples\nFragment: Welcome, everyone. Today we're going to look at eigenvalues.\nReply: 欢迎大家。今天我们来看特征值（eigenvalue）。\nFragment: Can everyone see the board?\nReply: 大家都能看到黑板吗？\n\nRecognition is unreliable", p);
        Assert.Contains("earlier lines.\n\nThe English line is also shown to the viewer. If you corrected such recognition errors, put one extra line after the translation: @@src: <the corrected English fragment>.", p);
        Assert.EndsWith("If nothing needed correcting, output only the translation.", p);
        Assert.Equal(10, p.Split('\n').Count(l => l.StartsWith("- ")));
    }

    [Fact]
    public void TraditionalChineseUsesTraditionalExamplesAndOthersHaveNone()
    {
        var hant = Prompts.TranslatorSystemPrompt(LanguageOption.Source("en-US"), LanguageOption.Target("zh-Hant"));
        Assert.Contains("Reply: 大家都看得到黑板嗎？", hant);
        var ja = Prompts.TranslatorSystemPrompt(LanguageOption.Source("en-US"), LanguageOption.Target("ja"));
        Assert.DoesNotContain("Examples", ja);
        Assert.EndsWith("never answer, agree, or reply to them.", ja);
    }
}

public class TranslationOutputTests
{
    [Fact]
    public void PlainTranslation()
    {
        var output = TranslationOutput.Split("所以解决办法是用动态规划。\n");
        Assert.Equal("所以解决办法是用动态规划。", output.Translation);
        Assert.Null(output.CorrectedSource);
    }

    [Fact]
    public void TranslationWithCorrectedSource()
    {
        var output = TranslationOutput.Split("所以解决办法是使用动态规划 (dynamic programming)。\n@@src: So the solution would be to use dynamic programming.");
        Assert.Equal("所以解决办法是使用动态规划 (dynamic programming)。", output.Translation);
        Assert.Equal("So the solution would be to use dynamic programming.", output.CorrectedSource);
    }

    [Fact]
    public void EmptyCorrectionIsIgnored()
    {
        Assert.Null(TranslationOutput.Split("好的。\n@@src:  ").CorrectedSource);
    }

    [Fact]
    public void FormatTagsLeakingIntoTranslationAreRemoved()
    {
        var raw = "[Translate] 的 — the fragment is unfinished, so I translate it as is.\n\n但这是一个拓扑序……";
        Assert.Equal("但这是一个拓扑序……", TranslationOutput.Split(raw, targetIsCJK: true).Translation);
    }

    [Fact]
    public void ThinkingOutLoudAfterMarkerIsIgnored()
    {
        var original = "That is called edit distance.";
        var raw = "这叫编辑距离（edit distance）。\n@@src: That is called edit distance.\n\nWait—I should check: nothing needed correcting, so I should not include this line.";
        var output = TranslationOutput.Split(raw, original, targetIsCJK: true);
        Assert.Equal("这叫编辑距离（edit distance）。", output.Translation);
        Assert.Null(output.CorrectedSource); // 和原文一样，不算纠正
    }

    [Fact]
    public void ImplausibleCorrectionIsRejected()
    {
        var original = "use dynamic program.";
        Assert.Equal("use dynamic programming.", TranslationOutput.Split("用动态规划。\n@@src: use dynamic programming.", original).CorrectedSource);
        var rambling = "用动态规划。\n@@src: use dynamic programming, which as I explained earlier in the lecture means storing results.";
        Assert.Null(TranslationOutput.Split(rambling, original).CorrectedSource);
    }

    [Fact]
    public void EnglishOnlyLinesAreKeptForNonCJKTargetsAndShortTokens()
    {
        // 目标语言不是中日韩文时不按“没有汉字”过滤；短的英文（网址、代码）也保留。
        Assert.Equal("Let me think about this sentence.", TranslationOutput.Split("Let me think about this sentence.", targetIsCJK: false).Translation);
        Assert.Equal("sourceforge.net\n网址如上", TranslationOutput.Split("sourceforge.net\n网址如上", targetIsCJK: true).Translation);
    }

    [Fact]
    public void PartialHidesMarker()
    {
        Assert.Equal("最小生成树", TranslationOutput.VisibleTranslation("最小生成树"));
        Assert.Equal("最小生成树。", TranslationOutput.VisibleTranslation("最小生成树。\n@"));
        Assert.Equal("最小生成树。", TranslationOutput.VisibleTranslation("最小生成树。\n@@sr"));
        Assert.Equal("最小生成树。", TranslationOutput.VisibleTranslation("最小生成树。\n@@src: minimum spa"));
        Assert.Equal("邮箱 a@b.com", TranslationOutput.VisibleTranslation("邮箱 a@b.com"));
        Assert.Equal("好的。", TranslationOutput.VisibleTranslation("好的。\nWait, I should double check this", targetIsCJK: true));
    }

    // Windows 版补充：Windows 换行。

    [Fact]
    public void WindowsLineEndingsAreHandled()
    {
        var output = TranslationOutput.Split("好的。\r\n@@src: okay then.\r\n", "okay than.");
        Assert.Equal("好的。", output.Translation);
        Assert.Equal("okay then.", output.CorrectedSource);
    }
}

// 与 macOS 版 CollapseRepeatsTests 相同。
public class CollapseRepeatsTests
{
    [Fact]
    public void CollapsesRunsOfThreeOrMore()
    {
        Assert.Equal("Okay, okay.", Segmenter.CollapseRepeats("Okay, okay, okay, okay."));
        Assert.Equal("say, to, to", Segmenter.CollapseRepeats("say, to, to, to, to, to, to"));
        Assert.Equal("Yeah. Yeah.", Segmenter.CollapseRepeats("Yeah. Yeah. Yeah."));
    }

    [Fact]
    public void KeepsOrdinaryRepeats()
    {
        Assert.Equal("Okay, okay. So, which one?", Segmenter.CollapseRepeats("Okay, okay. So, which one?"));
        Assert.Equal("It is what it is", Segmenter.CollapseRepeats("It is what it is"));
        Assert.Equal("", Segmenter.CollapseRepeats(""));
    }

    [Fact]
    public void TopicBlockComesFirstAndIsSentOnlyWhenChanged()
    {
        Assert.Equal("[Topic]\nTopic: graphs\n[Translate]\nhi", Prompts.TranslationMessage("hi", [], "Topic: graphs"));
        var note = new LiveSub.Core.Chat.TopicNote();
        Assert.Equal("hi", note.OngoingMessage("hi"));
        note.Set("Topic: graphs");
        Assert.Equal("[Topic]\nTopic: graphs\n[Translate]\nhi", note.OngoingMessage("hi"));
        Assert.Equal("hi", note.OngoingMessage("hi"));
        Assert.Equal("hi", TranslationOutput.Split("hi\n[Topic]").Translation);
    }
}
