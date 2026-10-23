using System.Net;
using LiveSub.App.Localization;
using LiveSub.App.Services;
using LiveSub.Core;
using LiveSub.Core.Translation;

namespace LiveSub.App.Translation;

/// <summary>Google 翻译（免费网页接口，不需要密钥，需要联网；与 macOS 的 GoogleTranslator 相同）。</summary>
public sealed class GoogleTranslator : ITranslator
{
    /// <summary>
    /// 所有 Google 翻译器共用一个 HttpClient，预热时建立的连接换了翻译器也能接着用。
    /// Google 平时 1.5 秒内就回；超时设短一点，一个接口卡住时能尽快换另一个。
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    private readonly string source;
    private readonly string target;
    private readonly object gate = new();
    /// <summary>只在 gate 内访问。</summary>
    private readonly GoogleEndpointRotation rotation = new();
    private readonly CancellationTokenSource disposed = new();
    /// <summary>上次写进日志的网络错误：断网时每句、每个接口都会失败，同样的错误只记一次，成功一次后重新记。</summary>
    private string? lastNetworkError;

    /// <summary>一个接口的一次请求失败。Temporary：限流或服务端出错，换接口、过一会儿再用它。</summary>
    private sealed class Failure(string message, bool temporary) : Exception(message)
    {
        public bool Temporary => temporary;
    }

    public GoogleTranslator(LanguageOption source, LanguageOption target)
    {
        this.source = source.TranslationCode;
        this.target = target.TranslationCode;
    }

    public string Name => Strings.Engine_Google;

    /// <summary>
    /// 每句单独翻译，不带上下文：macOS 版试过把前两句原文一起发（LiveCaptions-Translator 的做法），
    /// 2026-10-22 实测 8 句里 7 句译文完全一样、1 句反而错了，Google 免费接口基本不利用上下文。
    ///
    /// 限流时：先换另一个接口（被限流的冷却 60 秒）；两个都不行就等 1–4 秒再各试一次，还不行才算这句失败。
    /// </summary>
    public async Task<string> TranslateAsync(string text, IReadOnlyList<(string Source, string Translation)> context, Action<string>? onPartial,
                                             CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disposed.Token);
        var last = new Failure(Strings.Error_UnrecognizedReply, temporary: false);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            List<GoogleEndpoint> order;
            lock (gate) order = rotation.Order(DateTime.UtcNow);
            foreach (var endpoint in order)
            {
                try
                {
                    var result = await RequestAsync(text, endpoint, linked.Token);
                    lock (gate) rotation.MarkSuccess(endpoint);
                    lastNetworkError = null;
                    return result;
                }
                catch (Failure failure)
                {
                    last = failure;
                    if (!failure.Temporary) continue;
                    lock (gate) rotation.MarkLimited(endpoint, DateTime.UtcNow);
                    AppLog.Info($"google {endpoint} unavailable: {failure.Message}");
                }
            }
            if (attempt > 0) break;
            TimeSpan wait;
            lock (gate) wait = rotation.NextBackoff();
            await Task.Delay(wait, linked.Token);
        }
        throw new TranslatorException(Strings.Format(Strings.Error_Google, last.Message));
    }

    private async Task<string> RequestAsync(string text, GoogleEndpoint endpoint, CancellationToken cancellationToken)
    {
        // 测试用：LIVESUB_DEBUG_GOOGLE_FAIL=gtx（或 dictionaryExtension、all）假装这个接口被限流。
        if (Environment.GetEnvironmentVariable("LIVESUB_DEBUG_GOOGLE_FAIL") is { } fail
            && (fail == "all" || fail.Equals(endpoint.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new Failure("HTTP 429", temporary: true);
        }
        var url = GoogleTranslate.RequestUrl(text, source, target, endpoint);
        HttpResponseMessage response;
        try
        {
            response = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            // 断网、连不上：另一个接口多半也一样，但试一下不费事。原文（英文）只写进日志。
            var detail = $"{e.HttpRequestError}: {e.Message}";
            if (Interlocked.Exchange(ref lastNetworkError, detail) != detail) AppLog.Error($"google {endpoint} request failed", e);
            throw new Failure(ErrorText.Network(e), temporary: false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new Failure(Strings.Error_Timeout, temporary: false);
        }
        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                int status = (int)response.StatusCode;
                throw new Failure($"HTTP {status}", GoogleTranslate.IsTemporaryFailure(status));
            }
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                return GoogleTranslate.Parse(body, endpoint);
            }
            catch (GoogleTranslate.ParseException)
            {
                throw new Failure(Strings.Error_UnrecognizedReply, temporary: false);
            }
        }
    }

    /// <summary>先和两个接口都建立好连接（第一次请求要多花约 0.4 秒握手），第一句字幕、换接口时都不用等。</summary>
    public void WarmUp()
    {
        foreach (var endpoint in GoogleTranslate.Endpoints)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await RequestAsync("Hello", endpoint, disposed.Token);
                }
                catch (Exception e) when (e is Failure or OperationCanceledException)
                {
                    // 预热失败没关系，翻译时会再试。
                }
            });
        }
    }

    public void Dispose() => disposed.Cancel();
}
