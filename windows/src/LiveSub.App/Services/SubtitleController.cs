using System.Text;
using System.Windows.Threading;
using LiveSub.App.Audio;
using LiveSub.App.Localization;
using LiveSub.App.Speech;
using LiveSub.App.Translation;
using LiveSub.Core;
using LiveSub.Core.Audio;
using LiveSub.Core.Chat;
using LiveSub.Core.Speech;
using LiveSub.Core.Subtitles;

namespace LiveSub.App.Services;

/// <summary>
/// 整条链路：采集 → 分段 → Whisper 识别 → 分句 → 翻译队列（积压合并）→ 字幕 + 记录文件。
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

    /// <summary>界面需要刷新（字幕、状态条）。</summary>
    public event Action? Changed;

    public bool IsRunning { get; private set; }
    public bool IsBusy { get; private set; }
    /// <summary>正在准备（下载或加载识别模型）；此时“停止”会取消启动。</summary>
    public bool IsStarting => startCancel is not null;
    public string Status { get; private set; } = Strings.Status_NotStarted;
    /// <summary>最近的错误（10 秒后自动清除）。</summary>
    public string? Alert { get; private set; }
    /// <summary>状态条：最近的输入音量（0…1）、多久没收到音频、识别是否困难。</summary>
    public float InputLevel { get; private set; }
    public double SilentFor { get; private set; }
    public bool RecognitionDifficult { get; private set; }
    public string EngineName => session?.Name ?? TranslationEngines.CurrentName(Settings);
    public string? TranscriptPath => store?.FilePath;

    /// <summary>正在说、还没送去翻译的文字（分句器里剩下的 + 实时预览）。</summary>
    public string LiveText => Segmenter.Join(segmenter.Pending, volatileText);

    private AppSettings Settings => settingsService.Current;

    // 识别链路
    private IAudioSource? source;
    private DefaultDeviceWatcher? deviceWatcher;
    private readonly object chunkerGate = new();
    private SpeechChunker chunker = new();
    private WhisperRecognizer? recognizer;
    private Segmenter segmenter = new();
    private SegmentTimeline timeline = new();
    private DateTime captureStart;
    private int generation;
    private string volatileText = "";
    /// <summary>最近结束的一段语音的结束样本：早于它开始的预览已经过时。</summary>
    private long lastClosedChunkEnd = -1;
    private DateTime lastPreviewAt = DateTime.MinValue;
    private int filteredCount;
    private string listeningStatus = "";
    private string startedRecognitionKey = "";
    private CancellationTokenSource? startCancel;
    private SettingsChange pendingChange;

    // 翻译
    private IChatSession? session;
    /// <summary>当前会话是按哪种提示词建的（是否带“按发音纠正”）；与 <see cref="RecognitionDifficult"/> 不一致时换会话。</summary>
    private bool sessionDifficult;
    /// <summary>上次出现“重试也没用”的错误（未登录、缺密钥）；设置改变或重新开始前不再请求。</summary>
    private string? brokenMessage;
    private bool workerRunning;
    /// <summary>换下来、等正在翻译的句子完成后再关的旧会话；退出程序时一并关闭，不留下 claude 进程。</summary>
    private readonly HashSet<IChatSession> retired = [];
    private RecognitionQualityMonitor quality = new();
    private DateTime alertAt;

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
    }

    // MARK: - 开始 / 停止

    /// <summary>用户点“开始/停止”。开始时重新判断讲者好不好识别，并重试上次不可用的翻译服务。</summary>
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
            quality = new RecognitionQualityMonitor();
            RecognitionDifficult = false;
            if (brokenMessage is not null) ResetSession();
            await Start();
        }
    }

    public async Task Start()
    {
        if (IsRunning || IsBusy) return;
        IsBusy = true;
        var cancel = new CancellationTokenSource();
        startCancel = cancel;
        int gen = ++generation;
        SetStatus(Strings.Status_Preparing);
        AppLog.Info($"start: source={Settings.AudioSource} lang={Settings.SourceLanguage} engine={Settings.Engine} model={Settings.WhisperModel}");
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
            rec.Preview += (start, text) => dispatcher.BeginInvoke(() => HandlePreview(gen, start, text));
            rec.Filtered += () => dispatcher.BeginInvoke(() => HandleFiltered(gen));
            rec.Error += e => dispatcher.BeginInvoke(() => ShowAlert(Strings.Format(Strings.Error_Recognizer, e.Message)));
            recognizer = rec;

            store ??= new TranscriptStore(Settings.TranscriptFolder, DateTime.Now, Settings.Source, Settings.Target);
            lock (chunkerGate) chunker = new SpeechChunker();
            segmenter = new Segmenter();
            timeline = new SegmentTimeline();
            volatileText = "";
            lastClosedChunkEnd = -1;
            filteredCount = 0;

            captureStart = DateTime.UtcNow;
            StartAudio(gen);
            StartDeviceWatcher(gen);
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
            IsBusy = false;
            if (ReferenceEquals(startCancel, cancel)) startCancel = null;
            cancel.Dispose();
            RaiseChanged();
        }
    }

    private void StartAudio(int gen)
    {
        var testFile = Environment.GetEnvironmentVariable("LIVESUB_TEST_AUDIO_FILE");
        bool mic = Settings.AudioSource == AudioSourceMode.Microphone;
        IAudioSource s = testFile is not null ? new FileSource(testFile) : new WasapiSource(loopback: !mic);
        s.Samples += samples => OnSamples(gen, samples);
        s.Failed += e => dispatcher.BeginInvoke(() =>
        {
            if (gen != generation) return;
            AppLog.Error("audio: capture failed", e);
            ShowAlert(Strings.Format(Strings.Error_AudioStopped, e.Message));
        });
        try
        {
            s.Start();
        }
        catch (Exception e)
        {
            s.Dispose();
            throw new InvalidOperationException(
                Strings.Format(mic ? Strings.Error_Microphone : Strings.Error_SystemAudio, e.Message), e);
        }
        source = s;
    }

    /// <summary>监听默认设备的变化。它的生命周期跟随这次“开始—停止”，重启采集失败时也保留，下次设备变化还能再试。</summary>
    private void StartDeviceWatcher(int gen)
    {
        if (Environment.GetEnvironmentVariable("LIVESUB_TEST_AUDIO_FILE") is not null) return;
        bool mic = Settings.AudioSource == AudioSourceMode.Microphone;
        try
        {
            deviceWatcher = new DefaultDeviceWatcher(mic ? NAudio.CoreAudioApi.DataFlow.Capture : NAudio.CoreAudioApi.DataFlow.Render);
            deviceWatcher.Changed += () => dispatcher.BeginInvoke(() => OnDefaultDeviceChanged(gen));
        }
        catch (Exception e)
        {
            AppLog.Error("audio: device watcher unavailable", e);
        }
    }

    /// <summary>
    /// 插拔耳机、切换默认设备：重新开始采集（识别、字幕、记录都保留）。
    /// 新设备一时打不开时重试几次；都失败就正常停止，并在状态里说明原因，不留下“正在听但没有声音”的状态。
    /// </summary>
    private async void OnDefaultDeviceChanged(int gen)
    {
        if (gen != generation || !IsRunning || IsBusy) return;
        AppLog.Info("audio: default device changed, restarting capture");
        IsBusy = true;
        Exception? failure = null;
        try
        {
            await Task.Delay(800); // 设备切换时系统会连发几次通知，等它稳定下来。
            if (gen != generation || !IsRunning) return;
            await StopAudio();
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    StartAudio(gen);
                    failure = null;
                    break;
                }
                catch (Exception e)
                {
                    failure = e;
                    AppLog.Error($"audio: restart attempt {attempt} failed", e);
                    await Task.Delay(1500);
                    if (gen != generation || !IsRunning) return;
                }
            }
            if (failure is null) ShowAlert(Strings.Info_DeviceChanged);
        }
        finally
        {
            IsBusy = false;
        }
        if (failure is not null)
        {
            var message = failure.Message;
            await Stop();
            SetStatus(message);
            ShowAlert(message);
        }
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
        IsBusy = true;
        AppLog.Info("stop");
        IsRunning = false;
        ticker.Stop();
        try
        {
            await StopAudio();
            if (recognizer is { } rec)
            {
                // 等最后几段识别完，再把分句器里剩下的内容送去翻译。
                await rec.CompleteAsync(recognitionTimeout ?? TimeSpan.FromSeconds(15));
                await Task.Yield();
            }
            volatileText = "";
            Enqueue(segmenter.FlushAll());
            await CleanupRecognition();
            SetStatus(Strings.Status_Stopped);
        }
        finally
        {
            IsBusy = false;
            RaiseChanged();
        }
    }

    private async Task<bool> WaitWhileBusy(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (IsBusy && DateTime.UtcNow < deadline) await Task.Delay(50);
        return !IsBusy;
    }

    /// <summary>停止采集，并把正在说的一段送去识别（设备监听不在这里关，见 <see cref="StartDeviceWatcher"/>）。</summary>
    private async Task StopAudio()
    {
        if (source is { } s)
        {
            source = null;
            await Task.Run(s.Dispose);
        }
        SpeechChunk? last;
        lock (chunkerGate)
        {
            last = chunker.Flush();
            // 重新开始采集时接着原来的音频时间轴（按真实经过的时间），导出字幕的时间才对得上。
            long resumeAt = Math.Max(chunker.ProcessedSamples,
                (long)((DateTime.UtcNow - captureStart).TotalSeconds * AudioConverter.TargetRate));
            chunker = new SpeechChunker(startSample: resumeAt);
        }
        if (last is not null)
        {
            lastClosedChunkEnd = last.EndSample;
            recognizer?.Enqueue(last);
        }
    }

    private async Task CleanupRecognition()
    {
        deviceWatcher?.Dispose();
        deviceWatcher = null;
        if (source is { } s)
        {
            source = null;
            s.Dispose();
        }
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

    private string RecognitionKey => string.Join('\u0001', Settings.SourceLanguage, Settings.AudioSource, Settings.WhisperModel,
        Settings.FilterChinese, Settings.UseGpu);

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
        // 例如重新开始时识别状态恢复为“清晰”，旧会话却是按“难识别”的提示词建的：换掉。
        if (session is not null && sessionDifficult != RecognitionDifficult)
        {
            RetireLater(session);
            session = null;
        }
        if (session is not null || brokenMessage is not null) return;
        try
        {
            session = TranslationEngines.Create(Settings, secrets,
                Prompts.TranslatorSystemPrompt(Settings.Source, Settings.Target, RecognitionDifficult));
            sessionDifficult = RecognitionDifficult;
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

    private void OnSamples(int gen, float[] samples)
    {
        // 采集线程上调用。
        List<SpeechChunk> chunks;
        lock (chunkerGate)
        {
            if (gen != generation) return;
            chunks = chunker.Feed(samples);
        }
        foreach (var chunk in chunks)
        {
            Volatile.Write(ref lastClosedChunkEnd, chunk.EndSample);
            recognizer?.Enqueue(chunk);
        }
    }

    private void HandleFinal(int gen, RecognitionResult result)
    {
        if (gen != generation) return;
        if (Book.Lines.Count == 0) AppLog.Info("first final result");
        volatileText = "";
        timeline.AddFinal(result.Text, result.Start, result.End);
        UpdateQuality(result.Confidences);
        Enqueue(segmenter.AddFinal(result.Text));
        RaiseChanged();
    }

    private void HandlePreview(int gen, long chunkStart, string text)
    {
        // 这一段已经结束（确认结果会接着到来）：丢掉过时的预览。
        if (gen != generation || chunkStart < Volatile.Read(ref lastClosedChunkEnd)) return;
        volatileText = text;
        RaiseChanged();
    }

    private void HandleFiltered(int gen)
    {
        if (gen != generation) return;
        filteredCount++;
        volatileText = "";
        UpdateListeningStatus();
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        if (source is { } s)
        {
            float level = Math.Min(s.Level * 8, 1);
            InputLevel = MathF.Round(level * 10) / 10;
            SilentFor = (now - s.LastDataAt).TotalSeconds;
        }
        if (Alert is not null && now - alertAt > TimeSpan.FromSeconds(10)) Alert = null;

        bool speaking;
        lock (chunkerGate) speaking = chunker.InSpeech;
        var segments = segmenter.Tick(now, speechInProgress: speaking || (recognizer?.Busy ?? false) || volatileText.Length > 0);
        if (segments.Count > 0) Enqueue(segments);
        if (!speaking && volatileText.Length > 0 && !(recognizer?.Busy ?? false) && segmenter.Pending.Length == 0)
        {
            // 预览之后没有确认结果（例如被判为幻觉而丢掉）：别让它一直挂在屏幕上。
            if (now - lastPreviewAt > TimeSpan.FromSeconds(4)) volatileText = "";
        }
        MaybeRequestPreview(now, speaking);
        RaiseChanged();
    }

    /// <summary>实时预览：识别器空闲且速度够快时，每 1.2 秒识别一次正在说的这一段。</summary>
    private void MaybeRequestPreview(DateTime now, bool speaking)
    {
        if (!Settings.LivePreview || !speaking || recognizer is not { } rec || rec.Busy) return;
        if (rec.RealTimeFactor > 0.5 || now - lastPreviewAt < TimeSpan.FromSeconds(1.2)) return;
        SpeechChunk? snapshot;
        lock (chunkerGate) snapshot = chunker.Snapshot();
        if (snapshot is null) return;
        lastPreviewAt = now;
        rec.RequestPreview(snapshot);
    }

    // MARK: - 识别质量（自动判断是否需要按发音纠正）

    private void UpdateQuality(IReadOnlyList<double> confidences)
    {
        if (confidences.Count == 0 || !quality.Add(confidences)) return;
        RecognitionDifficult = quality.IsDifficult;
        AppLog.Info($"recognition {(RecognitionDifficult ? "difficult" : "clear")} (avg confidence {quality.AverageConfidence:F2})");
        // 换一个带/不带“按发音纠正”的翻译会话（PrepareSession 发现提示词不一致会自动换）。
        if (brokenMessage is null)
        {
            PrepareSession();
            UpdateListeningStatus();
        }
    }

    private void UpdateListeningStatus()
    {
        var src = Strings.IsChinese ? Settings.Source.DisplayName : Settings.Source.EnglishName;
        var dst = Strings.IsChinese ? Settings.Target.DisplayName : Settings.Target.EnglishName;
        var audio = Settings.AudioSource == AudioSourceMode.Microphone ? Strings.Source_Microphone : Strings.Source_System;
        listeningStatus = Strings.Format(Strings.Status_Listening, audio, src, dst, EngineName);
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

    private void Enqueue(List<string> segments)
    {
        if (segments.Count == 0) return;
        Book.Enqueue(segments, null, timeline, captureStart, DateTime.UtcNow);
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
        if (dialog.ShowDialog() != true) return;
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

    public void Dispose()
    {
        ticker.Stop();
        deviceWatcher?.Dispose();
        source?.Dispose();
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
