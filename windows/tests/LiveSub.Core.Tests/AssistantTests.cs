using LiveSub.Core;
using LiveSub.Core.Subtitles;

namespace LiveSub.Core.Tests;

public class AssistantConversationTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 1, 0, 0, DateTimeKind.Utc);

    private static SubtitleLine Line(int seconds, string source, LineState state = LineState.Done) =>
        new() { Time = T0.AddSeconds(seconds), Source = source, State = state };

    private static AssistantPrompt.Line Prompt(SubtitleLine l) => new(l.Time, l.Source);

    [Fact]
    public void FirstQuestionCarriesSettledLinesSinceStartAndWaitingLinesAsSpeaking()
    {
        var before = Line(-5, "Before the conversation.");
        var a = Line(1, "So we fill the table.");
        var b = Line(2, "Then we go back.", LineState.Translating);
        var empty = Line(3, "");
        var waiting = Line(4, "and the corner", LineState.Waiting);
        var conversation = new AssistantConversation(T0);

        var q = conversation.Begin("回溯怎么走？", [before, a, b, empty, waiting], "gives the answer")!;

        var expected = AssistantPrompt.Message([Prompt(a), Prompt(b)], "and the corner gives the answer", "回溯怎么走？");
        Assert.Equal(expected, q.NewSessionMessage);
        Assert.Equal(expected, q.OngoingMessage);
        Assert.Equal([a.Id, b.Id], q.SettledIds);
        Assert.True(conversation.IsAnswering);
        Assert.Equal(2, conversation.Messages.Count);
        Assert.Equal(AssistantRole.User, conversation.Messages[0].Role);
        Assert.Equal("回溯怎么走？", conversation.Messages[0].Text);
        Assert.Equal("", conversation.Messages[1].Text);
    }

    [Fact]
    public void LinesCountAsSentOnlyAfterSuccessfulReply()
    {
        var a = Line(1, "First.");
        var b = Line(2, "Second.");
        var conversation = new AssistantConversation(T0);

        var q1 = conversation.Begin("一", [a], "")!;
        Assert.True(conversation.Fail(q1, "超时"));
        Assert.False(conversation.WasSent(a.Id));
        Assert.True(conversation.Messages[1].Failed);
        Assert.False(conversation.IsAnswering);

        // 失败过：下一问的“已有会话”消息仍然带上第一句。
        var q2 = conversation.Begin("二", [a, b], "")!;
        Assert.Equal(AssistantPrompt.Message([Prompt(a), Prompt(b)], "", "二"), q2.OngoingMessage);
        Assert.True(conversation.Complete(q2, " 回答 "));
        Assert.Equal("回答", conversation.Messages[3].Text);
        Assert.True(conversation.WasSent(a.Id) && conversation.WasSent(b.Id));

        // 成功过：已有会话只带新增的句子，新会话（换了模型、进程重启）仍然带全部。
        var c = Line(3, "Third.");
        var q3 = conversation.Begin("三", [a, b, c], "")!;
        Assert.Equal(AssistantPrompt.Message([Prompt(c)], "", "三"), q3.OngoingMessage);
        Assert.Equal(AssistantPrompt.Message([Prompt(a), Prompt(b), Prompt(c)], "", "三"), q3.NewSessionMessage);
    }

    [Fact]
    public void OneQuestionAtATime()
    {
        var conversation = new AssistantConversation(T0);
        Assert.NotNull(conversation.Begin("一", [], ""));
        Assert.Null(conversation.Begin("二", [], ""));
        Assert.Equal(2, conversation.Messages.Count);
    }

    [Fact]
    public void PartialRepliesOnlyGrowAndStopAfterCompletion()
    {
        var conversation = new AssistantConversation(T0);
        var q = conversation.Begin("问", [], "")!;
        Assert.True(conversation.UpdateAnswer(q, "动态"));
        Assert.True(conversation.UpdateAnswer(q, "动态规划"));
        Assert.False(conversation.UpdateAnswer(q, "动态"));
        Assert.Equal("动态规划", conversation.Messages[1].Text);
        conversation.Complete(q, "动态规划。");
        Assert.False(conversation.UpdateAnswer(q, "动态规划。还有更长的"));
        Assert.Equal("动态规划。", conversation.Messages[1].Text);
    }

    [Fact]
    public void ResetStartsFreshAndIgnoresTheOldAnswer()
    {
        var a = Line(1, "Old line.");
        var conversation = new AssistantConversation(T0);
        var old = conversation.Begin("旧问题", [a], "")!;

        conversation.Reset(T0.AddSeconds(10));
        Assert.Empty(conversation.Messages);
        Assert.False(conversation.IsAnswering);
        Assert.False(conversation.UpdateAnswer(old, "迟到的回答"));
        Assert.False(conversation.Complete(old, "迟到的回答"));
        Assert.False(conversation.WasSent(a.Id));

        // 新对话只带重新开始以后的字幕；正在回答新问题时，旧问题的失败不影响它。
        var b = Line(11, "New line.");
        var q = conversation.Begin("新问题", [a, b], "")!;
        Assert.Equal(AssistantPrompt.Message([Prompt(b)], "", "新问题"), q.NewSessionMessage);
        Assert.False(conversation.Fail(old, "已取消"));
        Assert.True(conversation.IsAnswering);
    }

    [Fact]
    public void FailureBeforeSendingShowsQuestionAndError()
    {
        var conversation = new AssistantConversation(T0);
        conversation.AddFailure("问", "找不到 claude 命令。");
        Assert.Equal(2, conversation.Messages.Count);
        Assert.True(conversation.Messages[1].Failed);
        Assert.Equal("找不到 claude 命令。", conversation.Messages[1].Text);
        Assert.False(conversation.IsAnswering);
    }

    [Fact]
    public void QuickQuestionsMatchMacOS()
    {
        Assert.Equal(["总结最近的内容", "解释相关概念", "解释上一句"], AssistantPrompt.QuickQuestions);
    }
}

