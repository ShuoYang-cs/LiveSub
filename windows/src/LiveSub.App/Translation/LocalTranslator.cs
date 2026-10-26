using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using LiveSub.App.Localization;
using LiveSub.App.Services;
using LiveSub.Core;
using LiveSub.Core.Chat;
using LiveSub.Core.Translation;

namespace LiveSub.App.Translation;

/// <summary>本机翻译模型的文件：%LOCALAPPDATA%\LiveSub\LocalModel\ 里的模型和 llama.cpp 运行程序（与 macOS 的 LocalModelFiles 相同）。</summary>
public static class LocalModelFiles
{
    /// <summary>“模型服务”里这一栏的 Id（选了还没下载时滚到这里；和 macOS 相同）。</summary>
    public const string ServiceId = "D0000000-0000-4000-8000-000000000000";

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveSub", "LocalModel");

    /// <summary>
    /// ARM64 的 Windows 上用 ARM64 版：本程序是 x64，在 ARM64 上靠模拟运行，所以看系统的架构，不看本进程的；
    /// llama-server 是单独的进程，ARM64 版直接运行，比模拟的 x64 版快。
    /// </summary>
    public static LocalRuntime Runtime { get; } = LocalTranslationModel.Runtime(arm64: RuntimeInformation.OSArchitecture == Architecture.Arm64);

    public static string ModelPath => Path.Combine(Folder, LocalTranslationModel.FileName);
    public static string RuntimeFolder => Path.Combine(Folder, Runtime.Folder);
    public static string ServerPath => Path.Combine(RuntimeFolder, LocalRuntime.ServerFileName);
    public static bool HasRuntime => File.Exists(ServerPath);

    public static bool HasModel
    {
        get
        {
            var info = new FileInfo(ModelPath);
            return info.Exists && info.Length == LocalTranslationModel.Bytes;
        }
    }

    public static bool IsInstalled => HasRuntime && HasModel;
}

/// <summary>
/// 本机的 llama-server（与 macOS 的 LocalModelServer 相同）：第一次翻译时启动（加载模型要几秒），有翻译器在用时开着，
/// 没人用或闲置 10 分钟后关掉，App 退出时也关掉。只监听 127.0.0.1 上随机的空闲端口。
/// 进程放进 <see cref="ChildProcessJob"/>：本程序崩溃或被强制结束时，Windows 自动结束 llama-server，
/// 不会在后台一直占着 1.4 GB 内存（macOS 用 sh 守着做同样的事）。
/// </summary>
public sealed class LocalModelServer
{
    public static LocalModelServer Shared { get; } = new();

    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(10);
    /// <summary>加载模型时 /health 返回 503，加载好了才是 200；最多等这么久。</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);
    /// <summary>启动失败后这么久之内直接报同样的错，不再每句都重新启动一次（与 macOS 相同）。</summary>
    private static readonly TimeSpan FailureMemory = TimeSpan.FromMinutes(1);
    private static readonly HttpClient Health = new(LocalHandler()) { Timeout = TimeSpan.FromSeconds(2) };
    /// <summary>STATUS_DLL_NOT_FOUND：缺 dll，多半是没装 Visual C++ 运行库。</summary>
    private const int DllNotFound = unchecked((int)0xC0000135);

    /// <summary>连本机的服务不走系统代理（公司网络的代理不一定放行 127.0.0.1）。</summary>
    internal static SocketsHttpHandler LocalHandler() => new() { UseProxy = false };

    /// <summary>llama-server 的输出（每次启动时清空），和程序日志放在一起。</summary>
    public static string LogFile => Path.Combine(AppLog.Folder, "llama-server.log");

    private readonly object gate = new();
    // 以下只在 gate 内访问。
    private Process? process;
    private Task<Uri>? starting;
    /// <summary>正在运行（或正在启动）的服务用没用显卡：设置里的“显卡加速”改了，要重启服务才生效。</summary>
    private bool startedGpu;
    /// <summary>上次启动失败的时间、原因和当时的显卡设置。</summary>
    private (DateTime At, Exception Error, bool Gpu)? lastFailure;
    /// <summary>每次新的启动、每次关掉都加一：关掉之后才启动完的进程不再使用（否则会同时开着两个）。</summary>
    private int generation;
    private int users;
    private int inFlight;
    private DateTime lastUse = DateTime.UtcNow;
    /// <summary>最近几行输出：启动失败时放进错误说明里，方便排查。</summary>
    private readonly Queue<string> tail = new();

