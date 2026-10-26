using System.Net.Http.Headers;
using System.Text.Json;

namespace LiveSub.Core.Chat;

/// <summary>
/// 通过 HTTP 调用的大模型接口格式：OpenAI 兼容，或 Anthropic Messages API（与 macOS 的 ChatAPIFormat 相同）。
/// OpenAI 兼容格式一种实现即可覆盖 OpenAI、DeepSeek、通义千问、Gemini、OpenRouter 等云服务，以及 Ollama、LM Studio 等本地服务。
/// </summary>
public enum ChatApiFormat { OpenAI, Anthropic }

/// <summary>
/// 常用服务的模板：填好接口格式和地址，用户只需要填密钥、选模型（与 macOS 的 ChatEndpointPreset 相同）。
/// 模型名只是示例，以服务商当前文档或“获取列表”为准。
/// </summary>
/// <param name="BuiltInId">默认列出的服务的固定 Id（存密钥、判断用户删没删过用，和 macOS 相同）；其他模板没有。</param>
public sealed record ChatEndpointPreset(string Id, string Name, string ChineseName, ChatApiFormat Format, string BaseUrl,
                                        IReadOnlyList<string> ExampleModels, string? BuiltInId = null)
{
    /// <summary>
    /// 默认就列在“云端模型”里的服务（2026-10-22 macOS 用户要求）：用户只需要填自己的 API 密钥。
    /// 模型选的是各家快、不思考的那个（实时字幕要快），2026-10-22 按各家官方文档核对；以后可以在“模型服务”里点“获取列表”换。
    /// </summary>
    public static readonly IReadOnlyList<ChatEndpointPreset> BuiltIn =
    [
        new("openai", "OpenAI", "OpenAI", ChatApiFormat.OpenAI, "https://api.openai.com/v1", ["gpt-5.4-mini"],
            "B0000001-0000-4000-8000-000000000001"),
        new("dashscope", "Qwen", "通义千问", ChatApiFormat.OpenAI, "https://dashscope.aliyuncs.com/compatible-mode/v1", ["qwen-flash"],
            "B0000001-0000-4000-8000-000000000002"),
        new("deepseek", "DeepSeek", "DeepSeek", ChatApiFormat.OpenAI, "https://api.deepseek.com", ["deepseek-flash"],
            "B0000001-0000-4000-8000-000000000003"),
        new("kimi", "Kimi", "Kimi", ChatApiFormat.OpenAI, "https://api.moonshot.cn/v1", ["kimi-k2.6"],
            "B0000001-0000-4000-8000-000000000004"),
        new("grok", "Grok", "Grok", ChatApiFormat.OpenAI, "https://api.x.ai/v1", ["grok-4.20-0309-non-reasoning"],
            "B0000001-0000-4000-8000-000000000005"),
    ];

    /// <summary>“添加模型服务”菜单里的模板：默认列出的服务在前（删掉后可以从这里加回来）。</summary>
    public static readonly IReadOnlyList<ChatEndpointPreset> All =
    [
        .. BuiltIn,
        new("anthropic", "Anthropic", "Anthropic", ChatApiFormat.Anthropic, "https://api.anthropic.com",
            [AnthropicModelPolicy.Haiku, AnthropicModelPolicy.Sonnet]),
        new("gemini", "Gemini", "Gemini", ChatApiFormat.OpenAI, "https://generativelanguage.googleapis.com/v1beta/openai", []),
        new("openrouter", "OpenRouter", "OpenRouter", ChatApiFormat.OpenAI, "https://openrouter.ai/api/v1", []),
        new("ollama", "Ollama", "Ollama", ChatApiFormat.OpenAI, "http://localhost:11434/v1", []),
        new("lmstudio", "LM Studio", "LM Studio", ChatApiFormat.OpenAI, "http://localhost:1234/v1", []),
    ];

    /// <summary>云服务需要密钥；本机或局域网里的服务不需要。</summary>
    public bool RequiresKey => !ChatEndpoints.IsLocal(BaseUrl);
}

