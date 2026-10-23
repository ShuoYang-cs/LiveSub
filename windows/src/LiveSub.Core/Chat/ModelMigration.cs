using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiveSub.Core.Chat;

/// <summary>
/// Windows 0.1 的翻译设置：引擎（Claude Code / Anthropic API / OpenAI 兼容）+ 各自的模型，以及 OpenAI 兼容服务列表。
/// 只用来把旧的 settings.json 迁移到“翻译模型 + 模型服务”。
/// </summary>
public sealed class LegacyModelSettings
{
    /// <summary>旧设置里出现任何一个、而又没有 TranslationModel 时，说明是旧版的设置文件。</summary>
    private static readonly string[] Keys = ["Engine", "ClaudeCodeModel", "AnthropicModel", "AnthropicModels", "Providers", "ActiveProviderId"];

    /// <summary>迁移写出的新字段（翻译模型、模型服务）。有 TranslationModel 就不再当成旧版设置。</summary>
    private static readonly string[] MigratedKeys = ["TranslationModel", "Endpoints"];

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>ClaudeCode / Anthropic / OpenAICompatible。</summary>
    public string? Engine { get; set; }
    public string? ClaudeCodeModel { get; set; }
    public string? AnthropicModel { get; set; }
    public List<string>? AnthropicModels { get; set; }
    public List<LegacyProvider>? Providers { get; set; }
    public string? ActiveProviderId { get; set; }

    /// <summary>从设置文件里读出旧版的翻译设置；已经是新格式、没有旧字段或读不懂时返回 null（不迁移）。</summary>
    public static LegacyModelSettings? TryRead(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return null;
            if (root.ContainsKey("TranslationModel") || !Keys.Any(root.ContainsKey)) return null;
            return root.Deserialize<LegacyModelSettings>(Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 旧版设置还没迁移成（例如密钥文件读不出来）时保存设置：去掉迁移写出的新字段，换回原来文件里的旧字段，
    /// 保存后仍是旧版的设置文件，下次启动再迁移；其他设置（字号、位置等）照常保存。
    /// </summary>
    /// <param name="settingsJson">按新格式序列化的设置。</param>
    /// <param name="legacyJson">启动时读到的旧版设置文件。</param>
    public static string KeepUnmigrated(string settingsJson, string legacyJson)
    {
        if (JsonNode.Parse(settingsJson) is not JsonObject root) return settingsJson;
        foreach (var key in MigratedKeys) root.Remove(key);
        if (JsonNode.Parse(legacyJson) is JsonObject legacy)
        {
            foreach (var key in Keys)
            {
                if (legacy.TryGetPropertyValue(key, out var value)) root[key] = value?.DeepClone();
            }
        }
        return root.ToJsonString(Indented);
    }
}

/// <summary>旧版的一个 OpenAI 兼容服务（密钥按 Id 保存在 secrets.bin 里，名字是 provider:{Id}）。</summary>
public sealed class LegacyProvider
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? BaseUrl { get; set; }
    public string? Model { get; set; }
    public List<string>? Models { get; set; }
}

/// <param name="AnthropicEndpointId">
/// 迁移出了“Anthropic”服务时是它的 Id：旧的 Anthropic API 密钥要搬到这个服务名下。旧的 OpenAI 兼容服务保留原来的 Id，密钥不用搬。
/// </param>
public sealed record ModelMigrationResult(ModelChoice Translation, List<ModelEndpoint> Endpoints, string? AnthropicEndpointId);

/// <summary>迁移时读写的密钥表（App 里是加密保存的 secrets.bin）。</summary>
public interface ISecretTable
{
    /// <summary>密钥；没有时为 null。</summary>
    string? Get(string name);

    /// <summary>改一个密钥（null 或空白表示删除）。</summary>
    /// <returns>确实写进了文件。</returns>
    bool Set(string name, string? value);
}

/// <summary>
/// 旧的翻译设置 → 翻译模型 + 模型服务：
/// - 引擎是 Claude Code：翻译模型为 Claude Code 的同一个模型；
/// - 用过 Anthropic API（引擎是它，或者存过它的密钥）：新建“Anthropic”服务（Anthropic 格式，https://api.anthropic.com），
///   模型是原来选的模型加上获取到的模型列表；引擎是它时翻译模型选这个服务的原模型；
/// - 每个 OpenAI 兼容服务变成一个模型服务（Id 不变），模型是原来选的模型加上获取到的列表；引擎是它时选当前服务的模型。
/// 同样的输入总是得到同样的结果（Anthropic 服务的 Id 是固定的），迁移中途失败、下次再迁移一遍也不会多出服务或丢掉密钥。
/// </summary>
public static class ModelMigration
{
    /// <summary>迁移出的 Anthropic 服务的固定 Id。</summary>
    public const string AnthropicEndpointId = "anthropic";