    /// <summary>有翻译器在用：用完（<see cref="Release"/>）且没有别的翻译器时关掉，省下约 1.4 GB 内存。</summary>
    public void Acquire()
    {
        lock (gate) users++;
    }

    public void Release()
    {
        bool idle;
        lock (gate)
        {
            users = Math.Max(0, users - 1);
            idle = users == 0;
        }
        if (idle) Stop();
    }

    /// <summary>服务地址；还没启动时启动并等它加载好模型。</summary>
    /// <param name="useGpu">设置里的“显卡加速”：关闭时全部用处理器（<c>-ngl 0</c>）。和运行中的服务不同时重启它。</param>
    public Task<Uri> BaseUrlAsync(bool useGpu)
    {
        bool gpu = useGpu && LocalModelFiles.Runtime.UsesGpu;
        lock (gate)
        {
            Process? outdated = null;
            if (starting is { IsFaulted: false, IsCanceled: false } task && process is not { HasExited: true })
            {
                // 改了“显卡加速”（例如显卡驱动有问题，用户关掉了它）：没有请求在进行时按新设置重启；
                // 有请求在进行时先接着用，下一句再换，不打断正在翻译的句子。
                if (startedGpu == gpu || inFlight > 0) return task;
                outdated = process;
                process = null;
                starting = null;
                generation++;
            }
            // 启动失败后一分钟内不重试（每句都重新启动只会每句都等几秒再失败）；换了显卡设置时马上重试。
            if (outdated is null && lastFailure is { } failure && failure.Gpu == gpu && DateTime.UtcNow - failure.At < FailureMemory)
            {
                return Task.FromException<Uri>(failure.Error);
            }
            int gen = ++generation;
            startedGpu = gpu;
            var launch = Task.Run(async () =>
            {
                if (outdated is not null)
                {
                    // 先关掉按旧设置启动的服务，免得两个同时占着内存。
                    AppLog.Info("local model server restarting: GPU setting changed");
                    Kill(outdated);
                }
                try
                {
                    var url = await LaunchAsync(gen, gpu);
                    lock (gate) lastFailure = null;
                    return url;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // 在这里记下失败，不看 starting 还是不是这个任务：进程一启动就退出时，
                    // 退出回调（OnExited）可能已经先把 starting 清掉了（macOS 实测过同样的时序）。被关掉（取消）不算失败。
                    lock (gate) lastFailure = (DateTime.UtcNow, e, gpu);
                    throw;
                }
            });
            starting = launch;
            return launch;
        }
    }

    /// <summary>下载好了、或用户点“测试”时马上重试，不等一分钟。</summary>
    public void ClearFailure()
    {
        lock (gate) lastFailure = null;
    }

    /// <summary>一次请求的开始和结束：有请求在进行时不会因为闲置被关掉。</summary>
    public void Begin()
    {
        lock (gate)
        {
            inFlight++;
            lastUse = DateTime.UtcNow;
        }
    }

    public void End()
    {
        lock (gate)
        {
            inFlight--;
            lastUse = DateTime.UtcNow;
        }
    }

    /// <summary>关掉服务，并等它退出（删除模型文件夹前文件要先解锁）。</summary>
    public void Stop()
    {
        Process? old;
        lock (gate)
        {
            old = process;
            process = null;
            starting = null;
            generation++;
        }
        if (old is null) return;
        Kill(old);
        AppLog.Info("local model server stopped");
    }

    private static void Kill(Process p)
    {
        try
        {
            if (p.HasExited) return;
            p.Kill();
            p.WaitForExit(3000);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // 已经退出。
        }
    }

