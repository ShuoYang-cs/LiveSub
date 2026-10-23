using System.Globalization;
using System.Text.Json;
using System.Web;
using LiveSub.Core.Translation;

namespace LiveSub.Core.Tests;

// 与 macOS 版 Tests/LiveSubCoreTests/GoogleTranslateTests.swift 相同的输入与期望输出。
public class GoogleTranslateTests
{
    [Fact]
    public void LanguageCodes()
    {
        Assert.Equal("zh-CN", GoogleTranslate.LanguageCode("zh-Hans"));
        Assert.Equal("zh-TW", GoogleTranslate.LanguageCode("zh-Hant"));
        Assert.Equal("en", GoogleTranslate.LanguageCode("en"));
    }

    [Fact]
    public void RequestUrlEscapesText()
    {
        var url = GoogleTranslate.RequestUrl("1 + 1 = 2 & more?", "en", "zh-Hans");
        var query = HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("1 + 1 = 2 & more?", query["q"]);
        Assert.Equal("zh-CN", query["tl"]);
        Assert.Equal("en", query["sl"]);
        Assert.Equal("gtx", query["client"]);
        Assert.Contains("%2B", url.AbsoluteUri);
        Assert.StartsWith("https://translate.googleapis.com/translate_a/single?", url.AbsoluteUri);
    }

    [Fact]
    public void ParseJoinsSegments()
    {
        var json = """[[["你好。","Hello.",null,null,10],["世界","World",null,null,10]],null,"en"]""";
        Assert.Equal("你好。世界", GoogleTranslate.Parse(json));
    }

    [Fact]
    public void DictionaryExtensionUrlAndReplies()
    {
        var url = GoogleTranslate.RequestUrl("Hi", "en", "zh-Hans", GoogleEndpoint.DictionaryExtension);
        Assert.StartsWith("https://clients5.google.com/translate_a/t?client=dict-chrome-ex", url.AbsoluteUri);
        Assert.Contains("tl=zh-CN", url.AbsoluteUri);
        Assert.Equal("大家好。你今天怎么样？", GoogleTranslate.Parse("""["大家好。你今天怎么样？"]""", GoogleEndpoint.DictionaryExtension));
        Assert.Equal("大家好。", GoogleTranslate.Parse("""[["大家好。","en"]]""", GoogleEndpoint.DictionaryExtension));
        Assert.Throws<GoogleTranslate.ParseException>(() => GoogleTranslate.Parse("[]", GoogleEndpoint.DictionaryExtension));
    }

    [Fact]
    public void TemporaryFailures()
    {
        Assert.True(GoogleTranslate.IsTemporaryFailure(429));
        Assert.True(GoogleTranslate.IsTemporaryFailure(503));
        Assert.False(GoogleTranslate.IsTemporaryFailure(400));
    }

    [Fact]
    public void RotationSkipsLimitedEndpointUntilCooldownEnds()
    {
        var r = new GoogleEndpointRotation();
        var t0 = DateTime.UnixEpoch.AddSeconds(1000);
        Assert.Equal([GoogleEndpoint.Gtx, GoogleEndpoint.DictionaryExtension], r.Order(t0));
        r.MarkLimited(GoogleEndpoint.Gtx, t0);
        Assert.Equal([GoogleEndpoint.DictionaryExtension, GoogleEndpoint.Gtx], r.Order(t0.AddSeconds(30)));
        Assert.Equal([GoogleEndpoint.Gtx, GoogleEndpoint.DictionaryExtension], r.Order(t0.AddSeconds(61)));
        // 两个都在冷却：先结束冷却的排前面。
        r.MarkLimited(GoogleEndpoint.DictionaryExtension, t0.AddSeconds(10));
        Assert.Equal([GoogleEndpoint.Gtx, GoogleEndpoint.DictionaryExtension], r.Order(t0.AddSeconds(20)));
        r.MarkSuccess(GoogleEndpoint.Gtx);
        Assert.Equal([GoogleEndpoint.Gtx, GoogleEndpoint.DictionaryExtension], r.Order(t0.AddSeconds(20)));
    }

    [Fact]
    public void BackoffDoublesUpToFourSecondsAndResets()
    {
        var r = new GoogleEndpointRotation();
        Assert.Equal([1.0, 2, 4, 4], new[] { r.NextBackoff(), r.NextBackoff(), r.NextBackoff(), r.NextBackoff() }.Select(t => t.TotalSeconds));
        r.MarkSuccess(GoogleEndpoint.Gtx);
        Assert.Equal(1, r.NextBackoff().TotalSeconds);
    }

    [Fact]
    public void ParseRejectsUnexpectedReply()
    {
        Assert.Throws<GoogleTranslate.ParseException>(() => GoogleTranslate.Parse("{}"));
        Assert.Throws<GoogleTranslate.ParseException>(() => GoogleTranslate.Parse("[[]]"));
        Assert.Throws<GoogleTranslate.ParseException>(() => GoogleTranslate.Parse("<html>"));
    }
}

// 与 macOS 版 Tests/LiveSubCoreTests/LocalTranslationTests.swift 相同的输入与期望输出；运行程序和下载地址是 Windows 版的。
public class LocalTranslationTests
{
    private static readonly LanguageOption English = LanguageOption.Source("en-US");
    private static readonly LanguageOption Japanese = LanguageOption.Source("ja-JP");

