using System.IO.Compression;
using System.Text;
using System.Xml;

namespace LiveSub.Core.Chat;

/// <summary>AI 助手附件的上限（与 macOS 的 AttachmentLoader 相同）。</summary>
public static class AttachmentLimits
{
    /// <summary>图片长边缩到这么多像素：Claude 建议不超过约 1568，幻灯片上的字仍然看得清。</summary>
    public const int MaxImagePixels = 1568;
    /// <summary>Claude 读 PDF 原文件的上限是 100 页、32 MB；超过时只发提取出的文字。</summary>
    public const int MaxPdfPages = 100;
    public const int MaxPdfBytes = 20_000_000;
    public const long MaxFileBytes = 50_000_000;
    public const int MaxTextCharacters = 200_000;
    /// <summary>一次提问最多带这么多个附件。</summary>
    public const int MaxAttachments = 10;
}

/// <summary>按扩展名判断的附件种类（Windows 没有 macOS 的 UTType，按扩展名判断）。</summary>
public enum AttachmentKind
{
    Image,
    Pdf,
    /// <summary>Word 文档（.docx）。</summary>
    Word,
    /// <summary>OpenDocument 文本（.odt）。</summary>
    OpenDocument,
    Rtf,
    /// <summary>其他文件：能按文本读出来就当文本（代码、字幕文件等），不管扩展名认不认识。</summary>
    Other,
}

public static class AttachmentFiles
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif", ".ico", ".jxr", ".wdp",
    };

    public static AttachmentKind Classify(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (ImageExtensions.Contains(ext)) return AttachmentKind.Image;
        return ext.ToLowerInvariant() switch
        {
            ".pdf" => AttachmentKind.Pdf,
            ".docx" or ".docm" or ".dotx" => AttachmentKind.Word,
            ".odt" => AttachmentKind.OpenDocument,
            ".rtf" => AttachmentKind.Rtf,
            _ => AttachmentKind.Other,
        };
    }

    /// <summary>图片缩小后的像素大小：长边不超过 <paramref name="max"/>，保持宽高比；本来就不大时不变。</summary>
    public static (int Width, int Height) FitImage(int width, int height, int max = AttachmentLimits.MaxImagePixels)
    {
        int longEdge = Math.Max(width, height);
        if (width <= 0 || height <= 0 || longEdge <= max) return (width, height);
        double scale = (double)max / longEdge;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>
    /// 照片的方向信息（EXIF Orientation 1–8）→ 先水平镜像、再顺时针转多少度能转正
    /// （与 macOS 的 kCGImageSourceCreateThumbnailWithTransform 相同）。5、7 是转 90° 加镜像，手机前置摄像头的照片会有。
    /// </summary>
    public static (bool Mirror, int Rotation) Orientation(int exif) => exif switch
    {
        2 => (true, 0),
        3 => (false, 180),
        4 => (true, 180),
        5 => (true, 270),
        6 => (false, 90),
        7 => (true, 90),
        8 => (false, 270),
        _ => (false, 0),
    };

    /// <summary>看起来是 PDF：开头 1 KB 里有 “%PDF-”（规范允许前面有别的字节）。PdfPig 打不开时用来判断还要不要发原文件。</summary>
    public static bool LooksLikePdf(ReadOnlySpan<byte> data) => data[..Math.Min(data.Length, 1024)].IndexOf("%PDF-"u8) >= 0;

    /// <summary>
    /// 有透明部分的图片改存 JPEG 前合成到白底上（JPEG 没有透明通道，直接去掉的话透明的地方是黑的或乱的）。
    /// <paramref name="bgra"/> 是不预乘的 BGRA 像素，就地改成不透明。
    /// </summary>
    public static void FlattenOnWhite(Span<byte> bgra)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            int a = bgra[i + 3];
            if (a == 255) continue;
            for (int c = 0; c < 3; c++) bgra[i + c] = (byte)((bgra[i + c] * a + 255 * (255 - a) + 127) / 255);
            bgra[i + 3] = 255;
        }
    }

    /// <summary>
    /// 太长的文字只附上前面一部分，后面写明还有多少字没有附上（<paramref name="notice"/> 给出界面语言的说明）。
    /// </summary>
    public static string Truncate(string text, Func<int, string> notice, int max = AttachmentLimits.MaxTextCharacters)
    {
        if (text.Length <= max) return text;
        int cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..cut] + "\n" + notice(text.Length - cut);
    }

    /// <summary>
    /// 按文本读出文件内容：有 BOM 时按 BOM，否则先按 UTF-8；不是合法的 UTF-8 时用 <paramref name="fallback"/>
    /// （例如中文 Windows 的 GBK）。开头 8 KB 里有 NUL 字节的当作二进制文件，返回 null。
    /// </summary>
    public static string? DecodeText(byte[] data, Encoding? fallback = null)
    {
        var span = data.AsSpan();
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return Encoding.UTF8.GetString(span[3..]);
        if (span.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return Encoding.Unicode.GetString(span[2..]);
        if (span.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return Encoding.BigEndianUnicode.GetString(span[2..]);
        if (span[..Math.Min(8192, span.Length)].Contains((byte)0)) return null;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(span);
        }
        catch (DecoderFallbackException)
        {
            return fallback?.GetString(span);
        }
    }
}

