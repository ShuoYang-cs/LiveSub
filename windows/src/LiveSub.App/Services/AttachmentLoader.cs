using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using LiveSub.App.Localization;
using LiveSub.App.UI;
using LiveSub.Core.Chat;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace LiveSub.App.Services;

/// <summary>
/// 把 AI 助手的附件（拖进来、粘贴、选择的图片和文件）读成可以发给模型的内容（与 macOS 的 AttachmentLoader 相同）。
/// 读文件、缩小图片、提取 PDF 文字可能要一会儿，不要在界面线程上调用。
/// - 图片：长边缩到 1568 像素，按照片的方向信息转正；都存 JPEG，有透明部分的先合成到白底上。
/// - PDF：原文件和用 PdfPig 提取的文字都带上（Claude 读原文件，OpenAI 兼容格式发文字）；太大、页数太多时只发文字。
///   PdfPig 读不了某一页时跳过这页；整个打不开时（PdfPig 比 macOS 的 PDFKit 挑剔）不太大的照样发原文件。
/// - Word（.docx）、OpenDocument（.odt）、RTF：读出文字。旧的 .doc 读不了，提示存成 PDF。
/// - 其他文件：能按文本读出来就当文本（代码、字幕文件等），读不出来就是“暂不支持”。
/// </summary>
internal static class AttachmentLoader
{
    /// <summary>读不了的附件；Message 是给用户看的说明。</summary>
    public sealed class Failure(string message) : Exception(message);

    public static ChatAttachment Load(string path)
    {
        var name = Path.GetFileName(path);
        if (Directory.Exists(path)) throw new Failure(Strings.Format(Strings.Attachment_Unsupported, name));
        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new Failure(Strings.Format(Strings.Attachment_Unreadable, name));
        }
        if (size > AttachmentLimits.MaxFileBytes) throw new Failure(Strings.Format(Strings.Attachment_TooLarge, name));

