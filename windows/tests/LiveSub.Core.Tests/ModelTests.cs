using System.Text.Json;
using System.Text.Json.Serialization;
using LiveSub.Core.Chat;

namespace LiveSub.Core.Tests;

public class ModelCatalogTests
{
    private static readonly ModelEndpoint DeepSeek = new()
    {
        Id = "ds", Name = "DeepSeek", BaseUrl = "https://api.deepseek.com/v1", Models = ["deepseek-chat", "deepseek-reasoner"],
    };
    private static readonly ModelEndpoint Ollama = new() { Id = "ol", Name = "Ollama", BaseUrl = "http://localhost:11434/v1", Models = ["qwen3:8b"] };
    private static readonly ModelEndpoint Empty = new() { Id = "em", Name = "OpenAI", BaseUrl = "https://api.openai.com/v1" };

    private static readonly ModelChoice Haiku = ModelChoice.ClaudeCode("haiku");
    private static readonly ModelChoice Sonnet = ModelChoice.ClaudeCode("sonnet");
    private static readonly ModelChoice Opus = ModelChoice.ClaudeCode("opus");

    [Fact]
    public void DefaultsAreHaikuForTranslationAndSonnetForAssistant()
    {
        Assert.Equal(Haiku, ModelCatalog.DefaultTranslation);
        Assert.Equal(Sonnet, ModelCatalog.DefaultAssistant);
    }

    [Fact]
    public void GroupsWithoutServicesAlwaysListOpus()
    {
        // Opus 总是列在 Haiku、Sonnet 后面，没有单独的开关。
        var groups = ModelCatalog.Groups([]);
        var cloud = Assert.Single(groups);
        Assert.Equal(ModelGroupKind.Cloud, cloud.Kind);
        Assert.Equal([Haiku, Sonnet, Opus], cloud.Choices);
    }

    [Fact]
    public void CloudServicesFollowClaudeAndLocalOnesGetTheirOwnGroup()
    {
        var groups = ModelCatalog.Groups([Ollama, DeepSeek, Empty]);
        Assert.Equal([ModelGroupKind.Cloud, ModelGroupKind.Local], groups.Select(g => g.Kind));
        Assert.Equal([Haiku, Sonnet, Opus, ModelChoice.Endpoint("ds", "deepseek-chat"), ModelChoice.Endpoint("ds", "deepseek-reasoner")],
                     groups[0].Choices);
        Assert.Equal([ModelChoice.Endpoint("ol", "qwen3:8b")], groups[1].Choices);
    }

    [Fact]
    public void DisplayNames()
    {
        IReadOnlyList<ModelEndpoint> endpoints = [DeepSeek];
        Assert.Equal("Claude Haiku", ModelCatalog.DisplayName(Haiku, endpoints));
        Assert.Equal("Claude Opus", ModelCatalog.DisplayName(Opus, endpoints));
        Assert.Equal("DeepSeek · deepseek-chat", ModelCatalog.DisplayName(ModelChoice.Endpoint("ds", "deepseek-chat"), endpoints));
        // 服务已删除：只显示模型名。
        Assert.Equal("gpt-5", ModelCatalog.DisplayName(ModelChoice.Endpoint("gone", "gpt-5"), endpoints));
    }

    [Fact]
    public void DeletingAServiceFallsBackToDefaults()
    {
        var deepSeek = ModelChoice.Endpoint("ds", "deepseek-chat");
        Assert.Equal(Haiku, ModelCatalog.WithoutEndpoint(deepSeek, "ds", ModelCatalog.DefaultTranslation));
        Assert.Equal(deepSeek, ModelCatalog.WithoutEndpoint(deepSeek, "other", ModelCatalog.DefaultTranslation));
        Assert.Equal(Opus, ModelCatalog.WithoutEndpoint(Opus, "ds", ModelCatalog.DefaultTranslation));
    }

    [Fact]
    public void ParsesModelText()
    {
        Assert.Equal(["a", "b", "c", "d"], ModelCatalog.ParseModels(" a, b，c\nd ,, a\r\n"));
        Assert.Empty(ModelCatalog.ParseModels(" , ，"));
        Assert.Equal("a, b", ModelCatalog.JoinModels(["a", "b"]));
    }

