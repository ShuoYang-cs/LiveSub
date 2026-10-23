using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using LiveSub.App.Localization;
using LiveSub.App.Services;
using LiveSub.Core.Audio;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace LiveSub.App.Audio;

/// <summary>
/// 录音（与 macOS 的 AudioRecorder 相同）：把一路音源听到的声音存成 AAC（.m4a），和字幕记录放在一起。
/// 采集回调里只把样本放进队列，编码写盘在自己的线程上做，不拖慢识别。
///
/// 用 Windows 自带的 Media Foundation AAC 编码器（NAudio 的 MediaFoundationEncoder）。它只收 44.1/48 kHz 的 16 位整数 PCM，
/// 码率最低 96 kbps（macOS 用 64 kbps）：采集到的声音先换成 48 kHz 单声道（<see cref="SampleRate"/>），一小时约 43 MB。
/// 收到第一块声音时才建文件：打开录音后马上又关掉，不留下空文件。Windows 的 N 版本没有这个编码器（要装“媒体功能包”），
/// 这时报告 <see cref="Failed"/>，不录音，字幕照常。
/// 采集中断后重新打开时，中间的时间补成静音（<see cref="RecordingClock"/>），录音和字幕记录的时间对得上。
/// </summary>
internal sealed class AudioRecorder
{
    /// <summary>录音的采样率（AAC 编码器只收 44.1 kHz 或 48 kHz）。</summary>
    public const int SampleRate = 48_000;
    /// <summary>想要的码率；编码器选最接近的一档（Windows 的 AAC 编码器最低 96 kbps）。</summary>
    private const int DesiredBitRate = 64_000;
    /// <summary>编码器每次要多少数据：约 0.5 秒（NAudio 默认 4 秒，没有新声音时要等很久才凑满一次）。</summary>
    private const int ReadBufferBytes = SampleRate * 2 / 2;
    /// <summary>编码器的错误码：没有注册（N 版本没装媒体功能包）、找不到编码器。</summary>
    private const int ClassNotRegistered = unchecked((int)0x80040154);
    private const int CodecNotFound = unchecked((int)0xC00D5212);

    private readonly BlockingCollection<float[]> queue = new();
    private readonly Thread thread;
    private volatile bool failed;
    /// <summary>只在 clockGate 内访问；补的静音和声音按同样的顺序放进队列。</summary>
    private readonly RecordingClock clock = new(SampleRate);
    private readonly object clockGate = new();

    public string FilePath { get; }

    /// <summary>
    /// 录不了：在录音线程上触发。参数是给用户看的原因，以及是不是这台电脑根本没有 AAC 编码器
    /// （是的话再开始也录不了，由调用方关掉录音开关）。
    /// </summary>
    public event Action<string, bool>? Failed;

