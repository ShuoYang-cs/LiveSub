using System.Text.Json;

namespace LiveSub.Core.Chat;

/// <summary>
/// 随问题一起发给模型的附件（AI 助手用，与 macOS 的 ChatAttachment 相同）。文件由程序读好、图片缩小编码好之后放在这里。
/// </summary>
public abstract record ChatAttachment(string Name)
{
    /// <summary>图片：JPEG 或 PNG 数据。</summary>
    public sealed record Image(string Name, string MediaType, byte[] Data) : ChatAttachment(Name);

    /// <summary>
    /// PDF：Claude（Claude Code、Anthropic 格式）直接读原文件，连图表一起看；
    /// OpenAI 兼容的服务大多不收 PDF，改为发提取出的文字（<see cref="PlainText"/>）。
    /// </summary>
    public sealed record Pdf(string Name, byte[] Data, string PlainText) : ChatAttachment(Name);

    /// <summary>文本文件、Word 文档等提取出的文字。</summary>
    public sealed record Text(string Name, string Content) : ChatAttachment(Name);

    public bool IsImage => this is Image;

    /// <summary>界面上显示的样子（名字和是不是图片）。</summary>
    public AttachmentLabel Label => new(Name, IsImage);
}

/// <summary>附件在界面上显示的样子：名字和是不是图片（决定图标）。</summary>
public sealed record AttachmentLabel(string Name, bool IsImage);

/// <summary>
/// 把问题和附件组装成消息内容（与 macOS 的 ChatContent 相同）：Anthropic Messages API 和 Claude Code stream-json 用同一种 content 块，
/// OpenAI 兼容格式另一种。
/// </summary>
public static class ChatContent
{
    /// <summary>
    /// Anthropic Messages API 和 Claude Code stream-json 的 content 块（一个 JSON 数组）。
    /// 图片、PDF 放在文字前面（Anthropic 建议的顺序），文字附件和问题放在最后的文字块里。
    /// </summary>
    public static void WriteAnthropicBlocks(Utf8JsonWriter w, string text, IReadOnlyList<ChatAttachment>? attachments)
    {
        w.WriteStartArray();
        var documents = new List<ChatAttachment>();
        foreach (var attachment in attachments ?? [])
        {
            switch (attachment)
            {
                case ChatAttachment.Image image:
                    w.WriteStartObject();
                    w.WriteString("type", "image");
                    w.WriteStartObject("source");
                    w.WriteString("type", "base64");
                    w.WriteString("media_type", image.MediaType);
                    w.WriteBase64String("data", image.Data);
                    w.WriteEndObject();
                    w.WriteEndObject();
                    break;
                case ChatAttachment.Pdf pdf:
                    w.WriteStartObject();
                    w.WriteString("type", "document");
                    w.WriteString("title", pdf.Name);
                    w.WriteStartObject("source");
                    w.WriteString("type", "base64");
                    w.WriteString("media_type", "application/pdf");
                    w.WriteBase64String("data", pdf.Data);
                    w.WriteEndObject();
                    w.WriteEndObject();
                    break;
                default:
                    documents.Add(attachment);
                    break;
            }
        }
        w.WriteStartObject();
        w.WriteString("type", "text");
        w.WriteString("text", TextWithDocuments(text, documents));
        w.WriteEndObject();
        w.WriteEndArray();
    }