    [Fact]
    public void FetchedModelsKeepTheListedOnesFirst()
    {
        // 已经填写、服务里也有的排在前面；服务里没有的（多半是拼错了）去掉。
        Assert.Equal(["b", "a", "c"], ModelCatalog.MergeFetched(["x", "b"], ["a", "b", "c"]));
        Assert.Equal(["a", "b"], ModelCatalog.MergeFetched([], ["a", "b"]));
    }

    [Fact]
    public void ChoiceJsonShape()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        Assert.Equal("""{"Kind":"ClaudeCode","Model":"haiku"}""", JsonSerializer.Serialize(Haiku, options));
        var endpoint = ModelChoice.Endpoint("ds", "deepseek-chat");
        var json = JsonSerializer.Serialize(endpoint, options);
        Assert.Equal("""{"Kind":"Endpoint","Model":"deepseek-chat","EndpointId":"ds"}""", json);
        Assert.Equal(endpoint, JsonSerializer.Deserialize<ModelChoice>(json, options));
        Assert.DoesNotContain("IsLocal", JsonSerializer.Serialize(DeepSeek, options));
    }
}

public class ModelMigrationTests
{
    private static LegacyModelSettings Read(string json) => LegacyModelSettings.TryRead(json)!;

    [Fact]
    public void OnlyOldSettingsFilesAreMigrated()
    {
        Assert.Null(LegacyModelSettings.TryRead("""{"TranslationModel":{"Kind":"ClaudeCode","Model":"haiku"},"Engine":"ClaudeCode"}"""));
        Assert.Null(LegacyModelSettings.TryRead("""{"SourceLanguage":"en-US"}"""));
        Assert.Null(LegacyModelSettings.TryRead("not json"));
        Assert.Null(LegacyModelSettings.TryRead("[]"));
        Assert.Equal("Anthropic", LegacyModelSettings.TryRead("""{"Engine":"Anthropic"}""")!.Engine);
    }

    [Fact]
    public void ClaudeCodeKeepsItsModel()
    {
        var result = ModelMigration.Migrate(Read("""{"Engine":"ClaudeCode","ClaudeCodeModel":"sonnet","Providers":[]}"""), hasAnthropicKey: false);
        Assert.Equal(ModelChoice.ClaudeCode("sonnet"), result.Translation);
        Assert.Empty(result.Endpoints);
        Assert.Null(result.AnthropicEndpointId);

        var unknown = ModelMigration.Migrate(Read("""{"Engine":"ClaudeCode","ClaudeCodeModel":"fable"}"""), hasAnthropicKey: false);
        Assert.Equal(ModelChoice.ClaudeCode("haiku"), unknown.Translation);
    }

    [Fact]
    public void AnthropicApiBecomesAService()
    {
        var json = """
            {"Engine":"Anthropic","ClaudeCodeModel":"haiku","AnthropicModel":"claude-sonnet-5-5",
             "AnthropicModels":["claude-haiku-5-5","claude-sonnet-5-5","claude-opus-5-5"],"Providers":[],"ActiveProviderId":null}
            """;
        var result = ModelMigration.Migrate(Read(json), hasAnthropicKey: true);
        var endpoint = Assert.Single(result.Endpoints);
        Assert.Equal(ModelMigration.AnthropicEndpointId, endpoint.Id);
        Assert.Equal("Anthropic", endpoint.Name);
        Assert.Equal(ChatApiFormat.Anthropic, endpoint.Format);
        Assert.Equal("https://api.anthropic.com", endpoint.BaseUrl);
        Assert.Equal(["claude-sonnet-5-5", "claude-haiku-5-5", "claude-opus-5-5"], endpoint.Models);
        Assert.Equal(ModelChoice.Endpoint(ModelMigration.AnthropicEndpointId, "claude-sonnet-5-5"), result.Translation);
        Assert.Equal(ModelMigration.AnthropicEndpointId, result.AnthropicEndpointId);
    }

    [Fact]
    public void SavedAnthropicKeyIsKeptEvenWhenNotInUse()
    {
        var result = ModelMigration.Migrate(Read("""{"Engine":"ClaudeCode","ClaudeCodeModel":"opus","AnthropicModel":"claude-haiku-5-5"}"""),
                                            hasAnthropicKey: true);
        Assert.Equal(ModelChoice.ClaudeCode("opus"), result.Translation);
        Assert.Equal(["claude-haiku-5-5"], Assert.Single(result.Endpoints).Models);
        Assert.Equal(ModelMigration.AnthropicEndpointId, result.AnthropicEndpointId);
    }

