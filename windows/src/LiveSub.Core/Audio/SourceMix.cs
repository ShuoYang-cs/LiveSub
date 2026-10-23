namespace LiveSub.Core.Audio;

/// <summary>一个音源最近的输入：音量（RMS）和多久没收到音频（秒）。</summary>
public readonly record struct SourceInput(float Level, double SilentFor);

/// <summary>“正在说”的一行：音源标签（只在“两者”模式下有）和文字。</summary>
public sealed record LiveRow(string? Label, string Text);

/// <summary>
/// “两者”模式（麦克风和系统音频同时识别）下，把各个音源的状态合在一起显示，与 macOS 版相同：
/// 音量取最大，“多久没收到音频”取最小（两个音源都没有声音时才算“无音频输入”）。
/// </summary>
public static class SourceMix
{
    /// <param name="inputs">每个音源的输入；采集断了、正在等它恢复的音源为 null。</param>
    /// <returns>没有一个音源在采集时，音量为 0，没收到音频的时间为无穷大。</returns>
    public static SourceInput Combine(IEnumerable<SourceInput?> inputs)
    {
        float level = 0;
        double silent = double.PositiveInfinity;
        foreach (var input in inputs)
        {
            if (input is not { } i) continue;
            level = Math.Max(level, i.Level);
            silent = Math.Min(silent, i.SilentFor);
        }
        return new SourceInput(level, silent);
    }

    /// <summary>字幕下方“正在说”的各行，按音源的顺序；没有字母或数字的不显示（噪音有时被识别成一个“.”）。</summary>
    public static List<LiveRow> LiveRows(IEnumerable<LiveRow> rows) =>
        rows.Where(r => r.Text.Any(char.IsLetterOrDigit)).ToList();

    /// <summary>发给 AI 助手的“正在说”：各音源的文字用空格连起来（与 macOS 版相同）。</summary>
    public static string Speaking(IEnumerable<string> texts) =>
        string.Join(" ", texts.Select(t => t.Trim()).Where(t => t.Length > 0));
}