public class AssistantFilesTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PlainAnswerHasNoFiles()
    {
        var parsed = AssistantFiles.Parse("Jerry 说的是图文冲突时模型更信文字。");
        Assert.Equal("Jerry 说的是图文冲突时模型更信文字。", parsed.Text);
        Assert.Empty(parsed.Files);
        Assert.False(parsed.Writing);
    }

    [Fact]
    public void SavesBlockAndKeepsShortSentence()
    {
        const string reply = """
            已整理成文件。

            @@save: 组会总结
            # 组会总结

            - 图文冲突时模型更信文字
            @@end
            """;
        var parsed = AssistantFiles.Parse(reply);
        Assert.Equal("已整理成文件。", parsed.Text);
        Assert.Equal([new AssistantFiles.File("组会总结", "# 组会总结\n\n- 图文冲突时模型更信文字")], parsed.Files);
        Assert.False(parsed.Writing);
    }

    [Fact]
    public void MultipleFiles()
    {
        var parsed = AssistantFiles.Parse("@@save: a\nA\n@@end\n@@save: b\nB\n@@end");
        Assert.Equal(["a", "b"], parsed.Files.Select(f => f.Name));
        Assert.Equal("", parsed.Text);
    }

    [Fact]
    public void UnfinishedBlockWhileStreaming()
    {
        var parsed = AssistantFiles.Parse("好的。\n@@save: 笔记\n# 标题\n第一");
        Assert.Equal("好的。", parsed.Text);
        Assert.True(parsed.Writing);
        Assert.Equal([new AssistantFiles.File("笔记", "# 标题\n第一")], parsed.Files);
    }

    [Fact]
    public void CodeFencesAroundBlockAreDropped()
    {
        var parsed = AssistantFiles.Parse("已保存。\n```markdown\n@@save: x\n内容\n@@end\n```\n");
        Assert.Equal("已保存。", parsed.Text);
        Assert.Equal([new AssistantFiles.File("x", "内容")], parsed.Files);
    }

    [Fact]
    public void CodeInsideFileIsKept()
    {
        var parsed = AssistantFiles.Parse("@@save: code\n```swift\nlet a = 1\n```\n@@end\n后面还有一句。");
        Assert.Equal("```swift\nlet a = 1\n```", parsed.Files[0].Content);
        Assert.Equal("后面还有一句。", parsed.Text);
    }

    [Fact]
    public void WindowsLineEndings()
    {
        var parsed = AssistantFiles.Parse("好的。\r\n@@save: a\r\n第一行\r\n第二行\r\n@@end\r\n");
        Assert.Equal("好的。", parsed.Text);
        Assert.Equal([new AssistantFiles.File("a", "第一行\n第二行")], parsed.Files);
        Assert.False(parsed.Writing);
    }

    [Fact]
    public void FileNameIsSafeAndDated()
    {
        Assert.Equal("2026-10-21 21.32 组会总结.md", AssistantFiles.FileName("组会总结", "2026-10-21 21.32", "AI 助手"));
        // 不能带路径，去掉系统不允许的字符
        Assert.Equal("S passwd.md", AssistantFiles.FileName("../../etc/pass:wd?.txt", "S", "F"));
        Assert.Equal("S b.md", AssistantFiles.FileName("~/Desktop/a\\b.md", "S", "F"));
        // 已经以日期开头时不再加前缀
        Assert.Equal("2026-10-21 组会.md", AssistantFiles.FileName("2026-10-21 组会", "S", "F"));
        Assert.Equal("S AI 助手.md", AssistantFiles.FileName("  ..  ", "S", "AI 助手"));
        var longName = new string('长', 200);
        Assert.Equal("S ".Length + 80 + ".md".Length, AssistantFiles.FileName(longName, "S", "F").Length);
    }

    [Fact]
    public void FileNameDropsWindowsReservedCharactersAndControlCharacters()
    {
        Assert.Equal("S a b c d.md", AssistantFiles.FileName("a <b> | c\t d*\u200B", "S", "F"));
        // 截断时不把表情拆成两半。
        var name = AssistantFiles.FileName(new string('长', 79) + "😀😀", "S", "F");
        Assert.Equal("S " + new string('长', 79) + "😀.md", name);
    }

    [Fact]
    public void UniqueNameDoesNotOverwrite()
    {
        var existing = new HashSet<string> { "a.md", "a 2.md" };
        Assert.Equal("a 3.md", AssistantFiles.UniqueName("a.md", existing.Contains));
        Assert.Equal("b.md", AssistantFiles.UniqueName("b.md", existing.Contains));
    }

    [Fact]
    public void SystemPromptExplainsBlocks()
    {
        var prompt = AssistantPrompt.System(LanguageOption.Source("en-US"), LanguageOption.Target("zh-Hans"));
        Assert.Contains(AssistantFiles.StartMarker, prompt);
        Assert.Contains(AssistantFiles.EndMarker, prompt);
    }

    [Fact]
    public void StreamingHidesFileContentAndKeepsSavingUntilDone()
    {
        var conversation = new AssistantConversation(T0);
        var q = conversation.Begin("把总结存成文件", [], "")!;
        var answer = conversation.Messages[1];

        Assert.True(conversation.UpdateAnswer(q, "好的。\n@@save: 总结\n# 总"));
        Assert.Equal("好的。", answer.Text);
        Assert.True(answer.WritingFile);
        // 文件内容变长时回答也在变长（按原始回答比较）。
        Assert.True(conversation.UpdateAnswer(q, "好的。\n@@save: 总结\n# 总结\n- 一"));
        Assert.Empty(conversation.FilesToSave(q));

        Assert.True(conversation.Complete(q, "好的。\n@@save: 总结\n# 总结\n- 一\n@@end"));
        Assert.Equal("好的。", answer.Text);
        Assert.True(answer.WritingFile);
        Assert.Equal([new AssistantFiles.File("总结", "# 总结\n- 一")], conversation.FilesToSave(q));

        Assert.True(conversation.FilesSaved(q, [new AssistantSavedFile("2026-10-21 09.05 总结.md", @"C:\Docs\AI 助手\2026-10-21 09.05 总结.md")]));
        Assert.False(answer.WritingFile);
        Assert.Equal("2026-10-21 09.05 总结.md", Assert.Single(answer.Files).Name);
    }

    [Fact]
    public void AnswerWithoutFilesNeedsNoSaving()
    {
        var conversation = new AssistantConversation(T0);
        var q = conversation.Begin("问", [], "")!;
        conversation.Complete(q, "回答。");
        Assert.False(conversation.Messages[1].WritingFile);
        Assert.Empty(conversation.FilesToSave(q));
    }

    [Fact]
    public void FailedOrResetAnswersSaveNothing()
    {
        var conversation = new AssistantConversation(T0);
        var q = conversation.Begin("存文件", [], "")!;
        conversation.UpdateAnswer(q, "@@save: a\n内容");
        Assert.True(conversation.Fail(q, "超时"));
        Assert.False(conversation.Messages[1].WritingFile);
        Assert.Empty(conversation.FilesToSave(q));

        // 保存期间点了“新对话”：结果不再写进新对话。
        var q2 = conversation.Begin("再存一次", [], "")!;
        conversation.Complete(q2, "@@save: b\n内容\n@@end");
        Assert.Single(conversation.FilesToSave(q2));
        conversation.Reset(T0.AddSeconds(10));
        Assert.Empty(conversation.FilesToSave(q2));
        Assert.False(conversation.FilesSaved(q2, [new AssistantSavedFile("b.md", "b.md")]));
    }
}

