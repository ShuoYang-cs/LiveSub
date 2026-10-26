using System.Diagnostics;
using System.Text;
using LiveSub.App.Services;
using LiveSub.Core;
using LiveSub.Core.Chat;

namespace LiveSub.App.Translation;

/// <summary>怎样启动 claude：可执行文件，以及要放在参数最前面的脚本路径（npm 安装的 claude.cmd 改用 node 直接运行）。</summary>
public sealed record ClaudeLaunch(string FileName, IReadOnlyList<string> PrefixArguments)
{
    public string DisplayPath => PrefixArguments.Count > 0 ? PrefixArguments[0] : FileName;
}

/// <summary>定位本机的 claude 命令。</summary>
public static class ClaudeLocator
{
    public static ClaudeLaunch? Locate(string customPath)
    {
        var custom = Environment.ExpandEnvironmentVariables(customPath.Trim().Trim('"'));
        if (custom.Length > 0 && File.Exists(custom)) return FromFile(custom);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var name in new[] { "claude.exe", "claude.cmd" })
        {
            if (SearchPath(name) is { } hit && FromFile(hit) is { } launch) return launch;
        }
        foreach (var candidate in new[]
        {
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(appData, "npm", "claude.cmd"),
        })
        {
            if (File.Exists(candidate) && FromFile(candidate) is { } launch) return launch;
        }
        return null;
    }

    /// <summary>
    /// .cmd/.bat 不能直接带多行参数（系统提示词有换行）经过 cmd.exe，
    /// 所以 npm 安装的 claude.cmd 改为用 node 运行同目录下的 cli.js。
    /// </summary>
    private static ClaudeLaunch? FromFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".cmd" or ".bat")) return new ClaudeLaunch(path, []);
        var dir = Path.GetDirectoryName(path)!;
        var script = Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
        if (!File.Exists(script)) return null;
        var node = File.Exists(Path.Combine(dir, "node.exe")) ? Path.Combine(dir, "node.exe") : SearchPath("node.exe");
        return node is null ? null : new ClaudeLaunch(node, [script]);
    }

    private static string? SearchPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    /// <summary>在空目录里运行，避免读到任何项目的 CLAUDE.md。</summary>
    public static string WorkingDirectory()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveSub", "claude-workdir");
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>
/// 通过本机 Claude Code 命令行（用户自己的 Claude 登录额度）做翻译。
///
/// 保持一个长驻的 <c>claude -p --input-format stream-json</c> 进程：
/// - 省去每句话重新启动进程的时间；
/// - 同一会话里前面的句子自然成为上下文，术语前后一致；
/// - 每 40 句换一个新进程，防止上下文无限变长拖慢速度，新会话第一句带上最近几句作为上下文。
/// AI 助手和笔记也用它，轮数和超时按用途另设（AI 助手 10000 轮、180 秒；笔记 1 轮、600 秒）。
/// </summary>
public sealed class ClaudeCodeSession : IChatSession
{
    private readonly ClaudeLaunch launch;
    private readonly string model;
    private readonly string systemPrompt;
    private readonly int maxTurns;
    private readonly TimeSpan timeout;
    private readonly object gate = new();

    // 以下状态只在 gate 锁内访问。
    private Process? process;
    private Stream? stdin;
    private string stderrTail = "";
    private int turnsInSession;
    private int requestSerial;
    private Pending? pending;
    private bool disposed;

    private sealed class Pending
    {
        public required int Id;
        public required TaskCompletionSource<string> Completion;
        public required Action<string>? OnPartial;
        public readonly ClaudeTurn Turn = new();
        public Timer? Timeout;
        public CancellationTokenRegistration Cancellation;
    }

    public string Name { get; }

    /// <param name="maxTurns">一个进程最多回答几轮，到了就换新进程（新进程第一轮发 newSessionMessage）。</param>
    /// <param name="timeout">每轮最多等多久（默认与翻译相同）。</param>
    public ClaudeCodeSession(ClaudeLaunch launch, string model, string systemPrompt,
                             int maxTurns = ClaudeCli.MaxTurnsPerSession, TimeSpan? timeout = null)
    {
        this.launch = launch;
        this.model = model;
        this.systemPrompt = systemPrompt;
        this.maxTurns = maxTurns;
        this.timeout = timeout ?? ClaudeCli.Timeout;
        Name = "Claude · " + ModelCatalog.ClaudeModelName(model);
    }

