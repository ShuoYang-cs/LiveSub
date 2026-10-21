using LiveSub.App.Services;
using LiveSub.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LiveSub.App.Audio;

/// <summary>一个音源：输出 16 kHz 单声道 float 样本。事件在采集线程上触发。</summary>
public interface IAudioSource : IDisposable
{
    /// <summary>新样本（已转换成 16 kHz 单声道）。</summary>
    event Action<float[]>? Samples;
    /// <summary>采集意外中断（设备拔出、权限被收回等）。</summary>
    event Action<Exception>? Failed;

    void Start();
    void Stop();

    /// <summary>最近一块数据的音量（RMS）。</summary>
    float Level { get; }
    /// <summary>最近一次收到数据的时刻（UTC）。</summary>
    DateTime LastDataAt { get; }
}

/// <summary>
/// WASAPI 采集（NAudio WasapiRecorder）：默认麦克风，或默认输出设备正在播放的声音（环回采集）。
/// 默认设备变了（插拔耳机、切换设备）时，由 <see cref="DefaultDeviceWatcher"/> 通知控制器重新开始采集。
/// 不用 NAudio 的“默认设备流路由”：它只能用 BuildAsync 异步创建，用 Build 会直接抛异常。
///
/// 系统没有播放任何声音时，环回采集根本不送数据（不是送静音）。为了让分段器看到“停顿”、
/// 让导出字幕的时间轴与真实时间对齐，超过 200 毫秒没收到数据时按真实时间补静音。
/// </summary>
public sealed class WasapiSource : IAudioSource
{
    private readonly bool loopback;
    private readonly object gate = new();
    private WasapiRecorder? recorder;
    private AudioConverter? converter;
    private Timer? filler;
    private DateTime startedAt;
    private long emitted;
    private volatile bool stopping;
    private byte[] silence = [];
    /// <summary>最近一次收到真实数据包的时刻（补静音用）。</summary>
    private DateTime lastPacketAt;

    public event Action<float[]>? Samples;
    public event Action<Exception>? Failed;

    public float Level { get; private set; }
    public DateTime LastDataAt { get; private set; } = DateTime.UtcNow;

    public WasapiSource(bool loopback)
    {
        this.loopback = loopback;
    }

    public void Start()
    {
        var builder = new WasapiRecorderBuilder().WithSharedMode().WithEventSync().WithBufferLength(50);
        if (loopback) builder = builder.WithLoopbackCapture();
        var r = builder.Build();
        var format = r.WaveFormat;
        converter = new AudioConverter(format.SampleRate, format.Channels, SampleFormatOf(format));
        AppLog.Info($"audio: {(loopback ? "loopback" : "microphone")} {format.SampleRate} Hz, {format.Channels} ch, {format.Encoding} {format.BitsPerSample} bit");
        r.DataAvailable += OnData;
        r.RecordingStopped += (_, e) =>
        {
            if (!stopping && e.Exception is not null) Failed?.Invoke(e.Exception);
        };
        recorder = r;
        startedAt = DateTime.UtcNow;
        LastDataAt = lastPacketAt = startedAt;
        emitted = 0;
        r.StartRecording();
        if (loopback) filler = new Timer(_ => FillSilence(), null, 100, 100);
    }

    public void Stop()
    {
        stopping = true;
        filler?.Dispose();
        filler = null;
        try
        {
            recorder?.StopRecording();
        }
        catch (Exception e)
        {
            AppLog.Error("audio: stop failed", e);
        }
    }

    public void Dispose()
    {
        Stop();
        recorder?.Dispose();
        recorder = null;
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (converter is null || buffer.Length == 0) return;
        lock (gate)
        {
            // 标记为静音的数据块内容不可靠，按全零处理。
            if (flags.HasFlag(AudioClientBufferFlags.Silent))
            {
                if (silence.Length < buffer.Length) silence = new byte[buffer.Length];
                buffer = silence.AsSpan(0, buffer.Length);
            }
            var samples = converter.Convert(buffer);
            if (samples.Length == 0) return;
            Level = converter.LastRms;
            LastDataAt = lastPacketAt = DateTime.UtcNow;
            emitted += samples.Length;
            Samples?.Invoke(samples);
        }
    }

