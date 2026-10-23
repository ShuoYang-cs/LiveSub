using System.Diagnostics;
using System.Threading.Channels;
using LiveSub.App.Services;
using LiveSub.Core.Audio;
using LiveSub.Core.Speech;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace LiveSub.App.Speech;

/// <summary>一段确认的识别结果；时间是音频时间轴上的秒数（从开始采集算起）。<paramref name="Source"/> 是送来这段语音的音源序号。</summary>
public sealed record RecognitionResult(int Source, string Text, double Start, double End, IReadOnlyList<double> Confidences);

/// <summary>识别模型的下载与存放：%LOCALAPPDATA%\LiveSub\models。</summary>
public static class WhisperModels
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveSub", "models");

    public static string PathOf(WhisperModel model) => Path.Combine(Folder, model.FileName);

    public static bool IsDownloaded(WhisperModel model)
    {
        var info = new FileInfo(PathOf(model));
        return info.Exists && model.LooksComplete(info.Length);
    }

    /// <summary>确保模型已下载；返回模型文件路径。<paramref name="progress"/> 收到 0…100 的整数百分比。</summary>
    public static async Task<string> EnsureAsync(WhisperModel model, string? mirror, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var path = PathOf(model);
        if (IsDownloaded(model)) return path;
        Directory.CreateDirectory(Folder);
        var partial = Path.Combine(Folder, model.PartialFileName);
        var url = model.DownloadUrlFrom(mirror);
        AppLog.Info($"model: downloading {url}");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = File.Create(partial))
        {
            var buffer = new byte[256 * 1024];
            long done = 0;
            int lastPercent = -1;
            while (true)
            {
                int n = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (n == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                done += n;
                int percent = total is > 0 ? (int)(done * 100 / total.Value) : (int)(done * 100 / (model.ApproxMegabytes * 1_000_000L));
                percent = Math.Clamp(percent, 0, 100);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress?.Report(percent);
                }
            }
        }
        if (!model.LooksComplete(new FileInfo(partial).Length))
        {
            File.Delete(partial);
            throw new IOException("incomplete download");
        }
        File.Move(partial, path, overwrite: true);
        AppLog.Info($"model: saved {path}");
        return path;
    }
}

/// <summary>
/// Whisper.net 本地识别。Whisper 不是流式的：由 <see cref="SpeechChunker"/> 在停顿处切好一段段音频，
/// 这里在一个后台线程上逐段识别。确认结果优先；空闲时可以识别“正在说的这一段”作为实时预览。
/// “两者”模式下麦克风和系统音频共用这一个模型（不加载两份）：两个音源的语音段按送来的先后识别，
/// 每段带着音源序号，结果也带着它。
/// </summary>
public sealed class WhisperRecognizer : IAsyncDisposable
{
    private readonly WhisperFactory factory;
    private readonly WhisperProcessor processor;
    private readonly string language;
    private readonly bool filterChinese;
    private readonly Channel<(int Source, SpeechChunk Chunk)> finals =
        Channel.CreateUnbounded<(int Source, SpeechChunk Chunk)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    /// <summary>实时预览的请求和各音源排队的确认段数，都由 <see cref="gate"/> 保护。</summary>
    private readonly PreviewQueue<SpeechChunk> previews = new();
    private readonly Dictionary<int, int> queuedBySource = [];
    private readonly object gate = new();
    private readonly SemaphoreSlim wake = new(0);
    private int queued;

    /// <summary>确认结果（在后台线程上触发）。</summary>
    public event Action<RecognitionResult>? Final;
    /// <summary>实时预览：音源序号、这一段的开始样本序号与到目前为止的文字。</summary>
    public event Action<int, long, string>? Preview;
    /// <summary>因为判断为中文而丢掉了某个音源的一段。</summary>
    public event Action<int>? Filtered;
    public event Action<Exception>? Error;

    /// <summary>还有没识别完的确认段（任何音源）。</summary>
    public bool Busy => Volatile.Read(ref queued) > 0;

    /// <summary>这个音源还有没识别完的确认段。</summary>
    public bool IsBusy(int source)
    {
        lock (gate) return queuedBySource.GetValueOrDefault(source) > 0;
    }

    /// <summary>最近一段的识别耗时与音频时长之比（越小越快）；用来决定要不要做实时预览。</summary>
    public double RealTimeFactor { get; private set; }

    private static bool runtimeConfigured;