/// <summary>模型服务的地址、请求头和模型列表（两种接口格式共用）。</summary>
public static class ChatEndpoints
{
    /// <summary>Anthropic 要求的接口版本头。</summary>
    public const string AnthropicVersion = "2023-06-01";

    /// <summary>
    /// 本机或局域网里的服务（Ollama、LM Studio 等）：归为“本地模型”，也不需要密钥。
    /// 判断依据是接口地址的主机名：localhost、回环地址、.local、私有网段。
    /// macOS 只看本机地址；Windows 版沿用原来的判断，局域网里另一台电脑上的服务也算本地。
    /// </summary>
    public static bool IsLocal(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!System.Net.IPAddress.TryParse(uri.Host, out var ip)) return uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));
    }

    /// <summary>从 /models 的回复里取模型名（OpenAI 兼容格式，Ollama、LM Studio 也是）；跳过只做向量化的模型。</summary>
    public static List<string> ParseModelIds(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != System.Text.Json.JsonValueKind.Array) return [];
            var ids = new List<string>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == System.Text.Json.JsonValueKind.Object
                    && item.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String
                    && id.GetString() is { Length: > 0 } name && !name.Contains("embed", StringComparison.OrdinalIgnoreCase))
                    ids.Add(name);
            }
            return ids;
        }
        catch (System.Text.Json.JsonException) { return []; }
    }

    /// <summary>接口地址是不是完整的 http(s) 地址（新加的“自定义服务”只有 https://，还不能用）。</summary>
    public static bool IsValidBaseUrl(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.Host.Length > 0;

    /// <summary>
    /// Anthropic 接口的根地址：不带 /v1（官方 SDK 自己加 /v1/messages）。用户填了 /v1 或完整的 /v1/messages 也兼容。
    /// </summary>
    public static string AnthropicBaseUrl(string url)
    {
        var u = url.Trim().TrimEnd('/');
        if (u.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)) u = u[..^"/messages".Length];
        if (u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) u = u[..^"/v1".Length];
        return u;
    }

    /// <summary>模型列表的地址：OpenAI 兼容是 <c>{接口地址}/models</c>，Anthropic 是 <c>{根地址}/v1/models</c>（默认每页只有 20 个，要全部）。</summary>
    public static string ModelsUrl(ChatApiFormat format, string baseUrl) => format == ChatApiFormat.Anthropic
        ? AnthropicBaseUrl(baseUrl) + "/v1/models?limit=1000"
        : OpenAICompatibleTransport.NormalizeBaseUrl(baseUrl) + "/models";

    /// <summary>按接口格式加上密钥（没有密钥时不发）和版本头。</summary>
    public static void Authorize(HttpRequestMessage request, ChatApiFormat format, string? apiKey)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (format == ChatApiFormat.Anthropic)
        {
            request.Headers.Add("anthropic-version", AnthropicVersion);
            if (key is not null) request.Headers.Add("x-api-key", key);
        }
        else if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    /// <summary>读取服务提供的模型列表（“获取列表”按钮用）。两种格式的返回都是 <c>{"data":[{"id":...}]}</c>。</summary>
    public static async Task<List<string>> ListModelsAsync(HttpClient http, ChatApiFormat format, string baseUrl, string? apiKey,
                                                           CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsUrl(format, baseUrl));
        Authorize(request, format, apiKey);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new ChatProviderException(ChatErrorKind.Network, e.Message, inner: e);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await OpenAICompatibleTransport.HttpError(response, cancellationToken).ConfigureAwait(false);
            }
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return OpenAIStream.ParseModelList(body);
            }
            catch (JsonException e)
            {
                throw new ChatProviderException(ChatErrorKind.Other, OpenAIStream.ErrorFromBody(body), inner: e);
            }
        }
    }
}
