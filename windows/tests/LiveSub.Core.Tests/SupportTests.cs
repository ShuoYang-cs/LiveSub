using LiveSub.Core;
using LiveSub.Core.Speech;

namespace LiveSub.Core.Tests;

public class ClaudeCliTests
{
    [Fact]
    public void ArgumentsMatchMacOS()
    {
        var args = ClaudeCli.TranslatorArguments("haiku", "SYS\nPROMPT");
        Assert.Equal(
            ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
             "--model", "haiku", "--tools", "", "--setting-sources", "", "--strict-mcp-config", "--disable-slash-commands",
             "--no-session-persistence", "--system-prompt", "SYS\nPROMPT"],
            args);
    }

    [Fact]
    public void EnvironmentDropsNestedSessionMarkersAndPrependsPath()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = @"C:\Windows",
            ["CLAUDECODE"] = "1",
            ["CLAUDE_CODE_ENTRYPOINT"] = "cli",
            ["CLAUDE_CODE_SSE_PORT"] = "1234",
            ["CLAUDE_PID"] = "42",
            ["CLAUDE_CODE_GIT_BASH_PATH"] = @"C:\Git\bin\bash.exe",
            ["CLAUDE_CODE_USE_BEDROCK"] = "1",
            ["HOME"] = "x",
        };
        ClaudeCli.PrepareEnvironment(env, [@"C:\Users\u\.local\bin"], ';');
        Assert.False(env.ContainsKey("CLAUDECODE"));
        Assert.False(env.ContainsKey("CLAUDE_CODE_ENTRYPOINT"));
        Assert.False(env.ContainsKey("CLAUDE_CODE_SSE_PORT"));
        Assert.False(env.ContainsKey("CLAUDE_PID"));
        // 用户自己的 Claude Code 配置要保留。
        Assert.Equal(@"C:\Git\bin\bash.exe", env["CLAUDE_CODE_GIT_BASH_PATH"]);
        Assert.Equal("1", env["CLAUDE_CODE_USE_BEDROCK"]);
        Assert.Equal(@"C:\Users\u\.local\bin;C:\Windows", env["PATH"]);
        Assert.Equal("0", env["MAX_THINKING_TOKENS"]);
        Assert.Equal("1", env["DISABLE_AUTOUPDATER"]);
        Assert.Equal("1", env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);
        Assert.Equal("x", env["HOME"]);
    }

    [Fact]
    public void TurnPrefersResultThenAssistantThenPartial()
    {
        var turn = new ClaudeTurn();
        Assert.Equal(ClaudeTurn.Outcome.PartialChanged, turn.Apply(new ClaudeStreamEvent.TextDelta("你")));
        Assert.Equal(ClaudeTurn.Outcome.PartialChanged, turn.Apply(new ClaudeStreamEvent.TextDelta("好")));
        Assert.Equal("你好", turn.Partial);
        turn.Apply(new ClaudeStreamEvent.AssistantText("你好。"));
        Assert.Equal(ClaudeTurn.Outcome.Completed, turn.Apply(new ClaudeStreamEvent.Result("  ", false)));
        Assert.Equal("你好。", turn.FinalText);

        var failed = new ClaudeTurn();
        Assert.Equal(ClaudeTurn.Outcome.Failed, failed.Apply(new ClaudeStreamEvent.Result("Not logged in · Please run /login", true)));
        Assert.True(ClaudeCli.IsNotLoggedIn(failed.FinalText));
    }
}

public class WhisperSupportTests
{
    [Fact]
    public void ModelUrlsAndDefault()
    {
        Assert.Equal("base", WhisperModel.Default.Id);
        Assert.Equal("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin", WhisperModel.Small.DownloadUrl.ToString());
        Assert.Equal("https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-base.bin", WhisperModel.Base.DownloadUrlFrom("https://hf-mirror.com/").ToString());
        Assert.Equal(WhisperModel.Base.DownloadUrl, WhisperModel.Base.DownloadUrlFrom("  "));
        Assert.Equal(WhisperModel.Base, WhisperModel.Find("nope"));
        Assert.False(WhisperModel.Tiny.LooksComplete(1_000_000));
        Assert.True(WhisperModel.Tiny.LooksComplete(77_691_713));
    }

    [Fact]
    public void AnnotationsAndHallucinationsAreDropped()
    {
        Assert.Null(RecognitionText.Clean("[BLANK_AUDIO]"));
        Assert.Null(RecognitionText.Clean(" (music) ♪ ♪ "));
        Assert.Null(RecognitionText.Clean(" Thank you for watching!"));
        Assert.Null(RecognitionText.Clean("字幕由Amara.org社区提供"));
        Assert.Equal("So we start here.", RecognitionText.Clean(" [Music] So we start here."));
        Assert.Equal("Thank you for watching the demo.", RecognitionText.Clean("Thank you for watching the demo."));
    }

    [Fact]
    public void RepetitionLoopsAreCollapsed()
    {
        Assert.Equal("the end.", RecognitionText.Clean("the the the the the the end."));
        Assert.Equal("no no no", RecognitionText.Clean("no no no"));
    }

    [Fact]
    public void WordConfidencesGroupTokensIntoWords()
    {
        TokenProbability[] tokens =
        [
            new("[_BEG_]", 1f), new(" Eigen", 0.6f), new("values", 0.8f), new(" are", 0.9f), new(",", 0.2f),
            new(" cool", 1f), new("<|0.00|>", 0.1f),
        ];
        Assert.Equal([0.7, 0.9, 1.0], RecognitionText.WordConfidences(tokens).Select(v => Math.Round(v, 3)));
        Assert.Equal(3, RecognitionText.WordConfidences([new("特", 0.9f), new("征", 0.8f), new("值", 0.7f)]).Count);
    }

    [Fact]
    public void ChineseFilterDecision()
    {
        Assert.True(RecognitionText.IsForeignChinese("zh", 0.8f, "en"));
        Assert.False(RecognitionText.IsForeignChinese("en", 0.8f, "en"));
        Assert.False(RecognitionText.IsForeignChinese("zh", 0.4f, "en"));
        Assert.False(RecognitionText.IsForeignChinese("zh", 0.9f, "zh"));
    }

    [Fact]
    public void LanguageCodes()
    {
        Assert.Equal("en", LanguageOption.Source("en-GB").WhisperCode);
        Assert.Equal("zh", LanguageOption.Source("zh-CN").WhisperCode);
        Assert.True(LanguageOption.Target("ja").IsCJK);
        Assert.False(LanguageOption.Target("fr").IsCJK);
        Assert.Equal("en-US", LanguageOption.Source("xx").Id);
    }
}
