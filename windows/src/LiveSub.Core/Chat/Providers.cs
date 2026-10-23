using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LiveSub.Core.Chat;

/// <summary>
/// Anthropic 各模型的低延迟设置：
/// Haiku 关掉思考；Sonnet 5.5 / Opus 5.5 不能关思考（会返回 400），改为不传 thinking、把 effort 设为 low。
/// </summary>
public sealed record AnthropicModelPolicy(bool DisableThinking, string? Effort)
{
    public const string Haiku = "claude-haiku-5-5";
    public const string Sonnet = "claude-sonnet-5-5";
    public const string Opus = "claude-opus-5-5";

    /// <summary>翻译默认用最快的 Haiku。</summary>
    public const string DefaultModel = Haiku;

    /// <summary>不支持 effort 参数的旧模型（传了会报错）。</summary>
    private static readonly string[] NoEffortPrefixes =
    [
        "claude-3", "claude-sonnet-4-0", "claude-sonnet-4-2", "claude-sonnet-4-5", "claude-opus-4-0", "claude-opus-4-1", "claude-opus-4-2",
    ];

    public static AnthropicModelPolicy For(string model)
    {
        // 其他兼容 Anthropic 格式的服务（模型名不是 claude-…）不一定认识这些参数，什么都不加（与 macOS 相同）。
        if (!model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) return new(false, null);
        if (model.StartsWith("claude-haiku", StringComparison.OrdinalIgnoreCase)) return new(true, null);
        if (NoEffortPrefixes.Any(p => model.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return new(false, null);
        return new(false, "low");
    }
}

/// <summary>OpenAI 兼容请求里怎么关掉思考：不发参数、<c>thinking: {type: disabled}</c>，或 <c>enable_thinking: false</c>。</summary>
public enum OpenAIThinking { Default, Disabled, EnableThinkingFalse }

/// <summary>请求体构造（JSON 文本），便于单测与调试。</summary>
public static class RequestBodies
{
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// OpenAI 兼容 Chat Completions：<c>{model, messages:[{role:"system"},…], stream:true}</c>，
    /// 认得的服务再加上关掉思考的参数（<see cref="OpenAILatencySettings"/>）。
    /// </summary>
    public static string OpenAIChat(string model, string systemPrompt, IReadOnlyList<ChatMessage> messages, bool stream = true) =>
        Write(w =>
        {
            w.WriteString("model", model);
            w.WriteStartArray("messages");
            WriteMessage(w, "system", systemPrompt);
            foreach (var m in messages)
            {
                if (!m.HasAttachments)
                {
                    WriteMessage(w, Role(m), m.Text);
                    continue;
                }
                // 带附件的提问（AI 助手）：图片用 data URL，PDF 和文档发文字（ChatContent）。
                w.WriteStartObject();
                w.WriteString("role", Role(m));
                w.WritePropertyName("content");
                ChatContent.WriteOpenAIContent(w, m.Text, m.Attachments);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteBoolean("stream", stream);
            switch (OpenAILatencySettings(model))
            {
                case OpenAIThinking.Disabled:
                    w.WriteStartObject("thinking");
                    w.WriteString("type", "disabled");
                    w.WriteEndObject();
                    break;
                case OpenAIThinking.EnableThinkingFalse:
                    w.WriteBoolean("enable_thinking", false);
                    break;
            }
        });

    /// <summary>
    /// OpenAI 兼容服务里默认“思考”的模型：关掉，否则每句字幕要多等好几秒（与 macOS 的 ChatAPI.openAILatencySettings 相同）。
    /// 参数不是 OpenAI 标准的，只给认得它的服务发（OpenAI 收到不认识的参数会返回 400）。2026-10-22 查的各家文档：
    /// DeepSeek 的 deepseek-flash 默认开着思考；Kimi 只有 kimi-k2.6 能关（kimi-k3 不能关，kimi-k2.7-code 会拒绝）；
    /// 通义千问（百炼兼容模式）用 enable_thinking。
    /// </summary>
    public static OpenAIThinking OpenAILatencySettings(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.StartsWith("deepseek", StringComparison.Ordinal) || m.StartsWith("kimi-k2.6", StringComparison.Ordinal)) return OpenAIThinking.Disabled;
        if (m.StartsWith("qwen", StringComparison.Ordinal)) return OpenAIThinking.EnableThinkingFalse;
        return OpenAIThinking.Default;
    }

    /// <summary>Anthropic Messages API 请求体（按 <see cref="AnthropicModelPolicy"/> 设置 thinking / effort）。</summary>
    public static string AnthropicMessages(string model, string systemPrompt, IReadOnlyList<ChatMessage> messages,
                                           int maxTokens = AnthropicMaxTokens, bool stream = true) =>
        Write(w =>
        {
            var policy = AnthropicModelPolicy.For(model);
            w.WriteString("model", model);
            w.WriteNumber("max_tokens", maxTokens);
            w.WriteString("system", systemPrompt);
            w.WriteStartArray("messages");
            foreach (var m in messages)
            {
                if (!m.HasAttachments)
                {
                    WriteMessage(w, Role(m), m.Text);
                    continue;
                }
                // 带附件的提问：图片、PDF 是 content 块（与 Claude Code 的 stream-json 输入相同）。
                w.WriteStartObject();
                w.WriteString("role", Role(m));
                w.WritePropertyName("content");
                ChatContent.WriteAnthropicBlocks(w, m.Text, m.Attachments);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (policy.DisableThinking)
            {
                w.WriteStartObject("thinking");
                w.WriteString("type", "disabled");
                w.WriteEndObject();
            }
            if (policy.Effort is { } effort)
            {
                w.WriteStartObject("output_config");
                w.WriteString("effort", effort);
                w.WriteEndObject();
            }
            w.WriteBoolean("stream", stream);
        });

    /// <summary>字幕很短，但开着思考时思考内容也算在 max_tokens 里，留足余量。</summary>
    public const int AnthropicMaxTokens = 4096;

    private static string Role(ChatMessage m) => m.Role == ChatRole.User ? "user" : "assistant";

    private static void WriteMessage(Utf8JsonWriter w, string role, string content)
    {
        w.WriteStartObject();
        w.WriteString("role", role);
        w.WriteString("content", content);
        w.WriteEndObject();
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, Options))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>
/// OpenAI 兼容的 Chat Completions 接口。一个实现覆盖 OpenAI、DeepSeek、通义千问兼容模式、Gemini 兼容接口、
/// OpenRouter，以及 Ollama、LM Studio 等本地服务。
/// </summary>
public sealed class OpenAICompatibleTransport : IChatTransport
{
    private readonly HttpClient http;
    private readonly string baseUrl;
    private readonly string? apiKey;
    private readonly string model;

    public OpenAICompatibleTransport(HttpClient http, string baseUrl, string? apiKey, string model)
    {
        this.http = http;
        this.baseUrl = NormalizeBaseUrl(baseUrl);
        this.apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        this.model = model;
    }

    /// <summary>去掉末尾的斜杠；用户误填了完整的 /chat/completions 时也去掉。</summary>
    public static string NormalizeBaseUrl(string url)
    {
        var u = url.Trim().TrimEnd('/');
        if (u.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) u = u[..^"/chat/completions".Length];
        return u;
    }

    public async Task<string> StreamAsync(string systemPrompt, IReadOnlyList<ChatMessage> messages, Action<string> onDelta,
                                          CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions")
        {
            Content = new StringContent(RequestBodies.OpenAIChat(model, systemPrompt, messages), Encoding.UTF8, "application/json"),
        };
        Authorize(request);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new ChatProviderException(ChatErrorKind.Network, e.Message, inner: e);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await HttpError(response, cancellationToken).ConfigureAwait(false);
            }
            var text = new StringBuilder();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // 个别服务器忽略 stream 参数，直接返回完整 JSON。
            if (response.Content.Headers.ContentType?.MediaType == "application/json")
            {
                var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var chunk = OpenAIStream.Parse(body);
                if (chunk is StreamChunk.Error err) throw new ChatProviderException(ChatErrorKind.Http, err.Message);
                if (chunk is StreamChunk.Delta full)
                {
                    onDelta(full.Text);
                    return full.Text;
                }
                return "";
            }

            var parser = new SseParser();
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                foreach (var ev in parser.Feed(line))
                {
                    if (Handle(ev, text, onDelta)) return text.ToString();
                }
            }
            if (parser.Finish() is { } last) Handle(last, text, onDelta);
            return text.ToString();
        }
    }

    /// <summary>处理一条事件；返回 true 表示流结束。</summary>
    private static bool Handle(SseEvent ev, StringBuilder text, Action<string> onDelta)
    {
        switch (OpenAIStream.Parse(ev.Data))
        {
            case StreamChunk.Delta d:
                if (d.Text.Length == 0) return false;
                text.Append(d.Text);
                onDelta(d.Text);
                return false;
            case StreamChunk.Done:
                return true;
            case StreamChunk.Error e:
                throw new ChatProviderException(ChatErrorKind.Http, e.Message);
            default:
                return false;
        }
    }

    private void Authorize(HttpRequestMessage request)
    {
        // 本地服务（Ollama、LM Studio）不需要密钥，不发 Authorization 头。
        if (apiKey is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <summary>HTTP 错误响应 → 异常（两种接口格式共用）。</summary>
    internal static async Task<ChatProviderException> HttpError(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = "";
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
        }
        var message = OpenAIStream.ErrorFromBody(body);
        if (message.Length == 0) message = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        var kind = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? ChatErrorKind.Unauthorized
            : ChatErrorKind.Http;
        return new ChatProviderException(kind, message, (int)response.StatusCode);
    }
}
