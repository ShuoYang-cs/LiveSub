using System.Text.Json.Serialization;
using LiveSub.Core.Translation;

namespace LiveSub.Core.Chat;

/// <summary>模型列表用在哪里：翻译还有“基础”一组（Google 翻译）和本机的混元翻译；每家服务的具体模型两边分开选（与 macOS 的 ModelPurpose 相同）。</summary>
public enum ModelPurpose { Translation, Assistant }

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
    /// <summary>这家能选的模型。</summary>
    public List<string> Models { get; set; } = [];
    /// <summary>
    /// 翻译、AI 助手分别用哪个模型（在“模型服务”里选）。模型列表里每家服务只列一项（2026-10-22 macOS 用户要求，和 Claude 一样）。
    /// 旧的设置文件里没有这两项，读进来是 null，用第一个模型。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? TranslationModel { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AssistantModel { get; set; }

    /// <summary>本机或局域网里的服务：归为“本地模型”，不需要密钥。</summary>
    [JsonIgnore] public bool IsLocal => ChatEndpoints.IsLocal(BaseUrl);

    /// <summary>这家为翻译或 AI 助手选的模型；没选过、或选的模型已经从列表里删掉时用第一个，一个模型都没有时为 null。</summary>
    public string? ModelFor(ModelPurpose purpose)
    {
        var chosen = purpose == ModelPurpose.Translation ? TranslationModel : AssistantModel;
        if (chosen is not null && Models.Contains(chosen)) return chosen;
        return Models.Count > 0 ? Models[0] : null;
    }

    public void SetModel(string model, ModelPurpose purpose)
    {
        if (purpose == ModelPurpose.Translation) TranslationModel = model;
        else AssistantModel = model;
    }
}

public enum ModelKind
{
    /// <summary>本机 Claude Code 命令行：haiku / sonnet / opus。</summary>
    ClaudeCode,
    /// <summary>某个模型服务里的某个模型。</summary>
    Endpoint,
    /// <summary>Google 翻译的免费网页接口：不需要密钥，需要联网（只能翻译）。</summary>
    Google,
    /// <summary>下载到本机的混元翻译模型：离线、只能翻译。</summary>
    LocalModel,
}

/// <summary>
/// 翻译或 AI 助手使用的模型（与 macOS 的 ModelChoice 相同，Windows 版没有 Apple 本地翻译）。
/// 设置文件里是 <c>{"Kind":"ClaudeCode","Model":"haiku"}</c>、<c>{"Kind":"Endpoint","Model":"deepseek-chat","EndpointId":"…"}</c>
/// 或 <c>{"Kind":"Google","Model":""}</c>。
/// </summary>
public sealed record ModelChoice(
    ModelKind Kind,
    string Model,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EndpointId = null)
{
    public static ModelChoice ClaudeCode(string model) => new(ModelKind.ClaudeCode, model);

    public static ModelChoice Endpoint(string endpointId, string model) => new(ModelKind.Endpoint, model, endpointId);

    public static ModelChoice Google { get; } = new(ModelKind.Google, "");

    public static ModelChoice LocalModel { get; } = new(ModelKind.LocalModel, "");

    /// <summary>能不能做 AI 助手、笔记和按发音纠正（Google 翻译、混元翻译这类翻译专用的引擎不行）。</summary>
    [JsonIgnore] public bool IsLanguageModel => Kind is ModelKind.ClaudeCode or ModelKind.Endpoint;
}

public enum ModelGroupKind
{
    /// <summary>基础：只能翻译的引擎（Google 翻译），只在翻译模型里有。</summary>
    Basic,
    /// <summary>云端模型：Claude Code 与自己的 API。</summary>
    Cloud,
    /// <summary>本地模型：混元翻译（只在翻译模型里有），以及本机或局域网里的服务。</summary>
    Local,
}

/// <summary>设置界面和菜单里的一组模型。</summary>
public sealed record ModelGroup(ModelGroupKind Kind, IReadOnlyList<ModelChoice> Choices);

/// <summary>只能翻译的引擎在界面上的名字（随界面语言变，由 App 提供）。</summary>
public sealed record BasicEngineNames(string Google, string LocalModel);

/// <summary>可选的模型、显示名称，以及删除服务时的默认值（与 macOS 的 AppSettings.modelGroups 等相同）。</summary>
public static class ModelCatalog
{
    public const string Haiku = "haiku";
    public const string Sonnet = "sonnet";
    public const string Opus = "opus";