public class InlineMarkdownTests
{
    [Fact]
    public void BoldAndCode()
    {
        Assert.Equal(
            [new("用 ", InlineStyle.None), new("动态规划", InlineStyle.Bold), new("（", InlineStyle.None),
             new("dp[i]", InlineStyle.Code), new("）解决。", InlineStyle.None)],
            InlineMarkdown.Parse("用 **动态规划**（`dp[i]`）解决。"));
    }

    [Fact]
    public void NestedItalicAndCodeInsideBold()
    {
        Assert.Equal(
            [new("斜体", InlineStyle.Italic), new(" 和 ", InlineStyle.None), new("粗体里的 ", InlineStyle.Bold),
             new("code", InlineStyle.Bold | InlineStyle.Code)],
            InlineMarkdown.Parse("*斜体* 和 **粗体里的 `code`**"));
    }

    [Fact]
    public void ListsNewlinesAndLoneSymbolsStayAsIs()
    {
        const string text = "- 第一点\n* 第二点\n2 * 3 = 6\n__init__ 和 snake_case";
        Assert.Equal([new(text, InlineStyle.None)], InlineMarkdown.Parse(text));
    }

    [Fact]
    public void UnclosedMarkersWhileStreaming()
    {
        Assert.Equal([new("关键是 **记忆", InlineStyle.None)], InlineMarkdown.Parse("关键是 **记忆"));
        Assert.Equal([new("`dp", InlineStyle.None)], InlineMarkdown.Parse("`dp"));
        // 不跨行配对。
        Assert.Equal([new("**a\nb**", InlineStyle.None)], InlineMarkdown.Parse("**a\nb**"));
    }

