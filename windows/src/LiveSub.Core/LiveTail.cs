using System.Globalization;
using System.Text;

namespace LiveSub.Core;

/// <summary>
/// “正在说”只占一行：太长时省略前面、显示最新的部分，前面加“…”（macOS 用 truncationMode(.head)，WPF 没有从头省略，自己算）。
/// 有空格分词的语言只在词的开头截断，不切开单词；中日韩文字在任意两字之间都可以截断。
/// 截断后的文字不以空格或标点开头。
/// </summary>
public static class LiveTail
{
    public const string Ellipsis = "…";

    /// <param name="maxWidth">一行的宽度；还没排版（0 或负数）时原样返回。</param>
    /// <param name="measure">文字的显示宽度，与 <paramref name="maxWidth"/> 同一单位；文字越长越宽。</param>
    public static string Fit(string text, double maxWidth, Func<string, double> measure)
    {
        if (text.Length == 0 || maxWidth <= 0 || measure(text) <= maxWidth) return text;
        var trimmed = text.TrimEnd();
        // 只是末尾的空格放不下：去掉空格后放得下就不用截断。
        if (measure(trimmed) <= maxWidth) return trimmed;
        int[] elements = StringInfo.ParseCombiningCharacters(trimmed);
        if (elements.Length == 0) return text;
        // 最后一个词本身就放不下（很长的单词、网址）时按字截断；连最后一个字都放不下时只能显示它，多出的部分由界面裁掉。
        int start = Longest(trimmed, WordStarts(trimmed, elements), maxWidth, measure)
            ?? Longest(trimmed, CharacterStarts(trimmed, elements), maxWidth, measure)
            ?? elements[^1];
        return Ellipsis + trimmed[start..];
    }

    /// <summary>可以截断的位置（截断后文字的开头），从前往后：词的开头（前面是空格），以及中日韩文字的前后。</summary>
    internal static List<int> WordStarts(string text, int[] elements)
    {
        var starts = new List<int>();
        for (int k = 1; k < elements.Length; k++)
        {
            int i = elements[k], previous = elements[k - 1];
            if (!CanStart(text, i)) continue;
            if (TextUtil.IsSpace(text[previous]) || IsCJK(text, i) || IsCJK(text, previous)) starts.Add(i);
        }
        return starts;
    }

    /// <summary>按字截断时的位置：每个字（字素）的开头。</summary>
    private static List<int> CharacterStarts(string text, int[] elements)
    {
        var starts = new List<int>();
        for (int k = 1; k < elements.Length; k++)
        {
            if (CanStart(text, elements[k])) starts.Add(elements[k]);
        }
        return starts;
    }

    private static bool CanStart(string text, int index) =>
        !char.IsWhiteSpace(text[index]) && !char.IsPunctuation(text[index]);

    private static bool IsCJK(string text, int index) =>
        Rune.TryGetRuneAt(text, index, out var rune) && TextUtil.IsCJK(rune.Value);

    /// <summary>
    /// 在 <paramref name="starts"/>（从前往后）里找最靠前、加上“…”后放得下的位置。
    /// 截断得越靠后越窄，所以可以二分查找。都放不下时为 null。
    /// </summary>
    private static int? Longest(string text, List<int> starts, double maxWidth, Func<string, double> measure)
    {
        int low = 0, high = starts.Count - 1, found = -1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (measure(Ellipsis + text[starts[mid]..]) <= maxWidth)
            {
                found = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }
        return found < 0 ? null : starts[found];
    }
}
