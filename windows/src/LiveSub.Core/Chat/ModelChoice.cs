using System.Text.Json.Serialization;

namespace LiveSub.Core.Chat;

/// <summary>
/// 用户添加的模型服务：自己的 API（OpenAI 兼容或 Anthropic），或 Ollama、LM Studio 等本地服务（与 macOS 的 ModelEndpoint 相同）。
/// API 密钥不在这里，在 App 的 SecretStore 里按 Id 保存。
/// </summary>
public sealed class ModelEndpoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public ChatApiFormat Format { get; set; } = ChatApiFormat.OpenAI;
    public string BaseUrl { get; set; } = "";
    public List<string> Models { get; set; } = [];

    /// <summary>本机或局域网里的服务：归为“本地模型”，不需要密钥。</summary>
    [JsonIgnore] public bool IsLocal => ChatEndpoints.IsLocal(BaseUrl);
}

public enum ModelKind
{
    /// <summary>本机 Claude Code 命令行：haiku / sonnet / opus。</summary>
    ClaudeCode,
    /// <summary>某个模型服务里的某个模型。</summary>
    Endpoint,
}

/// <summary>
/// 翻译或 AI 助手使用的模型（与 macOS 的 ModelChoice 相同，Windows 版没有 Apple 本地翻译）。
/// 设置文件里是 <c>{"Kind":"ClaudeCode","Model":"haiku"}</c> 或 <c>{"Kind":"Endpoint","Model":"deepseek-chat","EndpointId":"…"}</c>。
/// </summary>
public sealed record ModelChoice(
    ModelKind Kind,
    string Model,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EndpointId = null)
{
    public static ModelChoice ClaudeCode(string model) => new(ModelKind.ClaudeCode, model);

    public static ModelChoice Endpoint(string endpointId, string model) => new(ModelKind.Endpoint, model, endpointId);
}

public enum ModelGroupKind
{
    /// <summary>云端模型：Claude Code 与自己的 API。</summary>
    Cloud,
    /// <summary>本地模型：本机或局域网里的服务。</summary>
    Local,
}

/// <summary>设置界面和菜单里的一组模型。</summary>
public sealed record ModelGroup(ModelGroupKind Kind, IReadOnlyList<ModelChoice> Choices);

/// <summary>可选的模型、显示名称，以及删除服务时的默认值（与 macOS 的 AppSettings.modelGroups 等相同）。</summary>
public static class ModelCatalog
{
    public const string Haiku = "haiku";
    public const string Sonnet = "sonnet";
    public const string Opus = "opus";

    public static readonly IReadOnlyList<string> ClaudeCodeModels = [Haiku, Sonnet, Opus];

    /// <summary>字幕翻译默认用最快的 Haiku。</summary>
    public static ModelChoice DefaultTranslation { get; } = ModelChoice.ClaudeCode(Haiku);

    /// <summary>AI 助手和笔记默认用 Sonnet。</summary>
    public static ModelChoice DefaultAssistant { get; } = ModelChoice.ClaudeCode(Sonnet);

    /// <summary>“Claude Haiku”，或“服务名 · 模型名”（服务已删除时只显示模型名）。</summary>
    public static string DisplayName(ModelChoice choice, IEnumerable<ModelEndpoint> endpoints)
    {
        if (choice.Kind == ModelKind.ClaudeCode) return "Claude " + Capitalize(choice.Model);
        var endpoint = endpoints.FirstOrDefault(e => e.Id == choice.EndpointId);
        return endpoint is null ? choice.Model : $"{endpoint.Name} · {choice.Model}";
    }

    /// <summary>
    /// 可选的模型，分为云端（Claude Code 与自己的 API）、本地两组；本地一组没有模型时不列出。
    /// 云端一组先列 Claude Haiku、Sonnet、Opus，再列自己的 API 里的模型。
    /// </summary>
    public static List<ModelGroup> Groups(IReadOnlyList<ModelEndpoint> endpoints)
    {
        var cloud = ClaudeCodeModels.Select(ModelChoice.ClaudeCode)
            .Concat(endpoints.Where(e => !e.IsLocal).SelectMany(Choices))
            .ToList();
        var local = endpoints.Where(e => e.IsLocal).SelectMany(Choices).ToList();
        var groups = new List<ModelGroup> { new(ModelGroupKind.Cloud, cloud) };
        if (local.Count > 0) groups.Add(new ModelGroup(ModelGroupKind.Local, local));
        return groups;
    }

    private static IEnumerable<ModelChoice> Choices(ModelEndpoint e) => e.Models.Select(m => ModelChoice.Endpoint(e.Id, m));

    /// <summary>删除一个模型服务后：正在用这个服务的地方换回默认模型。</summary>
    public static ModelChoice WithoutEndpoint(ModelChoice choice, string endpointId, ModelChoice fallback) =>
        choice.Kind == ModelKind.Endpoint && choice.EndpointId == endpointId ? fallback : choice;

    /// <summary>模型输入框的文字 → 模型列表：按逗号（含全角）和换行分开，去掉空白和重复。</summary>
    public static List<string> ParseModels(string text) =>
        text.Split([',', '，', '\n', '\r'])
            .Select(m => m.Trim())
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>模型列表 → 输入框里的文字。</summary>
    public static string JoinModels(IEnumerable<string> models) => string.Join(", ", models);

    /// <summary>“获取列表”的结果：已经填写、服务里也有的模型排在前面，其余按服务返回的顺序（已排序），方便挑选后删减。</summary>
    public static List<string> MergeFetched(IReadOnlyList<string> current, IReadOnlyList<string> fetched)
    {
        var kept = current.Where(fetched.Contains).Distinct().ToList();
        return kept.Concat(fetched.Where(m => !kept.Contains(m))).ToList();
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
