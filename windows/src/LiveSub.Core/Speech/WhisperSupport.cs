using System.Text;
using System.Text.RegularExpressions;

namespace LiveSub.Core.Speech;

/// <summary>Whisper ggml 模型：大小与下载地址。</summary>
public sealed record WhisperModel(string Id, int ApproxMegabytes)
{
    public static readonly WhisperModel Tiny = new("tiny", 75);
    public static readonly WhisperModel Base = new("base", 142);
    public static readonly WhisperModel Small = new("small", 466);

    public static readonly IReadOnlyList<WhisperModel> All = [Tiny, Base, Small];

    /// <summary>默认 base：普通笔记本上能实时，准确度比 tiny 好很多。</summary>
    public static WhisperModel Default => Base;

    public static WhisperModel Find(string id) => All.FirstOrDefault(m => m.Id == id) ?? Default;

    public string FileName => $"ggml-{Id}.bin";

    public const string DefaultHost = "https://huggingface.co";

    public Uri DownloadUrl => DownloadUrlFrom(null);

    /// <summary>从指定站点下载（例如 Hugging Face 的镜像）；为空时用 huggingface.co。</summary>
    public Uri DownloadUrlFrom(string? host)
    {
        var h = string.IsNullOrWhiteSpace(host) ? DefaultHost : host.Trim().TrimEnd('/');
        return new Uri($"{h}/ggerganov/whisper.cpp/resolve/main/{FileName}");
    }

    /// <summary>下载完成前用的临时文件名。</summary>
    public string PartialFileName => FileName + ".part";

    /// <summary>文件大小明显不对（下载被截断）时不当作已下载。</summary>
    public bool LooksComplete(long fileBytes) => fileBytes > ApproxMegabytes * 1_000_000L * 0.9;
}

/// <summary>Whisper 输出的一个词元：文字与概率。</summary>
public readonly record struct TokenProbability(string Text, float Probability);

/// <summary>识别文字的清理：去掉非语音标注和 Whisper 在静音、噪音上常见的“幻觉”句子。</summary>
public static class RecognitionText
{
    /// <summary>Whisper 在静音、音乐上常编出来的句子（统一小写、去掉标点后比较）。</summary>
    private static readonly HashSet<string> Hallucinations =
    [
        "thank you", "thank you for watching", "thanks for watching", "thank you so much for watching",
        "please subscribe", "please subscribe to my channel", "like and subscribe", "see you next time",
        "subtitles by the amaraorg community", "transcription by castingwords", "you",
        "字幕由amaraorg社区提供", "请不吝点赞订阅转发打赏支持明镜与点点栏目", "谢谢观看", "感谢观看", "请订阅",
        "ご視聴ありがとうございました", "시청해주셔서 감사합니다",
        "sous-titres réalisés para la communauté damaraorg", "untertitel der amaraorg-community",
        "untertitel im auftrag des zdf für funkde",
    ];

    /// <summary>[Music]、(laughs)、♪…♪、*sigh* 这类非语音标注。</summary>
    private static readonly Regex Annotations = new(@"\[[^\]]*\]|\([^\)]*\)|（[^）]*）|♪[^♪]*♪|♪|\*[^*]*\*", RegexOptions.Compiled);

    /// <summary>同一个词连续出现 5 次以上（Whisper 卡住时的循环）。</summary>
    private static readonly Regex Repeats = new(@"\b(\w+)(?:[\s,]+\1\b){4,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>返回清理后的文字；整段都是标注或幻觉时返回 null。</summary>
    public static string? Clean(string text)
    {
        var t = Annotations.Replace(text, " ");
        t = Repeats.Replace(t, "$1");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        if (t.Length == 0) return null;
        var key = Normalize(t);
        if (key.Length == 0 || Hallucinations.Contains(key)) return null;
        return t;
    }

    private static string Normalize(string text)
    {
        var sb = new StringBuilder();
        foreach (var r in text.ToLowerInvariant().EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(r) || r.Value == '-') sb.Append(r.ToString());
            else if (Rune.IsWhiteSpace(r) && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 把 Whisper 的词元概率合成逐词置信度（与 macOS 识别器的逐词置信度对应）：
    /// 跳过特殊词元（[_BEG_]、&lt;|en|&gt;、时间戳）和纯标点；以空格开头的词元开始一个新词；一个词的置信度取其词元的平均值。
    /// </summary>
    public static List<double> WordConfidences(IEnumerable<TokenProbability> tokens)
    {
        var words = new List<double>();
        double sum = 0;
        int count = 0;
        foreach (var token in tokens)
        {
            var text = token.Text;
            if (text.Length == 0 || text.StartsWith("[_") || text.StartsWith("<|")) continue;
            if (!text.Any(char.IsLetterOrDigit)) continue;
            bool startsWord = text[0] == ' ' || count == 0 || ContainsCJK(text);
            if (startsWord && count > 0)
            {
                words.Add(sum / count);
                sum = 0;
                count = 0;
            }
            sum += Math.Clamp(token.Probability, 0f, 1f);
            count++;
        }
        if (count > 0) words.Add(sum / count);
        return words;
    }

    private static bool ContainsCJK(string s) => s.EnumerateRunes().Any(r => TextUtil.IsCJK(r.Value));

    /// <summary>
    /// 过滤周围的中文交谈：讲话语言不是中文、而这一段被判断为中文（在“讲话语言/中文”两者里选）时丢掉。
    /// </summary>
    public static bool IsForeignChinese(string? detectedLanguage, float probability, string sourceWhisperCode) =>
        sourceWhisperCode != "zh" && detectedLanguage == "zh" && probability >= 0.5f;
}