    [Fact]
    public void EscapedSymbols()
    {
        Assert.Equal([new("a*b*c", InlineStyle.None)], InlineMarkdown.Parse(@"a\*b\*c"));
    }
}

public class NotesPathTests
{
    [Fact]
    public void NotesSitNextToTheTranscript()
    {
        var folder = Path.Combine("Documents", "LiveSub");
        Assert.Equal(Path.Combine(folder, "2026-10-21 09.05 笔记.md"),
            TranscriptFormat.NotesPath(Path.Combine(folder, "2026-10-21 09.05 字幕记录.md"), "字幕记录", "笔记"));
        Assert.Equal(Path.Combine(folder, "2026-10-21 09.05 Notes.md"),
            TranscriptFormat.NotesPath(Path.Combine(folder, "2026-10-21 09.05 Transcript.md"), "Transcript", "Notes"));
    }

    [Fact]
    public void NotesNeverOverwriteTheTranscript()
    {
        // 记录是在中文界面下建的，现在是英文界面。
        var folder = Path.Combine("Documents", "LiveSub");
        Assert.Equal(Path.Combine(folder, "2026-10-21 09.05 字幕记录 Notes.md"),
            TranscriptFormat.NotesPath(Path.Combine(folder, "2026-10-21 09.05 字幕记录.md"), "Transcript", "Notes"));
    }
}

