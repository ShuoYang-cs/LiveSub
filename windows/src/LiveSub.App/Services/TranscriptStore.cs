using System.Text;
using LiveSub.App.Localization;
using LiveSub.Core;

namespace LiveSub.App.Services;

/// <summary>
/// 把每一句原文 + 译文实时追加写入 Markdown 文件，崩溃或断电也不会丢失之前的记录。
/// 文件名与文件头按界面语言（与 macOS 版一致）：中文“字幕记录 / 开始时间 / 语言”，英文 “Transcript / Started / Language”。
/// 每次记录一个文件夹（<see cref="RecordFolders"/>）：字幕记录、AI 助手对话、录音、笔记都放在里面。
/// </summary>
public sealed class TranscriptStore
{
    public string FilePath { get; }
    public DateTime Started { get; }

    /// <summary>本次记录的文件夹。</summary>
    public string FolderPath => Path.GetDirectoryName(FilePath)!;

    /// <summary>AI 助手对话：“2026-11-23 13.07 AI 助手对话.md”。</summary>
    public string AssistantLogPath =>
        Path.Combine(FolderPath, Path.GetFileNameWithoutExtension(FilePath).Replace(Strings.Transcript_Name, Strings.Assistant_LogName) + ".md");

    public TranscriptStore(string folderPath, DateTime started, LanguageOption source, LanguageOption target)
    {
        Started = started;
        var stamp = TranscriptFormat.FileStamp(started);
        var preferred = Environment.ExpandEnvironmentVariables(folderPath);
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveSub", "Transcripts");
        var labels = Strings.IsChinese ? TranscriptLabels.Chinese : TranscriptLabels.English;

        FilePath = Path.Combine(fallback, $"{stamp} {Strings.Transcript_Name}.md");
        foreach (var root in new[] { preferred, fallback })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                RecordFolderMigration.Run(root);
                // 同一分钟里开始了两次记录：第二次另开一个“… 2”文件夹（以前是接着写进同一个文件）。
                var name = RecordFolders.FolderName(stamp, n => Directory.Exists(Path.Combine(root, n)) || File.Exists(Path.Combine(root, n)));
                var title = $"{name} {Strings.Transcript_Name}";
                var folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
                var path = Path.Combine(folder, title + ".md");
                File.WriteAllText(path, TranscriptFormat.Header(title, started, source, target, labels), new UTF8Encoding(false));
                FilePath = path;
                break;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                AppLog.Error($"transcript: cannot write to {root}", e);
            }
        }
    }

    public void Append(string text)
    {
        try
        {
            File.AppendAllText(FilePath, text, new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("transcript: append failed", e);
        }
    }
}

/// <summary>
/// 把旧版平铺在记录文件夹里的字幕记录、录音、笔记搬进各自的文件夹（<see cref="RecordFolders.MigrationPlan"/>，与 macOS 相同）。
/// 启动时（用过程序的才做）、新建记录前、打开字幕记录和录音记录窗口前各做一次；没有要搬的文件时只是列一次目录。
/// </summary>
public static class RecordFolderMigration
{
    private static readonly Lock Gate = new();

    /// <summary>另一个 LiveSub 正在写的文件不能搬：最近两分钟改过的文件这次先不动，下次再搬。</summary>
    private static readonly TimeSpan BusyInterval = TimeSpan.FromMinutes(2);

    public static void Run(string root)
    {
        lock (Gate)
        {
            List<string> files;
            try
            {
                if (!Directory.Exists(root)) return;
                var now = DateTime.UtcNow;
                files = Directory.EnumerateFiles(root)
                    .Where(f => now - File.GetLastWriteTimeUtc(f) > BusyInterval)
                    .Select(f => Path.GetFileName(f)).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                AppLog.Error($"record folders: cannot list {root}", e);
                return;
            }
            // 中文和英文界面写的文件名都认（macOS 英文界面的录音叫“Record”）。
            string[] keywords = ["字幕记录", "录音", "笔记", "Transcript", "Recording", "Record", "Notes",
                                 Strings.Transcript_Name, Strings.Recording_Name, Strings.Notes_Name];
            var plan = RecordFolders.MigrationPlan(files, keywords.Distinct().ToList(),
                (folder, file) => File.Exists(Path.Combine(root, folder, file)));
            if (plan.Count == 0) return;
            int moved = 0;
            foreach (var move in plan)
            {
                try
                {
                    var folder = Directory.CreateDirectory(Path.Combine(root, move.Folder)).FullName;
                    File.Move(Path.Combine(root, move.File), Path.Combine(folder, move.File), overwrite: false);
                    moved++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    AppLog.Error($"record folders: cannot move {move.File}", e);
                }
            }
            AppLog.Info($"record folders: moved {moved}/{plan.Count} old files into record folders");
        }
    }

    /// <summary>
    /// 先搬旧文件，再列出记录文件夹最上层和每次记录文件夹里符合 <paramref name="pattern"/> 的文件（字幕记录、录音记录窗口用）。
    /// 最上层还会有搬不了的旧文件（同名、正被另一个 LiveSub 写着），也要列出来。
    /// </summary>
    public static List<string> RecordFiles(string root, string pattern)
    {
        Run(root);
        var result = Directory.EnumerateFiles(root, pattern).ToList();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                result.AddRange(Directory.EnumerateFiles(dir, pattern));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                AppLog.Error($"record folders: cannot list {dir}", e);
            }
        }
        return result;
    }
}