    public AudioRecorder(string filePath)
    {
        FilePath = filePath;
        thread = new Thread(Run) { IsBackground = true, Name = "LiveSub.Recorder", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    /// <summary>采集线程上调用：48 kHz 单声道 float 样本。</summary>
    /// <param name="newCapture">这是新打开的采集对象送来的第一块：之前采集中断的时间补成静音。</param>
    public void Append(float[] samples, bool newCapture)
    {
        if (failed || samples.Length == 0) return;
        try
        {
            lock (clockGate)
            {
                long gap = clock.Add(samples.Length, DateTime.UtcNow, newCapture);
                if (gap > 0) AppLog.Info($"recording: filled {gap / (double)SampleRate:F1}s gap after capture restarted");
                // 一秒一块放进队列，编码线程转换时不用一次分配几十 MB。
                for (long left = gap; left > 0; left -= SampleRate) queue.Add(new float[Math.Min(left, SampleRate)]);
                queue.Add(samples);
            }
        }
        catch (InvalidOperationException)
        {
            // 已经结束录音。
        }
    }

    /// <summary>写完已经收到的声音后关闭文件（m4a 要关闭后才能播放）。会等编码线程结束，不要在界面线程上调用。</summary>
    public void Finish()
    {
        queue.CompleteAdding();
        thread.Join();
    }

    private void Run()
    {
        try
        {
            // 等到第一块声音再建文件。
            if (!queue.TryTake(out var first, Timeout.Infinite)) return;
            MediaFoundationApi.Startup();
            var format = new WaveFormat(SampleRate, 16, 1);
            using var mediaType = MediaFoundationEncoder.SelectMediaType(AudioSubtypes.MFAudioFormat_AAC, format, DesiredBitRate);
            if (mediaType is null)
            {
                Fail(Strings.Error_RecordingNoEncoder, noEncoder: true, null);
                return;
            }
            AppLog.Info($"recording: {Path.GetFileName(FilePath)} ({mediaType.AverageBytesPerSecond * 8 / 1000} kbps)");
            using var encoder = new MediaFoundationEncoder(mediaType) { DefaultReadBufferSize = ReadBufferBytes };
            // 明确指定 MPEG-4 容器：按文件名推断时要靠系统认得 .m4a 这个扩展名。
            // MP4 写完要回到开头补上索引，文件要能读写、能定位。
            using var output = new FileStream(FilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            encoder.Encode(output, new QueueProvider(format, first, queue), TranscodeContainerTypes.MFTranscodeContainerType_MPEG4);
        }
        catch (Exception e)
        {
            bool noEncoder = IsMissingEncoder(e);
            Fail(noEncoder ? Strings.Error_RecordingNoEncoder : Describe(e), noEncoder, e);
        }
    }

    /// <summary>
    /// 确实是没有编码器（Windows 的 N 版本没装媒体功能包时 Media Foundation 的 dll、编码器都没有）。
    /// 别的 Media Foundation 错误（文件、格式、驱动）显示真实原因，不能都说成“没有编码器”。
    /// </summary>
    private static bool IsMissingEncoder(Exception e)
    {
        if (e is TypeInitializationException { InnerException: { } inner }) e = inner;
        return e is DllNotFoundException || e is COMException { HResult: ClassNotRegistered or CodecNotFound };
    }

    /// <summary>给用户看的原因：文件写不进去说明原因；Media Foundation 的错误带上错误码，方便查。</summary>
    private static string Describe(Exception e) => e switch
    {
        UnauthorizedAccessException or IOException => ErrorText.Download(e),
        COMException c => $"{c.Message} (0x{c.HResult:X8})",
        _ => e.Message,
    };

    private void Fail(string reason, bool noEncoder, Exception? e)
    {
        failed = true;
        AppLog.Error($"recording failed: {Path.GetFileName(FilePath)}{(noEncoder ? " (no AAC encoder)" : "")}", e);
        // 不再收样本，把已经排队的丢掉，免得一直占着内存。
        while (queue.TryTake(out _)) { }
        Failed?.Invoke(reason, noEncoder);
    }

    /// <summary>
    /// 编码器从这里拉数据：没有新的声音时等着（编码在自己的线程上，等待不影响别的），录音结束、队列取空后返回 0，编码器写完文件。
    /// 编码比实时快得多，每块一到就交出去的话每次只有 10 毫秒：拿到第一块后再等一会儿（<see cref="BatchMilliseconds"/>），
    /// 把这期间到的块一起填进去（最多填满编码器给的缓冲区）。
    /// </summary>
    private sealed class QueueProvider(WaveFormat format, float[] first, BlockingCollection<float[]> queue) : IWaveProvider
    {
        private const int BatchMilliseconds = 250;
        private byte[] pending = Pcm16.FromFloat(first);
        private int offset;

        public WaveFormat WaveFormat => format;

        public int Read(Span<byte> buffer)
        {
            // 按整帧（2 字节）给。
            buffer = buffer[..(buffer.Length & ~1)];
            int filled = 0;
            long deadline = 0;
            while (filled < buffer.Length)
            {
                if (offset >= pending.Length)
                {
                    // 一点都没有时一直等（取不到表示录音结束，返回 0）；已经有了就最多等到 deadline，录音结束时马上返回。
                    int wait = filled == 0 ? Timeout.Infinite : (int)Math.Max(0, deadline - Environment.TickCount64);
                    if (!queue.TryTake(out var next, wait)) break;
                    pending = Pcm16.FromFloat(next);
                    offset = 0;
                    continue;
                }
                if (filled == 0) deadline = Environment.TickCount64 + BatchMilliseconds;
                int count = Math.Min(buffer.Length - filled, pending.Length - offset);
                pending.AsSpan(offset, count).CopyTo(buffer[filled..]);
                offset += count;
                filled += count;
            }
            return filled;
        }
    }
}

/// <summary>
/// 采集线程和界面线程之间交接录音器（与 macOS 的 RecorderSlot 相同）：录音可以在字幕运行中途打开或关掉；
/// 采集中断后重新打开时，新的采集接着往同一个录音器里写。
/// </summary>
internal sealed class RecorderSlot
{
    private AudioRecorder? recorder;

    /// <summary>正在录音：采集线程只在这时把声音另外换成录音的格式。</summary>
    public bool Active => Volatile.Read(ref recorder) is not null;

    /// <summary>换上新的录音器（或 null 停止录音），返回原来的（由调用方结束它）。</summary>
    public AudioRecorder? Set(AudioRecorder? value) => Interlocked.Exchange(ref recorder, value);

    /// <summary>还是这个录音器时才拿掉（它出错时；用户可能已经关掉又打开了录音）。</summary>
    public void Clear(AudioRecorder expected) => Interlocked.CompareExchange(ref recorder, null, expected);

    /// <summary>采集线程上调用。<paramref name="newCapture"/>：这是新打开的采集对象送来的第一块（见 <see cref="AudioRecorder.Append"/>）。</summary>
    public void Append(float[] samples, bool newCapture) => Volatile.Read(ref recorder)?.Append(samples, newCapture);
}
