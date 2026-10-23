using System.Text.Json;
using System.Text.Json.Serialization;
using LiveSub.Core.Chat;

namespace LiveSub.Core.Tests;

public class ModelCatalogTests
{
    private static readonly BasicEngineNames Names = new("Google 翻译", "混元翻译");

    private static ModelEndpoint DeepSeek() => new()
    {
        Id = "ds", Name = "DeepSeek", BaseUrl = "https://api.deepseek.com/v1", Models = ["deepseek-chat", "deepseek-reasoner"],
    };
    private static ModelEndpoint Ollama() => new() { Id = "ol", Name = "Ollama", BaseUrl = "http://localhost:11434/v1", Models = ["qwen3:8b"] };
    private static ModelEndpoint Empty() => new() { Id = "em", Name = "OpenAI", BaseUrl = "https://api.openai.com/v1" };

    private static readonly ModelChoice Haiku = ModelChoice.ClaudeCode("haiku");
    private static readonly ModelChoice Sonnet = ModelChoice.ClaudeCode("sonnet");
    private static readonly ModelChoice Opus = ModelChoice.ClaudeCode("opus");

    [Fact]
    public void DefaultsAreGoogleForTranslationAndSonnetForAssistant()
    {
        // 新用户默认 Google 翻译（与 macOS 相同）；AI 助手只能用大模型。
        Assert.Equal(ModelChoice.Google, ModelCatalog.DefaultTranslation);
        Assert.Equal(Sonnet, ModelCatalog.DefaultAssistant);
        Assert.Equal("haiku", ModelCatalog.DefaultClaudeModel(ModelPurpose.Translation));
        Assert.Equal("sonnet", ModelCatalog.DefaultClaudeModel(ModelPurpose.Assistant));
        Assert.False(ModelChoice.Google.IsLanguageModel);
        Assert.False(ModelChoice.LocalModel.IsLanguageModel);
        Assert.True(Haiku.IsLanguageModel);
        Assert.True(ModelChoice.Endpoint("ds", "deepseek-chat").IsLanguageModel);
    }

    [Fact]
    public void TranslationGroupsWithoutServices()
    {
        // 基础一组是 Google 翻译；Claude 只列一项（用“翻译用”的型号）；本地一组总有混元翻译。
        var groups = ModelCatalog.Groups(ModelPurpose.Translation, [], "opus");
        Assert.Equal([ModelGroupKind.Basic, ModelGroupKind.Cloud, ModelGroupKind.Local], groups.Select(g => g.Kind));
        Assert.Equal([ModelChoice.Google], groups[0].Choices);
        Assert.Equal([Opus], groups[1].Choices);
        Assert.Equal([ModelChoice.LocalModel], groups[2].Choices);
    }

    [Fact]
    public void AssistantGroupsHaveNoTranslationOnlyEngines()
    {
        var groups = ModelCatalog.Groups(ModelPurpose.Assistant, [], "sonnet");
        var cloud = Assert.Single(groups);
        Assert.Equal(ModelGroupKind.Cloud, cloud.Kind);
        Assert.Equal([Sonnet], cloud.Choices);

        var withLocal = ModelCatalog.Groups(ModelPurpose.Assistant, [Ollama()], "sonnet");
        Assert.Equal([ModelGroupKind.Cloud, ModelGroupKind.Local], withLocal.Select(g => g.Kind));
        Assert.Equal([ModelChoice.Endpoint("ol", "qwen3:8b")], withLocal[1].Choices);
    }

    [Fact]
    public void EachServiceIsListedOnceWithItsModelForThePurpose()
    {
        var ds = DeepSeek();
        ds.AssistantModel = "deepseek-reasoner";
        IReadOnlyList<ModelEndpoint> endpoints = [Ollama(), ds, Empty()];
        var translation = ModelCatalog.Groups(ModelPurpose.Translation, endpoints, "haiku");
        // 没有模型的服务不列出；本地服务排在混元翻译后面。
        Assert.Equal([Haiku, ModelChoice.Endpoint("ds", "deepseek-chat")], translation[1].Choices);
        Assert.Equal([ModelChoice.LocalModel, ModelChoice.Endpoint("ol", "qwen3:8b")], translation[2].Choices);
        var assistant = ModelCatalog.Groups(ModelPurpose.Assistant, endpoints, "sonnet");
        Assert.Equal([Sonnet, ModelChoice.Endpoint("ds", "deepseek-reasoner")], assistant[0].Choices);
    }

