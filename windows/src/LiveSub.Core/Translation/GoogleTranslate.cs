using System.Text;
using System.Text.Json;

namespace LiveSub.Core.Translation;

/// <summary>Google 翻译的两个免费网页接口。</summary>
public enum GoogleEndpoint
{
    /// <summary>translate.googleapis.com，client=gtx：回复里有逐段译文。</summary>
    Gtx,
    /// <summary>clients5.google.com，Chrome 词典扩展用的接口（LiveCaptions-Translator 用的就是它）：回复只有译文。</summary>
    DictionaryExtension,
}

/// <summary>
/// Google 翻译的免费网页接口（与 macOS 的 GoogleTranslate 相同）：不需要 API 密钥，也没有官方保证，短时间请求太多会被限流（HTTP 429）。
/// 有两个接口（<see cref="GoogleEndpoint"/>），一个被限流时换另一个（<see cref="GoogleEndpointRotation"/>）。
/// </summary>
public static class GoogleTranslate
{
    public static readonly IReadOnlyList<GoogleEndpoint> Endpoints = [GoogleEndpoint.Gtx, GoogleEndpoint.DictionaryExtension];

    /// <summary>Google 的语言代码：简繁中文用地区写法，其余和翻译代码相同。</summary>
    public static string LanguageCode(string translationCode) => translationCode switch
    {
        "zh-Hans" => "zh-CN",
        "zh-Hant" => "zh-TW",
        _ => translationCode,
    };

    public static Uri RequestUrl(string text, string source, string target, GoogleEndpoint endpoint = GoogleEndpoint.Gtx)
    {
        var (path, client) = endpoint == GoogleEndpoint.Gtx
            ? ("https://translate.googleapis.com/translate_a/single", "client=gtx&dt=t")
            : ("https://clients5.google.com/translate_a/t", "client=dict-chrome-ex");
        // EscapeDataString 会把“+”转成 %2B：不转的话服务器会把它当成空格。
        var query = $"{client}&sl={Uri.EscapeDataString(LanguageCode(source))}&tl={Uri.EscapeDataString(LanguageCode(target))}"
                    + $"&q={Uri.EscapeDataString(text)}";
        return new Uri(path + "?" + query);
    }

    /// <summary>这个状态码说明接口暂时不可用（限流或服务端出错），应该换接口，而不是放弃这句。</summary>
    public static bool IsTemporaryFailure(int status) => status == 429 || status >= 500;

    /// <summary>回复不是预期的格式。</summary>
    public sealed class ParseException(string message) : Exception(message);

    public static string Parse(string json, GoogleEndpoint endpoint = GoogleEndpoint.Gtx)
    {
        string text;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) throw new ParseException("not an array");
            var first = root[0];
            if (endpoint == GoogleEndpoint.Gtx)
            {
                // [[[译文, 原文, …], …], …]：第一项是逐段译文，拼起来就是整句。
                if (first.ValueKind != JsonValueKind.Array) throw new ParseException("no segments");
                var sb = new StringBuilder();
                foreach (var segment in first.EnumerateArray())
                {
                    if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0 && segment[0].ValueKind == JsonValueKind.String)
                    {
                        sb.Append(segment[0].GetString());
                    }
                }
                text = sb.ToString();
            }
            else
            {
                // 指定了源语言时是 ["译文"]，自动识别语言时是 [["译文", "en"]]。
                text = first.ValueKind switch
                {
                    JsonValueKind.String => first.GetString() ?? "",
                    JsonValueKind.Array when first.GetArrayLength() > 0 && first[0].ValueKind == JsonValueKind.String => first[0].GetString() ?? "",
                    _ => "",
                };
            }
        }
        catch (JsonException e)
        {
            throw new ParseException(e.Message);
        }
        if (text.Length == 0) throw new ParseException("empty");
        return text.Trim();
    }
}

/// <summary>
/// 两个 Google 接口轮换（与 macOS 的 GoogleEndpointRotation 相同）：被限流的接口冷却一段时间再用；都失败时等一会儿再试，
/// 等待时间按连续失败次数翻倍。（其他项目只是报错或让用户换接口，2026-10-22 调研 LiveCaptions-Translator、Easydict、py-googletrans、pot-desktop。）
/// 不是线程安全的，调用方加锁。
/// </summary>
public sealed class GoogleEndpointRotation
{
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(4);
    private readonly Dictionary<GoogleEndpoint, DateTime> coolingUntil = [];
    private int consecutiveFailures;

    /// <summary>这一次按什么顺序试：没在冷却的在前（gtx 优先），冷却中的按谁先结束排在后面（都在冷却时也只能试它们）。</summary>
    public List<GoogleEndpoint> Order(DateTime now)
    {
        bool Ready(GoogleEndpoint e) => !coolingUntil.TryGetValue(e, out var until) || until <= now;
        var ready = GoogleTranslate.Endpoints.Where(Ready);
        var cooling = GoogleTranslate.Endpoints.Where(e => !Ready(e)).OrderBy(e => coolingUntil[e]);
        return ready.Concat(cooling).ToList();
    }

    public void MarkLimited(GoogleEndpoint endpoint, DateTime now) => coolingUntil[endpoint] = now + Cooldown;

    public void MarkSuccess(GoogleEndpoint endpoint)
    {
        coolingUntil.Remove(endpoint);
        consecutiveFailures = 0;
    }

    /// <summary>所有接口都失败了一轮：返回重试前要等多久（1、2、4 秒，之后一直是 4 秒）。</summary>
    public TimeSpan NextBackoff()
    {
        consecutiveFailures += 1;
        var seconds = Math.Pow(2, consecutiveFailures - 1);
        return TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, seconds));
    }
}