        switch (AttachmentFiles.Classify(name))
        {
            case AttachmentKind.Image:
                return Image(Read(path, name), name);
            case AttachmentKind.Pdf:
                return Pdf(Read(path, name), name);
            case AttachmentKind.Word:
                return new ChatAttachment.Text(name, Truncated(Office(path, name, OfficeText.FromDocx)));
            case AttachmentKind.OpenDocument:
                return new ChatAttachment.Text(name, Truncated(Office(path, name, OfficeText.FromOdt)));
            case AttachmentKind.Rtf:
                return new ChatAttachment.Text(name, Truncated(Rtf(path, name)));
            default:
                // 纯文本、代码、字幕文件等：能按文本读出来就当文本，不管扩展名认不认识。
                var text = AttachmentFiles.DecodeText(Read(path, name), LegacyEncoding.Value)
                    ?? throw new Failure(Strings.Format(Strings.Attachment_Unsupported, name));
                return new ChatAttachment.Text(name, Truncated(text));
        }
    }

    /// <summary>缩小并重新编码图片（粘贴的、拖进来的图片数据也走这里）。</summary>
    public static ChatAttachment Image(byte[] data, string name)
    {
        try
        {
            return OnStaThread(() => EncodeImage(data, name));
        }
        catch (Exception e) when (e is not Failure)
        {
            AppLog.Error("assistant: image unreadable", e);
            throw new Failure(Strings.Format(Strings.Attachment_Unreadable, name));
        }
    }

    /// <summary>
    /// 剪贴板里只有系统位图、没有 "PNG" 格式时（微信、QQ 截图，PrintScreen，画图）：位图的 alpha 通道多半全是 0，
    /// 直接存成 PNG 是一张全透明的图。先换成不带 alpha 的 Bgr32 再编码。<paramref name="bitmap"/> 要先冻结（在界面线程上取出来的）。
    /// </summary>
    public static ChatAttachment Image(BitmapSource bitmap, string name)
    {
        byte[] png;
        try
        {
            png = OnStaThread(() => Encode(new PngBitmapEncoder(), new FormatConvertedBitmap(bitmap, PixelFormats.Bgr32, null, 0)));
        }
        catch (Exception e)
        {
            AppLog.Error("assistant: clipboard bitmap unreadable", e);
            throw new Failure(Strings.Format(Strings.Attachment_Unreadable, name));
        }
        return Image(png, name);
    }

    private static ChatAttachment EncodeImage(byte[] data, string name)
    {
        var decoder = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var (width, height) = AttachmentFiles.FitImage(frame.PixelWidth, frame.PixelHeight);
        bool hasAlpha = HasAlpha(frame);
        var (mirror, rotation) = AttachmentFiles.Orientation(Orientation(frame));

        // 解码时直接缩小（质量比先解码再缩放好，也省内存）。
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(data);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (width != frame.PixelWidth) image.DecodePixelWidth = width;
        image.EndInit();
        BitmapSource result = image;
        // 印刷用的 CMYK JPEG：照原样再存成 JPEG 还是 CMYK，很多看图程序和模型都读不了。先换成 RGB。
        if (result.Format == PixelFormats.Cmyk32) result = new FormatConvertedBitmap(result, PixelFormats.Bgr24, null, 0);
        // 手机照片常常是横着存的，靠方向信息转正（与 macOS 的 kCGImageSourceCreateThumbnailWithTransform 相同）；
        // 前置摄像头的照片还会镜像（方向 2、4、5、7）：先镜像再转。
        if (mirror || rotation != 0)
        {
            var transform = new TransformGroup();
            if (mirror) transform.Children.Add(new ScaleTransform(-1, 1));
            if (rotation != 0) transform.Children.Add(new RotateTransform(rotation));
            result = new TransformedBitmap(result, transform);
        }
        result.Freeze();

        // 有透明部分的图片一律合成到白底上存 JPEG：照原样发 PNG 时 Claude 把透明处当成黑色
        // （2026-10-23 Windows 实测，用户决定只改 Windows）。直接去掉 alpha 的话透明处也是黑的。
        BitmapSource opaque = hasAlpha ? OnWhite(result) : result;
        return new ChatAttachment.Image(name, "image/jpeg", Encode(new JpegBitmapEncoder { QualityLevel = 85 }, opaque));
    }

    private static BitmapSource OnWhite(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        AttachmentFiles.FlattenOnWhite(pixels);
        var flat = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        flat.Freeze();
        return flat;
    }

    private static byte[] Encode(BitmapEncoder encoder, BitmapSource source)
    {
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static bool HasAlpha(BitmapSource frame)
    {
        var f = frame.Format;
        if (f == PixelFormats.Bgra32 || f == PixelFormats.Pbgra32 || f == PixelFormats.Rgba64 || f == PixelFormats.Prgba64
            || f == PixelFormats.Rgba128Float || f == PixelFormats.Prgba128Float)
        {
            return true;
        }
        // 带透明色的调色板图片（GIF、8 位 PNG）。
        return frame.Palette?.Colors.Any(c => c.A < 255) == true;
    }

    /// <summary>照片的方向信息（EXIF Orientation 1–8，见 <see cref="AttachmentFiles.Orientation"/>）；没有时为 1。</summary>
    private static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("System.Photo.Orientation") is ushort value) return value;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException
                                     or System.Runtime.InteropServices.COMException)
        {
            // 这种格式没有方向信息。
        }
        return 1;
    }

    private static ChatAttachment Pdf(byte[] data, string name)
    {
        string text;
        int pages, failedPages = 0;
        try
        {
            using var document = PdfDocument.Open(data);
            pages = document.NumberOfPages;
            var builder = new StringBuilder();
            for (int i = 1; i <= pages; i++)
            {
                // 每页单独处理：PdfPig 读某一页出错（字体、内容不规范）时跳过这页，不让整个 PDF 都发不了。
                try
                {
                    builder.AppendLine(ContentOrderTextExtractor.GetText(document.GetPage(i)));
                }
                catch (Exception e)
                {
                    if (failedPages++ == 0) AppLog.Error($"assistant: pdf page {i} unreadable", e);
                }
            }
            if (failedPages > 0) AppLog.Info($"assistant: pdf skipped {failedPages}/{pages} pages");
            text = builder.ToString().Trim();
        }
        catch (PdfDocumentEncryptedException e)
        {
            // 加密的 PDF：Claude 也读不了。
            AppLog.Error("assistant: pdf encrypted", e);
            throw new Failure(Strings.Format(Strings.Attachment_Unreadable, name));
        }
        catch (Exception e)
        {
            // PdfPig 打不开（结构不规范；macOS 的 PDFKit 宽容得多，常常能打开）：不太大时照样把原文件发给 Claude，
            // 文字部分写明没能读出文字（OpenAI 兼容格式只发这段说明）。
            AppLog.Error("assistant: pdf unreadable", e);
            if (data.Length > AttachmentLimits.MaxPdfBytes || !AttachmentFiles.LooksLikePdf(data))
            {
                throw new Failure(Strings.Format(Strings.Attachment_Unreadable, name));
            }
            return new ChatAttachment.Pdf(name, data, Strings.Attachment_PdfTextUnavailable);
        }
        if (text.Length == 0) text = failedPages > 0 ? Strings.Attachment_PdfTextUnavailable : Strings.Attachment_PdfNoText;
        // Claude 读 PDF 原文件有大小和页数上限：超过时只发文字。
        if (data.Length > AttachmentLimits.MaxPdfBytes || pages > AttachmentLimits.MaxPdfPages)
        {
            return new ChatAttachment.Text(name, Truncated(text));
        }
        return new ChatAttachment.Pdf(name, data, Truncated(text));
    }

    private static string Office(string path, string name, Func<Stream, string> read)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return read(stream);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or XmlException or UnauthorizedAccessException)
        {
            throw new Failure(Strings.Format(Strings.Attachment_Unreadable, name));
        }
    }

    /// <summary>RTF 用 WPF 自己的 RTF 读取器（要在 STA 线程上）读出文字。</summary>
    private static string Rtf(string path, string name)
    {
        try
        {
            return OnStaThread(() =>
            {
                var document = new FlowDocument();
                var range = new TextRange(document.ContentStart, document.ContentEnd);
                using var stream = File.OpenRead(path);
                range.Load(stream, DataFormats.Rtf);
                return range.Text.Trim();
            });
        }
        catch (Exception e)
        {
            AppLog.Error("assistant: rtf unreadable", e);
            throw new Failure(Strings.Format(Strings.Attachment_Unreadable, name));
        }
    }

    private static byte[] Read(string path, string name)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new Failure(Strings.Format(Strings.Attachment_Unreadable, name));
        }
    }

    private static string Truncated(string text) =>
        AttachmentFiles.Truncate(text, remaining => Strings.Format(Strings.Attachment_Truncated, remaining));

    /// <summary>不是 UTF-8 的文本文件按系统的旧编码读（中文 Windows 上是 GBK）。claude 进程的错误输出也用它（见 ClaudeCodeSession）。</summary>
    internal static readonly Lazy<Encoding?> LegacyEncoding = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            // 按系统的“非 Unicode 程序的语言”，不按界面区域格式：英文区域格式的中文用户，旧文件也是 GBK。
            int codePage = (int)NativeMethods.GetACP();
            return codePage is 0 or 65001 ? null : Encoding.GetEncoding(codePage);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return null;
        }
    });

    /// <summary>
    /// WPF 的图片和文档类要在 STA 线程上用：开一个临时的 STA 线程做完再回来（调用方本来就在后台线程上等着）。
    /// 线程结束前关掉它的 Dispatcher，不留下没用的 Dispatcher。
    /// </summary>
    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception e)
            {
                error = ExceptionDispatchInfo.Capture(e);
            }
            finally
            {
                Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
            }
        }) { IsBackground = true, Name = "LiveSub.Attachment" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
        return result;
    }
}
