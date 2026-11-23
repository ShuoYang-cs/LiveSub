namespace LiveSub.Core.Tests;

public class RecordFoldersTests
{
    private static readonly string[] Keywords = ["字幕记录", "录音", "笔记", "Transcript", "Recording", "Record", "Notes"];

    [Fact]
    public void FolderIsNamedAfterStartTime()
    {
        Assert.Equal("2026-11-23 13.07", RecordFolders.FolderName("2026-11-23 13.07", _ => false));
        var taken = new HashSet<string> { "2026-11-23 13.07", "2026-11-23 13.07 2" };
        Assert.Equal("2026-11-23 13.07 3", RecordFolders.FolderName("2026-11-23 13.07", taken.Contains));
    }

    [Fact]
    public void StampNeedsSpaceAfterIt()
    {
        Assert.Equal("2026-11-18 13.07", RecordFolders.Stamp("2026-11-18 13.07 字幕记录.md"));
        Assert.Null(RecordFolders.Stamp("2026-11-18 13.07.md"));
        Assert.Null(RecordFolders.Stamp("课程总结 2026-11-18 13.07 笔记.md"));
    }

    [Fact]
    public void OldFilesAreGroupedByStartTime()
    {
        string[] files =
        [
            "2026-10-22 13.35 字幕记录.md", "2026-10-22 13.35 录音.m4a", "2026-10-22 13.35 录音（麦克风） 2.m4a",
            "2026-10-22 13.35 笔记.md", "2026-10-28 09.00 Transcript.md", "2026-10-28 09.00 Transcript Recording.m4a",
        ];
        var plan = RecordFolders.MigrationPlan(files, Keywords, (_, _) => false);
        Assert.Equal(6, plan.Count);
        Assert.Equal(files.Take(4).Order(StringComparer.Ordinal), plan.Where(m => m.Folder == "2026-10-22 13.35").Select(m => m.File));
        Assert.Equal(files.Skip(4).Order(StringComparer.Ordinal), plan.Where(m => m.Folder == "2026-10-28 09.00").Select(m => m.File));
    }

    [Fact]
    public void OtherFilesStayWhereTheyAre()
    {
        string[] files = ["我的课表.pdf", "2026-10-22 13.35 课件.pdf", "desktop.ini", "2026-10-22 字幕记录.md"];
        Assert.Empty(RecordFolders.MigrationPlan(files, Keywords, (_, _) => false));
    }

    [Fact]
    public void FilesInTheFolderAreNeverOverwritten()
    {
        string[] files = ["2026-10-22 13.35 字幕记录.md", "2026-10-22 13.35 笔记.md"];
        var plan = RecordFolders.MigrationPlan(files, Keywords, (folder, file) => file == "2026-10-22 13.35 字幕记录.md");
        Assert.Equal([new RecordFolders.Move("2026-10-22 13.35 笔记.md", "2026-10-22 13.35")], plan);
    }
}

public class AssistantLogTests
{
    private static readonly DateTime Time = new(2026, 11, 23, 13, 8, 5, DateTimeKind.Local);

    [Fact]
    public void EntryHasTimeQuestionAndAnswer()
    {
        var text = AssistantLog.Entry(Time, "老师刚才说的 pop 是什么？", [], "  `list.pop()` 按位置删除并返回元素。\n", [], false,
            AssistantLogLabels.Chinese);
        Assert.Equal("\n### [13:08:05] 老师刚才说的 pop 是什么？\n\n`list.pop()` 按位置删除并返回元素。\n", text);
    }

    [Fact]
    public void MultiLineQuestionStaysInHeading()
    {
        var text = AssistantLog.Entry(Time, "第一行\r\n\r\n第二行", [], "好", [], false, AssistantLogLabels.Chinese);
        Assert.StartsWith("\n### [13:08:05] 第一行 第二行\n", text);
    }

    [Fact]
    public void AttachmentsSavedFilesAndFailuresAreListed()
    {
        var text = AssistantLog.Entry(Time, "", ["slide.png", "讲义.pdf"], "已整理成文件。", ["2026-11-23 13.08 总结.md"], false,
            AssistantLogLabels.Chinese);
        Assert.Contains("（只发了附件）", text);
        Assert.Contains("附件：slide.png、讲义.pdf", text);
        Assert.Contains("已保存文件：2026-11-23 13.08 总结.md", text);
        Assert.Contains("*Answer failed: timeout*",
            AssistantLog.Entry(Time, "?", [], "timeout", [], true, AssistantLogLabels.English));
    }

    [Fact]
    public void SeparatorShowsNewConversationTime() =>
        Assert.Equal("\n---\n\n**新对话 13:08:05**\n", AssistantLog.Separator(Time, AssistantLogLabels.Chinese));
}
