using System.Text;
using LiveSub.App.Localization;
using LiveSub.Core;

namespace LiveSub.App.Services;

/// <summary>
/// 把每一句原文 + 译文实时追加写入 Markdown 文件，崩溃或断电也不会丢失之前的记录。
/// 文件名与文件头按界面语言（与 macOS 版一致）：中文“字幕记录 / 开始时间 / 语言”，英文 “Transcript / Started / Language”。
/// </summary>
public sealed class TranscriptStore
{
    public string FilePath { get; }
    public DateTime Started { get; }

    public TranscriptStore(string folderPath, DateTime started, LanguageOption source, LanguageOption target)
    {
        Started = started;
        var stamp = TranscriptFormat.FileStamp(started);
        var title = $"{stamp} {Strings.Transcript_Name}";
        var name = title + ".md";
        var preferred = Environment.ExpandEnvironmentVariables(folderPath);
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveSub", "Transcripts");
        var header = TranscriptFormat.Header(title, started, source, target,
            Strings.IsChinese ? TranscriptLabels.Chinese : TranscriptLabels.English);

        FilePath = Path.Combine(fallback, name);
        foreach (var folder in new[] { preferred, fallback })
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try
            {
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, name);
                // 同一分钟内开始了两次记录时接着写，不覆盖已有内容。
                if (!File.Exists(path)) File.WriteAllText(path, header, new UTF8Encoding(false));
                FilePath = path;
                break;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                AppLog.Error($"transcript: cannot write to {folder}", e);
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
