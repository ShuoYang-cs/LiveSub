using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using LiveSub.Core.Chat;

namespace LiveSub.App.Translation;

/// <summary>
/// Anthropic Messages API（官方 C# SDK），流式输出。
/// 低延迟设置见 <see cref="AnthropicModelPolicy"/>：Haiku 关掉思考；Sonnet / Opus 不能关思考，改用 effort = low。
/// </summary>
public sealed class AnthropicTransport : IChatTransport
{
    private readonly AnthropicClient client;
    private readonly string model;

    public AnthropicTransport(string apiKey, string model)
    {
        client = new AnthropicClient { ApiKey = apiKey, MaxRetries = 1, Timeout = TimeSpan.FromSeconds(30) };
        this.model = model;
    }

    public async Task<string> StreamAsync(string systemPrompt, IReadOnlyList<ChatMessage> messages, Action<string> onDelta,
                                          CancellationToken cancellationToken)
    {
        var parameters = BuildParameters(systemPrompt, messages);
        var text = new System.Text.StringBuilder();
        bool refused = false;
        try
        {
            await foreach (var streamEvent in client.Messages.CreateStreaming(parameters, cancellationToken).ConfigureAwait(false))
            {
                if (streamEvent.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var t))
                {
                    text.Append(t.Text);
                    onDelta(t.Text);
                }
                else if (streamEvent.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason == StopReason.Refusal)
                {
                    refused = true;
                }
            }
        }
        catch (AnthropicUnauthorizedException e)
        {
            throw new ChatProviderException(ChatErrorKind.Unauthorized, e.Message, 401, e);
        }
        catch (AnthropicForbiddenException e)
        {
            throw new ChatProviderException(ChatErrorKind.Unauthorized, e.Message, 403, e);
        }
        catch (AnthropicNotFoundException e)
        {
            throw new ChatProviderException(ChatErrorKind.Http, e.Message, 404, e);
        }
        catch (AnthropicRateLimitException e)
        {
            throw new ChatProviderException(ChatErrorKind.Http, e.Message, 429, e);
        }
        catch (AnthropicApiException e)
        {
            throw new ChatProviderException(ChatErrorKind.Http, e.Message, inner: e);
        }
        catch (AnthropicIOException e)
        {
            throw new ChatProviderException(ChatErrorKind.Network, e.Message, inner: e);
        }
        catch (AnthropicSseException e)
        {
            // 流式输出途中服务器发来的错误事件（例如 overloaded_error），不属于 AnthropicApiException。
            throw new ChatProviderException(ChatErrorKind.Http, e.Message, inner: e);
        }
        catch (AnthropicException e)
        {
            throw new ChatProviderException(ChatErrorKind.Other, e.Message, inner: e);
        }
        if (refused && text.Length == 0) throw new ChatProviderException(ChatErrorKind.Refused);
        return text.ToString();
    }

    private MessageCreateParams BuildParameters(string systemPrompt, IReadOnlyList<ChatMessage> messages)
    {
        var policy = AnthropicModelPolicy.For(model);
        var turns = messages.Select(m => new MessageParam
        {
            Role = m.Role == ChatRole.User ? Role.User : Role.Assistant,
            Content = m.Text,
        }).ToList();

        // 对话会逐句变长：开启自动缓存，前面的历史不用每次重新计费和处理（太短时服务器会忽略）。
        if (policy.DisableThinking)
        {
            return new MessageCreateParams
            {
                Model = model,
                MaxTokens = RequestBodies.AnthropicMaxTokens,
                System = systemPrompt,
                Messages = turns,
                Thinking = new ThinkingConfigDisabled(),
                CacheControl = new CacheControlEphemeral(),
            };
        }
        if (policy.Effort == "low")
        {
            return new MessageCreateParams
            {
                Model = model,
                MaxTokens = RequestBodies.AnthropicMaxTokens,
                System = systemPrompt,
                Messages = turns,
                OutputConfig = new OutputConfig { Effort = Effort.Low },
                CacheControl = new CacheControlEphemeral(),
            };
        }
        return new MessageCreateParams
        {
            Model = model,
            MaxTokens = RequestBodies.AnthropicMaxTokens,
            System = systemPrompt,
            Messages = turns,
            CacheControl = new CacheControlEphemeral(),
        };
    }

    /// <summary>列出账号可用的模型 ID。</summary>
    public static async Task<List<string>> ListModelsAsync(string apiKey, CancellationToken cancellationToken)
    {
        var client = new AnthropicClient { ApiKey = apiKey, MaxRetries = 1, Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            var page = await client.Models.List(cancellationToken: cancellationToken).ConfigureAwait(false);
            return page.Items.Select(m => m.ID).Where(id => id.StartsWith("claude-", StringComparison.Ordinal)).ToList();
        }
        catch (AnthropicUnauthorizedException e)
        {
            throw new ChatProviderException(ChatErrorKind.Unauthorized, e.Message, 401, e);
        }
        catch (AnthropicApiException e)
        {
            throw new ChatProviderException(ChatErrorKind.Http, e.Message, inner: e);
        }
        catch (AnthropicIOException e)
        {
            throw new ChatProviderException(ChatErrorKind.Network, e.Message, inner: e);
        }
        catch (AnthropicException e)
        {
            throw new ChatProviderException(ChatErrorKind.Other, e.Message, inner: e);
        }
    }
}
