namespace LiveSub.Core.Chat;

public enum ChatRole { User, Assistant }

public sealed record ChatMessage(ChatRole Role, string Text);

/// <summary>出错的种类。界面按种类显示本地化的说明。</summary>
public enum ChatErrorKind
{
    /// <summary>找不到 claude 命令。</summary>
    ClaudeNotFound,
    /// <summary>Claude Code 未登录。</summary>
    NotLoggedIn,
    /// <summary>API 密钥无效或缺失（HTTP 401/403）。</summary>
    Unauthorized,
    /// <summary>请求超时。</summary>
    Timeout,
    /// <summary>命令行进程意外退出。</summary>
    ProcessExited,
    /// <summary>接口返回了错误（HTTP 状态码见 <see cref="ChatProviderException.StatusCode"/>）。</summary>
    Http,
    /// <summary>连不上服务器。</summary>
    Network,
    /// <summary>模型拒绝回答。</summary>
    Refused,
    /// <summary>设置不完整（缺密钥、没选模型），Detail 是给用户看的说明。</summary>
    Configuration,
    /// <summary>其他错误，说明见 Detail。</summary>
    Other,
}

public sealed class ChatProviderException : Exception
{
    public ChatErrorKind Kind { get; }
    /// <summary>服务器或进程给出的原始说明（不含密钥）。</summary>
    public string Detail { get; }
    public int? StatusCode { get; }

    public ChatProviderException(ChatErrorKind kind, string detail = "", int? statusCode = null, Exception? inner = null)
        : base(string.IsNullOrEmpty(detail) ? kind.ToString() : $"{kind}: {detail}", inner)
    {
        Kind = kind;
        Detail = detail;
        StatusCode = statusCode;
    }

    /// <summary>这类错误重试没有意义，应提示用户检查设置。</summary>
    public bool IsPersistent => Kind is ChatErrorKind.ClaudeNotFound or ChatErrorKind.NotLoggedIn or ChatErrorKind.Unauthorized
        or ChatErrorKind.Configuration;
}

/// <summary>
/// 一个多轮对话会话：固定的系统提示词、对话历史、流式输出。
/// 会话是新开的（第一次、换了新会话、进程重启）时发 newSessionMessage，否则发 ongoingMessage——
/// 新会话看不到之前的对话，需要把上下文补上。调用方保证同一时间只有一个请求。
/// </summary>
public interface IChatSession : IDisposable
{
    /// <summary>显示在状态条上的名称，例如 “Claude Haiku”。</summary>
    string Name { get; }

    /// <summary>提前做好准备（例如启动进程），第一句就不用等。</summary>
    void WarmUp();

    /// <param name="onPartial">收到逐步变长的完整回复文本（不是增量）。</param>
    Task<string> RequestAsync(string newSessionMessage, string ongoingMessage, Action<string>? onPartial, CancellationToken cancellationToken);
}

/// <summary>一次流式请求：把整段对话发给提供方，逐段回调增量文本，返回完整回复。</summary>
public interface IChatTransport
{
    Task<string> StreamAsync(string systemPrompt, IReadOnlyList<ChatMessage> messages, Action<string> onDelta, CancellationToken cancellationToken);
}

/// <summary>
/// 基于 HTTP 接口的会话：自己保存对话历史，每次请求带上全部历史（接口本身是无状态的）。
/// 与 macOS 的 Claude 命令行会话一样，每 <c>maxTurns</c> 轮换新会话，防止上下文无限变长拖慢速度。
/// </summary>
public sealed class HistoryChatSession : IChatSession
{
    private readonly IChatTransport transport;
    private readonly string systemPrompt;
    private readonly int maxTurns;
    private readonly TimeSpan timeout;
    private readonly List<ChatMessage> history = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource disposed = new();

    public string Name { get; }

    /// <summary>当前会话已经完成的轮数（测试用）。</summary>
    public int TurnsInSession { get; private set; }

    public HistoryChatSession(string name, IChatTransport transport, string systemPrompt,
                              int maxTurns = ClaudeCli.MaxTurnsPerSession, TimeSpan? timeout = null)
    {
        Name = name;
        this.transport = transport;
        this.systemPrompt = systemPrompt;
        this.maxTurns = maxTurns;
        this.timeout = timeout ?? ClaudeCli.Timeout;
    }

    public void WarmUp() { }

    public async Task<string> RequestAsync(string newSessionMessage, string ongoingMessage, Action<string>? onPartial,
                                           CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TurnsInSession >= maxTurns)
            {
                history.Clear();
                TurnsInSession = 0;
            }
            var message = TurnsInSession == 0 ? newSessionMessage : ongoingMessage;
            var messages = new List<ChatMessage>(history) { new(ChatRole.User, message) };

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disposed.Token);
            linked.CancelAfter(timeout);
            var partial = new System.Text.StringBuilder();
            string reply;
            try
            {
                reply = await transport.StreamAsync(systemPrompt, messages, delta =>
                {
                    partial.Append(delta);
                    onPartial?.Invoke(partial.ToString());
                }, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !disposed.IsCancellationRequested)
            {
                throw new ChatProviderException(ChatErrorKind.Timeout);
            }
            reply = reply.Trim();
            // 只有成功的一轮才记入历史；失败时历史不变，下次请求的对话仍然完整。
            history.Add(new ChatMessage(ChatRole.User, message));
            history.Add(new ChatMessage(ChatRole.Assistant, reply));
            TurnsInSession += 1;
            return reply;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        disposed.Cancel();
    }
}
