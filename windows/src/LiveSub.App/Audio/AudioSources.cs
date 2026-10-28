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
///
/// 麦克风降噪：按“通话”类别打开麦克风（WithCommunicationsMode，在初始化前设置 AudioStreamCategory.Communications），
/// 系统和声卡驱动对它做通话用的语音处理：降噪、回声消除，较新的 Windows 11 上还有“语音清晰度”（Voice Clarity）。
/// 具体做哪些处理取决于驱动。设置类别失败时写日志，改为不降噪地打开，不让字幕因此启动失败。
/// 环回采集（系统声音）不用通话类别。
///
/// 录音（快捷按钮里的录音按钮）打开时，同一块数据另外换成 48 kHz 单声道交给 <see cref="RecorderSlot"/>，补的静音也一样补上，
/// 录下的声音和真实时间对齐。采集中断、换了新的 WasapiSource 时，两个采集之间的空缺由录音器补（第一块标上 newCapture）。
/// </summary>
public sealed class WasapiSource : IAudioSource
{
    private readonly bool loopback;
    private readonly bool noiseReduction;
    private readonly RecorderSlot? recording;
    private readonly object gate = new();
    private WasapiRecorder? recorder;
    /// <summary>正在录音的麦克风（环回采集时为 null，由 NAudio 自己找默认输出设备）。</summary>
    private MMDevice? device;
    private AudioConverter? converter;
    /// <summary>录音用的转换器（48 kHz 单声道）；只在录音时用。</summary>
    private AudioConverter? recordingConverter;
    private Timer? filler;
    private DateTime startedAt;
    private long emitted;
    private volatile bool stopping;
    private byte[] silence = [];
    /// <summary>最近一次收到真实数据包的时刻（补静音用）。</summary>
    private DateTime lastPacketAt;
    /// <summary>这个采集对象已经往录音里写过：第一块要告诉录音器这是新的采集，由它补上之前中断的时间。</summary>
    private bool recorded;

    /// <summary>
    /// 麦克风、环回采集各自上次写进日志的采集格式。每次打开都会新建一个 WasapiSource（麦克风被隐私设置挡住时每几秒重试一次），
    /// 只在第一次打开或格式变了时写，免得日志被同一行刷满。
    /// </summary>
    private static readonly Dictionary<bool, string> LoggedFormats = [];

    public event Action<float[]>? Samples;
    public event Action<Exception>? Failed;

    public float Level { get; private set; }
    public DateTime LastDataAt { get; private set; } = DateTime.UtcNow;

    /// <param name="noiseReduction">麦克风降噪（只对麦克风有效）。</param>
    /// <param name="recording">这一路的录音器（录音打开时才有录音器在里面）。</param>
    internal WasapiSource(bool loopback, bool noiseReduction = false, RecorderSlot? recording = null)
    {
        this.loopback = loopback;
        this.noiseReduction = noiseReduction && !loopback;
        this.recording = recording;
    }

    private static WasapiRecorderBuilder NewBuilder() => new WasapiRecorderBuilder().WithSharedMode().WithEventSync().WithBufferLength(50);

