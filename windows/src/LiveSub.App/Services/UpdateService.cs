using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using LiveSub.Core;
using LiveSub.App.Localization;

namespace LiveSub.App.Services;

public enum UpdatePhase { Idle, Available, Downloading, Installing, Failed }

/// <summary>
/// 检查更新、下载并安装新版本（与 macOS 的 UpdateManager 相同）：问 GitHub 有没有比现在新的发布 → 用户点“更新” →
/// 下载 zip 解压到 %LOCALAPPDATA%\LiveSub\update → 启动新版的 LiveSub.exe <c>--apply-update 装在哪 本进程pid</c>，本进程退出 →
/// 新进程等旧进程退出，把文件拷进安装目录（exe 最后拷，失败就退回去），再启动装好的程序（<see cref="UpdateApplier"/>）。
/// 下载的文件不带“来自网络”的标记，更新后不会再弹 SmartScreen。
/// </summary>
public sealed class UpdateService
{
    public static UpdateService Shared { get; private set; } = null!;

    private readonly SettingsService settings;
    private readonly System.Windows.Threading.Dispatcher dispatcher;
    private System.Windows.Threading.DispatcherTimer? timer;
    private static readonly HttpClient Http = CreateClient();

    public UpdatePhase Phase { get; private set; } = UpdatePhase.Idle;
    public ReleaseInfo? Release { get; private set; }
    public double Progress { get; private set; }
    /// <summary>失败时给用户看的说明。</summary>
    public string Message { get; private set; } = "";
    /// <summary>手动点“检查更新”后的结果（“已是最新版本”之类）；自动检查不写。</summary>
    public string StatusText { get; private set; } = "";
    public bool Checking { get; private set; }

    public event Action? Changed;

    public const string Repo = "ShuoYang-cs/LiveSub";

    public static string CurrentVersion { get; } =
        (typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public UpdateService(SettingsService settings, System.Windows.Threading.Dispatcher dispatcher)
    {
        this.settings = settings;
        this.dispatcher = dispatcher;
        Shared = this;
    }

    private AppSettings S => settings.Current;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"LiveSub/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>带 Vulkan 运行库的那一份（安装目录里有 runtimes\vulkan）更新时也下带 Vulkan 的。</summary>
    private static bool IsVulkanBuild => Directory.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", "vulkan"));
    private static string AssetSuffix => IsVulkanBuild ? "-Windows-x64-vulkan.zip" : "-Windows-x64.zip";

    /// <summary>启动时调用：先核对上次更新成没成，再按计划检查；之后每小时看一眼是不是该检查了（电脑睡眠也不会漏）。</summary>
    public void Start()
    {
        VerifyPreviousUpdate();
        UpdateApplier.CleanUpLeftovers();
        CheckIfDue();
        timer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromHours(1), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => CheckIfDue(), dispatcher);
        timer.Start();
    }

    private void CheckIfDue()
    {
        if (!S.AutoCheckUpdates || Checking || Phase != UpdatePhase.Idle) return;
        if (!UpdatePolicy.CheckDue(DateTime.Now, S.LastUpdateSuccess, S.LastUpdateAttempt)) return;
        _ = CheckAsync(manual: false);
    }

    public void CheckNow() => _ = CheckAsync(manual: true);