    [Fact]
    public void ProvidersBecomeServicesWithTheSameIds()
    {
        var json = """
            {"Engine":"OpenAICompatible","ClaudeCodeModel":"haiku",
             "Providers":[
               {"Id":"p1","Name":"DeepSeek","BaseUrl":"https://api.deepseek.com/v1","Model":"deepseek-chat","Models":["deepseek-chat","deepseek-reasoner"]},
               {"Id":"p2","Name":"Ollama","BaseUrl":"http://localhost:11434/v1","Model":"qwen3:8b","Models":[]}],
             "ActiveProviderId":"p2"}
            """;
        var result = ModelMigration.Migrate(Read(json), hasAnthropicKey: false);
        Assert.Equal(["p1", "p2"], result.Endpoints.Select(e => e.Id));
        Assert.All(result.Endpoints, e => Assert.Equal(ChatApiFormat.OpenAI, e.Format));
        Assert.Equal(["deepseek-chat", "deepseek-reasoner"], result.Endpoints[0].Models);
        Assert.Equal("http://localhost:11434/v1", result.Endpoints[1].BaseUrl);
        Assert.True(result.Endpoints[1].IsLocal);
        Assert.Equal(ModelChoice.Endpoint("p2", "qwen3:8b"), result.Translation);
        Assert.Null(result.AnthropicEndpointId);
    }

    [Fact]
    public void ActiveProviderFallbacks()
    {
        // 没选过服务：用第一个；服务没填模型：用获取到的第一个。
        var json = """
            {"Engine":"OpenAICompatible","Providers":[{"Id":"p1","Name":"X","BaseUrl":"https://x.example/v1","Model":"","Models":["m2","m1"]}]}
            """;
        Assert.Equal(ModelChoice.Endpoint("p1", "m2"), ModelMigration.Migrate(Read(json), false).Translation);

        // 服务一个模型都没有：留在 Claude Code。
        var none = """{"Engine":"OpenAICompatible","ClaudeCodeModel":"sonnet","Providers":[{"Id":"p1","Name":"X","BaseUrl":"https://x.example/v1"}]}""";
        var result = ModelMigration.Migrate(Read(none), false);
        Assert.Equal(ModelChoice.ClaudeCode("sonnet"), result.Translation);
        Assert.Empty(Assert.Single(result.Endpoints).Models);
    }

    [Fact]
    public void MigrationIsRepeatable()
    {
        var json = """
            {"Engine":"Anthropic","AnthropicModel":"claude-haiku-5-5",
             "Providers":[{"Id":"p1","Name":"DeepSeek","BaseUrl":"https://api.deepseek.com/v1","Model":"deepseek-chat"}]}
            """;
        var first = ModelMigration.Migrate(Read(json), hasAnthropicKey: true);
        // 第一次迁移后旧密钥已经搬走：第二次看不到它，结果也要相同（服务 Id 不变，密钥还能对上）。
        var second = ModelMigration.Migrate(Read(json), hasAnthropicKey: false);
        Assert.Equal(first.Translation, second.Translation);
        Assert.Equal(first.Endpoints.Select(e => (e.Id, e.Name, e.Format, e.BaseUrl, string.Join(",", e.Models))),
                     second.Endpoints.Select(e => (e.Id, e.Name, e.Format, e.BaseUrl, string.Join(",", e.Models))));
        Assert.Equal(first.AnthropicEndpointId, second.AnthropicEndpointId);
    }

    /// <summary>内存里的密钥表；Writable 为 false 时写文件失败（内存里照样改）。</summary>
    private sealed class FakeSecrets : ISecretTable
    {
        public readonly Dictionary<string, string> Saved = [];
        private readonly Dictionary<string, string> memory = [];
        public bool Writable = true;

        public FakeSecrets(params (string Name, string Value)[] entries)
        {
            foreach (var (name, value) in entries) Saved[name] = memory[name] = value;
        }

        public string? Get(string name) => memory.GetValueOrDefault(name);

