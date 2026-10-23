using System.Globalization;
using System.Text;

namespace LiveSub.Core;

/// <summary>
/// 与 Swift 版行为一致的字符串小工具。
/// Swift 的 <c>.whitespaces</c> 只含空格类字符和制表符，不含换行；<c>.whitespacesAndNewlines</c> 才含换行。
/// C# 的 <c>Trim()</c> 相当于后者，这里补上前者。
/// </summary>
public static class TextUtil
{
    /// <summary>Swift <c>CharacterSet.whitespaces</c>：空格分隔符（Zs）和制表符。</summary>
    public static bool IsSpace(char c) =>
        c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    /// <summary>去掉首尾空格和制表符，保留换行（对应 Swift <c>trimmingCharacters(in: .whitespaces)</c>）。</summary>
    public static string TrimSpaces(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsSpace(s[start])) start++;
        while (end > start && IsSpace(s[end - 1])) end--;
        return start == 0 && end == s.Length ? s : s[start..end];
    }

    /// <summary>按换行切成行，丢掉空行（对应 Swift <c>split(whereSeparator: \.isNewline)</c>）。</summary>
    public static string[] SplitLines(string s) =>
        s.Split(NewlineChars, StringSplitOptions.RemoveEmptyEntries);

    private static readonly char[] NewlineChars = ['\n', '\r', '\u000B', '\u000C', '\u0085', '\u2028', '\u2029'];

    /// <summary>中日韩文字（含全角标点），用于分句、计算长度和拼接时是否加空格。</summary>
    public static bool IsCJK(int scalar) => scalar switch
    {
        >= 0x3040 and <= 0x30FF => true,
        >= 0x3400 and <= 0x4DBF => true,
        >= 0x4E00 and <= 0x9FFF => true,
        >= 0xAC00 and <= 0xD7AF => true,
        >= 0xF900 and <= 0xFAFF => true,
        >= 0xFF01 and <= 0xFF60 => true,
        _ => false,
    };

    /// <summary>字符数（按用户看到的字符计，与 Swift <c>String.count</c> 一致）。</summary>
    public static int CharacterCount(string s) => new StringInfo(s).LengthInTextElements;

    /// <summary>Foundation <c>CharacterSet.alphanumerics</c>：字母、组合记号、数字。</summary>
    public static bool IsAlphanumeric(Rune r) => Rune.GetUnicodeCategory(r) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        _ => false,
    };

    /// <summary>
    /// 文件大小，按十进制单位（和 macOS 的 ByteCountFormatter、资源管理器以外的大多数下载界面一样）：
    /// 不到 1 MB 写 KB，不到 1 GB 写整数 MB，再大写一位小数的 GB（“37 MB”“1.2 GB”）。
    /// </summary>
    public static string FileSize(long bytes, IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        if (bytes >= 1_000_000_000) return string.Format(provider, "{0:0.0} GB", bytes / 1e9);
        if (bytes >= 1_000_000) return string.Format(provider, "{0:0} MB", bytes / 1e6);
        return string.Format(provider, "{0:0} KB", Math.Max(0, bytes) / 1e3);
    }
}
