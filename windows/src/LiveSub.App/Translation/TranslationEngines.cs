using LiveSub.App.Localization;
using LiveSub.App.Services;
using LiveSub.Core.Chat;

namespace LiveSub.App.Translation;

/// <summary>翻译模型菜单里的一项。</summary>
public sealed record ModelChoice(string Title, EngineKind Engine, string Model, string? ProviderId, bool Enabled, bool Selected);

/// <summary>一组模型（菜单里用分隔线和不可点的标题隔开）。</summary>
public sealed record ModelGroup(string Title, IReadOnlyList<ModelChoice> Choices);

/// <summary>按设置创建翻译会话；列出可选模型；把错误转成给用户看的说明。</summary>
public static class TranslationEngines
{
    /// <summary>所有 OpenAI 兼容服务共用一个 HttpClient（超时由会话自己控制）。</summary>
    public static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static readonly IReadOnlyList<string> ClaudeCodeModels = ["haiku", "sonnet", "opus"];

    public static IChatSession Create(AppSettings settings, SecretStore secrets, string systemPrompt)
    {
        switch (settings.Engine)
        {
            case EngineKind.ClaudeCode:
                var launch = ClaudeLocator.Locate(settings.ClaudePath)
                    ?? throw new ChatProviderException(ChatErrorKind.ClaudeNotFound);
                return new ClaudeCodeSession(launch, settings.ClaudeCodeModel, systemPrompt);

            case EngineKind.Anthropic:
                var key = secrets.Get(SecretStore.AnthropicKey)
                    ?? throw new ChatProviderException(ChatErrorKind.Configuration, Strings.Format(Strings.Error_MissingKey, "Anthropic API"));
                return new HistoryChatSession(AnthropicModelPolicy.DisplayName(settings.AnthropicModel),
                                              new AnthropicTransport(key, settings.AnthropicModel), systemPrompt);

            default:
                var provider = settings.ActiveProvider ?? settings.Providers.FirstOrDefault()
                    ?? throw new ChatProviderException(ChatErrorKind.Configuration, Strings.Format(Strings.Error_MissingModel, Strings.Section_Providers));
                if (provider.Model.Trim().Length == 0)
                {
                    throw new ChatProviderException(ChatErrorKind.Configuration, Strings.Format(Strings.Error_MissingModel, provider.Name));
                }
                var providerKey = secrets.Get(SecretStore.ProviderKey(provider.Id));
                if (provider.RequiresKey && providerKey is null)
                {
                    throw new ChatProviderException(ChatErrorKind.Configuration, Strings.Format(Strings.Error_MissingKey, provider.Name));
                }
                return new HistoryChatSession($"{provider.Name} · {provider.Model}",
                    new OpenAICompatibleTransport(Http, provider.BaseUrl, providerKey, provider.Model.Trim()), systemPrompt);
        }
    }

    /// <summary>状态条上显示的当前模型名称。</summary>
    public static string CurrentName(AppSettings settings) => settings.Engine switch
    {
        EngineKind.ClaudeCode => "Claude " + Capitalize(settings.ClaudeCodeModel),
        EngineKind.Anthropic => AnthropicModelPolicy.DisplayName(settings.AnthropicModel),
        _ => settings.ActiveProvider is { } p ? $"{p.Name} · {p.Model}" : Strings.Section_Providers,
    };

    /// <summary>托盘菜单和状态条菜单里的模型列表。</summary>
    public static List<ModelGroup> Choices(AppSettings settings, SecretStore secrets)
    {
        var groups = new List<ModelGroup>
        {
            new(Strings.Group_ClaudeCode, ClaudeCodeModels.Select(m => new ModelChoice(
                Capitalize(m), EngineKind.ClaudeCode, m, null, true,
                settings.Engine == EngineKind.ClaudeCode && settings.ClaudeCodeModel == m)).ToList()),
        };

        bool hasKey = secrets.Has(SecretStore.AnthropicKey);
        var anthropicModels = AnthropicModelPolicy.KnownModels.Concat(settings.AnthropicModels)
            .Append(settings.AnthropicModel).Distinct().ToList();
        groups.Add(new ModelGroup(hasKey ? Strings.Group_Anthropic : $"{Strings.Group_Anthropic} · {Strings.Model_NoKey}",
            anthropicModels.Select(m => new ModelChoice(AnthropicModelPolicy.DisplayName(m), EngineKind.Anthropic, m, null, hasKey,
                settings.Engine == EngineKind.Anthropic && settings.AnthropicModel == m)).ToList()));

        foreach (var p in settings.Providers)
        {
            bool ready = p.Model.Trim().Length > 0 && (!p.RequiresKey || secrets.Has(SecretStore.ProviderKey(p.Id)));
            var title = p.Model.Trim().Length > 0 ? p.Model : Strings.Model_NotConfigured;
            groups.Add(new ModelGroup(p.Name, [new ModelChoice(title, EngineKind.OpenAICompatible, p.Model, p.Id, ready,
                settings.Engine == EngineKind.OpenAICompatible && settings.ActiveProviderId == p.Id)]));
        }
        return groups;
    }

    public static void Select(AppSettings settings, ModelChoice choice)
    {
        settings.Engine = choice.Engine;
        switch (choice.Engine)
        {
            case EngineKind.ClaudeCode:
                settings.ClaudeCodeModel = choice.Model;
                break;
            case EngineKind.Anthropic:
                settings.AnthropicModel = choice.Model;
                break;
            default:
                settings.ActiveProviderId = choice.ProviderId;
                break;
        }
    }

    /// <summary>给用户看的错误说明。</summary>
    public static string Describe(Exception e) => e switch
    {
        ChatProviderException { Kind: ChatErrorKind.ClaudeNotFound } => Strings.Error_ClaudeNotFound,
        ChatProviderException { Kind: ChatErrorKind.NotLoggedIn } => Strings.Error_NotLoggedIn,
        ChatProviderException { Kind: ChatErrorKind.Unauthorized } c => Strings.Format(Strings.Error_Unauthorized, c.Detail),
        ChatProviderException { Kind: ChatErrorKind.Timeout } => Strings.Error_Timeout,
        ChatProviderException { Kind: ChatErrorKind.ProcessExited } c =>
            Strings.Format(Strings.Error_ProcessExited, c.Detail.Length == 0 ? Strings.Error_NoOutput : Shorten(c.Detail)),
        ChatProviderException { Kind: ChatErrorKind.Http } c => Strings.Format(Strings.Error_Http, c.StatusCode?.ToString() ?? "HTTP", Shorten(c.Detail)),
        ChatProviderException { Kind: ChatErrorKind.Network } c => Strings.Format(Strings.Error_Network, Shorten(c.Detail)),
        ChatProviderException { Kind: ChatErrorKind.Refused } => Strings.Error_Refused,
        ChatProviderException { Kind: ChatErrorKind.Configuration } c => c.Detail,
        ChatProviderException c => Strings.Format(Strings.Error_Other, Shorten(c.Detail)),
        _ => Strings.Format(Strings.Error_Other, Shorten(e.Message)),
    };

    private static string Shorten(string s)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length > 200 ? s[..200] + "…" : s;
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
