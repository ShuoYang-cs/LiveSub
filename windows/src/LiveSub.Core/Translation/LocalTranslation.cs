using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LiveSub.Core.Translation;

/// <summary>llama.cpp 运行程序（llama-server 和它的 dll）的一个 Windows 压缩包。</summary>
/// <param name="Folder">解压到模型文件夹下的哪个子文件夹（压缩包里的文件直接在根目录，没有外层文件夹）。</param>
/// <param name="UsesGpu">Vulkan 版可以用显卡，也带处理器后端（没有 Vulkan 驱动时用处理器）；ARM64 版只有处理器后端。</param>
public sealed record LocalRuntime(Uri Url, long Bytes, string Sha256, string Folder, bool UsesGpu)
{
    public const string ServerFileName = "llama-server.exe";
}

/// <summary>
/// 本机翻译模型：腾讯混元 Hy-MT2 1.8B（翻译专用，Apache-2.0，33 种语言），用 llama.cpp 的 llama-server 运行（与 macOS 的 LocalTranslationModel 相同）。
/// 2026-10-22 macOS 实测（M 系列芯片）：每句 0.2–0.3 秒翻完，占用约 1.4 GB 内存；同一时期的小模型里它体积最小、翻译最准。
/// Windows 上的速度还没实测。
/// </summary>
public static class LocalTranslationModel
{
    public const string Name = "Hy-MT2 1.8B";
    public const string FileName = "Hy-MT2-1.8B-Q4_K_M.gguf";
    public const long Bytes = 1_133_080_448;
    public const string Sha256 = "dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699";

    private const string HuggingFace = "https://huggingface.co";
    private const string HfMirror = "https://hf-mirror.com";
    private const string ModelPath = "/tencent/Hy-MT2-1.8B-GGUF/resolve/main/" + FileName;

    /// <summary>
    /// 依次尝试的下载地址：设置里填了“模型下载地址”时先用它（和 Whisper 模型一样），再试 Hugging Face，
    /// 打不开时（例如在中国大陆）换 hf-mirror.com。下载后都按 <see cref="Sha256"/> 校验。
    /// </summary>
    public static List<Uri> DownloadUrls(string? mirror)
    {
        var hosts = new List<string>();
        if (!string.IsNullOrWhiteSpace(mirror)) hosts.Add(mirror.Trim().TrimEnd('/'));
        hosts.Add(HuggingFace);
        hosts.Add(HfMirror);
        return hosts.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(h => Uri.TryCreate(h + ModelPath, UriKind.Absolute, out var uri) ? uri : null)
            .OfType<Uri>()
            .ToList();
    }

    /// <summary>llama.cpp 的版本：和 macOS 一样固定用测过的 b11829，不跟着每天的新版本走。</summary>
    public const string RuntimeVersion = "b11829";

    /// <summary>
    /// 运行程序：x64 用 Vulkan 版（可以用显卡；也带 ggml-cpu-*.dll，没有 Vulkan 驱动时用处理器），ARM64 用处理器版。
    /// 字节数和 SHA-256 来自 GitHub 发布页的 digest，2026-10-23 下载核对过。
    /// </summary>
    public static LocalRuntime Runtime(bool arm64)
    {
        const string v = RuntimeVersion;
        var asset = arm64 ? $"llama-{v}-bin-win-cpu-arm64.zip" : $"llama-{v}-bin-win-vulkan-x64.zip";
        return new LocalRuntime(
            new Uri($"https://github.com/ggml-org/llama.cpp/releases/download/{v}/{asset}"),
            arm64 ? 12_702_107 : 36_876_542,
            arm64 ? "82efa8a2a55bc4b57895465bd95d2979822b9da13938f95577460a5d36828141"
                  : "839ff55ce9cbee6a099b403b640ca6b694ee5d74b815e39f77fc5d2b20962f0d",
            $"llama-{v}",
            UsesGpu: !arm64);
    }

    /// <summary>
    /// llama-server 的参数（和 macOS 相同）：只监听 127.0.0.1；两个槽位，Google、混元翻译都是同时翻两句；
    /// 用显卡时把全部层放上去（<c>-ngl 99</c>），否则 <c>-ngl 0</c>。
    /// </summary>
    public static List<string> ServerArguments(string modelPath, int port, bool useGpu) =>
    [
        "-m", modelPath, "--host", "127.0.0.1", "--port", port.ToString(CultureInfo.InvariantCulture),
        "-c", "4096", "-np", "2", "--jinja", "--no-webui", "-ngl", useGpu ? "99" : "0",
    ];

    /// <summary>
    /// 官方提示词：涉及中文时用中文提示词，否则用英文提示词，语言名写全称。
    /// 有上文时用“背景信息”模板，带上前两句原文（不需要译文），让代词、术语前后一致。
    /// </summary>
    public static string Prompt(string text, IReadOnlyList<string> context, LanguageOption source, LanguageOption target)
    {
        bool chinese = source.TranslationCode.StartsWith("zh", StringComparison.Ordinal)
                       || target.TranslationCode.StartsWith("zh", StringComparison.Ordinal);
        var language = LanguageName(target.TranslationCode, chinese);
        var background = string.Join("\n", context.TakeLast(2));
        return (chinese, background.Length == 0) switch
        {
            (true, true) => $"将以下文本翻译为{language}，注意只需要输出翻译后的结果，不要额外解释：\n\n{text}",
            (true, false) => $"【背景信息】\n{background}\n\n请结合背景信息将以下文本翻译为{language}。\n\n【待翻译文本】\n{text}",
            (false, true) => $"Translate the following text into {language}. Note that you should only output the translated result without any additional explanation:\n\n{text}",
            (false, false) => $"[Background Information]\n{background}\n\nPlease translate the following text into {language}, taking the provided background information into consideration.\n\n[Source Text]\n{text}",
        };
    }

    private static readonly Dictionary<string, (string Chinese, string English)> LanguageNames = new()
    {
        ["zh-Hans"] = ("中文", "Chinese"),
        ["zh-Hant"] = ("繁体中文", "Traditional Chinese"),
        ["en"] = ("英语", "English"),
        ["ja"] = ("日语", "Japanese"),
        ["ko"] = ("韩语", "Korean"),
        ["fr"] = ("法语", "French"),
        ["de"] = ("德语", "German"),
        ["es"] = ("西班牙语", "Spanish"),
    };

    /// <summary>模型说明里的语言名（简体中文就叫“中文”）。</summary>
    internal static string LanguageName(string code, bool chinese) =>
        LanguageNames.TryGetValue(code, out var name) ? (chinese ? name.Chinese : name.English) : code;

    /// <summary>
    /// 请求体：OpenAI 兼容格式，一条用户消息（模型没有系统提示词），流式输出；采样参数是官方为 1.8B 推荐的
    /// temperature 0.7、top_p 0.6、top_k 20、repeat_penalty 1.05，max_tokens 512。
    /// </summary>
    public static string RequestBody(string prompt)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("temperature", 0.7);
            w.WriteNumber("top_p", 0.6);
            w.WriteNumber("top_k", 20);
            w.WriteNumber("repeat_penalty", 1.05);
            w.WriteNumber("max_tokens", 512);
            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteString("content", prompt);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteBoolean("stream", true);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
