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
        var body = RequestBodies.OpenAIChat("deepseek-chat", "SYS", Turns);
        Assert.Equal("""{"model":"deepseek-chat","messages":[{"role":"system","content":"SYS"},{"role":"user","content":"Hello."},{"role":"assistant","content":"你好。"},{"role":"user","content":"Bye."}],"stream":true}""", body);
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
        Assert.Equal("claude-haiku-5-5", AnthropicModelPolicy.DefaultModel);
    }

    [Fact]
    public void BaseUrlIsNormalized()
    {
        Assert.Equal("http://localhost:11434/v1", OpenAICompatibleTransport.NormalizeBaseUrl(" http://localhost:11434/v1/ "));
        Assert.Equal("https://api.deepseek.com/v1", OpenAICompatibleTransport.NormalizeBaseUrl("https://api.deepseek.com/v1/chat/completions"));
    }

    [Fact]
    public void LocalServersAreRecognized()
    {
        Assert.True(OpenAICompatibleTransport.IsLocalServer("http://localhost:11434/v1"));
        Assert.True(OpenAICompatibleTransport.IsLocalServer("http://127.0.0.1:1234/v1"));
        Assert.True(OpenAICompatibleTransport.IsLocalServer("http://192.168.1.20:11434/v1"));
        Assert.True(OpenAICompatibleTransport.IsLocalServer("http://[::1]:8080/v1"));
        Assert.False(OpenAICompatibleTransport.IsLocalServer("https://api.deepseek.com/v1"));
        Assert.False(OpenAICompatibleTransport.IsLocalServer("http://8.8.8.8/v1"));
        Assert.False(OpenAICompatibleTransport.IsLocalServer(""));
        // 预设与地址判断一致。
        Assert.All(ProviderPreset.All, p => Assert.Equal(!p.RequiresKey, OpenAICompatibleTransport.IsLocalServer(p.BaseUrl)));
    }

    [Fact]
    public void PresetsHaveDistinctIdsAndLocalOnesNeedNoKey()
    {
        Assert.Equal(ProviderPreset.All.Count, ProviderPreset.All.Select(p => p.Id).Distinct().Count());
        Assert.False(ProviderPreset.All.Single(p => p.Id == "ollama").RequiresKey);
        Assert.Equal("http://localhost:1234/v1", ProviderPreset.All.Single(p => p.Id == "lmstudio").BaseUrl);
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
        var transport = new OpenAICompatibleTransport(new HttpClient(handler), "https://api.example.com/v1", "k", "");
        Assert.Equal(["a", "b"], await transport.ListModelsAsync(CancellationToken.None));
        Assert.Equal("https://api.example.com/v1/models", handler.Request!.RequestUri!.ToString());
    }
}
