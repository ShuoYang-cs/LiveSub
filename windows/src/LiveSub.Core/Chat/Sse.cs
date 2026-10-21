using System.Text;
using System.Text.Json;

namespace LiveSub.Core.Chat;

/// <summary>一条 Server-Sent Events 事件。</summary>
public sealed record SseEvent(string? EventType, string Data);

/// <summary>
/// 逐行解析 Server-Sent Events（text/event-stream）。
/// 一条事件由若干 <c>field: value</c> 行组成，以空行结束；多行 data 用换行拼接；冒号开头的行是注释。
/// </summary>
public sealed class SseParser
{
    private string? eventType;
    private readonly StringBuilder data = new();
    private bool hasData;

    /// <summary>送入一行（不含换行符），返回因这一行而结束的事件（通常 0 或 1 条）。</summary>
    public IEnumerable<SseEvent> Feed(string line)
    {
        if (line.EndsWith('\r')) line = line[..^1];
        if (line.Length == 0)
        {
            if (Dispatch() is { } ev) yield return ev;
            yield break;
        }
        if (line[0] == ':') yield break;
        int colon = line.IndexOf(':');
        string field = colon < 0 ? line : line[..colon];
        string value = colon < 0 ? "" : line[(colon + 1)..];
        if (value.StartsWith(' ')) value = value[1..];
        switch (field)
        {
            case "event":
                eventType = value;
                break;
            case "data":
                if (hasData) data.Append('\n');
                data.Append(value);
                hasData = true;
                break;
        }
    }

    /// <summary>流结束：有些服务器最后一条事件后没有空行。</summary>
    public SseEvent? Finish() => Dispatch();

    private SseEvent? Dispatch()
    {
        SseEvent? ev = hasData ? new SseEvent(eventType, data.ToString()) : null;
        eventType = null;
        data.Clear();
        hasData = false;
        return ev;
    }
}

/// <summary>流式回复里的一段：文本增量、结束、错误。</summary>
public abstract record StreamChunk
{
    public sealed record Delta(string Text) : StreamChunk;
    public sealed record Done : StreamChunk;
    public sealed record Error(string Message) : StreamChunk;
    /// <summary>模型拒绝（Anthropic 的 stop_reason = refusal）。</summary>
    public sealed record Refusal : StreamChunk;
    public sealed record Ignore : StreamChunk;
}

/// <summary>OpenAI 兼容接口（Chat Completions）的流式事件。</summary>
public static class OpenAIStream
{
    public static StreamChunk Parse(string data)
    {
        var trimmed = data.Trim();
        if (trimmed == "[DONE]") return new StreamChunk.Done();
        if (trimmed.Length == 0) return new StreamChunk.Ignore();
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new StreamChunk.Ignore();
            if (ErrorMessage(root) is { } error) return new StreamChunk.Error(error);
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (ClaudeStream.Obj(first, "delta") is { } delta && ClaudeStream.Str(delta, "content") is { } text)
                {
                    return new StreamChunk.Delta(text);
                }
                // 个别服务器忽略 stream 参数，直接给完整消息。
                if (ClaudeStream.Obj(first, "message") is { } message && ClaudeStream.Str(message, "content") is { } full)
                {
                    return new StreamChunk.Delta(full);
                }
            }
            return new StreamChunk.Ignore();
        }
        catch (JsonException)
        {
            return new StreamChunk.Ignore();
        }
    }

    /// <summary>从错误响应里取出说明：<c>{"error":{"message":"..."}}</c>、<c>{"error":"..."}</c> 或 <c>{"message":"..."}</c>。</summary>
    public static string? ErrorMessage(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0]; // Gemini 有时返回数组
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String) return error.GetString();
            if (ClaudeStream.Str(error, "message") is { } m) return m;
            if (error.ValueKind == JsonValueKind.Object) return error.GetRawText();
        }
        return null;
    }

    /// <summary>HTTP 错误响应体 → 给用户看的说明；不是 JSON 时截取前 300 个字符。</summary>
    public static string ErrorFromBody(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (ErrorMessage(doc.RootElement) is { } m) return m;
            if (doc.RootElement.ValueKind == JsonValueKind.Object && ClaudeStream.Str(doc.RootElement, "message") is { } m2) return m2;
        }
        catch (JsonException)
        {
        }
        var text = body.Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    /// <summary><c>GET /models</c> 的结果：<c>{"data":[{"id":"..."}]}</c> → 排好序的模型名。</summary>
    public static List<string> ParseModelList(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        JsonElement list = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("data", out var data)) list = data;
            else if (root.TryGetProperty("models", out var models)) list = models;
        }
        if (list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray()
            .Select(m => m.ValueKind == JsonValueKind.String ? m.GetString() : ClaudeStream.Str(m, "id") ?? ClaudeStream.Str(m, "name"))
            .OfType<string>()
            .Where(id => id.Length > 0)
            .Distinct()
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>Anthropic Messages API 风格的流式事件（event: content_block_delta 等）。</summary>
public static class AnthropicStream
{
    public static StreamChunk Parse(string? eventType, string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            var type = ClaudeStream.Str(root, "type") ?? eventType;
            switch (type)
            {
                case "content_block_delta":
                    if (ClaudeStream.Obj(root, "delta") is { } delta && ClaudeStream.Str(delta, "type") == "text_delta"
                        && ClaudeStream.Str(delta, "text") is { } text)
                    {
                        return new StreamChunk.Delta(text);
                    }
                    return new StreamChunk.Ignore();
                case "message_delta":
                    if (ClaudeStream.Obj(root, "delta") is { } md && ClaudeStream.Str(md, "stop_reason") == "refusal")
                    {
                        return new StreamChunk.Refusal();
                    }
                    return new StreamChunk.Ignore();
                case "message_stop":
                    return new StreamChunk.Done();
                case "error":
                    return new StreamChunk.Error(OpenAIStream.ErrorMessage(root) ?? data);
                default:
                    return new StreamChunk.Ignore();
            }
        }
        catch (JsonException)
        {
            return new StreamChunk.Ignore();
        }
    }
}
