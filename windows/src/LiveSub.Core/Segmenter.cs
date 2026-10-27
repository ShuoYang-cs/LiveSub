using System.Text;

namespace LiveSub.Core;

/// <summary>
/// 把语音识别的“确认结果”切成适合翻译的句子片段。
///
/// 规则：
/// - 完整句子（以 . ? ! 。？！ 等结尾）且长度达到 <see cref="Config.MinUnits"/> 立即送出；过短的句子与后续合并，减少请求次数。
/// - 未完成的句子超过 <see cref="Config.MaxUnits"/> 时，在最后一个逗号/分号处切开（找不到就硬切），避免一句话等太久。
/// - 一段时间没有新的确认结果（讲者停顿）时，把剩余内容全部送出。
/// </summary>
public sealed class Segmenter
{
    public sealed record Config
    {
        /// <summary>句子最少多少“单位”才单独送出（英文按单词，中日韩按每 2 个字算 1 个单位）。</summary>
        public int MinUnits { get; init; } = 4;
        /// <summary>未完成句子的最大长度，超过就强制切分（与 macOS 相同，2026-10-22 从 32 降到 24：一条字幕不要太长）。</summary>
        public int MaxUnits { get; init; } = 24;
        /// <summary>距离上一次确认结果多久后，把剩余内容送出。</summary>
        public TimeSpan IdleFlush { get; init; } = TimeSpan.FromSeconds(1.6);

        public Config() { }

        public Config(int minUnits, int maxUnits, double idleFlushSeconds)
        {
            MinUnits = minUnits;
            MaxUnits = maxUnits;
            IdleFlush = TimeSpan.FromSeconds(idleFlushSeconds);
        }
    }

    public Config Settings { get; set; }
    public string Pending { get; private set; } = "";
    private DateTime? lastFinalAt;

    public Segmenter(Config? config = null)
    {
        Settings = config ?? new Config();
    }

    /// <summary>送入一段确认的识别文本，返回可以送去翻译的片段。</summary>
    public List<string> AddFinal(string text, DateTime? now = null)
    {
        var trimmed = CollapseRepeats(text.Trim());
        if (trimmed.Length == 0) return [];
        Pending = Join(Pending, trimmed);
        lastFinalAt = now ?? DateTime.UtcNow;
        return DrainCompleteSentences();
    }

    /// <summary>
    /// 同一个词连着出现 3 次以上（“Okay, okay, okay, okay.”“to, to, to, to”）只留前 2 个：多人讨论、音质差时识别器常常这样重复，
    /// 翻译出来是“好，好，好，好”，没有信息。结尾的标点跟着最后一个重复（与 macOS 相同）。
    /// </summary>
    public static string CollapseRepeats(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        static string Key(string t) => new(t.Where(char.IsLetterOrDigit).ToArray());
        static string Trailing(string t)
        {
            int end = t.Length;
            while (end > 0 && !char.IsLetterOrDigit(t[end - 1])) end--;
            return t[end..];
        }
        var output = new List<string>();
        int i = 0;
        while (i < tokens.Length)
        {
            int j = i + 1;
            var key = Key(tokens[i]).ToLowerInvariant();
            while (key.Length > 0 && j < tokens.Length && Key(tokens[j]).ToLowerInvariant() == key) j++;
            if (j - i >= 3)
            {
                output.Add(tokens[i]);
                var second = tokens[i + 1];
                second = second[..(second.Length - Trailing(second).Length)];
                output.Add(second + Trailing(tokens[j - 1]));
            }
            else
            {
                for (int k = i; k < j; k++) output.Add(tokens[k]);
            }
            i = j;
        }
        return string.Join(" ", output);
    }

    /// <summary>定时调用；<paramref name="speechInProgress"/> 为 true 表示当前还有未确认的语音，此时不做超时送出。</summary>
    public List<string> Tick(DateTime? now, bool speechInProgress)
    {
        if (Pending.Length == 0 || lastFinalAt is not { } last) return [];
        var idle = (now ?? DateTime.UtcNow) - last;
        // 有语音仍在识别时多等一会儿，但不能无限等（讲者一直讲不停）。
        var limit = speechInProgress ? Settings.IdleFlush * 3 : Settings.IdleFlush;
        if (idle < limit) return [];
        return FlushAll();
    }

    /// <summary>全部送出；太长时同样在从句边界切成几条，字幕不会一条占满整个窗口（与 macOS 相同）。</summary>
    public List<string> FlushAll()
    {
        var rest = Pending.Trim();
        Pending = "";
        lastFinalAt = null;
        var output = new List<string>();
        while (Units(rest) > Settings.MaxUnits)
        {
            var (head, tail) = SplitLong(rest, Settings.MaxUnits);
            if (head.Length == 0 || tail.Length == 0) break;
            output.Add(head);
            rest = tail;
        }
        if (rest.Length > 0) output.Add(rest);
        return output;
    }

    // MARK: - 内部

