using System.Text;

namespace LiveSub.App.Services;

/// <summary>
/// 运行日志：%LOCALAPPDATA%\LiveSub\logs\livesub.log（超过 2 MB 时换成 livesub.old.log）。
/// 出问题（卡住、没字幕）时用来看停在哪一步。只记录事件和错误，不记录 API 密钥；
/// 字幕原文只有在设置了环境变量 LIVESUB_DEBUG_LATENCY 时才会写入（延迟分析）。
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveSub", "logs");

    private static string FilePath => Path.Combine(Folder, "livesub.log");

    public static bool DebugLatency { get; } = Environment.GetEnvironmentVariable("LIVESUB_DEBUG_LATENCY") is not null;

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? e = null) =>
        Write("ERROR", e is null ? message : $"{message}: {e.GetType().Name}: {e.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 2_000_000)
                {
                    File.Move(FilePath, Path.Combine(Folder, "livesub.old.log"), overwrite: true);
                }
                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        System.Diagnostics.Debug.Write(line);
    }
}
