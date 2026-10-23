using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LiveSub.Core;

/// <summary><c>claude -p --output-format stream-json</c> 输出的一行事件。</summary>
public abstract record ClaudeStreamEvent
{
    public sealed record Initialized(string Model) : ClaudeStreamEvent;
    /// <summary>流式文本增量（需要 <c>--include-partial-messages</c>）。</summary>
    public sealed record TextDelta(string Text) : ClaudeStreamEvent;
    /// <summary>完整的助手消息文本。</summary>
    public sealed record AssistantText(string Text) : ClaudeStreamEvent;
    /// <summary>一轮结束。<c>IsError</c> 为 true 时 <c>Text</c> 是错误说明（例如未登录）。</summary>
    public sealed record Result(string Text, bool IsError) : ClaudeStreamEvent;
    public sealed record Other : ClaudeStreamEvent;
}

public static class ClaudeStream
{
    public static ClaudeStreamEvent? Parse(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            return null;
        }
        using (doc)
        {
            var obj = doc.RootElement;
            if (obj.ValueKind != JsonValueKind.Object || Str(obj, "type") is not { } type) return null;
            switch (type)
            {
                case "system":
                    return Str(obj, "subtype") == "init"
                        ? new ClaudeStreamEvent.Initialized(Str(obj, "model") ?? "")
                        : new ClaudeStreamEvent.Other();
                case "stream_event":
                    if (Obj(obj, "event") is { } ev && Str(ev, "type") == "content_block_delta"
                        && Obj(ev, "delta") is { } delta && Str(delta, "type") == "text_delta"
                        && Str(delta, "text") is { } text)
                    {
                        return new ClaudeStreamEvent.TextDelta(text);
                    }
                    return new ClaudeStreamEvent.Other();
                case "assistant":
                    if (Obj(obj, "message") is not { } message
                        || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array
                        || content.EnumerateArray().Any(b => b.ValueKind != JsonValueKind.Object))
                    {
                        return new ClaudeStreamEvent.Other();
                    }
                    var joined = string.Concat(content.EnumerateArray()
                        .Where(b => Str(b, "type") == "text")
                        .Select(b => Str(b, "text"))
                        .OfType<string>());
                    return new ClaudeStreamEvent.AssistantText(joined);
                case "result":
                    bool isErrorFlag = obj.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                    bool isError = isErrorFlag || (Str(obj, "subtype") ?? "success") != "success";
                    return new ClaudeStreamEvent.Result(Str(obj, "result") ?? "", isError);
                default:
                    return new ClaudeStreamEvent.Other();
            }
        }
    }

    /// <summary>
    /// 一条 stream-json 输入消息（一行 JSON）。附件（图片、PDF）和 Anthropic API 的 content 块格式相同
    /// （macOS 2026-10-22 用 Claude Code 实测能读）。
    /// </summary>
    public static string UserMessageLine(string text, IReadOnlyList<Chat.ChatAttachment>? attachments = null)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("type", "user");
            w.WriteStartObject("message");
            w.WriteString("role", "user");
            w.WritePropertyName("content");
            Chat.ChatContent.WriteAnthropicBlocks(w, text, attachments);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    internal static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    internal static JsonElement? Obj(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : null;
}

/// <summary>把字节流切成行。stdout 的数据块可能在任意位置断开（包括 UTF-8 多字节字符中间），所以按字节缓冲。</summary>
public sealed class LineBuffer
{
    private byte[] buffer = new byte[4096];
    private int count;

    public List<string> Append(ReadOnlySpan<byte> data)
    {
        if (count + data.Length > buffer.Length)
        {
            Array.Resize(ref buffer, Math.Max(buffer.Length * 2, count + data.Length));
        }
        data.CopyTo(buffer.AsSpan(count));
        count += data.Length;

        var lines = new List<string>();
        int start = 0;
        while (true)
        {
            int idx = Array.IndexOf(buffer, (byte)0x0A, start, count - start);
            if (idx < 0) break;
            lines.Add(Encoding.UTF8.GetString(buffer, start, idx - start));
            start = idx + 1;
        }
        if (start > 0)
        {
            Buffer.BlockCopy(buffer, start, buffer, 0, count - start);
            count -= start;
        }
        return lines;
    }
}