    [Fact]
    public void ModelForFallsBackToTheFirstModel()
    {
        var ds = DeepSeek();
        Assert.Equal("deepseek-chat", ds.ModelFor(ModelPurpose.Translation));
        ds.SetModel("deepseek-reasoner", ModelPurpose.Translation);
        Assert.Equal("deepseek-reasoner", ds.TranslationModel);
        Assert.Equal("deepseek-reasoner", ds.ModelFor(ModelPurpose.Translation));
        Assert.Equal("deepseek-chat", ds.ModelFor(ModelPurpose.Assistant));
        // 选的模型从列表里删掉了：用第一个。
        ds.Models = ["deepseek-flash"];
        Assert.Equal("deepseek-flash", ds.ModelFor(ModelPurpose.Translation));
        Assert.Null(Empty().ModelFor(ModelPurpose.Translation));
    }

    [Fact]
    public void DisplayAndServiceNames()
    {
        IReadOnlyList<ModelEndpoint> endpoints = [DeepSeek()];
        Assert.Equal("Claude · Haiku", ModelCatalog.DisplayName(Haiku, endpoints, Names));
        Assert.Equal("Claude · Opus", ModelCatalog.DisplayName(Opus, endpoints, Names));
        Assert.Equal("DeepSeek · deepseek-chat", ModelCatalog.DisplayName(ModelChoice.Endpoint("ds", "deepseek-chat"), endpoints, Names));
        Assert.Equal("Google 翻译", ModelCatalog.DisplayName(ModelChoice.Google, endpoints, Names));
        Assert.Equal("混元翻译 · Hy-MT2 1.8B", ModelCatalog.DisplayName(ModelChoice.LocalModel, endpoints, Names));
        // 服务已删除：只显示模型名。
        Assert.Equal("gpt-5", ModelCatalog.DisplayName(ModelChoice.Endpoint("gone", "gpt-5"), endpoints, Names));

        // 菜单和列表上只写服务名。
        Assert.Equal("Claude", ModelCatalog.ServiceName(Opus, endpoints, Names));
        Assert.Equal("DeepSeek", ModelCatalog.ServiceName(ModelChoice.Endpoint("ds", "deepseek-reasoner"), endpoints, Names));
        Assert.Equal("Google 翻译", ModelCatalog.ServiceName(ModelChoice.Google, endpoints, Names));
        Assert.Equal("混元翻译", ModelCatalog.ServiceName(ModelChoice.LocalModel, endpoints, Names));
        Assert.Equal("gpt-5", ModelCatalog.ServiceName(ModelChoice.Endpoint("gone", "gpt-5"), endpoints, Names));
        Assert.Equal("Sonnet", ModelCatalog.ClaudeModelName("sonnet"));
    }

    [Fact]
    public void OldChoicesAreAdoptedAsTheServicesModel()
    {
        // 旧设置里直接选过某家的某个模型：记成这家的“翻译用”，接着用原来的模型。
        IReadOnlyList<ModelEndpoint> endpoints = [DeepSeek()];
        Assert.True(ModelCatalog.Adopt(ModelChoice.Endpoint("ds", "deepseek-reasoner"), ModelPurpose.Translation, endpoints));
        Assert.Equal("deepseek-reasoner", endpoints[0].TranslationModel);
        Assert.Null(endpoints[0].AssistantModel);
        // 已经是这家选的模型、服务不存在、不是模型服务：不改。
        Assert.False(ModelCatalog.Adopt(ModelChoice.Endpoint("ds", "deepseek-reasoner"), ModelPurpose.Translation, endpoints));
        Assert.False(ModelCatalog.Adopt(ModelChoice.Endpoint("ds", "deepseek-chat"), ModelPurpose.Assistant, endpoints));
        Assert.False(ModelCatalog.Adopt(ModelChoice.Endpoint("gone", "x"), ModelPurpose.Assistant, endpoints));
        Assert.False(ModelCatalog.Adopt(Opus, ModelPurpose.Translation, endpoints));
    }