    private async Task<Uri> LaunchAsync(int gen, bool gpu)
    {
        if (!LocalModelFiles.IsInstalled) throw new LocalModelUnavailableException(Strings.Local_NotDownloaded);
        int port = FreePort();
        var psi = new ProcessStartInfo
        {
            FileName = LocalModelFiles.ServerPath,
            WorkingDirectory = LocalModelFiles.RuntimeFolder,
            UseShellExecute = false,
            // 不弹出控制台窗口。
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        string? device = gpu ? await (gpuDevice ??= Task.Run(FindGpuDevice)) : null;
        foreach (var arg in LocalTranslationModel.ServerArguments(LocalModelFiles.ModelPath, port, gpu, device)) psi.ArgumentList.Add(arg);

        var log = OpenLog();
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => WriteLog(log, e.Data);
        p.ErrorDataReceived += (_, e) => WriteLog(log, e.Data);
        p.Exited += (_, _) => OnExited(p, log);
        lock (gate) tail.Clear();
        try
        {
            p.Start();
        }
        catch (Win32Exception e)
        {
            log?.Dispose();
            throw new LocalModelUnavailableException(e.Message, e);
        }
        ChildProcessJob.Add(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        bool stale;
        lock (gate)
        {
            stale = gen != generation;
            if (!stale)
            {
                process = p;
                lastUse = DateTime.UtcNow;
            }
        }
        if (stale)
        {
            // 启动过程中被关掉了（删除模型、退出程序、没人用了）。
            Kill(p);
            throw new OperationCanceledException();
        }
        AppLog.Info($"local model server starting on port {port} (gpu: {gpu}{(device is null ? "" : $", {device}")})");

        var baseUrl = new Uri($"http://127.0.0.1:{port}/");
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < StartTimeout)
        {
            lock (gate)
            {
                // 启动过程中被关掉了。
                if (gen != generation) throw new OperationCanceledException();
            }
            if (p.HasExited) throw new LocalModelUnavailableException(StartFailure(p));
            try
            {
                using var response = await Health.GetAsync(new Uri(baseUrl, "health"));
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    AppLog.Info($"local model server ready in {watch.Elapsed.TotalSeconds:F1}s");
                    WatchIdle(p);
                    return baseUrl;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                // 还没开始监听。
            }
            await Task.Delay(250);
        }
        Stop();
        throw new LocalModelUnavailableException(Strings.Local_StartTimeout);
    }

    /// <summary>启动失败的原因：缺运行库时直接说明，否则带上输出的最后几行。</summary>
    private string StartFailure(Process p)
    {
        // 等输出读完，最后几行才完整。
        if (p.WaitForExit(2000)) p.WaitForExit();
        int code = p.ExitCode;
        AppLog.Error($"local model server failed to start (exit code {code})");
        if (code == DllNotFound) return Strings.Local_MissingRuntimeLibrary;
        string lines;
        lock (gate) lines = string.Join(" ", tail);
        return lines.Length > 0 ? lines : Strings.Format(Strings.Local_ExitCode, code);
    }

    private void OnExited(Process p, StreamWriter? log)
    {
        int code;
        try
        {
            code = p.ExitCode;
        }
        catch (InvalidOperationException)
        {
            code = 0;
        }
        AppLog.Info($"local model server exited ({code})");
        lock (gate)
        {
            if (ReferenceEquals(process, p))
            {
                process = null;
                starting = null;
            }
        }
        if (log is null) return;
        lock (log) log.Dispose();
    }

    /// <summary>闲置太久（例如停了字幕没退出程序）就关掉；下次翻译时再启动。</summary>
    private void WatchIdle(Process p)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(60));
                bool current, idle;
                lock (gate)
                {
                    current = ReferenceEquals(process, p);
                    idle = inFlight == 0 && DateTime.UtcNow - lastUse > IdleLimit;
                }
                if (!current) return;
                if (idle)
                {
                    AppLog.Info("local model server idle");
                    Stop();
                    return;
                }
            }
        });
    }

    private static StreamWriter? OpenLog()
    {
        try
        {
            Directory.CreateDirectory(AppLog.Folder);
            return new StreamWriter(new FileStream(LogFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("local model server: cannot open log", e);
            return null;
        }
    }

    /// <summary>标准输出和标准错误在不同的线程上回调，写同一个文件时加锁。</summary>
    private void WriteLog(StreamWriter? log, string? line)
    {
        if (line is null) return;
        lock (gate)
        {
            tail.Enqueue(line.Trim());
            while (tail.Count > 3) tail.Dequeue();
        }
        if (log is null) return;
        lock (log)
        {
            try
            {
                log.WriteLine(line);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>用哪块显卡：每次运行程序查一次（约 0.2 秒），见 <see cref="LocalTranslationModel.PreferredGpuDevice"/>。</summary>
    private static Task<string?>? gpuDevice;

    private static string? FindGpuDevice()
    {
        var bench = Path.Combine(LocalModelFiles.RuntimeFolder, "llama-bench.exe");
        if (!File.Exists(bench)) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = bench,
                WorkingDirectory = LocalModelFiles.RuntimeFolder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--list-devices");
            using var p = Process.Start(psi)!;
            ChildProcessJob.Add(p);
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10_000))
            {
                Kill(p);
                return null;
            }
            var device = LocalTranslationModel.PreferredGpuDevice(stdout.Result + "\n" + stderr.Result);
            AppLog.Info($"local model gpu device: {device ?? "default"}");
            return device;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            AppLog.Error("local model: cannot list GPU devices", e);
            return null;
        }
    }

    private static int FreePort()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        catch (SocketException e)
        {
            throw new LocalModelUnavailableException(Strings.Local_NoPort, e);
        }
    }
}