    [Fact]
    public void ChinesePromptWithoutContext()
    {
        var prompt = LocalTranslationModel.Prompt("Hello.", [], English, LanguageOption.Target("zh-Hans"));
        Assert.Equal("将以下文本翻译为中文，注意只需要输出翻译后的结果，不要额外解释：\n\nHello.", prompt);
    }

    [Fact]
    public void ChinesePromptUsesLastTwoLinesAsBackground()
    {
        var prompt = LocalTranslationModel.Prompt("C.", ["A.", "B1.", "B2."], English, LanguageOption.Target("zh-Hant"));
        Assert.Equal("【背景信息】\nB1.\nB2.\n\n请结合背景信息将以下文本翻译为繁体中文。\n\n【待翻译文本】\nC.", prompt);
    }

    [Fact]
    public void EnglishPromptWhenNoChineseInvolved()
    {
        var plain = LocalTranslationModel.Prompt("こんにちは。", [], Japanese, LanguageOption.Target("en"));
        Assert.StartsWith("Translate the following text into English. Note that", plain);
        Assert.EndsWith("\n\nこんにちは。", plain);
        var withContext = LocalTranslationModel.Prompt("x", ["y"], Japanese, LanguageOption.Target("ko"));
        Assert.Equal("[Background Information]\ny\n\nPlease translate the following text into Korean, taking the provided background information into consideration.\n\n[Source Text]\nx", withContext);
    }

    [Fact]
    public void EveryTargetLanguageHasAName()
    {
        foreach (var target in LanguageOption.Targets)
        {
            Assert.NotEqual(target.TranslationCode, LocalTranslationModel.LanguageName(target.TranslationCode, chinese: true));
            Assert.NotEqual(target.TranslationCode, LocalTranslationModel.LanguageName(target.TranslationCode, chinese: false));
        }
    }

    [Fact]
    public void RequestBodyHasOneUserMessageAndSampling()
    {
        using var doc = JsonDocument.Parse(LocalTranslationModel.RequestBody("p"));
        var body = doc.RootElement;
        var message = Assert.Single(body.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("p", message.GetProperty("content").GetString());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.Equal(20, body.GetProperty("top_k").GetInt32());
        Assert.Equal(0.7, body.GetProperty("temperature").GetDouble());
        Assert.Equal(0.6, body.GetProperty("top_p").GetDouble());
        Assert.Equal(1.05, body.GetProperty("repeat_penalty").GetDouble());
        Assert.Equal(512, body.GetProperty("max_tokens").GetInt32());
        Assert.False(body.TryGetProperty("model", out _));
    }

    [Fact]
    public void ModelFileMatchesMacOS()
    {
        Assert.Equal("Hy-MT2-1.8B-Q4_K_M.gguf", LocalTranslationModel.FileName);
        Assert.Equal(1_133_080_448, LocalTranslationModel.Bytes);
        Assert.Equal("dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699", LocalTranslationModel.Sha256);
    }

    [Fact]
    public void RuntimeMatchesArchitecture()
    {
        var x64 = LocalTranslationModel.Runtime(arm64: false);
        var arm64 = LocalTranslationModel.Runtime(arm64: true);
        Assert.Equal("https://github.com/ggml-org/llama.cpp/releases/download/b11829/llama-b11829-bin-win-vulkan-x64.zip", x64.Url.AbsoluteUri);
        Assert.EndsWith("win-cpu-arm64.zip", arm64.Url.AbsoluteUri);
        Assert.True(x64.UsesGpu);
        Assert.False(arm64.UsesGpu);
        Assert.Equal("llama-b11829", x64.Folder);
        Assert.All(new[] { x64, arm64 }, r => Assert.Equal(64, r.Sha256.Length));
    }

    [Fact]
    public void DownloadUrlsTryTheMirrorSettingFirst()
    {
        const string path = "/tencent/Hy-MT2-1.8B-GGUF/resolve/main/Hy-MT2-1.8B-Q4_K_M.gguf";
        Assert.Equal(["https://huggingface.co" + path, "https://hf-mirror.com" + path],
                     LocalTranslationModel.DownloadUrls(null).Select(u => u.AbsoluteUri));
        Assert.Equal(["https://mirror.example.com" + path, "https://huggingface.co" + path, "https://hf-mirror.com" + path],
                     LocalTranslationModel.DownloadUrls(" https://mirror.example.com/ ").Select(u => u.AbsoluteUri));
        // 填的就是 hf-mirror.com：不重复。
        Assert.Equal(["https://hf-mirror.com" + path, "https://huggingface.co" + path],
                     LocalTranslationModel.DownloadUrls("https://hf-mirror.com").Select(u => u.AbsoluteUri));
    }

    [Fact]
    public void ServerArgumentsMatchMacOS()
    {
        Assert.Equal(["-m", @"C:\m.gguf", "--host", "127.0.0.1", "--port", "8123", "-c", "4096", "-np", "2", "--jinja", "--no-webui", "-ngl", "99"],
                     LocalTranslationModel.ServerArguments(@"C:\m.gguf", 8123, useGpu: true));
        Assert.Equal("0", LocalTranslationModel.ServerArguments("m", 1, useGpu: false)[^1]);
    }

    [Fact]
    public void FileSizes()
    {
        var c = CultureInfo.InvariantCulture;
        Assert.Equal("1.2 GB", TextUtil.FileSize(1_169_956_990, c));
        Assert.Equal("37 MB", TextUtil.FileSize(36_876_542, c));
        Assert.Equal("512 KB", TextUtil.FileSize(512_000, c));
        Assert.Equal("0 KB", TextUtil.FileSize(0, c));
    }
}