    [Fact]
    public void ChoicesMissingFromTheListAreAddedNotReplaced()
    {
        // 与 macOS 的 adopt 相同：正在用、但已经不在可用模型里的（获取列表时被挤掉、手动删过），加进列表接着用，不换成第一个。
        IReadOnlyList<ModelEndpoint> endpoints = [DeepSeek()];
        var before = endpoints[0].Models.ToList();
        var choice = ModelChoice.Endpoint("ds", "gone-model");
        Assert.True(ModelCatalog.Adopt(choice, ModelPurpose.Assistant, endpoints));
        Assert.Equal([.. before, "gone-model"], endpoints[0].Models);
        Assert.Equal("gone-model", endpoints[0].AssistantModel);
        Assert.Equal(choice, ModelCatalog.Synced(choice, ModelPurpose.Assistant, endpoints));
        // 再来一次不重复加。
        Assert.False(ModelCatalog.Adopt(choice, ModelPurpose.Assistant, endpoints));
        Assert.Equal(before.Count + 1, endpoints[0].Models.Count);
    }

    [Fact]
    public void ChoicesFollowTheServicesModel()
    {
        IReadOnlyList<ModelEndpoint> endpoints = [DeepSeek()];
        var choice = ModelChoice.Endpoint("ds", "deepseek-chat");
        Assert.Equal(choice, ModelCatalog.Synced(choice, ModelPurpose.Translation, endpoints));
        // 在“模型服务”里换了这家的“翻译用”：正在用这家的翻译跟着换，AI 助手不变。
        endpoints[0].TranslationModel = "deepseek-reasoner";
        Assert.Equal(ModelChoice.Endpoint("ds", "deepseek-reasoner"), ModelCatalog.Synced(choice, ModelPurpose.Translation, endpoints));
        Assert.Equal(choice, ModelCatalog.Synced(choice, ModelPurpose.Assistant, endpoints));
        // 删掉了正在用的模型：换成第一个。
        endpoints[0].Models = ["deepseek-flash"];
        Assert.Equal(ModelChoice.Endpoint("ds", "deepseek-flash"), ModelCatalog.Synced(choice, ModelPurpose.Assistant, endpoints));
        // 一个模型都没有、服务删掉了、不是模型服务：保持原样。
        endpoints[0].Models = [];
        Assert.Null(ModelCatalog.Synced(choice, ModelPurpose.Translation, endpoints));
        Assert.Null(ModelCatalog.Synced(ModelChoice.Endpoint("gone", "x"), ModelPurpose.Translation, endpoints));
        Assert.Null(ModelCatalog.Synced(ModelChoice.Google, ModelPurpose.Translation, endpoints));
    }

    [Fact]
    public void OldClaudeChoicesBecomeTheClaudeModel()
    {
        // 旧设置里直接选过 Claude Opus（没有“翻译用”的型号）：接着用 Opus；已经存过型号的用存的。
        Assert.Equal("opus", ModelCatalog.AdoptClaudeModel("", Opus, ModelPurpose.Translation));
        Assert.Equal("opus", ModelCatalog.AdoptClaudeModel(null, Opus, ModelPurpose.Assistant));
        Assert.Equal("haiku", ModelCatalog.AdoptClaudeModel("haiku", Opus, ModelPurpose.Translation));
        // 选的不是 Claude：用默认型号（翻译 Haiku，AI 助手 Sonnet）；存的型号被手改坏了也一样。
        Assert.Equal("haiku", ModelCatalog.AdoptClaudeModel("", ModelChoice.Google, ModelPurpose.Translation));
        Assert.Equal("sonnet", ModelCatalog.AdoptClaudeModel("gpt", ModelChoice.Endpoint("ds", "x"), ModelPurpose.Assistant));
    }

    [Fact]
    public void OnlyModelChangedWithinTheSameService()
    {
        Assert.True(ModelCatalog.OnlyModelChanged(ModelChoice.Endpoint("ds", "a"), ModelChoice.Endpoint("ds", "b")));
        Assert.False(ModelCatalog.OnlyModelChanged(ModelChoice.Endpoint("ds", "a"), ModelChoice.Endpoint("ds", "a")));
        Assert.False(ModelCatalog.OnlyModelChanged(ModelChoice.Endpoint("ds", "a"), ModelChoice.Endpoint("qw", "a")));
        Assert.False(ModelCatalog.OnlyModelChanged(ModelChoice.Google, ModelChoice.Endpoint("ds", "a")));
        Assert.False(ModelCatalog.OnlyModelChanged(Haiku, Sonnet));
    }

    private static string Chinese(ChatEndpointPreset p) => p.ChineseName;

