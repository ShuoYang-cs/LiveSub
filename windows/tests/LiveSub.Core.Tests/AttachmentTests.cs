using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LiveSub.Core.Chat;
using LiveSub.Core.Subtitles;

namespace LiveSub.Core.Tests;

/// <summary>与 macOS 的 ChatAttachmentTests 相同，另加 Windows 版读文件用到的纯逻辑。</summary>
public class ChatAttachmentTests
{
    private static readonly ChatAttachment Png = new ChatAttachment.Image("slide.png", "image/png", [1, 2, 3]);
    private static readonly ChatAttachment Pdf = new ChatAttachment.Pdf("notes.pdf", [4, 5], "PDF 里的文字");
    private static readonly ChatAttachment Txt = new ChatAttachment.Text("a.md", "# 标题");

    private static JsonElement Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            write(w);
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    [Fact]
    public void AnthropicPutsImagesAndPdfsBeforeText()
    {
        var blocks = Json(w => ChatContent.WriteAnthropicBlocks(w, "这是什么？", [Txt, Png, Pdf]));
        Assert.Equal(["image", "document", "text"], blocks.EnumerateArray().Select(b => b.GetProperty("type").GetString()));
        var image = blocks[0].GetProperty("source");
        Assert.Equal("base64", image.GetProperty("type").GetString());
        Assert.Equal("image/png", image.GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), image.GetProperty("data").GetString());
        Assert.Equal("notes.pdf", blocks[1].GetProperty("title").GetString());
        Assert.Equal("application/pdf", blocks[1].GetProperty("source").GetProperty("media_type").GetString());
        // 文字附件放进最后的文字块，问题在最后。
        var text = blocks[2].GetProperty("text").GetString()!;
        Assert.Contains("[附件：a.md]\n# 标题\n[附件结束：a.md]", text);
        Assert.EndsWith("这是什么？", text);
        Assert.DoesNotContain("PDF 里的文字", text); // Claude 直接读 PDF，不重复发文字
    }

    [Fact]
    public void OpenAIKeepsPlainStringWithoutImages()
    {
        // 没有图片时 content 仍是字符串：本地服务不一定认数组。
        var content = Json(w => ChatContent.WriteOpenAIContent(w, "总结一下", [Pdf, Txt]));
        Assert.Equal(JsonValueKind.String, content.ValueKind);
        var text = content.GetString()!;
        Assert.Contains("[附件：notes.pdf]\nPDF 里的文字", text);
        Assert.Contains("[附件：a.md]", text);
        Assert.EndsWith("总结一下", text);
    }

    [Fact]
    public void OpenAIUsesDataUrlsForImages()
    {
        var parts = Json(w => ChatContent.WriteOpenAIContent(w, "看图", [Png]));
        Assert.Equal(["text", "image_url"], parts.EnumerateArray().Select(p => p.GetProperty("type").GetString()));
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String([1, 2, 3]), parts[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public void RequestBodiesCarryAttachmentsOnlyWhereGiven()
    {
        ChatMessage[] messages = [new(ChatRole.User, "旧问题"), new(ChatRole.Assistant, "旧回答"), new(ChatRole.User, "新问题", [Png])];
        using var anthropic = JsonDocument.Parse(RequestBodies.AnthropicMessages("claude-haiku-5-5", "S", messages));
        var turns = anthropic.RootElement.GetProperty("messages");
        Assert.Equal("旧问题", turns[0].GetProperty("content").GetString());
        Assert.Equal(2, turns[2].GetProperty("content").GetArrayLength());
        using var openAI = JsonDocument.Parse(RequestBodies.OpenAIChat("gemini", "S", messages));
        var openAITurns = openAI.RootElement.GetProperty("messages");
        Assert.Equal("旧问题", openAITurns[1].GetProperty("content").GetString());
        Assert.Equal(2, openAITurns[3].GetProperty("content").GetArrayLength());
    }

    [Fact]
    public void ClaudeCodeLineCarriesAttachments()
    {
        var line = ClaudeStream.UserMessageLine("问题", [Png, Pdf]);
        Assert.DoesNotContain("\n", line[..^1]);
        using var doc = JsonDocument.Parse(line);
        var content = doc.RootElement.GetProperty("message").GetProperty("content");
        Assert.Equal(["image", "document", "text"], content.EnumerateArray().Select(b => b.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task HistoryKeepsAttachmentsOfTheQuestion()
    {
        // 与 macOS 的 APIChatSession 相同：附件留在对话历史里，追问时模型还看得到。
        var transport = new FakeTransport();
        using var session = new HistoryChatSession("Test", transport, "SYS");
        await session.RequestAsync("看图", "看图", null, CancellationToken.None, [Png]);
        await session.RequestAsync("再问", "再问", null, CancellationToken.None);
        Assert.Equal([Png], transport.Requests[0][0].Attachments!);
        Assert.Equal([Png], transport.Requests[1][0].Attachments!);
        Assert.False(transport.Requests[1][2].HasAttachments);
    }
}

/// <summary>与 macOS 的 ChatAttachmentBudgetTests 相同。</summary>
public class ChatAttachmentBudgetTests
{
    private static readonly byte[] Big = new byte[12_000_000];
    private static readonly byte[] Small = new byte[2_000_000];

    [Fact]
    public void FitTurnsLargestPdfsIntoTextFirst()
    {
        ChatAttachment[] attachments =
        [
            new ChatAttachment.Pdf("a.pdf", Small, "A"),
            new ChatAttachment.Pdf("b.pdf", Big, "B"),
            new ChatAttachment.Image("c.png", "image/png", Big),
        ];
        var (fitted, changed) = ChatAttachmentBudget.Fit(attachments, 18_000_000);
        Assert.Equal(["b.pdf"], changed);
        Assert.Equal(new ChatAttachment.Text("b.pdf", "B"), fitted[1]);
        Assert.Same(attachments[0], fitted[0]);
        Assert.Same(attachments[2], fitted[2]);
    }

    [Fact]
    public void FitLeavesSmallAttachmentsAlone()
    {
        ChatAttachment[] attachments = [new ChatAttachment.Pdf("a.pdf", Small, "A")];
        Assert.Empty(ChatAttachmentBudget.Fit(attachments).TextOnly);
    }

    [Fact]
    public void CompactStripsOldestAttachmentsButKeepsTheNewQuestion()
    {
        ChatMessage[] messages =
        [
            new(ChatRole.User, "q1", [new ChatAttachment.Pdf("old.pdf", Big, "old")]),
            new(ChatRole.Assistant, "a1"),
            new(ChatRole.User, "q2", [new ChatAttachment.Image("new.png", "image/png", Big)]),
        ];
        var compacted = ChatAttachmentBudget.Compact(messages, 18_000_000);
        Assert.Equal([new ChatAttachment.Text("old.pdf", "old")], compacted[0].Attachments!);
        Assert.Same(messages[2].Attachments, compacted[2].Attachments);
        Assert.Equal(messages[^1..], ChatAttachmentBudget.Compact(messages[^1..], 18_000_000));
    }

    [Fact]
    public async Task HistorySessionSendsAndKeepsTheCompactedHistory()
    {
        // 第二问的图片加上第一问留着的 PDF 超过上限：PDF 换成文字发出去，历史里也存换过的，第三问不再带原文件。
        var transport = new FakeTransport();
        using var session = new HistoryChatSession("Test", transport, "SYS");
        await session.RequestAsync("q1", "q1", null, CancellationToken.None, [new ChatAttachment.Pdf("old.pdf", Big, "old")]);
        await session.RequestAsync("q2", "q2", null, CancellationToken.None, [new ChatAttachment.Image("new.png", "image/png", Big)]);
        await session.RequestAsync("q3", "q3", null, CancellationToken.None);
        Assert.IsType<ChatAttachment.Pdf>(transport.Requests[0][0].Attachments![0]);
        Assert.Equal([new ChatAttachment.Text("old.pdf", "old")], transport.Requests[1][0].Attachments!);
        Assert.IsType<ChatAttachment.Image>(transport.Requests[1][2].Attachments![0]);
        Assert.Equal([new ChatAttachment.Text("old.pdf", "old")], transport.Requests[2][0].Attachments!);
    }
}

public class AttachmentFilesTests
{
    [Fact]
    public void ClassifiesByExtension()
    {
        Assert.Equal(AttachmentKind.Image, AttachmentFiles.Classify("截图.PNG"));
        Assert.Equal(AttachmentKind.Image, AttachmentFiles.Classify("photo.jpeg"));
        Assert.Equal(AttachmentKind.Pdf, AttachmentFiles.Classify("slides.pdf"));
        Assert.Equal(AttachmentKind.Word, AttachmentFiles.Classify("report.docx"));
        Assert.Equal(AttachmentKind.OpenDocument, AttachmentFiles.Classify("a.odt"));
        Assert.Equal(AttachmentKind.Rtf, AttachmentFiles.Classify("a.rtf"));
        Assert.Equal(AttachmentKind.Other, AttachmentFiles.Classify("main.swift"));
        Assert.Equal(AttachmentKind.Other, AttachmentFiles.Classify("README"));
    }

    [Fact]
    public void ImagesShrinkToTheLongEdgeLimit()
    {
        Assert.Equal((1568, 882), AttachmentFiles.FitImage(3840, 2160));
        Assert.Equal((882, 1568), AttachmentFiles.FitImage(2160, 3840));
        // 本来就不大的图片不放大。
        Assert.Equal((800, 600), AttachmentFiles.FitImage(800, 600));
        Assert.Equal((1568, 1), AttachmentFiles.FitImage(10000, 2));
    }

    /// <summary>按 <see cref="AttachmentFiles.Orientation"/> 先镜像、再顺时针转，得到转正后的像素排列（每行从左到右）。</summary>
    private static int[][] Upright(int[][] stored, int exif)
    {
        var (mirror, rotation) = AttachmentFiles.Orientation(exif);
        var image = mirror ? stored.Select(row => row.Reverse().ToArray()).ToArray() : stored;
        for (int r = 0; r < rotation; r += 90)
        {
            // 顺时针转 90°：新的第 i 行是原来第 i 列从下往上读。
            int h = image.Length, w = image[0].Length;
            image = Enumerable.Range(0, w).Select(i => Enumerable.Range(0, h).Select(j => image[h - 1 - j][i]).ToArray()).ToArray();
        }
        return image;
    }

    [Fact]
    public void PhotoOrientationIncludingMirroredRotations()
    {
        // 转正后的图是 [[1,2,3],[4,5,6]]；按 EXIF 定义，各方向存成的样子转回来都应该是它。
        int[][] upright = [[1, 2, 3], [4, 5, 6]];
        var stored = new Dictionary<int, int[][]>
        {
            [1] = [[1, 2, 3], [4, 5, 6]],
            [2] = [[3, 2, 1], [6, 5, 4]],
            [3] = [[6, 5, 4], [3, 2, 1]],
            [4] = [[4, 5, 6], [1, 2, 3]],
            [5] = [[1, 4], [2, 5], [3, 6]],
            [6] = [[3, 6], [2, 5], [1, 4]],
            [7] = [[6, 3], [5, 2], [4, 1]],
            [8] = [[4, 1], [5, 2], [6, 3]],
        };
        foreach (var (exif, image) in stored) Assert.Equal(upright, Upright(image, exif));
        Assert.Equal((false, 0), AttachmentFiles.Orientation(0));
    }

    [Fact]
    public void PdfHeaderIsRecognized()
    {
        Assert.True(AttachmentFiles.LooksLikePdf("%PDF-1.7\n%\u00e2\u00e3"u8));
        Assert.True(AttachmentFiles.LooksLikePdf("junk before\r\n%PDF-1.4"u8));
        Assert.False(AttachmentFiles.LooksLikePdf("PK\u0003\u0004 docx"u8));
        Assert.False(AttachmentFiles.LooksLikePdf([]));
    }

    [Fact]
    public void TransparentPixelsAreFlattenedOnWhite()
    {
        byte[] pixels = [10, 20, 30, 255, 0, 0, 0, 0, 0, 0, 0, 128, 200, 100, 50, 0];
        AttachmentFiles.FlattenOnWhite(pixels);
        Assert.Equal(new byte[] { 10, 20, 30, 255, 255, 255, 255, 255, 127, 127, 127, 255, 255, 255, 255, 255 }, pixels);
    }

    [Fact]
    public void LongTextIsTruncatedWithNotice()
    {
        var text = new string('a', 30);
        Assert.Equal(text, AttachmentFiles.Truncate(text, n => $"[还有 {n} 字]", max: 30));
        Assert.Equal(new string('a', 10) + "\n[还有 20 字]", AttachmentFiles.Truncate(text, n => $"[还有 {n} 字]", max: 10));
        // 不从代理对中间切开。
        var emoji = "ab😀cd";
        Assert.Equal("ab\n[还有 4 字]", AttachmentFiles.Truncate(emoji, n => $"[还有 {n} 字]", max: 3));
    }

    [Fact]
    public void DecodesTextAndRejectsBinary()
    {
        Assert.Equal("你好", AttachmentFiles.DecodeText(Encoding.UTF8.GetBytes("你好")));
        Assert.Equal("你好", AttachmentFiles.DecodeText([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("你好")]));
        Assert.Equal("hi", AttachmentFiles.DecodeText([0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0]));
        Assert.Null(AttachmentFiles.DecodeText([0x50, 0x4B, 0x03, 0x04, 0x00, 0x00]));
        // 不是 UTF-8：用调用方给的编码（这里用 Latin-1 代替中文 Windows 的 GBK）。
        Assert.Equal("café", AttachmentFiles.DecodeText([0x63, 0x61, 0x66, 0xE9], Encoding.Latin1));
        Assert.Null(AttachmentFiles.DecodeText([0x63, 0x61, 0x66, 0xE9]));
    }

    private static MemoryStream Zip(string entry, string xml)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void ReadsWordParagraphsTabsAndBreaks()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:p><w:r><w:t>第一段</w:t></w:r><w:r><w:t xml:space="preserve"> 接着</w:t></w:r></w:p>
                <w:p/>
                <w:p/>
                <w:p><w:r><w:t>A</w:t><w:tab/><w:t>B</w:t><w:br/><w:t>C</w:t></w:r></w:p>
                <w:p><w:r><w:instrText>PAGE</w:instrText></w:r></w:p>
              </w:body>
            </w:document>
            """;
        using var docx = Zip("word/document.xml", xml);
        Assert.Equal("第一段 接着\n\nA\tB\nC", OfficeText.FromDocx(docx));
        using var notWord = Zip("other.xml", "<a/>");
        Assert.Throws<InvalidDataException>(() => OfficeText.FromDocx(notWord));
    }

    [Fact]
    public void ReadsOpenDocumentParagraphsAndSpaces()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0">
              <office:body><office:text>
                <text:h>标题</text:h>
                <text:p>一<text:s text:c="3"/>二<text:tab/>三</text:p>
                <text:p><text:span>带</text:span>样式<text:line-break/>换行</text:p>
              </office:text></office:body>
            </office:document-content>
            """;
        using var odt = Zip("content.xml", xml);
        Assert.Equal("标题\n一   二\t三\n带样式\n换行", OfficeText.FromOdt(odt));
    }
}

public class AssistantAttachmentTests
{
    [Fact]
    public void QuestionShowsAttachmentsAndSendsPrompt()
    {
        var conversation = new AssistantConversation(DateTime.UtcNow.AddMinutes(-1));
        var labels = new List<AttachmentLabel> { new("slide.png", true) };
        // 只带了附件、没写问题：面板上没有文字只有附件，发给模型的是替用户问的一句。
        var q = conversation.Begin("", Array.Empty<SubtitleLine>(), "", labels, prompt: "请看附件。")!;
        Assert.Equal("", conversation.Messages[0].Text);
        Assert.Equal(labels, conversation.Messages[0].Attachments);
        Assert.EndsWith("[我的问题]\n请看附件。", q.NewSessionMessage);
        Assert.Empty(conversation.Messages[1].Attachments);

        conversation.Fail(q, "出错");
        conversation.AddFailure("问", "找不到服务", labels);
        Assert.Equal(labels, conversation.Messages[^2].Attachments);
    }
}