    public static readonly IReadOnlyList<string> ClaudeCodeModels = [Haiku, Sonnet, Opus];

    /// <summary>新用户的字幕翻译默认用 Google 翻译：不需要配置（2026-10-22 macOS 用户决定）。删除正在用的服务时也换回它。</summary>
    public static ModelChoice DefaultTranslation => ModelChoice.Google;

    /// <summary>AI 助手和笔记默认用 Claude Sonnet。</summary>
    public static ModelChoice DefaultAssistant { get; } = ModelChoice.ClaudeCode(Sonnet);

    /// <summary>Claude 翻译要快，默认 Haiku；AI 助手更看重准确，默认 Sonnet（在“模型服务”里改）。</summary>
    public static string DefaultClaudeModel(ModelPurpose purpose) => purpose == ModelPurpose.Translation ? Haiku : Sonnet;

    /// <summary>
    /// 完整名称“服务 · 模型”：日志、笔记进度、状态里用。例如“Claude · Haiku”“通义千问 · qwen-flash”“混元翻译 · Hy-MT2 1.8B”；
    /// 服务已删除时只显示模型名。
    /// </summary>
    public static string DisplayName(ModelChoice choice, IEnumerable<ModelEndpoint> endpoints, BasicEngineNames names) => choice.Kind switch
    {
        ModelKind.Google => names.Google,
        ModelKind.LocalModel => names.LocalModel + " · " + LocalTranslationModel.Name,
        ModelKind.ClaudeCode => "Claude · " + ClaudeModelName(choice.Model),
        _ => endpoints.FirstOrDefault(e => e.Id == choice.EndpointId) is { } e ? $"{e.Name} · {choice.Model}" : choice.Model,
    };

    /// <summary>模型列表和菜单上显示的名字：只写服务名（“Claude”“通义千问”），具体模型在“模型服务”里看和改。</summary>
    public static string ServiceName(ModelChoice choice, IEnumerable<ModelEndpoint> endpoints, BasicEngineNames names) => choice.Kind switch
    {
        ModelKind.Google => names.Google,
        ModelKind.LocalModel => names.LocalModel,
        ModelKind.ClaudeCode => "Claude",
        _ => endpoints.FirstOrDefault(e => e.Id == choice.EndpointId)?.Name ?? choice.Model,
    };

    /// <summary>“haiku” → “Haiku”。</summary>
    public static string ClaudeModelName(string model) => model.Length == 0 ? model : char.ToUpperInvariant(model[0]) + model[1..];

    /// <summary>
    /// 可选的模型，分为基础（只能翻译）、云端（Claude Code 与自己的 API）、本地三组；本地一组没有模型时不列出。
    /// 每家服务只列一项，用哪个具体模型在“模型服务”里分别为翻译和 AI 助手选
    /// （三个 Claude、一串 GPT 型号并列显得突兀，2026-10-22 macOS 用户提出）。
    /// AI 助手用途没有“基础”一组，本地一组也没有混元翻译。
    /// </summary>
    /// <param name="claudeModel">这个用途选的 Claude 型号。</param>
    public static List<ModelGroup> Groups(ModelPurpose purpose, IReadOnlyList<ModelEndpoint> endpoints, string claudeModel)
    {
        ModelChoice? Choice(ModelEndpoint e) => e.ModelFor(purpose) is { } m ? ModelChoice.Endpoint(e.Id, m) : null;
        var cloud = endpoints.Where(e => !e.IsLocal).Select(Choice).OfType<ModelChoice>()
            .Prepend(ModelChoice.ClaudeCode(claudeModel))
            .ToList();
        var local = endpoints.Where(e => e.IsLocal).Select(Choice).OfType<ModelChoice>().ToList();
        if (purpose == ModelPurpose.Translation) local.Insert(0, ModelChoice.LocalModel);
        var groups = new List<ModelGroup>();
        if (purpose == ModelPurpose.Translation) groups.Add(new ModelGroup(ModelGroupKind.Basic, [ModelChoice.Google]));
        groups.Add(new ModelGroup(ModelGroupKind.Cloud, cloud));
        if (local.Count > 0) groups.Add(new ModelGroup(ModelGroupKind.Local, local));
        return groups;
    }

