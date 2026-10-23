using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using LiveSub.Core;
using LiveSub.Core.Chat;

namespace LiveSub.App.Translation;

/// <summary>
/// Anthropic Messages API（官方 C# SDK），流式输出。接口地址可以换成其他兼容 Anthropic 格式的服务。
/// 低延迟设置见 <see cref="AnthropicModelPolicy"/>：Haiku 关掉思考；Sonnet / Opus 不能关思考，改用 effort = low。
/// </summary>
public sealed class AnthropicTransport : IChatTransport
{
    private readonly AnthropicClient client;
    private readonly string model;

    /// <param name="apiKey">本地服务可以没有密钥（不发 x-api-key）。</param>
    /// <param name="baseUrl">根地址，例如 https://api.anthropic.com；SDK 自己加 /v1/messages。</param>
    /// <param name="requestTimeout">
    /// 会话每次请求的超时（翻译 25 秒，AI 助手 180 秒，笔记 600 秒）。SDK 的超时包括读完整个流式回复，
    /// 设得比它长 5 秒，由会话按自己的超时结束请求。
    /// </param>
    public AnthropicTransport(string? apiKey, string model, string baseUrl, TimeSpan? requestTimeout = null)
    {
        client = new AnthropicClient
        {
            ApiKey = apiKey,
            BaseUrl = ChatEndpoints.AnthropicBaseUrl(baseUrl),
            MaxRetries = 1,
            Timeout = (requestTimeout ?? ClaudeCli.Timeout) + TimeSpan.FromSeconds(5),
        };
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

    /// <summary>
    /// 带附件的提问（与 <see cref="ChatContent.WriteAnthropicBlocks"/> 的顺序相同）：图片、PDF 在前，文字附件和问题放在最后的文字块里。
    /// </summary>
    private static List<ContentBlockParam> Blocks(string text, IReadOnlyList<ChatAttachment> attachments)
    {
        var blocks = new List<ContentBlockParam>();
        foreach (var attachment in attachments)
        {
            switch (attachment)
            {
                case ChatAttachment.Image image:
                    blocks.Add(new ImageBlockParam(new Base64ImageSource
                    {
                        Data = Convert.ToBase64String(image.Data),
                        MediaType = image.MediaType,
                    }));
                    break;
                case ChatAttachment.Pdf pdf:
                    blocks.Add(new DocumentBlockParam(new Base64PdfSource(Convert.ToBase64String(pdf.Data))) { Title = pdf.Name });
                    break;
            }
        }
        blocks.Add(new TextBlockParam(ChatContent.TextWithDocuments(text, attachments.OfType<ChatAttachment.Text>())));
        return blocks;
    }

    private MessageCreateParams BuildParameters(string systemPrompt, IReadOnlyList<ChatMessage> messages)
    {
        var policy = AnthropicModelPolicy.For(model);
        var turns = messages.Select(m => new MessageParam
        {
            Role = m.Role == ChatRole.User ? Role.User : Role.Assistant,
            Content = m.HasAttachments ? Blocks(m.Text, m.Attachments!) : m.Text,
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
}