    /// <summary>
    /// OpenAI 兼容格式的 content：没有图片时是一段字符串（本地服务也都认），有图片时是数组，图片用 data URL。
    /// PDF 发提取出的文字。
    /// </summary>
    public static void WriteOpenAIContent(Utf8JsonWriter w, string text, IReadOnlyList<ChatAttachment>? attachments)
    {
        var all = attachments ?? [];
        var combined = TextWithDocuments(text, all.Where(a => !a.IsImage));
        var images = all.OfType<ChatAttachment.Image>().ToList();
        if (images.Count == 0)
        {
            w.WriteStringValue(combined);
            return;
        }
        w.WriteStartArray();
        w.WriteStartObject();
        w.WriteString("type", "text");
        w.WriteString("text", combined);
        w.WriteEndObject();
        foreach (var image in images)
        {
            w.WriteStartObject();
            w.WriteString("type", "image_url");
            w.WriteStartObject("image_url");
            w.WriteString("url", $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data)}");
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>文字附件（以及按文字发送的 PDF）放在问题前面，每个用文件名标出起止（与系统提示词里的说明一致）。</summary>
    public static string TextWithDocuments(string text, IEnumerable<ChatAttachment> documents)
    {
        var parts = new List<string>();
        foreach (var attachment in documents)
        {
            var content = attachment switch
            {
                ChatAttachment.Text t => t.Content,
                ChatAttachment.Pdf p => p.PlainText,
                _ => null,
            };
            if (content is not null) parts.Add($"[附件：{attachment.Name}]\n{content}\n[附件结束：{attachment.Name}]");
        }
        parts.Add(text);
        return string.Join("\n\n", parts);
    }
}

/// <summary>
/// 附件总大小的上限（与 macOS 的 ChatAttachmentBudget 相同）。Anthropic 接口一次请求最多 32 MB，
/// 几个大 PDF 加上之前对话里留着的附件很容易超过，整个请求被拒，用户只看到一句“请求太大”。
/// </summary>
public static class ChatAttachmentBudget
{
    /// <summary>原始字节数的上限：base64 后约 24 MB，给问题、字幕记录和回答留出余量。</summary>
    public const int MaxBinaryBytes = 18_000_000;

    public static long BinaryBytes(ChatAttachment attachment) => attachment switch
    {
        ChatAttachment.Image image => image.Data.LongLength,
        ChatAttachment.Pdf pdf => pdf.Data.LongLength,
        _ => 0,
    };

    public static long BinaryBytes(IEnumerable<ChatAttachment>? attachments) => attachments?.Sum(BinaryBytes) ?? 0;

    /// <summary>只留文字：PDF 换成提取出的文字，图片换成一句说明。</summary>
    public static ChatAttachment TextOnly(ChatAttachment attachment) => attachment switch
    {
        ChatAttachment.Image image => new ChatAttachment.Text(image.Name, "[An image sent earlier in this conversation; it is no longer attached.]"),
        ChatAttachment.Pdf pdf => new ChatAttachment.Text(pdf.Name, pdf.PlainText),
        _ => attachment,
    };

    /// <summary>
    /// 这一问的附件超过上限时，PDF 从大到小改成只发文字（图片缩小后最大约 3.5 MB，不用动）。
    /// 返回改过的附件和改成文字的文件名。
    /// </summary>
    public static (IReadOnlyList<ChatAttachment> Attachments, IReadOnlyList<string> TextOnly) Fit(
        IReadOnlyList<ChatAttachment> attachments, long maxBytes = MaxBinaryBytes)
    {
        var result = attachments.ToList();
        var changed = new List<string>();
        var pdfs = Enumerable.Range(0, result.Count)
            .Where(i => result[i] is ChatAttachment.Pdf)
            .OrderByDescending(i => BinaryBytes(result[i]))
            .ToList();
        foreach (var i in pdfs)
        {
            if (BinaryBytes(result) <= maxBytes) break;
            changed.Add(result[i].Name);
            result[i] = TextOnly(result[i]);
        }
        return (result, changed);
    }

    /// <summary>整段对话里的附件超过上限时，从最早的消息开始把附件换成文字，最新一条（这次的提问）不动。</summary>
    public static List<ChatMessage> Compact(IReadOnlyList<ChatMessage> messages, long maxBytes = MaxBinaryBytes)
    {
        var result = messages.ToList();
        var total = result.Sum(m => BinaryBytes(m.Attachments));
        for (var i = 0; i < result.Count - 1 && total > maxBytes; i++)
        {
            var before = BinaryBytes(result[i].Attachments);
            if (before == 0) continue;
            result[i] = result[i] with { Attachments = result[i].Attachments!.Select(TextOnly).ToList() };
            total -= before;
        }
        return result;
    }
}
