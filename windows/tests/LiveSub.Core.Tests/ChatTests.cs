using System.Net;
using System.Text;
using System.Text.Json;
using LiveSub.Core;
using LiveSub.Core.Chat;

namespace LiveSub.Core.Tests;

public class SseParserTests
{
    [Fact]
    public void ParsesOpenAIStyleDataLines()
    {
        var p = new SseParser();
        var events = new List<SseEvent>();
        foreach (var line in new[] { "data: {\"a\":1}", "", ": keep-alive", "", "data:{\"b\":2}", "", "data: [DONE]", "" })
        {
            events.AddRange(p.Feed(line));
        }
        Assert.Equal([new SseEvent(null, "{\"a\":1}"), new SseEvent(null, "{\"b\":2}"), new SseEvent(null, "[DONE]")], events);
    }

    [Fact]
    public void ParsesAnthropicStyleEventsAndMultilineData()
    {
        var p = new SseParser();
        var events = new List<SseEvent>();
        foreach (var line in new[] { "event: content_block_delta\r", "data: line1", "data: line2", "", "event: ping", "data: {}" })
        {
            events.AddRange(p.Feed(line));
        }
        // 最后一条没有空行结尾，流结束时取出。
        events.Add(p.Finish()!);
        Assert.Equal([new SseEvent("content_block_delta", "line1\nline2"), new SseEvent("ping", "{}")], events);
        Assert.Null(p.Finish());
    }
}

public class StreamChunkTests
{
    [Fact]
    public void OpenAIDeltaDoneAndError()
    {
        Assert.Equal(new StreamChunk.Delta("你好"), OpenAIStream.Parse("""{"id":"x","choices":[{"index":0,"delta":{"content":"你好"}}]}"""));
        Assert.Equal(new StreamChunk.Ignore(), OpenAIStream.Parse("""{"choices":[{"index":0,"delta":{"role":"assistant"}}]}"""));
        Assert.Equal(new StreamChunk.Ignore(), OpenAIStream.Parse("""{"choices":[{"index":0,"delta":{"content":null},"finish_reason":"stop"}]}"""));
        Assert.Equal(new StreamChunk.Done(), OpenAIStream.Parse(" [DONE] "));
        Assert.Equal(new StreamChunk.Error("Rate limit reached"), OpenAIStream.Parse("""{"error":{"message":"Rate limit reached","type":"requests"}}"""));
        Assert.Equal(new StreamChunk.Ignore(), OpenAIStream.Parse("not json"));
    }

    [Fact]
    public void OpenAIReasoningContentIsNotShown()
    {
        // DeepSeek 推理模型先输出 reasoning_content，不能当成译文。
        Assert.Equal(new StreamChunk.Ignore(), OpenAIStream.Parse("""{"choices":[{"delta":{"reasoning_content":"Let me think"}}]}"""));
    }

    [Fact]
    public void ErrorBodies()
    {
        Assert.Equal("Incorrect API key provided", OpenAIStream.ErrorFromBody("""{"error":{"message":"Incorrect API key provided","code":"invalid_api_key"}}"""));
        Assert.Equal("model not found", OpenAIStream.ErrorFromBody("""{"error":"model not found"}"""));
        Assert.Equal("API key not valid", OpenAIStream.ErrorFromBody("""[{"error":{"code":400,"message":"API key not valid","status":"INVALID_ARGUMENT"}}]"""));
        Assert.Equal("Bad Gateway", OpenAIStream.ErrorFromBody("  Bad Gateway  "));
    }

    [Fact]
    public void ModelList()
    {
        Assert.Equal(["deepseek-chat", "deepseek-reasoner"],
            OpenAIStream.ParseModelList("""{"object":"list","data":[{"id":"deepseek-reasoner"},{"id":"deepseek-chat"}]}"""));
        Assert.Equal(["llama3.2:latest"], OpenAIStream.ParseModelList("""{"models":[{"name":"llama3.2:latest"}]}"""));
    }

