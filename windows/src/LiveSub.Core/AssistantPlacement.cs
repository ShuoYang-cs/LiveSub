namespace LiveSub.Core;

/// <summary>屏幕上的矩形（与设备无关的像素，y 轴向下）。</summary>
public readonly record struct ScreenRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;

    /// <summary>两块有重叠（只碰到边不算）。</summary>
    public bool Intersects(ScreenRect other) =>
        Left < other.Right && other.Left < Right && Top < other.Bottom && other.Top < Bottom;
}

/// <summary>AI 助手面板放在哪里（与 macOS 版 AssistantWindowController 的规则相同）。</summary>
public static class AssistantPlacement
{
    /// <summary>放在字幕旁边时与字幕的间距。</summary>
    public const double Gap = 8;
    /// <summary>标题栏的高度：记住的位置只有标题栏还在某块屏幕上时才用（才能再拖回来）。</summary>
    public const double HeaderHeight = 36;

    /// <summary>
    /// 面板的左上角：用户拖过（<paramref name="savedLeft"/>、<paramref name="savedTop"/> 不为 null）并且标题栏还在某块屏幕上时放回原处，
    /// 否则放在字幕旁边。
    /// </summary>
    /// <param name="overlayScreen">字幕所在屏幕的工作区。</param>
    /// <param name="screens">所有屏幕的工作区。</param>
    public static (double Left, double Top) Place(ScreenRect overlay, ScreenRect overlayScreen, IEnumerable<ScreenRect> screens,
                                                  double width, double height, double? savedLeft, double? savedTop)
    {
        if (savedLeft is { } left && savedTop is { } top)
        {
            var header = new ScreenRect(left, top, width, HeaderHeight);
            if (screens.Any(s => s.Intersects(header))) return (left, top);
        }
        return BesideOverlay(overlay, overlayScreen, width, height);
    }

    /// <summary>
    /// 字幕右边（间距 <see cref="Gap"/>）；右边放不下就放左边，左边也放不下就贴着屏幕右边。
    /// 字幕在屏幕上半部分时顶边对齐，否则底边对齐；上下不超出屏幕。
    /// </summary>
    public static (double Left, double Top) BesideOverlay(ScreenRect overlay, ScreenRect screen, double width, double height)
    {
        double x = overlay.Right + Gap;
        if (x + width > screen.Right)
        {
            double left = overlay.Left - Gap - width;
            x = left >= screen.Left ? left : screen.Right - width;
        }
        bool upperHalf = overlay.Top + overlay.Height / 2 < screen.Top + screen.Height / 2;
        double y = upperHalf ? overlay.Top : overlay.Bottom - height;
        y = Math.Min(Math.Max(y, screen.Top), screen.Bottom - height);
        return (x, y);
    }
}
