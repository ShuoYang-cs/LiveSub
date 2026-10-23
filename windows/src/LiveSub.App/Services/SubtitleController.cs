using System.Diagnostics;
using System.Text;
using System.Windows.Threading;
using LiveSub.App.Audio;
using LiveSub.App.Localization;
using LiveSub.App.Speech;
using LiveSub.App.Translation;
using LiveSub.App.UI;
using LiveSub.Core;
using LiveSub.Core.Audio;
using LiveSub.Core.Chat;
using LiveSub.Core.Speech;
using LiveSub.Core.Subtitles;

namespace LiveSub.App.Services;

/// <summary>
/// 整条链路：采集 → 分段 → Whisper 识别 → 分句 → 翻译队列（积压合并）→ 字幕 + 记录文件。
/// 每个音源一条 <see cref="SourcePipeline"/>（“两者”模式下麦克风和系统音频各一条），共用一个识别器、翻译队列和识别质量判断。
/// 除了标明的地方，所有状态只在界面线程上访问（与 macOS 版的 @MainActor 相同）。
/// </summary>
public sealed class SubtitleController : IDisposable
{
    private readonly SettingsService settingsService;
    private readonly SecretStore secrets;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer ticker;
    private readonly DispatcherTimer applyTimer;

    public SubtitleBook Book { get; } = new();
    /// <summary>AI 助手对话（面板在 UI/AssistantWindow）。</summary>
    public AssistantSession Assistant { get; }

    /// <summary>界面需要刷新（字幕、状态条）。</summary>
    public event Action? Changed;

    public bool IsRunning { get; private set; }
    /// <summary>正在开始、停止，或正在重新打开某个音源的采集。</summary>
    public bool IsBusy => busy || pipelines.Any(p => p.Switching);
    /// <summary>正在准备（下载或加载识别模型）；此时“停止”会取消启动。</summary>
    public bool IsStarting => startCancel is not null;
    public string Status { get; private set; } = Strings.Status_NotStarted;
    /// <summary>最近的错误（10 秒后自动清除）。</summary>
    public string? Alert { get; private set; }
    /// <summary>状态条：最近的输入音量（0…1）、多久没收到音频、识别是否困难。有两个音源时音量取最大、时间取最短。</summary>
    public float InputLevel { get; private set; }
    public double SilentFor { get; private set; }
    public bool RecognitionDifficult { get; private set; }
    /// <summary>翻译时是否按发音纠正识别错误：打开了“自动纠正口音”，并且最近识别困难。</summary>
    public bool Correcting => Settings.AutoCorrect && RecognitionDifficult;
    /// <summary>
    /// 麦克风被 Windows 隐私设置挡住（或多半如此）：字幕不停，权限打开后自动继续。
    /// “两者”模式下只影响麦克风这一路，系统音频照常识别。
    /// </summary>
    public MicrophoneBlock MicrophoneBlock => pipelines.FirstOrDefault(p => p.IsMicrophone)?.Block ?? MicrophoneBlock.None;
    /// <summary>麦克风被挡住时字幕下方的提示。</summary>
    public string? MicrophoneNotice => MicrophoneBlock switch
    {
        MicrophoneBlock.Certain => Strings.Notice_MicrophoneOff,
        MicrophoneBlock.Likely => Strings.Notice_MicrophoneMaybeOff,
        _ => null,
    };
    public string EngineName => session?.Name ?? Settings.DisplayName(Settings.TranslationModel);
    public string? TranscriptPath => store?.FilePath;
    /// <summary>正在生成笔记（托盘菜单里的“生成笔记”这时不可点）。</summary>
    public bool IsGeneratingNotes { get; private set; }

    /// <summary>正在说、还没送去翻译的文字（分句器里剩下的 + 实时预览），各音源连在一起（给 AI 助手）。</summary>
    public string LiveText => SourceMix.Speaking(pipelines.Select(p => p.LiveText));

    /// <summary>字幕下方“正在说”的各行：每个音源一行，“两者”模式下带音源标签。</summary>
    public List<LiveRow> LiveRows => SourceMix.LiveRows(pipelines.Select(p => new LiveRow(p.Label, p.LiveText)));

    private AppSettings Settings => settingsService.Current;

    // 识别链路
    /// <summary>这次“开始”的各个音源；停止后清空。</summary>
    private List<SourcePipeline> pipelines = [];
    /// <summary>正在开始或停止。</summary>
    private bool busy;
    /// <summary>所有音源共用一个识别模型；语音段按送来的先后识别，结果带着音源序号。</summary>
    private WhisperRecognizer? recognizer;
    /// <summary>音频时间轴的 0 秒（各音源同时开始采集，共用这个时刻）。</summary>
    private DateTime captureStart;
    private int generation;
    private int filteredCount;
    private string listeningStatus = "";
    private string startedRecognitionKey = "";
    private CancellationTokenSource? startCancel;
    private SettingsChange pendingChange;

    // 采集中断后的恢复（字幕不停；每个音源各自恢复，见 SourcePipeline）
    /// <summary>采集一断就重新打开会反复失败；隔这么久再试。</summary>
    private static readonly TimeSpan AudioRetryDelay = TimeSpan.FromSeconds(3);
    /// <summary>重新打开后这么久没再断开，算恢复正常（权限被关闭时约 3 秒后才断开）。</summary>
    private static readonly TimeSpan AudioRecoveredAfter = TimeSpan.FromSeconds(5);