    /// <summary>加载模型（耗时约 1 秒，放在后台线程调用）。</summary>
    public WhisperRecognizer(string modelPath, string whisperLanguage, bool filterChinese, bool useGpu)
    {
        if (!runtimeConfigured)
        {
            runtimeConfigured = true;
            // 只能在第一次创建之前设置：不用显卡时只尝试处理器版本的运行库。
            if (!useGpu) RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
        }
        language = whisperLanguage;
        this.filterChinese = filterChinese && whisperLanguage != "zh";
        factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = useGpu });
        var builder = factory.CreateBuilder()
            .WithLanguage(whisperLanguage)
            .WithProbabilities()
            .WithNoContext()
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8));
        // 让普通话输出简体字。
        if (whisperLanguage == "zh") builder = builder.WithPrompt("以下是普通话的句子。");
        processor = builder.Build();
        AppLog.Info($"whisper: loaded {Path.GetFileName(modelPath)} runtime {RuntimeOptions.LoadedLibrary?.ToString() ?? "?"}");
        worker = Task.Run(RunAsync);
    }

    /// <summary>送入某个音源的一段确认的语音。</summary>
    public void Enqueue(SpeechChunk chunk, int source)
    {
        Interlocked.Increment(ref queued);
        lock (gate)
        {
            queuedBySource[source] = queuedBySource.GetValueOrDefault(source) + 1;
            previews.Remove(source);
        }
        finals.Writer.TryWrite((source, chunk));
        wake.Release();
    }

    /// <summary>请求识别某个音源正在说的这一段（每个音源只保留最新的一次请求）。</summary>
    public void RequestPreview(SpeechChunk snapshot, int source)
    {
        lock (gate) previews.Request(source, snapshot);
        wake.Release();
    }

    /// <summary>不再接收新音频，等已有的段识别完（最多等 <paramref name="timeout"/>）。</summary>
    public async Task CompleteAsync(TimeSpan timeout)
    {
        finals.Writer.TryComplete();
        wake.Release();
        await Task.WhenAny(worker, Task.Delay(timeout)).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await wake.WaitAsync(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            try
            {
                if (finals.Reader.TryRead(out var item))
                {
                    try
                    {
                        await RecognizeFinal(item.Source, item.Chunk).ConfigureAwait(false);
                    }
                    finally
                    {
                        // 识别出错也要减掉，否则 Busy 一直为 true，分句和实时预览都会受影响。
                        Interlocked.Decrement(ref queued);
                        lock (gate) queuedBySource[item.Source] = Math.Max(0, queuedBySource.GetValueOrDefault(item.Source) - 1);
                    }
                    continue;
                }
                if (finals.Reader.Completion.IsCompleted) break;
                bool hasPreview;
                int previewSource;
                SpeechChunk? preview;
                lock (gate) hasPreview = previews.TryTake(out previewSource, out preview);
                if (hasPreview && preview is not null) await RecognizePreview(previewSource, preview).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                AppLog.Error("whisper: recognition failed", e);
                Error?.Invoke(e);
            }
        }
    }

    private async Task RecognizeFinal(int source, SpeechChunk chunk)
    {
        var watch = Stopwatch.StartNew();
        if (filterChinese)
        {
            // 在“讲话语言 / 中文”两者之间判断，比在全部语言里判断更稳。
            var (detected, probability) = processor.DetectLanguageWithProbability(chunk.Samples, [language, "zh"]);
            if (RecognitionText.IsForeignChinese(detected, probability, language))
            {
                Filtered?.Invoke(source);
                return;
            }
        }
        await foreach (var segment in processor.ProcessAsync(chunk.Samples, stop.Token).ConfigureAwait(false))
        {
            if (RecognitionText.Clean(segment.Text) is not { } text) continue;
            var confidences = RecognitionText.WordConfidences(
                (segment.Tokens ?? []).Select(t => new TokenProbability(t.Text ?? "", t.Probability)));
            double start = chunk.StartSeconds + Math.Max(0, segment.Start.TotalSeconds);
            double end = chunk.StartSeconds + Math.Min(chunk.DurationSeconds, segment.End.TotalSeconds);
            Final?.Invoke(new RecognitionResult(source, text, start, Math.Max(start, end), confidences));
        }
        RealTimeFactor = watch.Elapsed.TotalSeconds / Math.Max(0.5, chunk.DurationSeconds);
    }

    private async Task RecognizePreview(int source, SpeechChunk snapshot)
    {
        var parts = new List<string>();
        await foreach (var segment in processor.ProcessAsync(snapshot.Samples, stop.Token).ConfigureAwait(false))
        {
            if (RecognitionText.Clean(segment.Text) is { } text) parts.Add(text);
        }
        Preview?.Invoke(source, snapshot.StartSample, string.Join(" ", parts));
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        wake.Release();
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            AppLog.Error("whisper: worker ended with error", e);
        }
        await processor.DisposeAsync().ConfigureAwait(false);
        factory.Dispose();
    }
}