    private List<string> DrainCompleteSentences()
    {
        var output = new List<string>();
        var sentences = SplitSentences(Pending);
        // 最后一个片段如果没有句末标点，则保留为未完成部分。
        var tail = "";
        if (sentences.Count > 0 && !EndsSentence(sentences[^1]))
        {
            tail = sentences[^1];
            sentences.RemoveAt(sentences.Count - 1);
        }

        var carry = "";
        foreach (var sentence in sentences)
        {
            carry = Join(carry, sentence);
            if (Units(carry) >= Settings.MinUnits)
            {
                // 识别器常把好几句用逗号连成一个长句，字幕太长不好读，在逗号处再切。
                while (Units(carry) > Settings.MaxUnits)
                {
                    var (head, rest) = SplitLong(carry, Settings.MaxUnits);
                    if (head.Length == 0 || rest.Length == 0) break;
                    output.Add(head);
                    carry = rest;
                }
                output.Add(carry);
                carry = "";
            }
        }
        // 过短的完整句并入未完成部分，等后续内容一起送出。
        tail = Join(carry, tail);

        while (Units(tail) > Settings.MaxUnits)
        {
            var (head, rest) = SplitLong(tail, Settings.MaxUnits);
            // 保险：切不动时停下，避免死循环（正常输入不会走到这里）。
            if (head.Length == 0 || rest.Length >= tail.Length) break;
            output.Add(head);
            tail = rest;
        }
        Pending = tail;
        return output;
    }

    public static string Join(string a, string b)
    {
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        // 中日韩文字之间不加空格。
        if (TextUtil.IsCJK(a[^1]) || TextUtil.IsCJK(b[0])) return a + b;
        return a + " " + b;
    }

    internal static readonly HashSet<char> Terminators = ['.', '?', '!', '。', '？', '！', '…'];
    internal static readonly HashSet<char> Closers = ['"', '\'', '”', '’', ')', '）', '」', '』'];
    internal static readonly HashSet<string> Abbreviations =
    [
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc", "e.g", "i.e", "fig", "eq", "no", "approx", "inc", "ltd", "co", "u.s", "u.k", "a.m", "p.m",
    ];
    private static readonly HashSet<char> BreakChars = [',', ';', ':', '，', '；', '：', '、'];

    internal static bool EndsSentence(string s)
    {
        var t = TextUtil.TrimSpaces(s);
        while (t.Length > 0 && Closers.Contains(t[^1])) t = t[..^1];
        if (t.Length == 0 || !Terminators.Contains(t[^1])) return false;
        return !(t[^1] == '.' && EndsWithAbbreviation(t));
    }

    internal static bool EndsWithAbbreviation(string t)
    {
        // t 以 "." 结尾；取最后一个词（去掉末尾句点）判断。
        var word = t[..^1].Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.ToLowerInvariant() ?? "";
        // 不把单个字母当缩写：课堂上 "equals lambda v." 这种以变量结尾的句子远比人名缩写常见，
        // 而误切出的 "J." 太短会被并入下一句，代价很小。
        return Abbreviations.Contains(word);
    }

    /// <summary>按句末标点切分，标点保留在句子末尾。小数点（3.14）和缩写不切。</summary>
    internal static List<string> SplitSentences(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            current.Append(c);
            if (Terminators.Contains(c))
            {
                // 吞掉连续标点和右引号/括号
                while (i + 1 < text.Length && (Terminators.Contains(text[i + 1]) || Closers.Contains(text[i + 1])))
                {
                    i += 1;
                    current.Append(text[i]);
                }
                char? next = i + 1 < text.Length ? text[i + 1] : null;
                bool isCJKStop = c is '。' or '？' or '！';
                bool boundary = next is null || char.IsWhiteSpace(next.Value) || isCJKStop;
                if (boundary && !(c == '.' && EndsWithAbbreviation(TextUtil.TrimSpaces(current.ToString()))))
                {
                    var s = TextUtil.TrimSpaces(current.ToString());
                    if (s.Length > 0) result.Add(s);
                    current.Clear();
                }
            }
            i += 1;
        }
        var rest = TextUtil.TrimSpaces(current.ToString());
        if (rest.Length > 0) result.Add(rest);
        return result;
    }

    /// <summary>把过长的未完成句在 maxUnits 之内的最后一个从句边界处切开。</summary>
    internal static (string Head, string Remainder) SplitLong(string text, int maxUnits)
    {
        double unitsSoFar = 0;
        int? lastBreak = null;
        int hardCut = text.Length;
        bool inWord = false;
        for (int idx = 0; idx < text.Length; idx++)
        {
            char c = text[idx];
            if (TextUtil.IsCJK(c))
            {
                unitsSoFar += 0.5;
                inWord = false;
            }
            else if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                unitsSoFar += 1;
            }
            if ((int)unitsSoFar > maxUnits)
            {
                hardCut = idx;
                break;
            }
            if (BreakChars.Contains(c) && maxUnits * 0.3 < unitsSoFar)
            {
                lastBreak = idx + 1;
            }
        }
        int cut = lastBreak ?? hardCut;
        if (lastBreak is null)
        {
            // 硬切时退回到上一个空格，避免切断单词。
            int j = cut;
            while (j > 0 && !char.IsWhiteSpace(text[j - 1]) && !TextUtil.IsCJK(text[j - 1])) j -= 1;
            if (j > 0) cut = j;
        }
        var head = TextUtil.TrimSpaces(text[..cut]);
        var rest = TextUtil.TrimSpaces(text[cut..]);
        return (head, rest);
    }

    /// <summary>长度单位：英文等按单词计；中日韩每 2 个字计 1 个单位。</summary>
    public static int Units(string text)
    {
        int cjk = 0, words = 0;
        bool inWord = false;
        foreach (var r in text.EnumerateRunes())
        {
            if (TextUtil.IsCJK(r.Value))
            {
                cjk += 1;
                inWord = false;
            }
            else if (TextUtil.IsAlphanumeric(r))
            {
                if (!inWord) words += 1;
                inWord = true;
            }
            else
            {
                inWord = false;
            }
        }
        return words + (cjk + 1) / 2;
    }
}
