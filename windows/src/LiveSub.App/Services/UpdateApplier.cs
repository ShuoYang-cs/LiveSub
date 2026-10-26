using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using LiveSub.Core;
using LiveSub.App.Localization;

namespace LiveSub.App.Services;

/// <summary>更新的“动手”部分：下载解压，以及新版自己来装自己时的换文件（与 macOS 的 UpdateApplier 相同）。</summary>
public static class UpdateApplier
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveSub", "update");

    private static string PendingFile => Path.Combine(SettingsService.Folder, "update-pending.txt");

    private static string LogFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveSub", "logs", "update.log");

    /// <summary>这个安装目录能不能就地更新：必须能写，也不能是在压缩包里直接运行时解出来的临时目录。</summary>
    public static string? InstallProblem(string installDir)
    {
        var dir = installDir.TrimEnd(Path.DirectorySeparatorChar);
        var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        if (dir.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
            return Strings.Update_RunningFromTemp;
        try
        {
            var probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Strings.Format(Strings.Update_NotWritable, dir);
        }
        return null;
    }

    // MARK: 下载

    /// <summary>下载 zip 并解压，返回解压出来的文件夹（里面有新版的 LiveSub.exe）。</summary>
    public static async Task<string> DownloadAsync(HttpClient http, string url, string version, Action<double> progress)
    {
        var folder = Path.Combine(Folder, version);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        var zip = Path.Combine(folder, "LiveSub.zip");

        // 自检：LIVESUB_TEST_UPDATE_ZIP=本地 zip 代替下载（测换文件那一步）。
        if (Environment.GetEnvironmentVariable("LIVESUB_TEST_UPDATE_ZIP") is { Length: > 0 } local)
        {
            File.Copy(local, zip, overwrite: true);
        }
        else
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(zip);
            var buffer = new byte[81920];
            long received = 0;
            int n;
            while ((n = await input.ReadAsync(buffer)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n));
                received += n;
                if (total > 0) progress(Math.Min(1.0, (double)received / total));
            }
        }
        var extracted = Path.Combine(folder, "extracted");
        ZipFile.ExtractToDirectory(zip, extracted);
        File.Delete(zip);
        if (!File.Exists(Path.Combine(extracted, "LiveSub.exe"))) throw new InvalidOperationException(Strings.Update_NoExe);
        return extracted;
    }

    // MARK: 更新前后的纸条

    public static void WritePending(string version)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.Folder);
            File.WriteAllText(PendingFile, version);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 记不下也不该挡住更新。
        }
    }

    public static void ClearPending()
    {
        try { File.Delete(PendingFile); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>启动时读出上一次打算更新到哪一版，并撕掉纸条。</summary>
    public static string? TakePending()
    {
        try
        {
            if (!File.Exists(PendingFile)) return null;
            var version = File.ReadAllText(PendingFile).Trim();
            File.Delete(PendingFile);
            return version.Length > 0 ? version : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>更新留下的 .old 文件和下载缓存。</summary>
    public static void CleanUpLeftovers()
    {
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var old in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.old", SearchOption.AllDirectories)) File.Delete(old);
                if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 尽力而为。
            }
        });
    }

    // MARK: 换文件（新版的程序以 --apply-update 启动时跑）

    /// <summary>
    /// <paramref name="target"/> 是安装目录，<paramref name="pid"/> 是旧进程；自己所在的文件夹（缓存目录里）就是新版。
    /// 逐个文件拷进去：原来的文件改名成 .old；LiveSub.exe 放最后拷（版本号是从它读的，它一换更新才算生效，
    /// 前面任何一样没拷成，回滚之后还是完整的旧版）。返回退出码。
    /// </summary>
    public static int Apply(string target, int pid)
    {
        var staged = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        void Say(string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        Say($"apply-update: {staged} -> {target}, waiting for pid {pid}");
        try
        {
            using var old = Process.GetProcessById(pid);
            if (!old.WaitForExit(30_000)) { Say("old process still running, giving up"); return 1; }
        }
        catch (ArgumentException)
        {
            // 已经退出了。
        }

        var files = Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(staged, f))
            .OrderBy(f => string.Equals(f, "LiveSub.exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ToList();
        var done = new List<(string Dst, bool HadOld)>();
        try
        {
            foreach (var rel in files)
            {
                var dst = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                bool hadOld = File.Exists(dst);
                if (hadOld) File.Move(dst, dst + ".old", overwrite: true);
                done.Add((dst, hadOld));
                File.Copy(Path.Combine(staged, rel), dst, overwrite: true);
            }
        }
        catch (Exception e)
        {
            Say($"copy failed: {e.Message}; rolling back {done.Count} files");
            foreach (var (dst, hadOld) in done)
            {
                try
                {
                    if (File.Exists(dst)) File.Delete(dst);
                    if (hadOld) File.Move(dst + ".old", dst);
                }
                catch (Exception re) when (re is IOException or UnauthorizedAccessException)
                {
                    Say($"rollback failed for {dst}: {re.Message}");
                }
            }
            Launch(target, Say);
            return 1;
        }
        Say("installed, launching");
        Launch(target, Say);
        return 0;
    }

    private static void Launch(string target, Action<string> say)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(target, "LiveSub.exe")) { UseShellExecute = false, WorkingDirectory = target });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            say($"cannot launch: {e.Message}");
        }
    }
}