    /// <summary>提前启动进程，第一句翻译就不用等进程启动。</summary>
    public void WarmUp()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (gate)
            {
                try
                {
                    EnsureProcess();
                }
                catch (Exception e)
                {
                    AppLog.Error("claude: warm-up failed", e);
                }
            }
        });
    }

    /// <param name="attachments">AI 助手的图片和文件：和 Anthropic API 一样的 content 块（<see cref="ClaudeStream.UserMessageLine"/>）。</param>
    public Task<string> RequestAsync(string newSessionMessage, string ongoingMessage, Action<string>? onPartial,
                                     CancellationToken cancellationToken, IReadOnlyList<ChatAttachment>? attachments = null)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // 理论上调用方是串行的；保险起见让旧请求失败而不是永远挂起。
            // 旧请求的回复还会从这个进程里出来，会被当成新请求的回复（错位一句），所以连进程一起换掉。
            if (pending is not null)
            {
                Finish(new ChatProviderException(ChatErrorKind.Timeout));
                TerminateProcess();
            }
            try
            {
                EnsureProcess();
                var message = turnsInSession == 0 ? newSessionMessage : ongoingMessage;
                int id = ++requestSerial;
                var p = new Pending { Id = id, Completion = completion, OnPartial = onPartial };
                pending = p;
                turnsInSession += 1;
                if (AppLog.DebugLatency)
                {
                    var names = string.Join(", ", (attachments ?? []).Select(a => a.Name));
                    AppLog.Info($"[claude #{id} →] {message}{(names.Length == 0 ? "" : $" [附件 {names}]")}");
                }
                var bytes = Encoding.UTF8.GetBytes(ClaudeStream.UserMessageLine(message, attachments));
                stdin!.Write(bytes);
                stdin.Flush();
                p.Timeout = new Timer(_ => OnTimeout(id), null, timeout, System.Threading.Timeout.InfiniteTimeSpan);
                p.Cancellation = cancellationToken.Register(() =>
                {
                    lock (gate)
                    {
                        if (pending?.Id != id) return;
                        Finish(new OperationCanceledException(cancellationToken));
                        // 与超时相同：取消的这一句的回复还会出来，换掉进程避免错位。
                        TerminateProcess();
                    }
                });
            }
            catch (Exception e) when (e is not ChatProviderException)
            {
                if (pending?.Completion == completion) pending = null;
                TerminateProcess();
                completion.TrySetException(e is System.ComponentModel.Win32Exception or FileNotFoundException
                    ? new ChatProviderException(ChatErrorKind.ClaudeNotFound, e.Message, inner: e)
                    : new ChatProviderException(ChatErrorKind.ProcessExited, e.Message, inner: e));
            }
        }
        return completion.Task;
    }

    private void OnTimeout(int id)
    {
        lock (gate)
        {
            if (pending?.Id != id) return;
            Finish(new ChatProviderException(ChatErrorKind.Timeout));
            TerminateProcess();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            Finish(new OperationCanceledException());
            TerminateProcess();
        }
    }

    // MARK: - 进程管理（gate 锁内）

    private void EnsureProcess()
    {
        if (process is { HasExited: false }) return;
        var psi = new ProcessStartInfo
        {
            FileName = launch.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = ClaudeLocator.WorkingDirectory(),
        };
        foreach (var arg in launch.PrefixArguments) psi.ArgumentList.Add(arg);
        foreach (var arg in ClaudeCli.TranslatorArguments(model, systemPrompt)) psi.ArgumentList.Add(arg);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ClaudeCli.PrepareEnvironment(psi.Environment, [Path.Combine(home, ".local", "bin")], Path.PathSeparator);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Start();
        ChildProcessJob.Add(p);
        process = p;
        stdin = p.StandardInput.BaseStream;
        stderrTail = "";
        turnsInSession = 0;
        p.Exited += (_, _) => OnExited(p);
        _ = Task.Run(() => ReadStdout(p));
        _ = Task.Run(() => ReadStderr(p));
        AppLog.Info($"claude: started pid {p.Id} model {model}");
    }

    private void TerminateProcess()
    {
        var p = process;
        if (p is null) return;
        process = null;
        try
        {
            stdin?.Close();
        }
        catch (IOException)
        {
        }
        stdin = null;
        turnsInSession = 0;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (Exception e)
        {
            // Kill(entireProcessTree) 可能抛 AggregateException 等；这里可能在计时器线程上，不能让异常让程序崩溃。
            AppLog.Error("claude: kill failed", e);
        }
    }

    private void OnExited(Process p)
    {
        lock (gate)
        {
            if (!ReferenceEquals(process, p)) return;
            process = null;
            stdin = null;
            turnsInSession = 0;
            var tail = stderrTail.Trim();
            AppLog.Error($"claude: process exited ({SafeExitCode(p)})");
            Finish(ClaudeCli.IsNotLoggedIn(tail)
                ? new ChatProviderException(ChatErrorKind.NotLoggedIn, tail)
                : new ChatProviderException(ChatErrorKind.ProcessExited, tail));
        }
    }

    private static string SafeExitCode(Process p)
    {
        try
        {
            return p.ExitCode.ToString();
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }

    private async Task ReadStdout(Process p)
    {
        var buffer = new byte[16 * 1024];
        var lines = new LineBuffer();
        try
        {
            var stream = p.StandardOutput.BaseStream;
            while (true)
            {
                int n = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (n == 0) break;
                foreach (var line in lines.Append(buffer.AsSpan(0, n)))
                {
                    if (ClaudeStream.Parse(line) is { } ev) Handle(ev, p);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private async Task ReadStderr(Process p)
    {
        try
        {
            // 按字节读：claude 写 UTF-8，但路径指错时（例如指到别的程序）错误输出是系统的旧编码（中文 Windows 上是 GBK），
            // 按 UTF-8 读出来是乱码。留最后一段字节，每次整段重新解码：不是合法的 UTF-8 时按旧编码。
            var buffer = new byte[4096];
            var stream = p.StandardError.BaseStream;
            var bytes = new List<byte>();
            while (true)
            {
                int n = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (n == 0) break;
                bytes.AddRange(buffer.AsSpan(0, n));
                if (bytes.Count > 2400)
                {
                    int cut = bytes.Count - 2400;
                    // 不从 UTF-8 字符的中间截断。
                    while (cut < bytes.Count && (bytes[cut] & 0xC0) == 0x80) cut++;
                    bytes.RemoveRange(0, cut);
                }
                var text = AttachmentFiles.DecodeText([.. bytes], AttachmentLoader.LegacyEncoding.Value) ?? "";
                lock (gate) stderrTail = text.Length > 600 ? text[^600..] : text;
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private void Handle(ClaudeStreamEvent ev, Process from)
    {
        Action<string>? partialCallback = null;
        string partial = "";
        lock (gate)
        {
            if (!ReferenceEquals(process, from) || pending is not { } p) return;
            switch (p.Turn.Apply(ev))
            {
                case ClaudeTurn.Outcome.PartialChanged:
                    partialCallback = p.OnPartial;
                    partial = p.Turn.Partial;
                    break;
                case ClaudeTurn.Outcome.Completed:
                    if (AppLog.DebugLatency) AppLog.Info($"[claude #{p.Id} ←] {p.Turn.FinalText}");
                    Finish(null, p.Turn.FinalText);
                    if (turnsInSession >= maxTurns) TerminateProcess();
                    break;
                case ClaudeTurn.Outcome.Failed:
                    var text = p.Turn.FinalText;
                    Finish(ClaudeCli.IsNotLoggedIn(text)
                        ? new ChatProviderException(ChatErrorKind.NotLoggedIn, text)
                        : new ChatProviderException(ChatErrorKind.Other, text));
                    if (turnsInSession >= maxTurns) TerminateProcess();
                    break;
            }
        }
        // 回调放在锁外，避免界面线程与读线程互相等待。
        partialCallback?.Invoke(partial);
    }

    /// <summary>结束当前请求（gate 锁内调用）。</summary>
    private void Finish(Exception? error, string result = "")
    {
        if (pending is not { } p) return;
        pending = null;
        p.Timeout?.Dispose();
        // 在锁内只能 Unregister（不等待）：Dispose 会等正在执行的取消回调，而回调要拿同一把锁，会死锁。
        p.Cancellation.Unregister();
        if (error is null) p.Completion.TrySetResult(result);
        else p.Completion.TrySetException(error);
    }
}
