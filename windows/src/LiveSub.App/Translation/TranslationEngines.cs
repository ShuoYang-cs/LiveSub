using LiveSub.App.Localization;
using LiveSub.App.Services;
using LiveSub.Core;
using LiveSub.Core.Chat;

namespace LiveSub.App.Translation;

/// <summary>模型菜单里的一项：服务名（具体模型在“模型服务”里选），以及是不是当前选中的模型。</summary>
public sealed record ModelMenuItem(ModelChoice Choice, string Title, bool Selected);

/// <summary>模型菜单里的一组（“基础”“云端模型”“本地模型”，菜单里是不可点的标题）。</summary>
public sealed record ModelMenuGroup(string Title, IReadOnlyList<ModelMenuItem> Items);

/// <summary>按所选模型创建会话（翻译、AI 助手、笔记）；列出可选模型；把错误转成给用户看的说明。</summary>
public static class TranslationEngines
{
    /// <summary>所有 HTTP 模型服务共用一个 HttpClient（超时由会话自己控制）。</summary>
    public static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// 大模型会话（翻译、AI 助手、笔记）：Claude Code → 长驻的 claude 进程；Anthropic 格式的服务 → 官方 SDK；
    /// OpenAI 兼容的服务 → HTTP 流式接口。Google 翻译、混元翻译不是大模型，不能用在这里（翻译器见 SubtitleController）。
    /// </summary>
    /// <param name="maxTurns">一个会话最多几轮，之后换新会话（翻译 40 轮；AI 助手 10000 轮，相当于不换；笔记 1 轮）。</param>
    /// <param name="timeout">每次请求的超时（默认是翻译用的 25 秒）。</param>
    public static IChatSession Create(ModelChoice choice, AppSettings settings, SecretStore secrets, string systemPrompt,
                                      int maxTurns = ClaudeCli.MaxTurnsPerSession, TimeSpan? timeout = null)
    {
        if (!choice.IsLanguageModel) throw new ChatProviderException(ChatErrorKind.Configuration, Strings.Error_NotLanguageModel);
        if (choice.Kind == ModelKind.ClaudeCode)
        {
            var launch = ClaudeLocator.Locate(settings.ClaudePath)
                ?? throw new ChatProviderException(ChatErrorKind.ClaudeNotFound);
            return new ClaudeCodeSession(launch, choice.Model, systemPrompt, maxTurns, timeout);
        }

        var endpoint = settings.Endpoint(choice.EndpointId)
            ?? throw new ChatProviderException(ChatErrorKind.Configuration, Strings.Error_EndpointMissing);
        if (!ChatEndpoints.IsValidBaseUrl(endpoint.BaseUrl))
        {
            throw new ChatProviderException(ChatErrorKind.Configuration, Strings.Format(Strings.Error_InvalidBaseUrl, endpoint.BaseUrl));
        }
        var key = secrets.Get(SecretStore.EndpointKey(endpoint.Id));
        if (key is null && !endpoint.IsLocal)
        {
            throw new ChatProviderException(ChatErrorKind.Configuration, Strings.Format(Strings.Error_MissingKey, endpoint.Name));
        }
        IChatTransport transport = endpoint.Format == ChatApiFormat.Anthropic
            ? new AnthropicTransport(key, choice.Model, endpoint.BaseUrl, timeout)
            : new OpenAICompatibleTransport(Http, endpoint.BaseUrl, key, choice.Model);
        return new HistoryChatSession(settings.DisplayName(choice), transport, systemPrompt, maxTurns, timeout);
    }

    /// <summary>读取模型服务提供的模型列表。</summary>
    public static async Task<List<string>> ListModelsAsync(ModelEndpoint endpoint, SecretStore secrets, CancellationToken cancellationToken)
    {
        if (!ChatEndpoints.IsValidBaseUrl(endpoint.BaseUrl))
        {
            throw new ChatProviderException(ChatErrorKind.Configuration, Strings.Format(Strings.Error_InvalidBaseUrl, endpoint.BaseUrl));
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await ChatEndpoints.ListModelsAsync(Http, endpoint.Format, endpoint.BaseUrl,
                secrets.Get(SecretStore.EndpointKey(endpoint.Id)), timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatProviderException(ChatErrorKind.Timeout);
        }
    }

    /// <summary>托盘菜单、状态条菜单和设置里的翻译模型列表：基础、云端模型、本地模型三组，每家服务一项，带当前选中的模型。</summary>
    public static List<ModelMenuGroup> TranslationMenu(AppSettings settings) =>
        ModelMenu(settings, ModelPurpose.Translation, settings.TranslationModel);

    /// <summary>AI 助手面板里的模型列表：云端模型、本地模型两组（没有只能翻译的 Google 翻译、混元翻译），选中的是 AI 助手的模型。</summary>
    public static List<ModelMenuGroup> AssistantMenu(AppSettings settings) =>
        ModelMenu(settings, ModelPurpose.Assistant, settings.AssistantModel);

    private static List<ModelMenuGroup> ModelMenu(AppSettings settings, ModelPurpose purpose, ModelChoice selected) =>
        settings.ModelGroups(purpose).Select(g => new ModelMenuGroup(
            g.Kind switch
            {
                ModelGroupKind.Basic => Strings.Group_Basic,
                ModelGroupKind.Local => Strings.Group_Local,
                _ => Strings.Group_Cloud,
            },
            g.Choices.Select(c => new ModelMenuItem(c, settings.ServiceName(c), c == selected)).ToList()))
        .ToList();

    /// <summary>给用户看的错误说明。</summary>
    public static string Describe(Exception e) => e switch
    {
        TranslatorException t => t.Message,
        ChatProviderException { Kind: ChatErrorKind.ClaudeNotFound } => Strings.Error_ClaudeNotFound,
        ChatProviderException { Kind: ChatErrorKind.NotLoggedIn } => Strings.Error_NotLoggedIn,
        ChatProviderException { Kind: ChatErrorKind.Unauthorized } c => Strings.Format(Strings.Error_Unauthorized, c.Detail),
        ChatProviderException { Kind: ChatErrorKind.Timeout } => Strings.Error_Timeout,
        ChatProviderException { Kind: ChatErrorKind.ProcessExited } c =>
            Strings.Format(Strings.Error_ProcessExited, c.Detail.Length == 0 ? Strings.Error_NoOutput : Shorten(c.Detail)),
        ChatProviderException { Kind: ChatErrorKind.Http } c => Strings.Format(Strings.Error_Http, c.StatusCode?.ToString() ?? "HTTP", Shorten(c.Detail)),
        // .NET 的网络错误说明只有英文：按种类换成中英文的说明（原文在日志里）。
        ChatProviderException { Kind: ChatErrorKind.Network, InnerException: HttpRequestException h } =>
            Strings.Format(Strings.Error_Network, ErrorText.Network(h)),
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
}
