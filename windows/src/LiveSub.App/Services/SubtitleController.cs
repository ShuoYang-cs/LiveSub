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
    /// <summary>正在把听到的声音存成文件（录音按钮打开、字幕在运行）。</summary>
    public bool IsRecording { get; private set; }
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
    /// <summary>翻译时是否按发音纠正识别错误：打开了“自动纠正口音”、最近识别困难，并且有能纠正的大模型。</summary>
    public bool Correcting => Settings.AutoCorrect && RecognitionDifficult && CorrectionModel is not null;
    /// <summary>
    /// 按发音纠正用的大模型（与 macOS 相同）：所选翻译模型是大模型就用它；Google 翻译、混元翻译不能纠正，本机有 Claude Code 时
    /// 临时换成 Claude Sonnet（ICT 2026-10-22 实测：带上下文时 Sonnet 和 Haiku 一样快，但 Haiku 会编造原文没有的词）。
    /// </summary>
    public ModelChoice? CorrectionModel => Settings.TranslationModel.IsLanguageModel ? Settings.TranslationModel
        : ClaudeAvailable && !correctionBroken ? ModelChoice.ClaudeCode(ModelCatalog.Sonnet) : null;
    /// <summary>本机有没有 Claude Code（准备翻译器时查一次：判断能不能纠正要用，不能每次刷新状态条都去找）。</summary>
    public bool ClaudeAvailable { get; private set; }
    /// <summary>打开了“自动纠正口音”，但没有能纠正的大模型（用 Google 翻译、混元翻译，本机又没有可用的 Claude Code）。</summary>
    public bool CorrectionUnavailable => Settings.AutoCorrect && CorrectionModel is null;
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
    /// <summary>状态里的翻译模型：正在用的翻译器的完整名称（“Claude · Haiku”“Google 翻译”）。</summary>
    public string EngineName => translator?.Name ?? Settings.DisplayName(Settings.TranslationModel);
    /// <summary>状态条上的翻译模型：服务名；Google 翻译、混元翻译在纠正期间实际用的是 Claude，写成“Claude · 纠正中”（与 macOS 相同）。</summary>
    public string EngineLabel => !Settings.TranslationModel.IsLanguageModel && Correcting && CorrectionModel is { } model
        ? Strings.Format(Strings.Overlay_Correcting, Settings.ServiceName(model))
        : Settings.ServiceName(Settings.TranslationModel);
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
    /// <summary>正在用的翻译器：大模型会话，或 Google 翻译、混元翻译（识别困难时临时换成带纠正的 Claude 会话）。</summary>
    private ITranslator? translator;
    /// <summary>
    /// 选了 Google 翻译、混元翻译这类只能翻译的模型时，它们的翻译器（与 macOS 的 basicTranslator 相同）。
    /// 识别困难时 <see cref="translator"/> 临时换成带纠正的 Claude 会话，清楚了再换回它；Claude 出错时这一句也用它顶上。
    /// </summary>
    private ITranslator? basicTranslator;
    /// <summary>当前大模型会话是按哪种提示词建的（是否带“按发音纠正”）；与 <see cref="Correcting"/> 不一致时换会话。</summary>
    private bool sessionCorrecting;
    /// <summary>上次出现“重试也没用”的错误（未登录、缺密钥）；设置改变或重新开始前不再请求。</summary>
    private string? brokenMessage;
    /// <summary>纠正用的 Claude 出过“重试也没用”的错误（例如未登录）：这次不再纠正，一直用基础翻译器；设置改变或重新开始时再试。</summary>
    private bool correctionBroken;
    /// <summary>混元翻译启动不了，这次改用了 Google 翻译；设置改变或重新开始时再试混元翻译。</summary>
    private bool localModelFellBack;
    /// <summary>正在翻译的句数。</summary>
    private int activeTranslations;
    /// <summary>换下来、等正在翻译的句子完成后再关的旧翻译器；退出程序时一并关闭，不留下 claude、llama-server 进程。</summary>
    private readonly HashSet<ITranslator> retired = [];
    private RecognitionQualityMonitor quality = new();
    private DateTime alertAt;

    /// <summary>正在生成笔记的会话；退出程序时一并关闭。</summary>
    private IChatSession? notesSession;

    // 记录
    private TranscriptStore? store;
    private DateTime? sessionStart;

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
        LocalModelStore.Shared.InstalledChanged += OnLocalModelInstalledChanged;
        Assistant = new AssistantSession(settingsService, secrets, dispatcher, () => (Book.Lines, LiveText));
    }

    // MARK: - 开始 / 停止

    /// <summary>
    /// 用户点“开始/停止”。开始时重新判断讲者好不好识别，并重试上次不可用的翻译服务（包括纠正用的 Claude）；
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
            if (brokenMessage is not null || correctionBroken || localModelFellBack) ResetSession();
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
        if (Settings.HasStartedOnce != true)
        {
            Settings.HasStartedOnce = true;
            settingsService.Commit(SettingsChange.None);
        }
        SetStatus(Strings.Status_Preparing);
        AppLog.Info($"start: source={Settings.AudioSource} noiseReduction={Settings.MicNoiseReduction} lang={Settings.SourceLanguage} model={Settings.DisplayName(Settings.TranslationModel)} whisper={Settings.WhisperModel} autoCorrect={Settings.AutoCorrect}");
        try
        {
            // 先启动翻译器（Claude 进程、llama-server 启动需要几秒），与加载识别模型同时进行。
            PrepareTranslator();

            var model = WhisperModel.Find(Settings.WhisperModel);
            var progress = new Progress<int>(p => SetStatus(Strings.Format(Strings.Status_DownloadingModel, model.Id, p)));
            string modelPath;
            try
            {
                modelPath = await WhisperModels.EnsureAsync(model, Settings.ModelMirror, progress, cancel.Token);
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                AppLog.Error("whisper model download failed", e);
                throw new InvalidOperationException(Strings.Format(Strings.Error_ModelDownload, ErrorText.Download(e)), e);
            }
            SetStatus(Strings.Status_LoadingModel);
            var language = Settings.Source.WhisperCode;
            bool filter = Settings.FilterChineseActive, gpu = Settings.UseGpu;
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
            if (Settings.RecordAudio) StartRecording();
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
            SourceKind.TestFile => new FileSource(TestAudioFile!, p.Recording),
            SourceKind.Microphone => new WasapiSource(loopback: false, noiseReduction: p.NoiseReduction, recording: p.Recording),
            _ => new WasapiSource(loopback: true, recording: p.Recording),
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
            FinishRecording();
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
        FinishRecording();
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

    // MARK: - 录音

    /// <summary>录音按钮（与 macOS 相同）：字幕运行中也可以随时打开或关掉；没在运行时只改设置，下次开始时录音。</summary>
    public void ToggleRecording()
    {
        // 运行中按实际是否在录切换：录音出错停下了（按钮已经不是红的）时，点一下是重新开始录，不是关掉开关。
        bool record = !RecordButtonOn;
        Settings.RecordAudio = record;
        settingsService.Commit(SettingsChange.None);
        if (IsRunning)
        {
            if (record) StartRecording();
            else FinishRecording();
        }
        RaiseChanged();
    }

    /// <summary>录音按钮、托盘菜单的对勾：运行中按实际是否在录（录音出错会停下）；没在运行时是“下次开始时录音”的开关。</summary>
    public bool RecordButtonOn => IsRunning ? IsRecording : Settings.RecordAudio;

    /// <summary>
    /// 每个音源一个录音文件，和字幕记录放在一起、名字对应：“2026-10-22 13.35 录音.m4a”；“两者”时后面带音源（与 macOS 相同）。
    /// </summary>
    private void StartRecording()
    {
        if (store is not { } transcript) return;
        foreach (var p in pipelines)
        {
            if (p.Recording.Active) continue;
            var suffix = p.Label is null ? null : Strings.IsChinese ? $"（{p.Label}）" : $" ({p.Label})";
            var path = RecordingFiles.PathFor(transcript.FilePath, Strings.Transcript_Name, Strings.Recording_Name, suffix, File.Exists);
            var recorder = new AudioRecorder(path);
            recorder.Failed += (reason, noEncoder) => dispatcher.BeginInvoke(() => OnRecordingFailed(p, recorder, reason, noEncoder));
            p.Recording.Set(recorder);
        }
        IsRecording = pipelines.Any(p => p.Recording.Active);
    }

    /// <summary>
    /// 录不了：这一路不再录，字幕照常，录音按钮按实际是否在录显示。确认这台电脑没有 AAC 编码器时把录音开关关掉
    /// （所有音源都录不了），免得每次开始都再提示一遍；“两者”时两路都会报告，只提示一次。
    /// </summary>
    private void OnRecordingFailed(SourcePipeline p, AudioRecorder recorder, string reason, bool noEncoder)
    {
        p.Recording.Clear(recorder);
        if (noEncoder)
        {
            bool first = Settings.RecordAudio;
            if (first)
            {
                Settings.RecordAudio = false;
                settingsService.Commit(SettingsChange.None);
            }
            FinishRecording();
            if (first) ShowAlert(reason);
        }
        else
        {
            ShowAlert(Strings.Format(Strings.Error_RecordingFailed, reason));
        }
        IsRecording = pipelines.Any(x => x.Recording.Active);
        RaiseChanged();
    }

    /// <summary>关闭录音文件：在后台等它写完（m4a 要写完才能播放），不卡界面。退出程序前等它们写完（<see cref="WaitForRecordings"/>）。</summary>
    private void FinishRecording()
    {
        IsRecording = false;
        foreach (var p in pipelines)
        {
            if (p.Recording.Set(null) is not { } recorder) continue;
            var task = Task.Run(() =>
            {
                recorder.Finish();
                if (File.Exists(recorder.FilePath)) AppLog.Info($"recording saved: {Path.GetFileName(recorder.FilePath)}");
            });
            lock (finishingRecordings) finishingRecordings.Add(task);
            _ = task.ContinueWith(t =>
            {
                lock (finishingRecordings) finishingRecordings.Remove(t);
            }, TaskScheduler.Default);
        }
    }

    /// <summary>还没写完的录音文件：退出程序时等一会儿，否则 m4a 没写完、放不出来。</summary>
    private readonly List<Task> finishingRecordings = [];

    private Task WaitForRecordings(TimeSpan timeout)
    {
        Task[] pending;
        lock (finishingRecordings) pending = [.. finishingRecordings];
        return pending.Length == 0 ? Task.CompletedTask : Task.WhenAny(Task.WhenAll(pending), Task.Delay(timeout));
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
        await WaitForRecordings(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// 等翻译队列处理完（最多 <paramref name="timeout"/>）；仍在等待或翻译中的句子只写原文，保证记录里不缺句子。
    /// </summary>
    private async Task FlushPendingTranslations(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while ((activeTranslations > 0 || Book.Backlog > 0) && DateTime.UtcNow < deadline) await Task.Delay(100);
        foreach (var line in Book.TakeRemaining())
        {
            var translation = line.State is LineState.Done or LineState.Failed ? line.Translation : "";
            store?.Append(TranscriptFormat.Entry(line.Time, line.SourceLabel, line.Source, translation, line.OriginalSource));
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
        Settings.WhisperModel, Settings.FilterChineseActive, Settings.UseGpu);

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
            PrepareTranslator();
            UpdateListeningStatus();
        }
        RaiseChanged();
    }

    /// <summary>混元翻译下载好了：正在运行的字幕换成混元翻译（下载前先用的是 Google 翻译）。删掉时同样重新准备。</summary>
    private void OnLocalModelInstalledChanged()
    {
        if (Settings.TranslationModel.Kind == ModelKind.LocalModel) OnSettingsChanged(SettingsChange.Translation);
    }

    // MARK: - 翻译器

    /// <summary>
    /// 准备翻译器（与 macOS 的 prepareTranslators 相同）。大模型：按当前是否识别困难建带或不带“按发音纠正”的会话。
    /// Google 翻译、混元翻译：建好基础翻译器；识别困难（并且打开了“自动纠正口音”）时临时换成 Claude Sonnet 的纠正会话，清楚了换回来。
    /// 旧会话等正在翻译的句子完成后再关，新的紧接着建好，中间不留没有翻译器的空档。
    /// </summary>
    private void PrepareTranslator()
    {
        if (!Settings.TranslationModel.IsLanguageModel)
        {
            if (basicTranslator is null)
            {
                ClaudeAvailable = ClaudeLocator.Locate(Settings.ClaudePath) is not null;
                basicTranslator = CreateBasicTranslator();
                basicTranslator.WarmUp();
                // 刚从大模型换成 Google 翻译、混元翻译，设置还没应用（ResetSession 要等 0.8 秒）时，下一句就会走到这里：
                // 原来的大模型会话不能直接丢掉，否则没人关它，claude 进程一直留着。
                if (translator is not null) RetireLater(translator);
                translator = basicTranslator;
                AppLog.Info($"translator: {basicTranslator.Name}");
            }
            PrepareCorrection();
            return;
        }
        // 例如重新开始时识别状态恢复为“清晰”，旧会话却是按“纠正”的提示词建的：换掉。
        if (translator is not null && sessionCorrecting != Correcting)
        {
            RetireLater(translator);
            translator = null;
        }
        if (translator is not null || brokenMessage is not null) return;
        try
        {
            translator = CreateSession(Settings.TranslationModel);
        }
        catch (ChatProviderException e)
        {
            brokenMessage = TranslationEngines.Describe(e);
            ShowAlert(brokenMessage);
            AppLog.Error("translator: unavailable", e);
        }
    }

    /// <summary>Google 翻译、混元翻译：识别困难时换成带纠正的 Claude 会话，清楚了换回基础翻译器（它一直留着，不关）。</summary>
    private void PrepareCorrection()
    {
        bool correcting = Correcting;
        bool usingCorrection = translator is not null && !ReferenceEquals(translator, basicTranslator);
        if (correcting == usingCorrection) return;
        if (!correcting)
        {
            RetireLater(translator);
            translator = basicTranslator;
            return;
        }
        try
        {
            translator = CreateSession(CorrectionModel!);
        }
        catch (ChatProviderException e)
        {
            correctionBroken = true;
            translator = basicTranslator;
            AppLog.Error("translator: correction unavailable", e);
        }
    }

    /// <summary>大模型翻译会话；提示词里带不带“按发音纠正”看当前是否识别困难。</summary>
    private ChatTranslator CreateSession(ModelChoice model)
    {
        var session = TranslationEngines.Create(model, Settings, secrets,
            Prompts.TranslatorSystemPrompt(Settings.Source, Settings.Target, Correcting));
        sessionCorrecting = Correcting;
        session.WarmUp();
        AppLog.Info($"translator: {session.Name}{(sessionCorrecting ? " (correcting)" : "")}");
        return new ChatTranslator(session);
    }

    /// <summary>Google 翻译或混元翻译。混元翻译还没下载时这次先用 Google 翻译，并打开“模型服务”里的下载按钮（与 macOS 相同）。</summary>
    private ITranslator CreateBasicTranslator()
    {
        if (Settings.TranslationModel.Kind == ModelKind.LocalModel)
        {
            if (LocalModelFiles.IsInstalled) return new LocalTranslator(Settings.Source, Settings.Target, () => Settings.UseGpu);
            ShowAlert(Strings.Info_LocalModelMissing);
            settingsService.RequestSetup(LocalModelFiles.ServiceId);
        }
        return new GoogleTranslator(Settings.Source, Settings.Target);
    }

    /// <summary>换翻译器；旧的等正在翻译的句子完成后再关。</summary>
    private void ResetSession()
    {
        RetireLater(translator);
        if (!ReferenceEquals(basicTranslator, translator)) RetireLater(basicTranslator);
        translator = null;
        basicTranslator = null;
        brokenMessage = null;
        correctionBroken = false;
        localModelFellBack = false;
    }

    /// <summary>
    /// 混元翻译启动不了（文件被删、被安全软件拦截、加载超时）：这次改用 Google 翻译，免得每句都等它启动、每句都失败
    /// （与 macOS 的 fallBackFromLocalModel 相同）。改设置或重新开始时再试混元翻译。
    /// </summary>
    private bool FallBackFromLocalModel()
    {
        if (basicTranslator is not LocalTranslator local) return false;
        var google = new GoogleTranslator(Settings.Source, Settings.Target);
        google.WarmUp();
        if (ReferenceEquals(translator, local)) translator = google;
        RetireLater(local);
        basicTranslator = google;
        localModelFellBack = true;
        AppLog.Info("translator: local model unavailable, using Google Translate");
        return true;
    }

    private void RetireLater(ITranslator? old)
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
        // 换一个带/不带“按发音纠正”的翻译会话；Google 翻译、混元翻译换成或换回 Claude 的纠正会话（PrepareTranslator 会自动换）。
        PrepareTranslator();
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

    /// <summary>
    /// Google 翻译、混元翻译每句独立翻译，同时翻两句，后一句不用等前一句（ICT 2026-10-22 实测排队要多等 1 秒多）；
    /// 不多于两句，免得一下子发太多请求被 Google 限流（混元的 llama-server 也只开了两个槽位）。
    /// 大模型会话带着上下文，要按顺序一句一句来（与 macOS 的 runWorker 相同）。
    /// </summary>
    private void RunWorker()
    {
        int limit = basicTranslator is not null && ReferenceEquals(translator, basicTranslator) ? 2 : 1;
        while (activeTranslations < limit && Book.DequeueMerged() is { } line)
        {
            activeTranslations++;
            _ = TranslateThenContinue(line);
        }
    }

    private async Task TranslateThenContinue(SubtitleLine line)
    {
        try
        {
            await TranslateLine(line);
        }
        finally
        {
            activeTranslations--;
            RunWorker();
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
        PrepareTranslator();
        if (translator is not { } primary)
        {
            if (brokenMessage is not null) ShowAlert(brokenMessage);
            Fail(id);
            return;
        }
        bool cjk = Settings.Target.IsCJK;
        try
        {
            var raw = await primary.TranslateAsync(text, context, partial =>
            {
                var visible = TranslationOutput.VisibleTranslation(partial, cjk);
                dispatcher.BeginInvoke(() =>
                {
                    if (Book.UpdatePartial(id, visible, DateTime.UtcNow)) RaiseChanged();
                });
            }, CancellationToken.None);
            var output = TranslationOutput.Split(raw, text, cjk);
            Finish(id, output.Translation, output.CorrectedSource);
            return;
        }
        catch (Exception e)
        {
            var message = TranslationEngines.Describe(e);
            AppLog.Error("translate failed", e);
            // 换成 Google 翻译后，下面用它翻这一句。同时在翻的另一句也会失败：已经换过、提示过了，不再提示一遍
            // （否则后一条提示盖掉前一条，用户看不到“这次先用 Google 翻译”）。
            bool alreadyTold = false;
            if (e is LocalModelUnavailableException)
            {
                if (FallBackFromLocalModel()) message = Strings.Format(Strings.Info_LocalModelFallback, message);
                else alreadyTold = localModelFellBack;
            }
            if (e is ChatProviderException { IsPersistent: true } && ReferenceEquals(primary, translator))
            {
                // 纠正用的 Claude 不能用：这次不再纠正，换回基础翻译器；所选的大模型不能用：设置改变或重新开始前不再请求。
                if (basicTranslator is not null) correctionBroken = true;
                else brokenMessage = message;
                RetireLater(primary);
                translator = basicTranslator;
            }
            if (e is not OperationCanceledException && !alreadyTold) ShowAlert(message);
        }
        // 纠正用的 Claude 出错时，这一句先用原来的 Google 翻译、混元翻译翻；混元翻译启动不了、刚换成 Google 翻译时用 Google 翻译翻
        // （Windows 没有 Apple 本地翻译兜底，其他情况就算失败）。
        if (basicTranslator is { } basic && !ReferenceEquals(basic, primary))
        {
            try
            {
                Finish(id, await basic.TranslateAsync(text, context, null, CancellationToken.None), null);
                return;
            }
            catch (Exception e)
            {
                AppLog.Error("basic translate failed", e);
            }
        }
        Fail(id);
    }

    private void Finish(Guid id, string translation, string? correctedSource)
    {
        var line = Book.Finish(id, translation, null, correctedSource, Settings.KeepOriginalText, DateTime.UtcNow);
        if (line is null) return;
        if (AppLog.DebugLatency) LogLatency(line);
        Persist();
        RaiseChanged();
    }

    private void Fail(Guid id)
    {
        if (Book.Fail(id, DateTime.UtcNow) is not null) Persist();
        RaiseChanged();
    }

    /// <summary>按字幕顺序写进记录：前面还有句子没翻完时先不写（见 <see cref="SubtitleBook.TakePersistable"/>）。</summary>
    private void Persist()
    {
        foreach (var line in Book.TakePersistable())
        {
            store?.Append(TranscriptFormat.Entry(line.Time, line.SourceLabel, line.Source, line.Translation, line.OriginalSource));
        }
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

    /// <param name="owner">对话框的所有者窗口（字幕记录窗口）；没有时由 <see cref="SystemDialogs"/> 临时建一个。</param>
    public void ExportSubtitles(SubtitleFileFormat format, System.Windows.Window? owner = null)
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
        if (SystemDialogs.Show(dialog, owner) != true) return;
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
        // 没经过“退出”就结束时（例如注销）也把录音文件写完。
        FinishRecording();
        WaitForRecordings(TimeSpan.FromSeconds(3)).Wait();
        Assistant.Shutdown();
        notesSession?.Dispose();
        notesSession = null;
        foreach (var p in pipelines)
        {
            p.DeviceWatcher?.Dispose();
            p.Source?.Dispose();
        }
        LocalModelStore.Shared.InstalledChanged -= OnLocalModelInstalledChanged;
        translator?.Dispose();
        if (!ReferenceEquals(basicTranslator, translator)) basicTranslator?.Dispose();
        translator = null;
        basicTranslator = null;
        List<ITranslator> old;
        lock (retired)
        {
            old = [.. retired];
            retired.Clear();
        }
        foreach (var t in old) t.Dispose();
        // 保险：还有翻译器没关时（例如正在启动），llama-server 也不留下。
        LocalModelServer.Shared.Stop();
        if (recognizer is { } rec)
        {
            recognizer = null;
            rec.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
    }
}