    public const string AnthropicBaseUrl = "https://api.anthropic.com";

    public static ModelMigrationResult Migrate(LegacyModelSettings legacy, bool hasAnthropicKey)
    {
        var engine = legacy.Engine ?? "ClaudeCode";
        var claudeModel = legacy.ClaudeCodeModel is { } m && ModelCatalog.ClaudeCodeModels.Contains(m) ? m : ModelCatalog.Haiku;
        var translation = ModelChoice.ClaudeCode(claudeModel);
        var endpoints = new List<ModelEndpoint>();
        string? anthropicId = null;

        bool usedAnthropic = engine.Equals("Anthropic", StringComparison.OrdinalIgnoreCase);
        if (usedAnthropic || hasAnthropicKey)
        {
            var model = NonEmpty(legacy.AnthropicModel) ?? AnthropicModelPolicy.DefaultModel;
            endpoints.Add(new ModelEndpoint
            {
                Id = AnthropicEndpointId,
                Name = "Anthropic",
                Format = ChatApiFormat.Anthropic,
                BaseUrl = AnthropicBaseUrl,
                Models = Models(model, legacy.AnthropicModels),
            });
            anthropicId = AnthropicEndpointId;
            if (usedAnthropic) translation = ModelChoice.Endpoint(AnthropicEndpointId, model);
        }

        var providers = (legacy.Providers ?? []).Where(p => NonEmpty(p.Id) is not null).ToList();
        foreach (var p in providers)
        {
            if (endpoints.Any(e => e.Id == p.Id)) continue;
            endpoints.Add(new ModelEndpoint
            {
                Id = p.Id!,
                Name = p.Name?.Trim() ?? "",
                Format = ChatApiFormat.OpenAI,
                BaseUrl = p.BaseUrl?.Trim() ?? "",
                Models = Models(p.Model, p.Models),
            });
        }

        if (engine.Equals("OpenAICompatible", StringComparison.OrdinalIgnoreCase))
        {
            // 旧版没选过服务时用第一个；服务没填模型时用它获取到的第一个模型；都没有时留在 Claude Code。
            var active = providers.FirstOrDefault(p => p.Id == legacy.ActiveProviderId) ?? providers.FirstOrDefault();
            if (active is not null && endpoints.FirstOrDefault(e => e.Id == active.Id) is { Models.Count: > 0 } endpoint)
            {
                translation = ModelChoice.Endpoint(endpoint.Id, endpoint.Models[0]);
            }
        }
        return new ModelMigrationResult(translation, endpoints, anthropicId);
    }

    /// <summary>
    /// 把迁移结果写到磁盘。顺序保证中途失败时什么都不丢，下次启动再迁移一遍（结果相同）：
    /// 1. 旧的 Anthropic 密钥复制到新服务名下（新名下已有密钥时不动），没写进文件就停下，不保存设置；
    /// 2. 保存新格式的设置，失败就停下，旧设置和旧密钥都还在；
    /// 3. 设置保存好、新名下确实有密钥了，才删掉旧密钥（删不掉只是多留一份，不影响使用）。
    /// </summary>
    /// <param name="anthropicKeyName">迁移出的 Anthropic 服务的密钥名；没有这个服务时为 null。</param>
    /// <param name="legacyKeyName">旧版 Anthropic 密钥的名字。</param>
    /// <param name="saveSettings">保存新格式的设置，成功时返回 true。</param>
    /// <returns>新格式的设置已经保存（迁移完成）。</returns>
    public static bool Commit(string? anthropicKeyName, string legacyKeyName, ISecretTable secrets, Func<bool> saveSettings)
    {
        if (anthropicKeyName is not null && secrets.Get(anthropicKeyName) is null && secrets.Get(legacyKeyName) is { } key
            && !secrets.Set(anthropicKeyName, key))
        {
            return false;
        }
        if (!saveSettings()) return false;
        if (anthropicKeyName is not null && secrets.Get(anthropicKeyName) is not null) secrets.Set(legacyKeyName, null);
        return true;
    }

    /// <summary>原来选的模型放在最前面，再加上获取到的列表，去掉空白和重复。</summary>
    private static List<string> Models(string? selected, IEnumerable<string>? fetched) =>
        new[] { selected }.Concat(fetched ?? [])
            .Select(m => m?.Trim() ?? "")
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
