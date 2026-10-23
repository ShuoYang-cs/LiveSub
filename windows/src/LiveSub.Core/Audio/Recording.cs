using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LiveSub.Core.Audio;

/// <summary>
/// 录音文件的名字（与 macOS 相同）：和字幕记录放在一起、名字对应，“2026-10-22 13.35 字幕记录.md”的录音是
/// “2026-10-22 13.35 录音.m4a”；“两者”时每路一个文件，后面带音源名；重名时加序号，不覆盖已有的录音。
/// </summary>
public static partial class RecordingFiles
{
    public const string Extension = ".m4a";

    /// <param name="transcriptName">字幕记录文件名里的“字幕记录”（英文界面为 Transcript）。</param>
    /// <param name="recordingName">换成的“录音”（英文界面为 Recording）。</param>
    /// <param name="labelSuffix">“两者”时的音源后缀，例如“（麦克风）”；只有一个音源时为 null。</param>
    public static string PathFor(string transcriptPath, string transcriptName, string recordingName, string? labelSuffix,
                                 Func<string, bool> exists)
    {
        var folder = Path.GetDirectoryName(transcriptPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(transcriptPath);
        // 记录是在另一种界面语言下建的、名字里没有“字幕记录”时，在后面加上“录音”，免得和记录同名。
        name = name.Contains(transcriptName, StringComparison.Ordinal)
            ? name.Replace(transcriptName, recordingName, StringComparison.Ordinal)
            : $"{name} {recordingName}";
        name += labelSuffix ?? "";
        var path = Path.Combine(folder, name + Extension);
        for (int n = 2; exists(path); n++) path = Path.Combine(folder, $"{name} {n}{Extension}");
        return path;
    }

    /// <summary>
    /// 同一次的字幕记录：去掉音源后缀（“（麦克风）”“ (Microphone)”）和重名时加的序号，“录音”换回“字幕记录”。
    /// 没有这个文件时为 null。
    /// </summary>
    public static string? TranscriptFor(string recordingPath, string transcriptName, string recordingName, Func<string, bool> exists)
    {
        var folder = Path.GetDirectoryName(recordingPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(recordingPath);
        int suffix = name.IndexOf('（');
        if (suffix < 0) suffix = name.IndexOf(" (", StringComparison.Ordinal);
        if (suffix >= 0) name = name[..suffix];
        name = TrailingNumber().Replace(name, "");
        if (!name.Contains(recordingName, StringComparison.Ordinal)) return null;
        name = name.Replace(recordingName, transcriptName, StringComparison.Ordinal).Trim();
        var path = Path.Combine(folder, name + ".md");
        return exists(path) ? path : null;
    }

    /// <summary>时长：不到一小时“m:ss”，否则“h:mm:ss”（与 macOS 的录音记录相同）。</summary>
    public static string Duration(TimeSpan duration)
    {
        long s = (long)Math.Round(Math.Max(0, duration.TotalSeconds));
        return s >= 3600
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", s / 3600, s / 60 % 60, s % 60)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", s / 60, s % 60);
    }

    [GeneratedRegex(@" \d+$")]
    private static partial Regex TrailingNumber();
}

/// <summary>
/// 录音和真实时间对齐：采集中断后重新打开（换了设备、麦克风被隐私设置挡住又放开）时，中间那段时间录音里没有声音，
/// 不补上的话录音比字幕短，之后的话和字幕记录的时间对不上。同一个采集对象里没有数据的时间由采集自己补静音；
/// 两个采集对象之间的空缺在这里算：记下第一块声音的真实时间和已写入的样本数，新的采集送来第一块时把空缺补成静音。
/// 最多补 <see cref="MaxGap"/>：电脑睡眠后醒来不会补出几个小时的静音。
/// </summary>
public sealed class RecordingClock(int sampleRate)
{
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(30);
    /// <summary>比这短的空缺不补：采集回调本身就有几十毫秒的抖动。</summary>
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(100);

    /// <summary>录音第 0 个样本对应的真实时刻；每次新的采集开始时重新对齐（补的静音有上限，系统时钟和声卡时钟也会慢慢错开）。</summary>
    private DateTime? start;
    private long written;

    /// <summary>已经写入（包括补上的静音）的样本数。</summary>
    public long Written => written;

    /// <summary>
    /// 收到一块声音。<paramref name="now"/> 是收到它的时刻（这块声音是在那之前的一段时间里录的）；
    /// <paramref name="newCapture"/>：这是新打开的采集送来的第一块。返回这块前面要补的静音样本数（一般是 0）。
    /// </summary>
    public long Add(int samples, DateTime now, bool newCapture)
    {
        var blockStart = now - TimeSpan.FromSeconds((double)samples / sampleRate);
        long gap = 0;
        if (start is not { } origin)
        {
            start = blockStart;
        }
        else if (newCapture)
        {
            long missing = (long)((blockStart - origin).TotalSeconds * sampleRate) - written;
            if (missing >= MinGap.TotalSeconds * sampleRate) gap = Math.Min(missing, (long)(MaxGap.TotalSeconds * sampleRate));
            // 重新对齐：补了上限以后，下一次中断只补那一次的空缺，不会再补一遍睡眠的时间。
            start = blockStart - TimeSpan.FromSeconds((double)(written + gap) / sampleRate);
        }
        written += gap + samples;
        return gap;
    }
}

/// <summary>float 样本（−1…1）换成 16 位整数 PCM（小端）：Windows 自带的 AAC 编码器只收整数 PCM。</summary>
public static class Pcm16
{
    public static byte[] FromFloat(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float s = float.IsNaN(samples[i]) ? 0 : Math.Clamp(samples[i], -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)Math.Round(s * short.MaxValue));
        }
        return bytes;
    }
}