    [Fact]
    public void BuiltInServicesAreAddedInFrontWithFixedIds()
    {
        var saved = new List<ModelEndpoint> { Ollama() };
        var all = ModelCatalog.WithBuiltInServices(saved, [], Chinese);
        Assert.Equal(["OpenAI", "通义千问", "DeepSeek", "Kimi", "Grok", "Ollama"], all.Select(e => e.Name));
        Assert.Equal("B0000001-0000-4000-8000-000000000002", all[1].Id);
        Assert.Equal(["qwen-flash"], all[1].Models);
        Assert.Equal("https://dashscope.aliyuncs.com/compatible-mode/v1", all[1].BaseUrl);
        Assert.All(all.Take(5), e => Assert.True(ModelCatalog.IsBuiltIn(e.Id)));
        Assert.False(ModelCatalog.IsBuiltIn("ol"));
        // 再补一次不会重复（固定 Id）。
        Assert.Equal(6, ModelCatalog.WithBuiltInServices(all, [], Chinese).Count);
    }

    [Fact]
    public void DismissedOrAlreadyAddedBuiltInsAreNotAddedAgain()
    {
        // 删掉过的不补；自己加过同一个地址的（主机名相同，路径不同也算）不补。
        var saved = new List<ModelEndpoint> { DeepSeek() };
        var all = ModelCatalog.WithBuiltInServices(saved, ["B0000001-0000-4000-8000-000000000001"], p => p.Name);
        Assert.Equal(["Qwen", "Kimi", "Grok", "DeepSeek"], all.Select(e => e.Name));
        Assert.Equal("ds", all[^1].Id);
    }

    [Fact]
    public void DeletingAServiceFallsBackToDefaults()
    {
        var deepSeek = ModelChoice.Endpoint("ds", "deepseek-chat");
        Assert.Equal(ModelChoice.Google, ModelCatalog.WithoutEndpoint(deepSeek, "ds", ModelCatalog.DefaultTranslation));
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
        Assert.Equal("""{"Kind":"Google","Model":""}""", JsonSerializer.Serialize(ModelChoice.Google, options));
        Assert.Equal(ModelChoice.LocalModel, JsonSerializer.Deserialize<ModelChoice>("""{"Kind":"LocalModel","Model":""}""", options));

        // 服务：没选过“翻译用”“AI 助手用”时不写这两项（旧设置文件读进来也是这样）。
        var ds = DeepSeek();
        var plain = JsonSerializer.Serialize(ds, options);
        Assert.DoesNotContain("IsLocal", plain);
        Assert.DoesNotContain("TranslationModel", plain);
        ds.SetModel("deepseek-reasoner", ModelPurpose.Assistant);
        var read = JsonSerializer.Deserialize<ModelEndpoint>(JsonSerializer.Serialize(ds, options), options)!;
        Assert.Equal("deepseek-reasoner", read.AssistantModel);
        Assert.Null(read.TranslationModel);
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

    [Fact]
    public void UnmigratedSettingsDoNotKeepDefaultClaudeModels()
    {
        // secrets.bin 读不出来的那次运行，内存里的 Claude 型号是默认值（翻译 Haiku）；不能写进文件，
        // 否则下次真正迁移时会当成用户选过的型号，盖过旧设置里的 Sonnet。
        var legacy = """{"Engine":"ClaudeCode","ClaudeCodeModel":"sonnet"}""";
        var current = """
            {"TranslationModel":{"Kind":"ClaudeCode","Model":"haiku"},"Endpoints":[],"ClaudeTranslationModel":"haiku",
             "ClaudeAssistantModel":"sonnet","DismissedBuiltIns":["B0000001-0000-4000-8000-000000000001"],"VisibleLines":4}
            """;
        var json = LegacyModelSettings.KeepUnmigrated(current, legacy);
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        Assert.False(root.ContainsKey("ClaudeTranslationModel"));
        Assert.False(root.ContainsKey("ClaudeAssistantModel"));
        Assert.False(root.ContainsKey("DismissedBuiltIns"));
        Assert.Equal(4, (int)root["VisibleLines"]!);
        // 下次启动真正迁移：文件里没有存过型号，接着用旧设置里的 Sonnet。
        var translation = ModelMigration.Migrate(LegacyModelSettings.TryRead(json)!, hasAnthropicKey: false).Translation;
        Assert.Equal(ModelChoice.ClaudeCode("sonnet"), translation);
        Assert.Equal("sonnet", ModelCatalog.AdoptClaudeModel((string?)root["ClaudeTranslationModel"], translation, ModelPurpose.Translation));
    }
}