public class AssistantPlacementTests
{
    private static readonly ScreenRect Screen = new(0, 0, 1920, 1040);

    [Fact]
    public void RightOfTheOverlayBottomAligned()
    {
        // 字幕在屏幕下半部分：底边对齐。
        var overlay = new ScreenRect(100, 800, 900, 120);
        Assert.Equal((1008.0, 500.0), AssistantPlacement.BesideOverlay(overlay, Screen, 380, 420));
    }

    [Fact]
    public void LeftWhenTheRightSideIsFullTopAligned()
    {
        // 字幕在屏幕上半部分：顶边对齐。
        var overlay = new ScreenRect(1000, 100, 900, 120);
        Assert.Equal((612.0, 100.0), AssistantPlacement.BesideOverlay(overlay, Screen, 380, 420));
    }

    [Fact]
    public void AgainstTheRightEdgeWhenNeitherSideFits()
    {
        var overlay = new ScreenRect(100, 800, 1720, 120);
        Assert.Equal((1540.0, 500.0), AssistantPlacement.BesideOverlay(overlay, Screen, 380, 420));
    }

    [Fact]
    public void StaysOnScreenVertically()
    {
        var overlay = new ScreenRect(100, 400, 900, 100);
        Assert.Equal((1008.0, 140.0), AssistantPlacement.BesideOverlay(overlay, Screen, 380, 900));
        var low = new ScreenRect(100, 960, 900, 300);
        Assert.Equal(0.0, AssistantPlacement.BesideOverlay(low, Screen, 380, 1040).Top);
    }

    [Fact]
    public void SavedPositionIsUsedWhileItsHeaderIsOnAScreen()
    {
        var overlay = new ScreenRect(100, 800, 900, 120);
        ScreenRect[] screens = [Screen, new(1920, 0, 1280, 1000)];
        Assert.Equal((2000.0, 50.0), AssistantPlacement.Place(overlay, Screen, screens, 380, 420, 2000, 50));
        // 只露出一部分标题栏也算（还能拖回来）。
        Assert.Equal((-300.0, 10.0), AssistantPlacement.Place(overlay, Screen, screens, 380, 420, -300, 10));
        // 那块屏幕已经拔掉了：回到字幕旁边。
        Assert.Equal((1008.0, 500.0), AssistantPlacement.Place(overlay, Screen, [Screen], 380, 420, 2000, 50));
        // 标题栏在屏幕下面（只有面板下半截会在屏幕外也不行）。
        Assert.Equal((1008.0, 500.0), AssistantPlacement.Place(overlay, Screen, screens, 380, 420, 100, 1040));
        // 没拖过。
        Assert.Equal((1008.0, 500.0), AssistantPlacement.Place(overlay, Screen, screens, 380, 420, null, null));
    }
}