    // 翻译
    private IChatSession? session;
    /// <summary>当前会话是按哪种提示词建的（是否带“按发音纠正”）；与 <see cref="Correcting"/> 不一致时换会话。</summary>
    private bool sessionCorrecting;
    /// <summary>上次出现“重试也没用”的错误（未登录、缺密钥）；设置改变或重新开始前不再请求。</summary>
    private string? brokenMessage;
    private bool workerRunning;
    /// <summary>换下来、等正在翻译的句子完成后再关的旧会话；退出程序时一并关闭，不留下 claude 进程。</summary>
    private readonly HashSet<IChatSession> retired = [];
    private RecognitionQualityMonitor quality = new();
    private DateTime alertAt;

    /// <summary>正在生成笔记的会话；退出程序时一并关闭。</summary>
    private IChatSession? notesSession;

    // 记录
    private TranscriptStore? store;
    private DateTime? sessionStart;
    /// <summary>已经写进记录文件的句子（避免重复写入）。</summary>
    private readonly HashSet<Guid> persisted = [];

    public SubtitleController(SettingsService settingsService, SecretStore secrets, Dispatcher dispatcher)
    {
        this.settingsService = settingsService;
        this.secrets = secrets;
        this.dispatcher = dispatcher;
        ticker = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        ticker.Tick += (_, _) => Tick();
        applyTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromMilliseconds(800) };
        applyTimer.Tick += async (_, _) =>
        {
            applyTimer.Stop();
            var change = pendingChange;
            pendingChange = SettingsChange.None;
            await ApplySettings(change);
        };
        settingsService.Changed += OnSettingsChanged;
        Assistant = new AssistantSession(settingsService, secrets, dispatcher, () => (Book.Lines, LiveText));
    }

    // MARK: - 开始 / 停止

    /// <summary>
    /// 用户点“开始/停止”。开始时重新判断讲者好不好识别，并重试上次不可用的翻译服务；
    /// AI 助手开始新的对话（设置变化引起的自动重启不会清空对话），面板开着时提前启动它的会话。
    /// </summary>
    public async Task Toggle()
    {
        if (IsBusy)
        {
            // 准备过程中（例如正在下载模型）点“停止”：取消启动。
            startCancel?.Cancel();
            return;
        }
        if (IsRunning)
        {
            await Stop();
        }
        else
        {
            Assistant.NewConversation();
            if (Settings.AssistantVisible) Assistant.WarmUp();
            quality = new RecognitionQualityMonitor();
            RecognitionDifficult = false;
            if (brokenMessage is not null) ResetSession();
            await Start();
        }
    }

    public async Task Start()
    {
        if (IsRunning || IsBusy) return;
        busy = true;
        var cancel = new CancellationTokenSource();
        startCancel = cancel;
        int gen = ++generation;
        SetStatus(Strings.Status_Preparing);
        AppLog.Info($"start: source={Settings.AudioSource} noiseReduction={Settings.MicNoiseReduction} lang={Settings.SourceLanguage} model={Settings.DisplayName(Settings.TranslationModel)} whisper={Settings.WhisperModel} autoCorrect={Settings.AutoCorrect}");
        try
        {
            // 先启动翻译会话（Claude 进程启动需要几秒），与加载识别模型同时进行。
            PrepareSession();

            var model = WhisperModel.Find(Settings.WhisperModel);
            var progress = new Progress<int>(p => SetStatus(Strings.Format(Strings.Status_DownloadingModel, model.Id, p)));
            string modelPath;
            try
            {
                modelPath = await WhisperModels.EnsureAsync(model, Settings.ModelMirror, progress, cancel.Token);
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                throw new InvalidOperationException(Strings.Format(Strings.Error_ModelDownload, e.Message), e);
            }
            SetStatus(Strings.Status_LoadingModel);
            var language = Settings.Source.WhisperCode;
            bool filter = Settings.FilterChinese, gpu = Settings.UseGpu;
            var rec = await Task.Run(() => new WhisperRecognizer(modelPath, language, filter, gpu), cancel.Token);
            if (cancel.IsCancellationRequested)
            {
                await rec.DisposeAsync();
                throw new OperationCanceledException();
            }
            rec.Final += r => dispatcher.BeginInvoke(() => HandleFinal(gen, r));
            rec.Preview += (source, start, text) => dispatcher.BeginInvoke(() => HandlePreview(gen, source, start, text));
            rec.Filtered += source => dispatcher.BeginInvoke(() => HandleFiltered(gen, source));
            rec.Error += e => dispatcher.BeginInvoke(() => ShowAlert(Strings.Format(Strings.Error_Recognizer, e.Message)));
            recognizer = rec;

            store ??= new TranscriptStore(Settings.TranscriptFolder, DateTime.Now, Settings.Source, Settings.Target);
            pipelines = CreatePipelines();
            filteredCount = 0;

            captureStart = DateTime.UtcNow;
            if (pipelines.Any(p => p.IsMicrophone)) AppLog.Info($"audio: microphone privacy {MicrophonePrivacy.Describe()}");
            StartAudioOrWait(gen);
            foreach (var p in pipelines) StartDeviceWatcher(gen, p);
            sessionStart ??= captureStart;

            IsRunning = true;
            startedRecognitionKey = RecognitionKey;
            ticker.Start();
            UpdateListeningStatus();
            AppLog.Info("start: running");
        }
        catch (OperationCanceledException)
        {
            await CleanupRecognition();
            SetStatus(Strings.Status_Stopped);
        }
        catch (Exception e)
        {
            AppLog.Error("start failed", e);
            await CleanupRecognition();
            var message = e is InvalidOperationException && e.InnerException is not null ? e.Message : Strings.Format(Strings.Status_StartFailed, e.Message);
            SetStatus(message);
            ShowAlert(message);
        }
        finally
        {
            busy = false;
            if (ReferenceEquals(startCancel, cancel)) startCancel = null;
            cancel.Dispose();
            RaiseChanged();
        }
    }

    private static string? TestAudioFile => Environment.GetEnvironmentVariable("LIVESUB_TEST_AUDIO_FILE");

    private static bool UsingTestAudio => TestAudioFile is not null;

    /// <summary>
    /// 按“音源”设置建链路（与 macOS 相同）：麦克风、系统音频，或两者各一条；“两者”时每句字幕标出来自哪个音源。
    /// 用测试音频文件时只有一条。
    /// </summary>
    private List<SourcePipeline> CreatePipelines()
    {
        if (UsingTestAudio) return [new SourcePipeline(0, SourceKind.TestFile, null, false)];
        var mode = Settings.AudioSource;
        bool both = mode == AudioSourceMode.Both;
        var list = new List<SourcePipeline>();
        if (mode != AudioSourceMode.System)
        {
            list.Add(new SourcePipeline(list.Count, SourceKind.Microphone, both ? Strings.Source_Microphone : null, Settings.MicNoiseReduction));
        }
        if (mode != AudioSourceMode.Microphone)
        {
            list.Add(new SourcePipeline(list.Count, SourceKind.SystemAudio, both ? Strings.Source_System : null, false));
        }
        return list;
    }

    private SourcePipeline? Pipeline(int index) => index >= 0 && index < pipelines.Count ? pipelines[index] : null;

    /// <summary>“两者”模式下的提示前面加上音源，说明是哪一路出了问题。</summary>
    private static string SourceAlert(SourcePipeline p, string message) => p.Label is null ? message : $"{p.Label} · {message}";

    /// <summary>
    /// 开始采集。系统拒绝访问麦克风时不算启动失败：字幕照常开始，等权限打开后自动开始采集。
    /// “两者”模式下一个音源打不开时另一个照常工作，打不开的那个之后定期重新打开；两个都打不开才算启动失败。
    /// </summary>
    private void StartAudioOrWait(int gen)
    {
        InvalidOperationException? failure = null;
        bool anyStarted = false;
        foreach (var p in pipelines)
        {
            try
            {
                StartAudio(p, gen);
                anyStarted = true;
            }
            catch (InvalidOperationException e) when (p.IsMicrophone && MicrophonePrivacy.IsAccessDenied(e))
            {
                AppLog.Error("audio: microphone access denied", e.InnerException);
                WaitForAudio(p, MicrophoneBlock.Certain);
                anyStarted = true;
            }
            catch (InvalidOperationException e) when (pipelines.Count > 1)
            {
                var cause = e.InnerException ?? e;
                AppLog.Error($"audio: {p.Name} unavailable", cause);
                failure ??= e;
                ShowAlert(e.Message);
                WaitForAudio(p, ClassifyFailure(p, cause, TimeSpan.Zero));
            }
        }
        if (!anyStarted && failure is not null) throw failure;
    }

    private void StartAudio(SourcePipeline p, int gen)
    {
        IAudioSource s = p.Kind switch
        {
            SourceKind.TestFile => new FileSource(TestAudioFile!),
            SourceKind.Microphone => new WasapiSource(loopback: false, noiseReduction: p.NoiseReduction),
            _ => new WasapiSource(loopback: true),
        };
        s.Samples += samples => OnSamples(gen, p, samples);
        s.Failed += e => dispatcher.BeginInvoke(() =>
        {
            if (gen != generation || !ReferenceEquals(p.Source, s)) return;
            OnCaptureFailed(p, e);
        });
        try
        {
            s.Start();
        }
        catch (Exception e)
        {
            s.Dispose();
            throw new InvalidOperationException(
                Strings.Format(p.Kind == SourceKind.SystemAudio ? Strings.Error_SystemAudio : Strings.Error_Microphone, e.Message), e);
        }
        p.Source = s;
        p.SourceStartedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// 某个音源的采集意外中断：字幕不停，停掉坏掉的采集，之后定期重新打开（<see cref="RecoverAudio"/>）。
    /// 麦克风权限被关闭时 WASAPI 照样能打开麦克风，约 3 秒后报“设备已断开”：这时提示去隐私设置里打开，打开后自动继续。
    /// “两者”模式下另一个音源不受影响。
    /// </summary>
    private async void OnCaptureFailed(SourcePipeline p, Exception e)
    {
        // 正在开始、停止，或正在切换这个音源的设备：这个采集马上会被换掉。
        if (busy || p.Switching) return;
        if (UsingTestAudio)
        {
            AppLog.Error("audio: capture failed", e);
            ShowAlert(Strings.Format(Strings.Error_AudioStopped, e.Message));
            return;
        }
        var ranFor = DateTime.UtcNow - p.SourceStartedAt;
        if (ShouldLogAudioFailure(p)) AppLog.Error($"audio: {p.Name} capture failed after {ranFor.TotalSeconds:F1}s", e);
        var block = ClassifyFailure(p, e, ranFor);
        bool firstFailure = !p.AudioLost;
        p.Switching = true;
        try
        {
            // 正在说的一段照常送去识别。
            await StopAudio(p);
        }
        finally
        {
            p.Switching = false;
        }
        if (!IsRunning) return;
        if (block == MicrophoneBlock.None && firstFailure) ShowAlert(SourceAlert(p, Strings.Format(Strings.Error_AudioStopped, e.Message)));
        WaitForAudio(p, block);
    }

    private static bool ShouldLogAudioFailure(SourcePipeline p) => p.AudioFailures < 3 || p.AudioFailures % 20 == 0;

    private static MicrophoneBlock ClassifyFailure(SourcePipeline p, Exception e, TimeSpan ranFor)
    {
        if (!p.IsMicrophone) return MicrophoneBlock.None;
        return MicrophoneAccess.Classify(
            switchDenied: MicrophonePrivacy.IsDenied(),
            accessDenied: MicrophonePrivacy.IsAccessDenied(e),
            deviceLost: MicrophonePrivacy.IsDeviceLost(e),
            ranFor: ranFor,
            microphonePresent: MicrophonePrivacy.HasMicrophone());
    }

    /// <summary>这个音源进入（或继续）等待采集恢复。</summary>
    private void WaitForAudio(SourcePipeline p, MicrophoneBlock block)
    {
        if (block != p.Block || !p.AudioLost)
        {
            var privacy = p.IsMicrophone ? $", microphone privacy {MicrophonePrivacy.Describe()}" : "";
            AppLog.Info($"audio: {p.Name} waiting for capture (microphone block: {block}{privacy})");
        }
        p.AudioLost = true;
        p.AudioFailures++;
        p.Block = block;
        p.NextAudioRetry = DateTime.UtcNow + AudioRetryDelay;
        UpdateListeningStatus();
    }

    /// <summary>
    /// 每次 Tick：采集断了的音源隔一会儿重新打开；重新打开后几秒没再断开，就恢复正常。
    /// 隐私开关读得到且还是“拒绝”时不去打开麦克风，每秒看一次开关。
    /// </summary>
    private void RecoverAudio(DateTime now)
    {
        if (!IsRunning || busy) return;
        foreach (var p in pipelines) RecoverAudio(p, now);
    }

    private void RecoverAudio(SourcePipeline p, DateTime now)
    {
        if (!p.AudioLost || p.Switching) return;
        if (p.Source is not null)
        {
            if (now - p.SourceStartedAt < AudioRecoveredAfter) return;
            AppLog.Info($"audio: {p.Name} capture resumed");
            ResetAudioWait(p);
            UpdateListeningStatus();
            return;
        }
        if (now < p.NextAudioRetry) return;
        if (p.Block == MicrophoneBlock.Certain && MicrophonePrivacy.IsDenied())
        {
            p.NextAudioRetry = now + TimeSpan.FromSeconds(1);
            return;
        }
        // 断开期间没有送样本：时间轴接到现在，导出字幕的时间才对得上。
        lock (p.ChunkerGate) p.Chunker = new SpeechChunker(startSample: Math.Max(p.Chunker.ProcessedSamples, ElapsedSamples));
        try
        {
            StartAudio(p, generation);
            // 确定是权限问题、现在又能打开了：提示马上撤掉。还要等几秒才算恢复，期间又断开会重新判断。
            if (p.Block == MicrophoneBlock.Certain)
            {
                p.Block = MicrophoneBlock.None;
                UpdateListeningStatus();
            }
        }
        catch (InvalidOperationException e)
        {
            var cause = e.InnerException ?? e;
            if (ShouldLogAudioFailure(p)) AppLog.Error($"audio: {p.Name} reopen failed", cause);
            WaitForAudio(p, ClassifyFailure(p, cause, TimeSpan.Zero));
        }
    }

    private static void ResetAudioWait(SourcePipeline p)
    {
        p.AudioLost = false;
        p.AudioFailures = 0;
        p.Block = MicrophoneBlock.None;
    }

    /// <summary>
    /// 监听这个音源的默认设备：麦克风看默认录音设备，系统音频看默认输出设备，各自只重开自己。
    /// 它的生命周期跟随这次“开始—停止”，重启采集失败时也保留，下次设备变化还能再试。
    /// </summary>
    private void StartDeviceWatcher(int gen, SourcePipeline p)
    {
        if (p.Kind == SourceKind.TestFile) return;
        try
        {
            p.DeviceWatcher = new DefaultDeviceWatcher(p.IsMicrophone ? NAudio.CoreAudioApi.DataFlow.Capture : NAudio.CoreAudioApi.DataFlow.Render);
            p.DeviceWatcher.Changed += () => dispatcher.BeginInvoke(() => OnDefaultDeviceChanged(gen, p));
        }
        catch (Exception e)
        {
            AppLog.Error($"audio: {p.Name} device watcher unavailable", e);
        }
    }

    /// <summary>
    /// 插拔耳机、切换默认设备：重新开始这个音源的采集（识别、字幕、记录都保留）。
    /// 新设备一时打不开时重试几次；都失败就正常停止，并在状态里说明原因，不留下“正在听但没有声音”的状态。
    /// “两者”模式下不停止：另一个音源照常工作，打不开的这个之后定期重新打开。
    /// </summary>
    private async void OnDefaultDeviceChanged(int gen, SourcePipeline p)
    {
        if (gen != generation || !IsRunning || busy || p.Switching) return;
        AppLog.Info($"audio: default device changed, restarting {p.Name} capture");
        p.Switching = true;
        Exception? failure = null;
        try
        {
            await Task.Delay(800); // 设备切换时系统会连发几次通知，等它稳定下来。
            if (gen != generation || !IsRunning) return;
            await StopAudio(p);
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    StartAudio(p, gen);
                    failure = null;
                    break;
                }
                catch (Exception e)
                {
                    if (p.IsMicrophone && MicrophonePrivacy.IsAccessDenied(e))
                    {
                        // 麦克风权限被关闭：不停止字幕，等权限打开。
                        AppLog.Error("audio: microphone access denied", e.InnerException);
                        WaitForAudio(p, MicrophoneBlock.Certain);
                        failure = null;
                        break;
                    }
                    failure = e;
                    AppLog.Error($"audio: {p.Name} restart attempt {attempt} failed", e);
                    await Task.Delay(1500);
                    if (gen != generation || !IsRunning) return;
                }
            }
            if (failure is null) ShowAlert(Strings.Info_DeviceChanged);
        }
        finally
        {
            p.Switching = false;
        }
        if (failure is null) return;
        var message = failure.Message;
        if (pipelines.Count > 1)
        {
            ShowAlert(SourceAlert(p, message));
            WaitForAudio(p, MicrophoneBlock.None);
            return;
        }
        await Stop();
        SetStatus(message);
        ShowAlert(message);
    }

    /// <param name="recognitionTimeout">最多等多久让最后几段识别完（退出程序时用较短的时间）。</param>
    /// <param name="busyTimeout">正在启动或重启采集时，最多等多久再停。</param>
    public async Task Stop(TimeSpan? recognitionTimeout = null, TimeSpan? busyTimeout = null)
    {
        var waitBusy = busyTimeout ?? TimeSpan.FromSeconds(10);
        if (!IsRunning)
        {
            // 正在准备（例如下载模型）：取消启动并等它结束。
            startCancel?.Cancel();
            await WaitWhileBusy(waitBusy);
            if (!IsRunning) return;
        }
        if (IsBusy && !await WaitWhileBusy(waitBusy))
        {
            AppLog.Error("stop: still busy, giving up");
            return;
        }
        if (!IsRunning) return;
        busy = true;
        AppLog.Info("stop");
        IsRunning = false;
        ticker.Stop();
        try
        {
            foreach (var p in pipelines) await StopAudio(p);
            if (recognizer is { } rec)
            {
                // 等最后几段识别完，再把分句器里剩下的内容送去翻译。
                await rec.CompleteAsync(recognitionTimeout ?? TimeSpan.FromSeconds(15));
                await Task.Yield();
            }
            foreach (var p in pipelines)
            {
                p.VolatileText = "";
                Enqueue(p.Segmenter.FlushAll(), p);
            }
            await CleanupRecognition();
            SetStatus(Strings.Status_Stopped);
        }
        finally
        {
            busy = false;
            RaiseChanged();
        }
    }

    private async Task<bool> WaitWhileBusy(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (IsBusy && DateTime.UtcNow < deadline) await Task.Delay(50);
        return !IsBusy;
    }

    /// <summary>停止这个音源的采集，并把正在说的一段送去识别（设备监听不在这里关，见 <see cref="StartDeviceWatcher"/>）。</summary>
    private async Task StopAudio(SourcePipeline p)
    {
        if (p.Source is { } s)
        {
            p.Source = null;
            await Task.Run(s.Dispose);
        }
        SpeechChunk? last;
        lock (p.ChunkerGate)
        {
            last = p.Chunker.Flush();
            // 重新开始采集时接着原来的音频时间轴（按真实经过的时间），导出字幕的时间才对得上。
            long resumeAt = Math.Max(p.Chunker.ProcessedSamples, ElapsedSamples);
            p.Chunker = new SpeechChunker(startSample: resumeAt);
        }
        if (last is not null)
        {
            p.LastClosedChunkEnd = last.EndSample;
            recognizer?.Enqueue(last, p.Index);
        }
    }

    /// <summary>从开始录音到现在，按真实经过的时间算的样本数。</summary>
    private long ElapsedSamples => (long)((DateTime.UtcNow - captureStart).TotalSeconds * AudioConverter.TargetRate);

    private async Task CleanupRecognition()
    {
        foreach (var p in pipelines)
        {
            ResetAudioWait(p);
            p.DeviceWatcher?.Dispose();
            p.DeviceWatcher = null;
            if (p.Source is { } s)
            {
                p.Source = null;
                s.Dispose();
            }
        }
        pipelines = [];
        if (recognizer is { } rec)
        {
            recognizer = null;
            await rec.DisposeAsync();
        }
        generation++;
    }

    /// <summary>结束当前记录文件，下次开始时新建一个。</summary>
    public async Task NewSession()
    {
        await Stop();
        if (IsRunning || IsBusy)
        {
            // 没能停下来（例如正在切换音频设备）：不清空，否则后面的字幕不会写进任何记录。
            ShowAlert(Strings.Error_Busy);
            return;
        }
        await FlushPendingTranslations(TimeSpan.FromSeconds(10));
        Book.Clear();
        persisted.Clear();
        store = null;
        sessionStart = null;
        ResetSession();
        Assistant.NewConversation();
        SetStatus(Strings.Status_NewSession);
        RaiseChanged();
    }

    /// <summary>退出程序前：尽快停止（时间都较短），把还没翻译完的句子也写进记录。</summary>
    public async Task ShutdownAsync()
    {
        await Stop(recognitionTimeout: TimeSpan.FromSeconds(3), busyTimeout: TimeSpan.FromSeconds(3));
        await FlushPendingTranslations(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// 等翻译队列处理完（最多 <paramref name="timeout"/>）；仍在等待或翻译中的句子只写原文，保证记录里不缺句子。
    /// </summary>
    private async Task FlushPendingTranslations(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while ((workerRunning || Book.Backlog > 0) && DateTime.UtcNow < deadline) await Task.Delay(100);
        foreach (var line in Book.Lines.Where(l => l.State is LineState.Waiting or LineState.Translating).ToList())
        {
            if (!persisted.Add(line.Id)) continue;
            store?.Append(TranscriptFormat.Entry(line.Time, line.SourceLabel, line.Source, "", line.OriginalSource));
        }
    }

    // MARK: - 设置

    private void OnSettingsChanged(SettingsChange change)
    {
        if (change.HasFlag(SettingsChange.Appearance)) RaiseChanged();
        if ((change & (SettingsChange.Translation | SettingsChange.Recognition)) == 0) return;
        // 稍等一下再应用，避免在文本框里打字时每个字都重来一次。
        pendingChange |= change;
        applyTimer.Stop();
        applyTimer.Start();
    }

    private string RecognitionKey => string.Join('\u0001', Settings.SourceLanguage, Settings.AudioSource, Settings.MicNoiseReduction,
        Settings.WhisperModel, Settings.FilterChinese, Settings.UseGpu);

    private async Task ApplySettings(SettingsChange change)
    {
        AppLog.Info($"apply settings {change} (running: {IsRunning})");
        while (IsBusy) await Task.Delay(100);
        if (change.HasFlag(SettingsChange.Translation) || change.HasFlag(SettingsChange.Recognition)) ResetSession();
        if (IsRunning && RecognitionKey != startedRecognitionKey)
        {
            await Stop();
            await Start();
        }
        else if (IsRunning)
        {
            PrepareSession();
            UpdateListeningStatus();
        }
        RaiseChanged();
    }

    // MARK: - 翻译会话

    private void PrepareSession()
    {
        // 例如重新开始时识别状态恢复为“清晰”，旧会话却是按“纠正”的提示词建的：换掉。
        // 旧会话等正在翻译的句子完成后再关，新会话紧接着建好，中间不留没有翻译器的空档。
        if (session is not null && sessionCorrecting != Correcting)
        {
            RetireLater(session);
            session = null;
        }
        if (session is not null || brokenMessage is not null) return;
        try
        {
            session = TranslationEngines.Create(Settings.TranslationModel, Settings, secrets,
                Prompts.TranslatorSystemPrompt(Settings.Source, Settings.Target, Correcting));
            sessionCorrecting = Correcting;
            session.WarmUp();
            AppLog.Info($"translator: {session.Name}");
        }
        catch (ChatProviderException e)
        {
            brokenMessage = TranslationEngines.Describe(e);
            ShowAlert(brokenMessage);
            AppLog.Error("translator: unavailable", e);
        }
    }

    /// <summary>换翻译会话；旧会话等正在翻译的句子完成后再关。</summary>
    private void ResetSession()
    {
        RetireLater(session);
        session = null;
        brokenMessage = null;
    }

    private void RetireLater(IChatSession? old)
    {
        if (old is null) return;
        lock (retired) retired.Add(old);
        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
        {
            lock (retired)
            {
                if (!retired.Remove(old)) return;
            }
            old.Dispose();
        }, TaskScheduler.Default);
    }

    // MARK: - 识别事件（界面线程）

    private void OnSamples(int gen, SourcePipeline p, float[] samples)
    {
        // 采集线程上调用。
        List<SpeechChunk> chunks;
        lock (p.ChunkerGate)
        {
            if (gen != generation) return;
            chunks = p.Chunker.Feed(samples);
        }
        foreach (var chunk in chunks)
        {
            Volatile.Write(ref p.LastClosedChunkEnd, chunk.EndSample);
            recognizer?.Enqueue(chunk, p.Index);
        }
    }

    private void HandleFinal(int gen, RecognitionResult result)
    {
        if (gen != generation || Pipeline(result.Source) is not { } p) return;
        if (Book.Lines.Count == 0) AppLog.Info("first final result");
        p.VolatileText = "";
        p.Timeline.AddFinal(result.Text, result.Start, result.End);
        UpdateQuality(result.Confidences);
        Enqueue(p.Segmenter.AddFinal(result.Text), p);
        RaiseChanged();
    }

    private void HandlePreview(int gen, int source, long chunkStart, string text)
    {
        // 这一段已经结束（确认结果会接着到来）：丢掉过时的预览。
        if (gen != generation || Pipeline(source) is not { } p || chunkStart < Volatile.Read(ref p.LastClosedChunkEnd)) return;
        p.VolatileText = text;
        RaiseChanged();
    }

    private void HandleFiltered(int gen, int source)
    {
        if (gen != generation || Pipeline(source) is not { } p) return;
        filteredCount++;
        p.VolatileText = "";
        UpdateListeningStatus();
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        // 有两个音源时音量取最大、“多久没收到音频”取最短；采集断了、正在等它恢复的音源不算。
        var input = SourceMix.Combine(pipelines.Select(p =>
            p.Source is { } s ? new SourceInput(s.Level, (now - s.LastDataAt).TotalSeconds) : (SourceInput?)null));
        float level = Math.Min(input.Level * 8, 1);
        InputLevel = MathF.Round(level * 10) / 10;
        SilentFor = input.SilentFor;
        RecoverAudio(now);
        if (Alert is not null && now - alertAt > TimeSpan.FromSeconds(10)) Alert = null;
        foreach (var p in pipelines) TickPipeline(p, now);
        RaiseChanged();
    }

    private void TickPipeline(SourcePipeline p, DateTime now)
    {
        bool speaking;
        lock (p.ChunkerGate) speaking = p.Chunker.InSpeech;
        bool recognizing = recognizer?.IsBusy(p.Index) ?? false;
        var segments = p.Segmenter.Tick(now, speechInProgress: speaking || recognizing || p.VolatileText.Length > 0);
        if (segments.Count > 0) Enqueue(segments, p);
        if (!speaking && p.VolatileText.Length > 0 && !recognizing && p.Segmenter.Pending.Length == 0)
        {
            // 预览之后没有确认结果（例如被判为幻觉而丢掉）：别让它一直挂在屏幕上。
            if (now - p.LastPreviewAt > TimeSpan.FromSeconds(4)) p.VolatileText = "";
        }
        MaybeRequestPreview(p, now, speaking);
    }

    /// <summary>实时预览：识别器空闲且速度够快时，每 1.2 秒识别一次这个音源正在说的这一段（两个音源轮流）。</summary>
    private void MaybeRequestPreview(SourcePipeline p, DateTime now, bool speaking)
    {
        if (!Settings.LivePreview || !speaking || recognizer is not { } rec || rec.Busy) return;
        if (rec.RealTimeFactor > 0.5 || now - p.LastPreviewAt < TimeSpan.FromSeconds(1.2)) return;
        SpeechChunk? snapshot;
        lock (p.ChunkerGate) snapshot = p.Chunker.Snapshot();
        if (snapshot is null) return;
        p.LastPreviewAt = now;
        rec.RequestPreview(snapshot, p.Index);
    }

    // MARK: - 识别质量（自动判断是否需要按发音纠正）

    private void UpdateQuality(IReadOnlyList<double> confidences)
    {
        if (confidences.Count == 0 || !quality.Add(confidences)) return;
        RecognitionDifficult = quality.IsDifficult;
        AppLog.Info($"recognition {(RecognitionDifficult ? "difficult" : "clear")} (avg confidence {quality.AverageConfidence:F2})");
        // 没打开自动纠正时只在状态条上提示，不换翻译会话。
        if (!Settings.AutoCorrect || brokenMessage is not null) return;
        // 换一个带/不带“按发音纠正”的翻译会话（PrepareSession 发现提示词不一致会自动换）。
        PrepareSession();
        UpdateListeningStatus();
    }

    private void UpdateListeningStatus()
    {
        var src = Strings.IsChinese ? Settings.Source.DisplayName : Settings.Source.EnglishName;
        var dst = Strings.IsChinese ? Settings.Target.DisplayName : Settings.Target.EnglishName;
        listeningStatus = Strings.Format(Strings.Status_Listening, Settings.AudioSource.Title(), src, dst, EngineName);
        if (MicrophoneBlock != MicrophoneBlock.None)
        {
            // “两者”模式下系统音频照常识别，状态里说清楚。
            SetStatus(pipelines.Count > 1 ? Strings.Status_WaitingMicrophoneBoth : Strings.Status_WaitingMicrophone);
            return;
        }
        SetStatus(filteredCount > 0 ? $"{listeningStatus} · {Strings.Format(Strings.Status_Filtered, filteredCount)}" : listeningStatus);
    }

    private void SetStatus(string status)
    {
        Status = status;
        RaiseChanged();
    }

    public void ShowAlert(string message)
    {
        Alert = message;
        alertAt = DateTime.UtcNow;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    // MARK: - 翻译队列

    /// <summary>把这个音源分好的句子加入字幕和翻译队列；“两者”模式下带上音源标签（积压合并只合并同一音源的句子）。</summary>
    private void Enqueue(List<string> segments, SourcePipeline p)
    {
        if (segments.Count == 0) return;
        Book.Enqueue(segments, p.Label, p.Timeline, captureStart, DateTime.UtcNow);
        RunWorker();
        RaiseChanged();
    }

    private async void RunWorker()
    {
        if (workerRunning) return;
        workerRunning = true;
        try
        {
            while (Book.DequeueMerged() is { } line)
            {
                await TranslateLine(line);
            }
        }
        finally
        {
            workerRunning = false;
        }
    }

    private async Task TranslateLine(SubtitleLine line)
    {
        var id = line.Id;
        var text = line.Source;
        Book.MarkTranslating(id, DateTime.UtcNow);
        var context = Book.RecentContext(id);
        RaiseChanged();

        if (Settings.Source.TranslationCode == Settings.Target.TranslationCode)
        {
            Finish(id, "", null);
            return;
        }
        PrepareSession();
        if (session is not { } current)
        {
            if (brokenMessage is not null) ShowAlert(brokenMessage);
            Fail(id);
            return;
        }
        bool cjk = Settings.Target.IsCJK;
        try
        {
            var raw = await current.RequestAsync(Prompts.TranslationMessage(text, context), text, partial =>
            {
                var visible = TranslationOutput.VisibleTranslation(partial, cjk);
                dispatcher.BeginInvoke(() =>
                {
                    if (Book.UpdatePartial(id, visible, DateTime.UtcNow)) RaiseChanged();
                });
            }, CancellationToken.None);
            var output = TranslationOutput.Split(raw, text, cjk);
            Finish(id, output.Translation, output.CorrectedSource);
        }
        catch (Exception e)
        {
            var message = TranslationEngines.Describe(e);
            AppLog.Error("translate failed", e);
            if (e is ChatProviderException { IsPersistent: true })
            {
                brokenMessage = message;
                RetireLater(session);
                session = null;
            }
            if (e is not OperationCanceledException) ShowAlert(message);
            Fail(id);
        }
    }

    private void Finish(Guid id, string translation, string? correctedSource)
    {
        var line = Book.Finish(id, translation, null, correctedSource, Settings.KeepOriginalText, DateTime.UtcNow);
        if (line is null) return;
        if (AppLog.DebugLatency) LogLatency(line);
        Persist(line);
        RaiseChanged();
    }

    private void Fail(Guid id)
    {
        if (Book.Fail(id, DateTime.UtcNow) is { } line) Persist(line);
        RaiseChanged();
    }

    private void Persist(SubtitleLine line)
    {
        if (!persisted.Add(line.Id)) return;
        store?.Append(TranscriptFormat.Entry(line.Time, line.SourceLabel, line.Source, line.Translation, line.OriginalSource));
    }

    private static void LogLatency(SubtitleLine l)
    {
        static string Gap(DateTime? a, DateTime? b) => a is { } x && b is { } y ? $"{(y - x).TotalSeconds,5:F2}" : "  -  ";
        var now = DateTime.UtcNow;
        AppLog.Info($"[latency] 说完→入队 {Gap(l.AudioEnd, l.Time)}  入队→开始 {Gap(l.Time, l.TranslateStartedAt)}"
                    + $"  开始→首字 {Gap(l.TranslateStartedAt, l.FirstTokenAt)}  首字→完成 {Gap(l.FirstTokenAt, now)}"
                    + $"  总计 {Gap(l.AudioEnd, now)}  | {(l.Source.Length > 50 ? l.Source[..50] : l.Source)}");
    }

    // MARK: - 导出字幕

    /// <summary>字幕文件内容：时间相对这份记录开始录音的时刻；文字按当前“显示内容”。</summary>
    public string SubtitleFileContents(SubtitleFileFormat format) =>
        Book.SubtitleFile(format, Settings.DisplayMode, sessionStart);

    public void ExportSubtitles(SubtitleFileFormat format)
    {
        var content = SubtitleFileContents(format);
        var ext = format == SubtitleFileFormat.Srt ? "srt" : "vtt";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = (store is null ? Strings.Export_DefaultName : Path.GetFileNameWithoutExtension(store.FilePath)) + "." + ext,
            InitialDirectory = store is null ? Settings.TranscriptFolder : Path.GetDirectoryName(store.FilePath),
            Filter = format == SubtitleFileFormat.Srt ? $"{Strings.Export_FilterSrt}|*.srt" : $"{Strings.Export_FilterVtt}|*.vtt",
            DefaultExt = ext,
            AddExtension = true,
        };
        if (SystemDialogs.Show(dialog) != true) return;
        try
        {
            // SRT 带 BOM：Windows 上不少播放器靠它识别 UTF-8 中文。
            File.WriteAllText(dialog.FileName, content, new UTF8Encoding(format == SubtitleFileFormat.Srt));
            SetStatus(Strings.Format(Strings.Status_Exported, Path.GetFileName(dialog.FileName)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowAlert(Strings.Format(Strings.Error_ExportFailed, e.Message));
        }
    }

    // MARK: - 笔记

    /// <summary>
    /// 用 AI 助手的模型把本次全部字幕整理成笔记（与 macOS 版相同），保存在字幕记录旁边（没有记录文件时放在临时文件夹），
    /// 然后用默认程序打开。
    /// </summary>
    public async void GenerateNotes()
    {
        if (IsGeneratingNotes) return;
        var done = Book.Lines.Where(l => l.Source.Length > 0).ToList();
        if (done.Count == 0)
        {
            SetStatus(Strings.Status_NoNotes);
            return;
        }
        IChatSession notes;
        try
        {
            notes = TranslationEngines.Create(Settings.AssistantModel, Settings, secrets, Prompts.NotesPrompt(Settings.Target),
                maxTurns: 1, timeout: TimeSpan.FromSeconds(600));
        }
        catch (ChatProviderException e)
        {
            AppLog.Error("notes: unavailable", e);
            SetStatus(TranslationEngines.Describe(e));
            return;
        }
        notesSession = notes;
        IsGeneratingNotes = true;
        SetStatus(Strings.Format(Strings.Status_GeneratingNotes, Settings.DisplayName(Settings.AssistantModel)));
        var transcript = TranscriptFormat.Plain(done.Select(l => (l.Time, l.Source, l.Translation)));
        var path = store is { } s
            ? TranscriptFormat.NotesPath(s.FilePath, Strings.Transcript_Name, Strings.Notes_Name)
            : Path.Combine(Path.GetTempPath(), Strings.Notes_TempName + ".md");
        AppLog.Info($"notes: generating from {done.Count} lines");
        try
        {
            // 在后台线程上发：全部字幕很长，Claude Code 要等进程读走才写得完，在界面线程上写会卡住界面。await 之后回到界面线程。
            var text = await Task.Run(() => notes.RequestAsync(transcript, transcript, null, CancellationToken.None));
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
            SetStatus(Strings.Format(Strings.Status_NotesSaved, Path.GetFileName(path)));
            AppLog.Info("notes: saved");
            OpenFile(path);
        }
        catch (Exception e)
        {
            AppLog.Error("notes: failed", e);
            var reason = e is IOException or UnauthorizedAccessException ? e.Message : TranslationEngines.Describe(e);
            SetStatus(Strings.Format(Strings.Status_NotesFailed, reason));
        }
        finally
        {
            notes.Dispose();
            if (ReferenceEquals(notesSession, notes)) notesSession = null;
            IsGeneratingNotes = false;
            RaiseChanged();
        }
    }

    private static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception e)
        {
            AppLog.Error("notes: open failed", e);
        }
    }

    public void Dispose()
    {
        ticker.Stop();
        Assistant.Shutdown();
        notesSession?.Dispose();
        notesSession = null;
        foreach (var p in pipelines)
        {
            p.DeviceWatcher?.Dispose();
            p.Source?.Dispose();
        }
        session?.Dispose();
        session = null;
        List<IChatSession> old;
        lock (retired)
        {
            old = [.. retired];
            retired.Clear();
        }
        foreach (var s in old) s.Dispose();
        if (recognizer is { } rec)
        {
            recognizer = null;
            rec.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
    }
}