    public void Start()
    {
        if (loopback)
        {
            Begin(NewBuilder().WithLoopbackCapture().Build(), "loopback");
            filler = new Timer(_ => FillSilence(), null, 100, 100);
            return;
        }
        using (var enumerator = new MMDeviceEnumerator())
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }
        KeepOtherSoundsAtFullVolume(device);
        if (noiseReduction)
        {
            try
            {
                Begin(NewBuilder().WithDevice(device).WithCommunicationsMode().Build(), "microphone (noise reduction)");
                return;
            }
            catch (Exception e) when (!MicrophonePrivacy.IsAccessDenied(e))
            {
                // 例如驱动不支持通话类别：不降噪也照常录音。权限问题照常抛出，由控制器提示去隐私设置里打开。
                AppLog.Error("audio: noise reduction unavailable, capturing without it", e);
            }
        }
        Begin(NewBuilder().WithDevice(device).Build(), "microphone");
    }

    /// <summary>开始录音。失败时关掉这个录音器再抛出，可以换一种方式重新打开。</summary>
    private void Begin(WasapiRecorder r, string kind)
    {
        try
        {
            var format = r.WaveFormat;
            converter = new AudioConverter(format.SampleRate, format.Channels, SampleFormatOf(format));
            recordingConverter = new AudioConverter(format.SampleRate, format.Channels, SampleFormatOf(format), AudioRecorder.SampleRate);
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
            // 打开成功后再写：降噪打不开、换成普通方式时，日志里只有真正在用的这一种。
            LogFormat($"{kind} {format.SampleRate} Hz, {format.Channels} ch, {format.Encoding} {format.BitsPerSample} bit");
        }
        catch
        {
            recorder = null;
            r.Dispose();
            throw;
        }
    }

    private void LogFormat(string description)
    {
        lock (LoggedFormats)
        {
            if (LoggedFormats.TryGetValue(loopback, out var last) && last == description) return;
            LoggedFormats[loopback] = description;
        }
        AppLog.Info($"audio: {description}");
    }

    /// <summary>
    /// 按通话类别录音时（麦克风同时是“默认通信设备”时也可能），Windows 会按“检测到通信活动时降低其他声音的音量”
    /// 把其他声音调小，“两者”模式下正在识别的系统声音也会变小。开始录音前让这个会话不触发它；做不到时只写日志。
    /// </summary>
    private static void KeepOtherSoundsAtFullVolume(MMDevice device)
    {
        try
        {
            device.AudioSessionManager.AudioSessionControl.SetDuckingPreference(true);
        }
        catch (Exception e)
        {
            // 有的电脑上麦克风的会话不支持这个设置（AUDCLNT_E_WRONG_ENDPOINT_TYPE，Windows 11 21H2 实测），
            // 但同一台电脑上开着降噪时系统声音也没有被调小，所以不算错误，每次运行只记一次。
            if (duckingFailureLogged) return;
            duckingFailureLogged = true;
            AppLog.Info($"audio: ducking opt-out unavailable ({e.GetType().Name}: {e.Message})");
        }
    }

    private static bool duckingFailureLogged;

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
        device?.Dispose();
        device = null;
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
            if (recording is { Active: true } && recordingConverter is not null) Record(recordingConverter.Convert(buffer));
            if (samples.Length == 0) return;
            // 慢慢回落：状态条每 0.25 秒看一次，只看最后一块会常常落在字与字之间的空隙里（与 macOS 相同）。
            Level = SourceMix.DecayedLevel(Level, converter.LastRms, samples.Length);
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
            if (recording is { Active: true }) Record(new float[missing * (AudioRecorder.SampleRate / AudioConverter.TargetRate)]);
        }
    }

    /// <summary>在 gate 内调用。</summary>
    private void Record(float[] samples)
    {
        if (samples.Length == 0 || recording is null) return;
        recording.Append(samples, newCapture: !recorded);
        recorded = true;
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
    private readonly RecorderSlot? recording;
    private WaveStream? reader;
    private AudioConverter? converter;
    private AudioConverter? recordingConverter;
    private Timer? timer;
    private readonly object gate = new();

    public event Action<float[]>? Samples;
    public event Action<Exception>? Failed;

    public float Level { get; private set; }
    public DateTime LastDataAt { get; private set; } = DateTime.UtcNow;

    internal FileSource(string path, RecorderSlot? recording = null)
    {
        this.path = path;
        this.recording = recording;
    }

    public void Start()
    {
        reader = path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? new WaveFileReader(path) : new MediaFoundationReader(path);
        var format = reader.WaveFormat;
        converter = new AudioConverter(format.SampleRate, format.Channels, WasapiSource.SampleFormatOf(format));
        recordingConverter = new AudioConverter(format.SampleRate, format.Channels, WasapiSource.SampleFormatOf(format), AudioRecorder.SampleRate);
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
                    if (recording is { Active: true })
                    {
                        recording.Append(n > 0 ? recordingConverter.Convert(buffer.AsSpan(0, n)) : new float[AudioRecorder.SampleRate / 10], newCapture: false);
                    }
                    Level = n > 0 ? SourceMix.DecayedLevel(Level, converter.LastRms, samples.Length) : 0;
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
