namespace LiveSub.Core;

/// <summary>
/// 字幕窗口的默认大小和位置（与 macOS 相同，2026-10-22 用户决定和 ICT 一样）：面积是屏幕的 1/16、宽高比 3:1，放在屏幕右上角。
/// </summary>
public static class OverlayLayout
{
    /// <summary>离屏幕边缘的距离。</summary>
    public const double Margin = 16;
    public const double MinWidth = 320;

    /// <summary>默认大小：面积是屏幕的 1/16，宽高比 3:1（宽 = √(3·屏宽·屏高/16)）。</summary>
    public static (double Width, double Height) DefaultSize(double screenWidth, double screenHeight)
    {
        if (screenWidth <= 0 || screenHeight <= 0) return (MinWidth * 2, Math.Round(MinWidth * 2 / 3));
        double width = Math.Max(MinWidth, Math.Round(Math.Sqrt(3 * screenWidth * screenHeight / 16)));
        return (width, Math.Round(width / 3));
    }

    /// <summary>默认位置：工作区右上角，离边缘 <paramref name="margin"/>（默认 <see cref="Margin"/>；按物理像素算时传入放大后的值）。</summary>
    public static (double Left, double Top) TopRight(ScreenRect work, double width, double margin = Margin) =>
        (work.Right - width - margin, work.Top + margin);

    /// <summary>
    /// 推回屏幕内（默认放在右上角，往上长、往右长都会出界；与 macOS 的 fit 相同）。比屏幕还大时贴着左上角。
    /// </summary>
    public static (double Left, double Top) Clamp(ScreenRect frame, ScreenRect work)
    {
        double left = Math.Max(work.Left, Math.Min(frame.Left, work.Right - frame.Width));
        double top = Math.Max(work.Top, Math.Min(frame.Top, work.Bottom - frame.Height));
        return (left, top);
    }
}

/// <summary>
/// 设置类窗口（设置、模型服务、字幕记录、录音记录）放进屏幕的工作区。高 DPI 的小屏上（例如 1366×768 放大到 150%，
/// 工作区只有约 480 个与设备无关的像素高），固定 760 高的设置窗口比屏幕还高，居中后标题栏在屏幕外面，拖不回来。
/// 单位只要一致就行（与设备无关的像素或物理像素）。
/// </summary>
public static class WindowFit
{
    /// <summary>打开时窗口离工作区上下（左右）留的总空隙。</summary>
    public const double Gap = 40;

    /// <summary>打开时的大小：想要的大小，但不超过工作区减去 <see cref="Gap"/>。</summary>
    public static double Size(double desired, double available) => Math.Max(1, Math.Min(desired, available - Gap));

    /// <summary>显示以后：比工作区大的缩小到工作区，位置推回工作区内（标题栏一定在屏幕上）。</summary>
    public static ScreenRect Fit(ScreenRect frame, ScreenRect work)
    {
        double width = Math.Min(frame.Width, work.Width), height = Math.Min(frame.Height, work.Height);
        var (left, top) = OverlayLayout.Clamp(new ScreenRect(frame.Left, frame.Top, width, height), work);
        return new ScreenRect(left, top, width, height);
    }
}