    [Fact]
    public void AnthropicEvents()
    {
        Assert.Equal(new StreamChunk.Delta("Hi"),
            AnthropicStream.Parse("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi"}}"""));
        Assert.Equal(new StreamChunk.Ignore(),
            AnthropicStream.Parse("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"..."}}"""));
        Assert.Equal(new StreamChunk.Refusal(),
            AnthropicStream.Parse("message_delta", """{"type":"message_delta","delta":{"stop_reason":"refusal"}}"""));
        Assert.Equal(new StreamChunk.Done(), AnthropicStream.Parse("message_stop", """{"type":"message_stop"}"""));
        Assert.Equal(new StreamChunk.Error("Overloaded"),
            AnthropicStream.Parse("error", """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""));
        Assert.Equal(new StreamChunk.Ignore(), AnthropicStream.Parse("ping", """{"type":"ping"}"""));
    }
}

public class RequestBodyTests
{
    private static readonly ChatMessage[] Turns = [new(ChatRole.User, "Hello."), new(ChatRole.Assistant, "你好。"), new(ChatRole.User, "Bye.")];

    [Fact]
    public void OpenAIBody()
    {
        var body = RequestBodies.OpenAIChat("gpt-5.4-mini", "SYS", Turns);
        Assert.Equal("""{"model":"gpt-5.4-mini","messages":[{"role":"system","content":"SYS"},{"role":"user","content":"Hello."},{"role":"assistant","content":"你好。"},{"role":"user","content":"Bye."}],"stream":true}""", body);
    }

    [Fact]
    public void ThinkingIsTurnedOffOnlyWhereSupported()
    {
        // 与 macOS 的 ChatAPITests.thinkingIsTurnedOffOnlyWhereSupported 相同。
        static JsonElement Body(string model) => JsonDocument.Parse(RequestBodies.OpenAIChat(model, "S", [new(ChatRole.User, "hi")])).RootElement;
        Assert.Equal("disabled", Body("deepseek-flash").GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("disabled", Body("kimi-k2.6").GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(Body("qwen-flash").GetProperty("enable_thinking").GetBoolean());
        Assert.False(Body("qwen-flash").TryGetProperty("thinking", out _));
        // OpenAI、Grok 收到不认识的参数会报错，不能带；kimi-k3 不能关思考。
        foreach (var model in new[] { "gpt-5.4-mini", "grok-4.20-0309-non-reasoning", "kimi-k3" })
        {
            Assert.False(Body(model).TryGetProperty("thinking", out _));
            Assert.False(Body(model).TryGetProperty("enable_thinking", out _));
        }
    }

    [Fact]
    public void AnthropicBodyFollowsModelPolicy()
    {
        using (var haiku = JsonDocument.Parse(RequestBodies.AnthropicMessages("claude-haiku-5-5", "SYS", Turns)))
        {
            Assert.Equal("disabled", haiku.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            Assert.False(haiku.RootElement.TryGetProperty("output_config", out _));
            Assert.Equal("SYS", haiku.RootElement.GetProperty("system").GetString());
            Assert.Equal(3, haiku.RootElement.GetProperty("messages").GetArrayLength());
        }
        foreach (var model in new[] { "claude-sonnet-5-5", "claude-opus-5-5" })
        {
            using var doc = JsonDocument.Parse(RequestBodies.AnthropicMessages(model, "SYS", Turns));
            Assert.False(doc.RootElement.TryGetProperty("thinking", out _));
            Assert.Equal("low", doc.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        }
    }

    [Fact]
    public void ModelPolicy()
    {
        Assert.Equal(new AnthropicModelPolicy(true, null), AnthropicModelPolicy.For("claude-haiku-5-5"));
        Assert.Equal(new AnthropicModelPolicy(false, "low"), AnthropicModelPolicy.For("claude-sonnet-5-5"));
        Assert.Equal(new AnthropicModelPolicy(false, "low"), AnthropicModelPolicy.For("claude-opus-5-5"));
        Assert.Equal(new AnthropicModelPolicy(false, null), AnthropicModelPolicy.For("claude-sonnet-4-5"));
        Assert.Equal(new AnthropicModelPolicy(false, null), AnthropicModelPolicy.For("deepseek-chat"));
        Assert.Equal("claude-haiku-5-5", AnthropicModelPolicy.DefaultModel);
    }

    [Fact]
    public void BaseUrlIsNormalized()
    {
        Assert.Equal("http://localhost:11434/v1", OpenAICompatibleTransport.NormalizeBaseUrl(" http://localhost:11434/v1/ "));
        Assert.Equal("https://api.deepseek.com/v1", OpenAICompatibleTransport.NormalizeBaseUrl("https://api.deepseek.com/v1/chat/completions"));
    }

    [Fact]
    public void ParseModelIdsSkipsEmbeddingModels()
    {
        var json = """{"object":"list","data":[{"id":"qwen3:4b"},{"id":"nomic-embed-text"},{"id":"llama3.2"}]}""";
        Assert.Equal(["qwen3:4b", "llama3.2"], ChatEndpoints.ParseModelIds(json));
        Assert.Empty(ChatEndpoints.ParseModelIds("nope"));
        Assert.Empty(ChatEndpoints.ParseModelIds("[]"));
    }

    [Fact]
    public void LocalServersAreRecognized()
    {
        Assert.True(ChatEndpoints.IsLocal("http://localhost:11434/v1"));
        Assert.True(ChatEndpoints.IsLocal("http://127.0.0.1:1234/v1"));
        Assert.True(ChatEndpoints.IsLocal("http://192.168.1.20:11434/v1"));
        Assert.True(ChatEndpoints.IsLocal("http://[::1]:8080/v1"));
        Assert.True(ChatEndpoints.IsLocal("http://studio.local:1234/v1"));
        Assert.False(ChatEndpoints.IsLocal("https://api.deepseek.com/v1"));
        Assert.False(ChatEndpoints.IsLocal("http://8.8.8.8/v1"));
        Assert.False(ChatEndpoints.IsLocal(""));
        // 预设里只有本地服务不需要密钥。
        Assert.All(ChatEndpointPreset.All, p => Assert.Equal(!p.RequiresKey, ChatEndpoints.IsLocal(p.BaseUrl)));
    }

    [Fact]
    public void PresetsMatchMacOS()
    {
        var all = ChatEndpointPreset.All;
        Assert.Equal(all.Count, all.Select(p => p.Id).Distinct().Count());
        // 添加菜单里也有默认列出的服务，而且不重复。
        Assert.Equal(all.Count, all.Select(p => p.Name).Distinct().Count());
        Assert.Equal(["OpenAI", "Qwen", "DeepSeek", "Kimi", "Grok", "Anthropic", "Gemini", "OpenRouter", "Ollama", "LM Studio"],
                     all.Select(p => p.Name));
        var anthropic = all.Single(p => p.Id == "anthropic");
        Assert.Equal(ChatApiFormat.Anthropic, anthropic.Format);
        Assert.Equal("https://api.anthropic.com", anthropic.BaseUrl);
        Assert.Equal(["claude-haiku-5-5", "claude-sonnet-5-5"], anthropic.ExampleModels);
        Assert.All(all.Where(p => p != anthropic), p => Assert.Equal(ChatApiFormat.OpenAI, p.Format));
        Assert.Equal("通义千问", all.Single(p => p.Id == "dashscope").ChineseName);
        Assert.Equal(["deepseek-flash"], all.Single(p => p.Id == "deepseek").ExampleModels);
        Assert.Equal("https://api.deepseek.com", all.Single(p => p.Id == "deepseek").BaseUrl);
        Assert.False(all.Single(p => p.Id == "ollama").RequiresKey);
        Assert.Equal("http://localhost:1234/v1", all.Single(p => p.Id == "lmstudio").BaseUrl);
    }

    [Fact]
    public void BuiltInServicesHaveStableIdsAndModels()
    {
        // 与 macOS 的 ChatAPITests.builtInServicesHaveStableIDsAndModels 相同，Id 也和 macOS 一样。
        var presets = ChatEndpointPreset.BuiltIn;
        Assert.Equal(["OpenAI", "通义千问", "DeepSeek", "Kimi", "Grok"], presets.Select(p => p.ChineseName));
        Assert.Equal(Enumerable.Range(1, 5).Select(i => $"B0000001-0000-4000-8000-00000000000{i}"), presets.Select(p => p.BuiltInId));
        Assert.All(presets, p => Assert.True(Guid.TryParse(p.BuiltInId, out _)));
        Assert.All(presets, p =>
        {
            Assert.True(p.RequiresKey);
            Assert.NotEmpty(p.ExampleModels);
            Assert.Equal(ChatApiFormat.OpenAI, p.Format);
        });
        Assert.Equal(["gpt-5.4-mini", "qwen-flash", "deepseek-flash", "kimi-k2.6", "grok-4.20-0309-non-reasoning"],
                     presets.Select(p => p.ExampleModels[0]));
        Assert.All(ChatEndpointPreset.All.Except(presets), p => Assert.Null(p.BuiltInId));
    }

    [Fact]
    public void BaseUrlValidity()
    {
        Assert.True(ChatEndpoints.IsValidBaseUrl("https://api.deepseek.com/v1"));
        Assert.True(ChatEndpoints.IsValidBaseUrl(" http://localhost:11434/v1 "));
        // 新加的“自定义服务”只有 https://，还不能用。
        Assert.False(ChatEndpoints.IsValidBaseUrl("https://"));
        Assert.False(ChatEndpoints.IsValidBaseUrl("api.deepseek.com"));
        Assert.False(ChatEndpoints.IsValidBaseUrl("ftp://example.com"));
        Assert.False(ChatEndpoints.IsValidBaseUrl(""));
    }

    [Fact]
    public void AnthropicRootDropsV1()
    {
        Assert.Equal("https://api.anthropic.com", ChatEndpoints.AnthropicBaseUrl(" https://api.anthropic.com/ "));
        Assert.Equal("https://api.anthropic.com", ChatEndpoints.AnthropicBaseUrl("https://api.anthropic.com/v1"));
        Assert.Equal("https://api.anthropic.com", ChatEndpoints.AnthropicBaseUrl("https://api.anthropic.com/v1/messages"));
        Assert.Equal("https://proxy.example.com/anthropic", ChatEndpoints.AnthropicBaseUrl("https://proxy.example.com/anthropic/v1/"));
        Assert.Equal("https://api.anthropic.com/v1/models?limit=1000", ChatEndpoints.ModelsUrl(ChatApiFormat.Anthropic, "https://api.anthropic.com/v1"));
        Assert.Equal("https://api.deepseek.com/v1/models", ChatEndpoints.ModelsUrl(ChatApiFormat.OpenAI, "https://api.deepseek.com/v1/"));
    }
}

/// <summary>按顺序返回预设回复的假接口，记录收到的对话。</summary>
internal sealed class FakeTransport : IChatTransport
{
    public readonly List<List<ChatMessage>> Requests = [];
    public Func<int, string> Reply = i => $"reply {i}";
    public Exception? FailWith;
    public TimeSpan Delay = TimeSpan.Zero;

    public async Task<string> StreamAsync(string systemPrompt, IReadOnlyList<ChatMessage> messages, Action<string> onDelta, CancellationToken cancellationToken)
    {
        Requests.Add(messages.ToList());
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        if (FailWith is not null) throw FailWith;
        var text = Reply(Requests.Count);
        foreach (var word in text.Split(' ')) onDelta(word + " ");
        return text;
    }
}

public class HistoryChatSessionTests
{
    [Fact]
    public async Task FirstMessageCarriesContextAndLaterOnesOnlyTheFragment()
    {
        var transport = new FakeTransport();
        using var session = new HistoryChatSession("Test", transport, "SYS");
        var partials = new List<string>();
        var first = await session.RequestAsync("[Context]\n…\n[Translate]\none", "one", partials.Add, CancellationToken.None);
        var second = await session.RequestAsync("[Context]\n…\n[Translate]\ntwo", "two", null, CancellationToken.None);

        Assert.Equal("reply 1", first);
        Assert.Equal("reply 2", second);
        Assert.Equal(["reply ", "reply 1 "], partials);
        Assert.Equal("[Context]\n…\n[Translate]\none", transport.Requests[0].Single().Text);
        Assert.Equal(["[Context]\n…\n[Translate]\none", "reply 1", "two"], transport.Requests[1].Select(m => m.Text));
    }

    [Fact]
    public async Task SessionRestartsAfterMaxTurns()
    {
        var transport = new FakeTransport();
        using var session = new HistoryChatSession("Test", transport, "SYS", maxTurns: 2);
        for (int i = 0; i < 3; i++) await session.RequestAsync($"new{i}", $"cont{i}", null, CancellationToken.None);
        Assert.Equal("new0", transport.Requests[0][^1].Text);
        Assert.Equal("cont1", transport.Requests[1][^1].Text);
        // 第三句开了新会话：历史清空，并带上下文。
        Assert.Single(transport.Requests[2]);
        Assert.Equal("new2", transport.Requests[2][0].Text);
    }

    [Fact]
    public async Task FailedTurnIsNotRecorded()
    {
        var transport = new FakeTransport { FailWith = new ChatProviderException(ChatErrorKind.Http, "boom", 500) };
        using var session = new HistoryChatSession("Test", transport, "SYS");
        await Assert.ThrowsAsync<ChatProviderException>(() => session.RequestAsync("new", "cont", null, CancellationToken.None));
        transport.FailWith = null;
        await session.RequestAsync("new again", "cont again", null, CancellationToken.None);
        // 上一轮失败，会话仍是新的：发带上下文的版本。
        Assert.Equal("new again", transport.Requests[1].Single().Text);
        Assert.Equal(1, session.TurnsInSession);
    }

    [Fact]
    public async Task SlowReplyTimesOut()
    {
        var transport = new FakeTransport { Delay = TimeSpan.FromSeconds(5) };
        using var session = new HistoryChatSession("Test", transport, "SYS", timeout: TimeSpan.FromMilliseconds(100));
        var e = await Assert.ThrowsAsync<ChatProviderException>(() => session.RequestAsync("a", "a", null, CancellationToken.None));
        Assert.Equal(ChatErrorKind.Timeout, e.Kind);
    }
}

/// <summary>返回固定响应的假 HTTP 处理器，记录请求。</summary>
internal sealed class FakeHandler(HttpStatusCode status, string body, string contentType) : HttpMessageHandler
{
    public HttpRequestMessage? Request;
    public string? RequestBody;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Request = request;
        RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        return response;
    }
}

public class OpenAICompatibleTransportTests
{
    private static readonly ChatMessage[] One = [new(ChatRole.User, "Hello.")];

    [Fact]
    public async Task StreamsDeltasAndStopsAtDone()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n"
                + "data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}\n\n"
                + "data: {\"choices\":[{\"delta\":{\"content\":\"好。\"}}]}\n\n"
                + "data: [DONE]\n\n"
                + "data: {\"choices\":[{\"delta\":{\"content\":\"不该出现\"}}]}\n\n";
        var handler = new FakeHandler(HttpStatusCode.OK, sse, "text/event-stream");
        var transport = new OpenAICompatibleTransport(new HttpClient(handler), "https://api.example.com/v1/", "sk-test", "m1");
        var deltas = new List<string>();
        var text = await transport.StreamAsync("SYS", One, deltas.Add, CancellationToken.None);

        Assert.Equal("你好。", text);
        Assert.Equal(["你", "好。"], deltas);
        Assert.Equal("https://api.example.com/v1/chat/completions", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Contains("\"stream\":true", handler.RequestBody);
    }

    [Fact]
    public async Task LocalServerWithoutKeySendsNoAuthorization()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "data: [DONE]\n\n", "text/event-stream");
        var transport = new OpenAICompatibleTransport(new HttpClient(handler), "http://localhost:11434/v1", "  ", "llama3.2");
        await transport.StreamAsync("SYS", One, _ => { }, CancellationToken.None);
        Assert.Null(handler.Request!.Headers.Authorization);
    }

    [Fact]
    public async Task HttpErrorBodyIsSurfaced()
    {
        var handler = new FakeHandler(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key provided"}}""", "application/json");
        var transport = new OpenAICompatibleTransport(new HttpClient(handler), "https://api.example.com/v1", "bad", "m1");
        var e = await Assert.ThrowsAsync<ChatProviderException>(() => transport.StreamAsync("SYS", One, _ => { }, CancellationToken.None));
        Assert.Equal(ChatErrorKind.Unauthorized, e.Kind);
        Assert.Equal(401, e.StatusCode);
        Assert.Equal("Incorrect API key provided", e.Detail);
        Assert.True(e.IsPersistent);
    }

    [Fact]
    public async Task NonStreamingJsonReplyIsAccepted()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"好的。"}}]}""", "application/json");
        var transport = new OpenAICompatibleTransport(new HttpClient(handler), "http://localhost:1234/v1", null, "local");
        Assert.Equal("好的。", await transport.StreamAsync("SYS", One, _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task ListsModels()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"data":[{"id":"b"},{"id":"a"}]}""", "application/json");
        var models = await ChatEndpoints.ListModelsAsync(new HttpClient(handler), ChatApiFormat.OpenAI, "https://api.example.com/v1", "k",
                                                         CancellationToken.None);
        Assert.Equal(["a", "b"], models);
        Assert.Equal("https://api.example.com/v1/models", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task ListsAnthropicModelsWithAnthropicHeaders()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"data":[{"id":"claude-sonnet-5-5","type":"model"},{"id":"claude-haiku-5-5","type":"model"}],"has_more":false}""",
                                      "application/json");
        var models = await ChatEndpoints.ListModelsAsync(new HttpClient(handler), ChatApiFormat.Anthropic, "https://api.anthropic.com", "sk-ant",
                                                         CancellationToken.None);
        Assert.Equal(["claude-haiku-5-5", "claude-sonnet-5-5"], models);
        var request = handler.Request!;
        Assert.Equal("https://api.anthropic.com/v1/models?limit=1000", request.RequestUri!.ToString());
        Assert.Equal("sk-ant", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal(ChatEndpoints.AnthropicVersion, request.Headers.GetValues("anthropic-version").Single());
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task ModelListErrorsAreSurfaced()
    {
        var handler = new FakeHandler(HttpStatusCode.Unauthorized, """{"error":{"message":"invalid x-api-key"}}""", "application/json");
        var e = await Assert.ThrowsAsync<ChatProviderException>(() => ChatEndpoints.ListModelsAsync(
            new HttpClient(handler), ChatApiFormat.Anthropic, "http://localhost:4000", null, CancellationToken.None));
        Assert.Equal(ChatErrorKind.Unauthorized, e.Kind);
        Assert.Equal("invalid x-api-key", e.Detail);
        // 没有密钥时不发密钥头。
        Assert.False(handler.Request!.Headers.Contains("x-api-key"));
    }
}