    private async Task CheckAsync(bool manual)
    {
        if (Checking || Phase is UpdatePhase.Downloading or UpdatePhase.Installing) return;
        Checking = true;
        if (manual) StatusText = Strings.Update_Checking;
        Changed?.Invoke();
        S.LastUpdateAttempt = DateTime.Now;
        try
        {
            string json;
            try
            {
                json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=30");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                if (manual) StatusText = Strings.Update_Unreachable;
                return;
            }
            S.LastUpdateSuccess = DateTime.Now;
            var release = ReleaseFeed.Latest(json, "windows-", AssetSuffix);
            if (release is null)
            {
                if (manual) StatusText = Strings.Update_UpToDate;
                return;
            }
            bool newer = AppVersion.IsNewer(release.Version, CurrentVersion);
            if (manual)
            {
                // 手动检查：用户主动问了，跳过过的版本也告诉他。
                if (newer) { S.SkippedVersion = ""; Offer(release); StatusText = ""; }
                else StatusText = Strings.Update_UpToDate;
            }
            else if (UpdatePolicy.ShouldOffer(release, CurrentVersion, S.SkippedVersion))
            {
                Offer(release);
            }
            // 自检：不点界面直接装（LIVESUB_TEST_AUTOUPDATE=1）。
            if (Environment.GetEnvironmentVariable("LIVESUB_TEST_AUTOUPDATE") == "1" && Phase == UpdatePhase.Available) Install();
        }
        finally
        {
            Checking = false;
            settings.Commit(SettingsChange.None);
            Changed?.Invoke();
        }
    }

    private void Offer(ReleaseInfo release)
    {
        Release = release;
        Phase = UpdatePhase.Available;
    }

    public void Later()
    {
        if (Phase != UpdatePhase.Available) return;
        Phase = UpdatePhase.Idle;
        Changed?.Invoke();
    }

    public void Skip()
    {
        if (Phase == UpdatePhase.Available && Release is { } r)
        {
            S.SkippedVersion = r.Version;
            settings.Commit(SettingsChange.None);
        }
        Phase = UpdatePhase.Idle;
        Changed?.Invoke();
    }

    public void DismissFailure()
    {
        if (Phase != UpdatePhase.Failed) return;
        Phase = UpdatePhase.Idle;
        Changed?.Invoke();
    }

    public async void Install()
    {
        if (Phase != UpdatePhase.Available || Release is not { } release) return;
        if (release.AssetUrl is not { } asset) { OpenPage(release, Strings.Update_NoAsset); return; }
        if (UpdateApplier.InstallProblem(AppContext.BaseDirectory) is { } problem) { OpenPage(release, problem); return; }
        Phase = UpdatePhase.Downloading;
        Progress = 0;
        Changed?.Invoke();
        try
        {
            var staged = await UpdateApplier.DownloadAsync(Http, asset, release.Version, p =>
                dispatcher.BeginInvoke(() => { if (Phase == UpdatePhase.Downloading) { Progress = p; Changed?.Invoke(); } }));
            Phase = UpdatePhase.Installing;
            Changed?.Invoke();
            var target = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            UpdateApplier.WritePending(release.Version);
            Process.Start(new ProcessStartInfo(Path.Combine(staged, "LiveSub.exe"))
            {
                ArgumentList = { "--apply-update", target, Environment.ProcessId.ToString() },
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = staged,
            });
            AppLog.Info($"update: handing over to {release.Version}");
            await Task.Delay(500);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception e)
        {
            UpdateApplier.ClearPending();
            AppLog.Error("update failed", e);
            Fail(Strings.Format(Strings.Update_Failed, e.Message));
        }
    }

    /// <summary>装不上（装在不能写的位置、在压缩包里直接运行……）时退回手动下载：打开发布页。</summary>
    private void OpenPage(ReleaseInfo release, string reason)
    {
        if (release.Page is { } page)
        {
            try { Process.Start(new ProcessStartInfo(page) { UseShellExecute = true }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        Fail(Strings.Format(Strings.Update_OpenedPage, reason));
    }

    private void Fail(string message)
    {
        Message = message;
        Phase = UpdatePhase.Failed;
        Changed?.Invoke();
    }

    /// <summary>上一次点了“更新”，这次启动的版本还是旧的：照实告诉用户。</summary>
    private void VerifyPreviousUpdate()
    {
        if (UpdateApplier.TakePending() is not { } pending) return;
        if (AppVersion.IsNewer(pending, CurrentVersion))
        {
            Message = Strings.Format(Strings.Update_PendingFailed, pending, CurrentVersion);
            Phase = UpdatePhase.Failed;
        }
    }
}
