using System.IO.Compression;
using System.Security.Cryptography;
using LiveSub.App.Localization;
using LiveSub.App.Translation;
using LiveSub.Core.Translation;

namespace LiveSub.App.Services;

/// <summary>
/// 下载、删除本机翻译模型（“模型服务”里的“混元翻译”一栏；与 macOS 的 LocalModelStore 相同）。
/// 先下 llama.cpp 运行程序（几十 MB），再下模型（1.1 GB；填了“模型下载地址”时先用它，Hugging Face 打不开时换镜像），
/// 都按 SHA-256 校验，不对就删掉、报错。只在界面线程上访问。
/// </summary>
public sealed class LocalModelStore
{
    public static LocalModelStore Shared { get; } = new();

    public enum Phase { Idle, Downloading, Verifying, Failed }

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    /// <summary>这么久收不到数据就当这个地址失败（与 macOS URLSession 默认的 60 秒相同）。</summary>
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(60);

    public Phase State { get; private set; } = Phase.Idle;
    /// <summary>下载失败的原因（<see cref="Phase.Failed"/> 时）。</summary>
    public string? Error { get; private set; }
    public bool Installed { get; private set; } = LocalModelFiles.IsInstalled;
    /// <summary>已经下载的字节数（运行程序 + 模型）。</summary>
    public long Received { get; private set; }
    public static long Total => LocalModelFiles.Runtime.Bytes + LocalTranslationModel.Bytes;

    /// <summary>状态或进度变了（刷新“模型服务”窗口）。</summary>
    public event Action? Changed;
    /// <summary>下载好了或删掉了（正在运行的字幕换翻译器）。</summary>
    public event Action? InstalledChanged;

    private CancellationTokenSource? cancel;

    /// <param name="mirror">设置里的“模型下载地址”，为空时只用 Hugging Face 和 hf-mirror.com。</param>
    public async void Download(string? mirror)
    {
        if (cancel is not null) return;
        var cts = new CancellationTokenSource();
        cancel = cts;
        Error = null;
        Received = 0;
        SetState(Phase.Downloading);
        try
        {
            await InstallAsync(mirror, cts.Token);
            SetInstalled(LocalModelFiles.IsInstalled);
            State = Phase.Idle;
            // 上次启动失败（例如文件不全）记下的错误作废：下一句马上用新下载的文件启动。
            LocalModelServer.Shared.ClearFailure();
            AppLog.Info("local model installed");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            State = Phase.Idle;
            AppLog.Info("local model download cancelled");
        }
        catch (Exception e)
        {
            // 网络、磁盘错误换成中英文的说明（.NET 的原文只有英文，写进了日志）。
            Error = e switch
            {
                StoreException => e.Message,
                HttpRequestException or IOException or UnauthorizedAccessException => ErrorText.Download(e),
                _ => e.GetType().Name + ": " + e.Message,
            };
            State = Phase.Failed;
            AppLog.Error("local model download failed", e);
        }
        finally
        {
            cancel = null;
            cts.Dispose();
            Changed?.Invoke();
        }
    }

    public void Cancel() => cancel?.Cancel();

    /// <summary>
    /// 按磁盘上的文件重新判断“已下载”（与 macOS 的 refresh 相同）：文件可能被手动删掉、被安全软件隔离。
    /// 下载、校验进行中时不动。
    /// </summary>
    public void Refresh()
    {
        if (cancel is not null || State is Phase.Downloading or Phase.Verifying) return;
        bool installed = LocalModelFiles.IsInstalled;
        if (installed == Installed) return;
        AppLog.Info($"local model: installed changed on disk ({installed})");
        SetInstalled(installed);
        Changed?.Invoke();
    }