    private void FillSilence()
    {
        lock (gate)
        {
            var now = DateTime.UtcNow;
            if (stopping || now - lastPacketAt < TimeSpan.FromMilliseconds(200)) return;
            long expected = (long)((now - startedAt).TotalSeconds * AudioConverter.TargetRate);
            long missing = Math.Min(expected - emitted, AudioConverter.TargetRate);
            if (missing <= 0) return;
            emitted += missing;
            Level = 0;
            LastDataAt = now;
            Samples?.Invoke(new float[missing]);
        }
    }

    /// <summary>WASAPI 共享模式通常给 32 位浮点；也处理 16/24/32 位整数。</summary>
    internal static SampleFormat SampleFormatOf(WaveFormat format)
    {
        var encoding = format.Encoding;
        if (encoding == WaveFormatEncoding.Extensible && format is WaveFormatExtensible ext)
        {
            encoding = ext.SubFormat == FloatSubFormat ? WaveFormatEncoding.IeeeFloat : WaveFormatEncoding.Pcm;
        }
        if (encoding == WaveFormatEncoding.IeeeFloat) return SampleFormat.Float32;
        return format.BitsPerSample switch
        {
            16 => SampleFormat.Int16,
            24 => SampleFormat.Int24,
            32 => SampleFormat.Int32,
            _ => throw new NotSupportedException($"{format.BitsPerSample}-bit audio"),
        };
    }

    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
}

/// <summary>
/// 用音频文件代替真实声音（环境变量 LIVESUB_TEST_AUDIO_FILE），按真实速度送出，便于不说话也能测试整条链路。
/// </summary>
public sealed class FileSource : IAudioSource
{
    private readonly string path;
    private WaveStream? reader;
    private AudioConverter? converter;
    private Timer? timer;
    private readonly object gate = new();

    public event Action<float[]>? Samples;
    public event Action<Exception>? Failed;

    public float Level { get; private set; }
    public DateTime LastDataAt { get; private set; } = DateTime.UtcNow;

    public FileSource(string path)
    {
        this.path = path;
    }

    public void Start()
    {
        reader = path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? new WaveFileReader(path) : new MediaFoundationReader(path);
        var format = reader.WaveFormat;
        converter = new AudioConverter(format.SampleRate, format.Channels, WasapiSource.SampleFormatOf(format));
        int bytesPer100ms = format.AverageBytesPerSecond / 10 / format.BlockAlign * format.BlockAlign;
        var buffer = new byte[bytesPer100ms];
        timer = new Timer(_ =>
        {
            lock (gate)
            {
                if (reader is null) return;
                try
                {
                    int n = reader.Read(buffer, 0, buffer.Length);
                    // 文件读完后继续送静音，让最后一段能结束。
                    var samples = n > 0 ? converter.Convert(buffer.AsSpan(0, n)) : new float[AudioConverter.TargetRate / 10];
                    Level = n > 0 ? converter.LastRms : 0;
                    LastDataAt = DateTime.UtcNow;
                    Samples?.Invoke(samples);
                }
                catch (Exception e)
                {
                    Failed?.Invoke(e);
                }
            }
        }, null, 0, 100);
    }

    public void Stop()
    {
        timer?.Dispose();
        timer = null;
    }

    public void Dispose()
    {
        Stop();
        lock (gate)
        {
            reader?.Dispose();
            reader = null;
        }
    }
}

/// <summary>监听默认音频设备的变化（插拔耳机、切换输出），以便重新开始采集。</summary>
public sealed class DefaultDeviceWatcher : IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly MMDeviceNotificationClient client;
    private readonly DataFlow flow;

    /// <summary>默认设备变了（在系统线程上触发）。</summary>
    public event Action? Changed;

    public DefaultDeviceWatcher(DataFlow flow)
    {
        this.flow = flow;
        client = enumerator.CreateNotificationClient(false);
        client.DefaultDeviceChanged += OnDefaultDeviceChanged;
    }

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
    {
        if (e.Flow == flow && e.Role == Role.Console) Changed?.Invoke();
    }

    public void Dispose()
    {
        client.DefaultDeviceChanged -= OnDefaultDeviceChanged;
        client.Dispose();
        enumerator.Dispose();
    }
}