    /// <summary>
    /// 选中的具体模型记到那家服务上（旧设置里直接选过某家的某个模型时，记成这家的“翻译用”或“AI 助手用”，接着用原来的模型）。
    /// 正在用、但不在这家可用模型里的（以前“获取列表”时被挤掉、或手动删过，照样能用）先加进列表，
    /// 不能悄悄换成列表第一个（与 macOS 的 AppSettings.adopt 相同）。用户在“模型服务”里删掉正在用的模型时不经过这里，照旧换成第一个。
    /// </summary>
    /// <returns>改动了服务。</returns>
    public static bool Adopt(ModelChoice choice, ModelPurpose purpose, IReadOnlyList<ModelEndpoint> endpoints)
    {
        if (choice.Kind != ModelKind.Endpoint || string.IsNullOrWhiteSpace(choice.Model)
            || endpoints.FirstOrDefault(e => e.Id == choice.EndpointId) is not { } endpoint || endpoint.ModelFor(purpose) == choice.Model)
        {
            return false;
        }
        if (!endpoint.Models.Contains(choice.Model)) endpoint.Models.Add(choice.Model);
        endpoint.SetModel(choice.Model, purpose);
        return true;
    }

    /// <summary>
    /// 这家服务现在为翻译 / AI 助手选的模型（在“模型服务”里换了这家的模型、或删掉了正在用的模型时，正在用这家的地方跟着换）。
    /// 不是模型服务、服务已删除或它一个模型都没有时为 null（保持原样）。
    /// </summary>
    public static ModelChoice? Synced(ModelChoice choice, ModelPurpose purpose, IReadOnlyList<ModelEndpoint> endpoints) =>
        choice.Kind == ModelKind.Endpoint && endpoints.FirstOrDefault(e => e.Id == choice.EndpointId)?.ModelFor(purpose) is { } model
            ? ModelChoice.Endpoint(choice.EndpointId!, model)
            : null;

    /// <summary>
    /// 设置文件里这个用途的 Claude 型号；旧设置里没有（或被手改坏）时，以前直接选过某个 Claude 型号的接着用它，否则用默认型号。
    /// </summary>
    public static string AdoptClaudeModel(string? saved, ModelChoice choice, ModelPurpose purpose)
    {
        if (saved is not null && ClaudeCodeModels.Contains(saved)) return saved;
        return choice.Kind == ModelKind.ClaudeCode && ClaudeCodeModels.Contains(choice.Model) ? choice.Model : DefaultClaudeModel(purpose);
    }

    /// <summary>同一家服务只是换了模型（不用再提示填密钥）。</summary>
    public static bool OnlyModelChanged(ModelChoice old, ModelChoice now) =>
        old != now && old.Kind == ModelKind.Endpoint && now.Kind == ModelKind.Endpoint && old.EndpointId == now.EndpointId;

    /// <summary>
    /// 默认列出的云端服务（OpenAI、通义千问、DeepSeek、Kimi、Grok）：没有的补上，放在用户自己添加的前面。
    /// 用户删掉过的（<paramref name="dismissed"/>）、或者自己已经加过同一个地址（主机名相同）的，不再补。
    /// </summary>
    /// <param name="name">服务在界面上的名字（中文界面用中文名）。</param>
    public static List<ModelEndpoint> WithBuiltInServices(IReadOnlyList<ModelEndpoint> saved, IReadOnlyCollection<string> dismissed,
                                                          Func<ChatEndpointPreset, string> name)
    {
        var hosts = saved.Select(e => Host(e.BaseUrl)).OfType<string>().ToHashSet();
        var missing = ChatEndpointPreset.BuiltIn
            .Where(p => p.BuiltInId is { } id && !dismissed.Contains(id) && saved.All(e => e.Id != id)
                        && !(Host(p.BaseUrl) is { } host && hosts.Contains(host)))
            .Select(p => new ModelEndpoint
            {
                Id = p.BuiltInId!,
                Name = name(p),
                Format = p.Format,
                BaseUrl = p.BaseUrl,
                Models = [.. p.ExampleModels],
            });
        return missing.Concat(saved).ToList();
    }

    /// <summary>这个服务是不是默认列出的服务（删掉时要记住，不再补上）。</summary>
    public static bool IsBuiltIn(string endpointId) => ChatEndpointPreset.BuiltIn.Any(p => p.BuiltInId == endpointId);

    private static string? Host(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : null;

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
}