    /// <summary>删除整个文件夹（模型和运行程序）。先关掉正在运行的 llama-server，否则文件删不掉。</summary>
    public void Delete()
    {
        Cancel();
        LocalModelServer.Shared.Stop();
        try
        {
            if (Directory.Exists(LocalModelFiles.Folder)) Directory.Delete(LocalModelFiles.Folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("local model: delete failed", e);
        }
        Received = 0;
        Error = null;
        SetInstalled(LocalModelFiles.IsInstalled);
        SetState(Phase.Idle);
    }

    private async Task InstallAsync(string? mirror, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(LocalModelFiles.Folder);
        var runtime = LocalModelFiles.Runtime;
        if (!LocalModelFiles.HasRuntime)
        {
            var archive = await FetchAsync(runtime.Url, runtime.Sha256, offset: 0, cancellationToken);
            try
            {
                await Task.Run(() => Extract(archive), cancellationToken);
            }
            finally
            {
                TryDelete(archive);
            }
            if (!LocalModelFiles.HasRuntime) throw new StoreException(Strings.Local_ServerMissing);
        }
        Received = runtime.Bytes;
        Changed?.Invoke();
        if (LocalModelFiles.HasModel) return;
        Exception last = new StoreException(Strings.Local_DownloadFailed);
        foreach (var url in LocalTranslationModel.DownloadUrls(mirror))
        {
            try
            {
                var file = await FetchAsync(url, LocalTranslationModel.Sha256, runtime.Bytes, cancellationToken);
                File.Move(file, LocalModelFiles.ModelPath, overwrite: true);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is StoreException or HttpRequestException or IOException or TaskCanceledException)
            {
                // 这个地址不行（打不开、断了、校验不对）：换下一个。
                last = e;
                AppLog.Error($"local model download from {url.Host} failed", e);
                Received = runtime.Bytes;
                SetState(Phase.Downloading);
            }
        }
        throw last;
    }

    /// <summary>下载到模型文件夹里的临时文件，校验通过才返回它的路径。进度按 <paramref name="offset"/> 加上本文件已下载的字节数算。</summary>
    private async Task<string> FetchAsync(Uri url, string sha256, long offset, CancellationToken cancellationToken)
    {
        var target = Path.Combine(LocalModelFiles.Folder, ".download-" + Path.GetFileName(url.AbsolutePath));
        var progress = new Progress<long>(written =>
        {
            if (State != Phase.Downloading) return;
            Received = offset + written;
            Changed?.Invoke();
        });
        AppLog.Info($"local model: downloading {url}");
        try
        {
            // 在后台线程上下载和校验：1.1 GB 的文件，每一块都回到界面线程会拖慢界面。
            await Task.Run(() => DownloadToFile(url, target, progress, cancellationToken), cancellationToken);
            SetState(Phase.Verifying);
            var digest = await Task.Run(() => Sha256Of(target, cancellationToken), cancellationToken);
            if (!digest.Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new StoreException(Strings.Local_VerifyFailed);
            SetState(Phase.Downloading);
            return target;
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    /// <summary>
    /// 下载整个文件。HttpClient 的超时是整个请求的（1.1 GB 不能设上限），连接没断、却一直收不到数据时会永远等下去：
    /// 每收到一块重新计时，<see cref="StallLimit"/> 内没有数据就报错，换下一个地址。用户取消仍按取消处理。
    /// </summary>
    private static async Task DownloadToFile(Uri url, string target, IProgress<long> progress, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallLimit);
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            if (!response.IsSuccessStatusCode) throw new StoreException(Strings.Format(Strings.Local_HttpFailed, (int)response.StatusCode));
            await using var input = await response.Content.ReadAsStreamAsync(stall.Token);
            await using var output = File.Create(target);
            var buffer = new byte[256 * 1024];
            long written = 0, reported = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, stall.Token)) > 0)
            {
                stall.CancelAfter(StallLimit);
                await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                written += n;
                // 每 2 MB 报一次进度，界面不用刷新几千次。
                if (written - reported >= 2 << 20)
                {
                    reported = written;
                    progress.Report(written);
                }
            }
            progress.Report(written);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Error($"local model: no data from {url.Host} for {StallLimit.TotalSeconds:0}s");
            throw new StoreException(Strings.Local_DownloadStalled, e);
        }
    }

    private static async Task<string> Sha256Of(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static void Extract(string archive)
    {
        try
        {
            ZipFile.ExtractToDirectory(archive, LocalModelFiles.RuntimeFolder, overwriteFiles: true);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new StoreException(Strings.Local_ExtractFailed, e);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void SetState(Phase phase)
    {
        State = phase;
        Changed?.Invoke();
    }

    private void SetInstalled(bool installed)
    {
        if (Installed == installed) return;
        Installed = installed;
        InstalledChanged?.Invoke();
    }

    /// <summary>下载失败的原因（Message 是给用户看的说明）。</summary>
    private sealed class StoreException(string message, Exception? inner = null) : Exception(message, inner);
}