        public bool Set(string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) memory.Remove(name);
            else memory[name] = value;
            if (!Writable) return false;
            Saved.Clear();
            foreach (var (k, v) in memory) Saved[k] = v;
            return true;
        }
    }

    private const string NewKey = "provider:anthropic";
    private const string OldKey = "anthropic";

    [Fact]
    public void CommitCopiesKeyThenSavesThenDeletesOldKey()
    {
        var secrets = new FakeSecrets((OldKey, "sk-old"));
        var order = new List<string>();
        bool done = ModelMigration.Commit(NewKey, OldKey, secrets, () =>
        {
            // 保存设置时新密钥已经写进文件，旧密钥还在。
            order.Add($"save {secrets.Saved.GetValueOrDefault(NewKey)} {secrets.Saved.GetValueOrDefault(OldKey)}");
            return true;
        });
        Assert.True(done);
        Assert.Equal(["save sk-old sk-old"], order);
        Assert.Equal(new Dictionary<string, string> { [NewKey] = "sk-old" }, secrets.Saved);
    }

    [Fact]
    public void CommitStopsWhenTheCopiedKeyIsNotWritten()
    {
        var secrets = new FakeSecrets((OldKey, "sk-old")) { Writable = false };
        bool saved = false;
        Assert.False(ModelMigration.Commit(NewKey, OldKey, secrets, () => saved = true));
        // 新格式的设置没保存，文件里的旧密钥也没动：下次启动再迁移。
        Assert.False(saved);
        Assert.Equal(new Dictionary<string, string> { [OldKey] = "sk-old" }, secrets.Saved);
    }

    [Fact]
    public void CommitKeepsOldKeyWhenSettingsAreNotSavedAndIsRepeatable()
    {
        var secrets = new FakeSecrets((OldKey, "sk-old"));
        Assert.False(ModelMigration.Commit(NewKey, OldKey, secrets, () => false));
        Assert.Equal("sk-old", secrets.Saved[OldKey]);
        Assert.Equal("sk-old", secrets.Saved[NewKey]);

        // 下次启动再迁移：新密钥已经在了，不再复制；这次保存成功，旧密钥删掉。
        Assert.True(ModelMigration.Commit(NewKey, OldKey, secrets, () => true));
        Assert.Equal(new Dictionary<string, string> { [NewKey] = "sk-old" }, secrets.Saved);
    }

    [Fact]
    public void CommitDoesNotOverwriteAnExistingKey()
    {
        var secrets = new FakeSecrets((OldKey, "sk-old"), (NewKey, "sk-new"));
        Assert.True(ModelMigration.Commit(NewKey, OldKey, secrets, () => true));
        Assert.Equal(new Dictionary<string, string> { [NewKey] = "sk-new" }, secrets.Saved);
    }

    [Fact]
    public void CommitWithoutAnthropicServiceOnlySavesSettings()
    {
        var secrets = new FakeSecrets(("provider:p1", "sk-p1")) { Writable = false };
        bool saved = false;
        Assert.True(ModelMigration.Commit(null, OldKey, secrets, () => saved = true));
        Assert.True(saved);
        Assert.Equal(new Dictionary<string, string> { ["provider:p1"] = "sk-p1" }, secrets.Saved);
    }

    [Fact]
    public void UnmigratedSettingsStayInTheOldFormat()
    {
        var legacy = """
            {"Engine":"OpenAICompatible","ClaudeCodeModel":"sonnet","AnthropicModel":null,
             "Providers":[{"Id":"p1","Name":"DeepSeek","BaseUrl":"https://api.deepseek.com/v1","Model":"deepseek-chat"}],
             "ActiveProviderId":"p1","VisibleLines":2}
            """;
        var current = """
            {"SourceLanguage":"en-US","TranslationModel":{"Kind":"ClaudeCode","Model":"haiku"},"Endpoints":[],"VisibleLines":4}
            """;
        var json = LegacyModelSettings.KeepUnmigrated(current, legacy);
        // 仍是旧版的设置文件，旧字段原样保留，下次启动按它迁移；其他设置是新的。
        var read = LegacyModelSettings.TryRead(json);
        Assert.NotNull(read);
        Assert.Equal("OpenAICompatible", read.Engine);
        Assert.Equal("p1", read.ActiveProviderId);
        Assert.Equal("deepseek-chat", Assert.Single(read.Providers!).Model);
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        Assert.False(root.ContainsKey("TranslationModel"));
        Assert.False(root.ContainsKey("Endpoints"));
        Assert.True(root.ContainsKey("AnthropicModel"));
        Assert.Equal(4, (int)root["VisibleLines"]!);
        Assert.Equal("en-US", (string)root["SourceLanguage"]!);
        Assert.Equal(ModelChoice.Endpoint("p1", "deepseek-chat"), ModelMigration.Migrate(read, hasAnthropicKey: false).Translation);
    }
}