/// <summary>
/// 一轮 Claude 请求的输出累积：流式增量、完整消息、结束结果。
/// 结束时优先用 result 文本，其次完整消息，最后才用拼起来的增量。
/// </summary>
public sealed class ClaudeTurn
{
    public string Partial { get; private set; } = "";
    public string AssistantText { get; private set; } = "";

    public enum Outcome { None, PartialChanged, Completed, Failed }

    /// <summary>结束时的文本（成功时是译文，失败时是错误说明）。</summary>
    public string FinalText { get; private set; } = "";

    public Outcome Apply(ClaudeStreamEvent ev)
    {
        switch (ev)
        {
            case ClaudeStreamEvent.TextDelta d:
                Partial += d.Text;
                return Outcome.PartialChanged;
            case ClaudeStreamEvent.AssistantText a:
                AssistantText += a.Text;
                return Outcome.None;
            case ClaudeStreamEvent.Result { IsError: true } r:
                FinalText = r.Text;
                return Outcome.Failed;
            case ClaudeStreamEvent.Result r:
                FinalText = (new[] { r.Text, AssistantText, Partial }.FirstOrDefault(t => t.Trim().Length > 0) ?? "").Trim();
                return Outcome.Completed;
            default:
                return Outcome.None;
        }
    }
}

/// <summary>调用本机 claude 命令的参数与环境（与 macOS 版一致）。</summary>
public static class ClaudeCli
{
    public const int MaxTurnsPerSession = 40;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

    /// <summary>长驻翻译进程的命令行参数。</summary>
    public static IReadOnlyList<string> TranslatorArguments(string model, string systemPrompt) =>
    [
        "-p",
        "--input-format", "stream-json",
        "--output-format", "stream-json",
        "--verbose",
        "--include-partial-messages",
        "--model", model,
        "--tools", "",
        "--setting-sources", "",
        "--strict-mcp-config",
        "--disable-slash-commands",
        "--no-session-persistence",
        "--system-prompt", systemPrompt,
    ];

    /// <summary>如果本应用是从 Claude Code 的终端里启动的，子进程要去掉的嵌套会话标记（与 macOS 版相同）。</summary>
    public static readonly IReadOnlyList<string> NestedSessionVariables =
        ["CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT", "CLAUDE_PID"];

    /// <summary>
    /// 调整子进程环境：去掉嵌套会话标记，补上常见安装目录，关掉思考、自动更新和非必要的网络请求。
    /// 只去掉 <see cref="NestedSessionVariables"/>；其他 CLAUDE_CODE_* 是用户自己的配置
    /// （例如 CLAUDE_CODE_GIT_BASH_PATH、通过 Bedrock / Vertex 使用），要保留。
    /// Windows 的环境变量名不区分大小写（常见的是 Path），这里按不区分大小写处理。
    /// </summary>
    public static void PrepareEnvironment(IDictionary<string, string?> env, IEnumerable<string> extraPaths, char pathSeparator)
    {
        foreach (var key in env.Keys.ToList())
        {
            if (NestedSessionVariables.Any(v => v.Equals(key, StringComparison.OrdinalIgnoreCase))) env.Remove(key);
        }
        var pathKey = env.Keys.FirstOrDefault(k => k.Equals("PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
        env.TryGetValue(pathKey, out var path);
        var parts = extraPaths.Where(p => p.Length > 0).ToList();
        if (!string.IsNullOrEmpty(path)) parts.Add(path);
        env[pathKey] = string.Join(pathSeparator, parts);
        env["MAX_THINKING_TOKENS"] = "0";
        env["DISABLE_AUTOUPDATER"] = "1";
        env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
    }

    /// <summary>未登录的错误提示（重试没有意义）。</summary>
    public static bool IsNotLoggedIn(string message) =>
        message.Contains("not logged in", StringComparison.OrdinalIgnoreCase);
}