/// <summary>用本机的混元翻译模型翻译（与 macOS 的 LocalTranslator 相同）：每句带上前两句原文作背景，流式输出。</summary>
public sealed class LocalTranslator : ITranslator
{
    private static readonly HttpClient Http = new(LocalModelServer.LocalHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly LanguageOption source;
    private readonly LanguageOption target;
    /// <summary>设置里的“显卡加速”，每次请求时读：改了以后不用重新开始，下一句就按新设置重启服务。</summary>
    private readonly Func<bool> useGpu;
    private readonly LocalModelServer server = LocalModelServer.Shared;
    private readonly CancellationTokenSource disposed = new();
    private int released;

    public LocalTranslator(LanguageOption source, LanguageOption target, Func<bool> useGpu)
    {
        this.source = source;
        this.target = target;
        this.useGpu = useGpu;
        server.Acquire();
    }

    public string Name => Strings.Engine_LocalModel;

    /// <summary>提前启动服务、加载模型，第一句不用等。</summary>
    public void WarmUp()
    {
        var launch = server.BaseUrlAsync(useGpu());
        _ = launch.ContinueWith(t => AppLog.Error("local model: warm-up failed", t.Exception?.InnerException),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>等服务启动好（“测试”按钮计时前用：用时不算启动和加载模型的时间）。</summary>
    public Task PrepareAsync(CancellationToken cancellationToken) => server.BaseUrlAsync(useGpu()).WaitAsync(cancellationToken);

    public async Task<string> TranslateAsync(string text, IReadOnlyList<(string Source, string Translation)> context, Action<string>? onPartial,
                                             CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disposed.Token);
        var baseUrl = await server.BaseUrlAsync(useGpu()).WaitAsync(linked.Token);
        server.Begin();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                return await StreamAsync(baseUrl, text, context, onPartial, timeout.Token);
            }
            catch (OperationCanceledException) when (!linked.IsCancellationRequested)
            {
                throw new TranslatorException(Strings.Format(Strings.Error_LocalModel, Strings.Error_Timeout));
            }
        }
        finally
        {
            server.End();
        }
    }

    private async Task<string> StreamAsync(Uri baseUrl, string text, IReadOnlyList<(string Source, string Translation)> context,
                                           Action<string>? onPartial, CancellationToken cancellationToken)
    {
        var prompt = LocalTranslationModel.Prompt(text, context.Select(c => c.Source).ToList(), source, target);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "v1/chat/completions"))
        {
            Content = new StringContent(LocalTranslationModel.RequestBody(prompt), Encoding.UTF8, "application/json"),
        };
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            // 连的是本机的 llama-server：连不上多半是它退出了，不是网络问题。原文（英文）由调用方写进日志。
            throw new TranslatorException(Strings.Format(Strings.Error_LocalModel, Strings.Local_ConnectionLost), e);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var message = OpenAIStream.ErrorFromBody(body);
                throw new TranslatorException(Strings.Format(Strings.Error_LocalModel, message.Length > 0 ? message : $"HTTP {(int)response.StatusCode}"));
            }
            var result = new StringBuilder();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var parser = new SseParser();
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                foreach (var ev in parser.Feed(line))
                {
                    switch (OpenAIStream.Parse(ev.Data))
                    {
                        case StreamChunk.Delta d when d.Text.Length > 0:
                            result.Append(d.Text);
                            onPartial?.Invoke(result.ToString());
                            break;
                        case StreamChunk.Done:
                            return result.ToString().Trim();
                        case StreamChunk.Error e:
                            throw new TranslatorException(Strings.Format(Strings.Error_LocalModel, e.Message));
                    }
                }
            }
            return result.ToString().Trim();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) != 0) return;
        disposed.Cancel();
        server.Release();
    }
}