/// <summary>
/// 从 Word（.docx）和 OpenDocument（.odt）文档里读出文字（macOS 用系统的文本框架读，Windows 没有现成的，直接读压缩包里的 XML）。
/// 每段一行，制表符和换行保留；图片、批注、页眉页脚不读。
/// </summary>
public static class OfficeText
{
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string OdfTextNamespace = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    /// <summary>.docx 的正文（word/document.xml）。不是 Word 文档时抛出 <see cref="InvalidDataException"/>。</summary>
    public static string FromDocx(Stream file)
    {
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException("word/document.xml");
        using var reader = XmlReader.Create(entry.Open(), Settings);
        var paragraphs = new List<string>();
        var current = new StringBuilder();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == WordNamespace)
            {
                switch (reader.LocalName)
                {
                    case "t":
                        // ReadElementContentAsString 已经读到下一个节点，不再 Read。
                        current.Append(reader.ReadElementContentAsString());
                        continue;
                    case "tab":
                        current.Append('\t');
                        break;
                    case "br":
                    case "cr":
                        current.Append('\n');
                        break;
                    case "p" when reader.IsEmptyElement:
                        paragraphs.Add("");
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.NamespaceURI == WordNamespace && reader.LocalName == "p")
            {
                paragraphs.Add(current.ToString());
                current.Clear();
            }
            reader.Read();
        }
        if (current.Length > 0) paragraphs.Add(current.ToString());
        return Join(paragraphs);
    }

    /// <summary>.odt 的正文（content.xml）：段落和标题，<c>text:s</c> 是连续的空格。不是 OpenDocument 时抛出 <see cref="InvalidDataException"/>。</summary>
    public static string FromOdt(Stream file)
    {
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.GetEntry("content.xml") ?? throw new InvalidDataException("content.xml");
        using var reader = XmlReader.Create(entry.Open(), Settings);
        var paragraphs = new List<string>();
        var current = new StringBuilder();
        // 正在读的段落（或标题）的层数：段落里可以套 span 等元素；只收段落里的文字。
        int depth = 0;
        while (reader.Read())
        {
            bool odf = reader.NamespaceURI == OdfTextNamespace;
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when odf && reader.LocalName is "p" or "h":
                    if (reader.IsEmptyElement)
                    {
                        if (depth == 0) paragraphs.Add("");
                    }
                    else
                    {
                        depth++;
                    }
                    break;
                case XmlNodeType.Element when odf && depth > 0 && reader.LocalName == "s":
                    int count = int.TryParse(reader.GetAttribute("c", OdfTextNamespace), out var c) ? Math.Clamp(c, 1, 1000) : 1;
                    current.Append(' ', count);
                    break;
                case XmlNodeType.Element when odf && depth > 0 && reader.LocalName == "tab":
                    current.Append('\t');
                    break;
                case XmlNodeType.Element when odf && depth > 0 && reader.LocalName == "line-break":
                    current.Append('\n');
                    break;
                case XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.CDATA when depth > 0:
                    current.Append(reader.Value);
                    break;
                case XmlNodeType.EndElement when odf && reader.LocalName is "p" or "h" && depth > 0:
                    depth--;
                    if (depth == 0)
                    {
                        paragraphs.Add(current.ToString());
                        current.Clear();
                    }
                    break;
            }
        }
        return Join(paragraphs);
    }

    /// <summary>每段一行；去掉首尾的空行，连续的空行合成一行。</summary>
    private static string Join(List<string> paragraphs)
    {
        var lines = new List<string>();
        foreach (var p in paragraphs)
        {
            if (p.Trim().Length == 0 && (lines.Count == 0 || lines[^1].Length == 0)) continue;
            lines.Add(p.Trim().Length == 0 ? "" : p);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }
}
