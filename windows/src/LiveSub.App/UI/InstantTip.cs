using System.Windows;
using System.Windows.Controls;

namespace LiveSub.App.UI;

/// <summary>
/// 鼠标停到按钮上立刻显示的说明（与 macOS 的快捷按钮和 AI 助手相同，2026-10-22 用户要求）：系统的 ToolTip 要停一会儿才出现。
/// 画在窗口自己最上面的一层里（不另开窗口，不抢焦点，不挡点击），在按钮下方（<c>above</c> 时上方），靠窗口边缘时向内挪，不被裁掉。
/// 说明文字按当前状态现算：点了之后（开始→停止、录音→停止录音）跟着变。
/// </summary>
internal sealed class InstantTip
{
    /// <summary>说明和按钮之间的空隙。</summary>
    private const double Gap = 4;
    private const double EdgeMargin = 4;

    private readonly Canvas layer = new() { IsHitTestVisible = false };
    private readonly TextBlock text = new() { FontSize = 11, FontWeight = FontWeights.Medium };
    private readonly Border bubble;
    private FrameworkElement? target;
    private Func<string>? currentText;
    private bool currentAbove;

    /// <param name="host">窗口最外层的格子：说明画在它上面、盖住所有内容。</param>
    public InstantTip(Grid host)
    {
        text.SetResourceReference(TextBlock.ForegroundProperty, OverlayPalette.Fg);
        bubble = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3),
            BorderThickness = new Thickness(1),
            Child = text,
            Visibility = Visibility.Collapsed,
        };
        bubble.SetResourceReference(Border.BackgroundProperty, OverlayPalette.Bg92);
        bubble.SetResourceReference(Border.BorderBrushProperty, OverlayPalette.Fg12);
        layer.Children.Add(bubble);
        Grid.SetRowSpan(layer, Math.Max(1, host.RowDefinitions.Count));
        Grid.SetColumnSpan(layer, Math.Max(1, host.ColumnDefinitions.Count));
        Panel.SetZIndex(layer, 1000);
        host.Children.Add(layer);
    }

    /// <summary>当前显示说明的按钮（快捷按钮的悬停样式用）。</summary>
    public FrameworkElement? Target => target;

    /// <param name="above">画在按钮上方（输入栏的按钮）。</param>
    public void Attach(FrameworkElement element, Func<string> describe, bool above = false)
    {
        element.MouseEnter += (_, _) => Show(element, describe, above);
        element.MouseLeave += (_, _) =>
        {
            if (ReferenceEquals(target, element)) Hide();
        };
        element.IsVisibleChanged += (_, _) =>
        {
            if (!element.IsVisible && ReferenceEquals(target, element)) Hide();
        };
    }

    public void Attach(FrameworkElement element, string description, bool above = false) => Attach(element, () => description, above);

    /// <summary>状态变了（例如点了“开始”）：正在显示的说明换成新的文字。</summary>
    public void Refresh()
    {
        if (target is null || currentText is null) return;
        text.Text = currentText();
        Position();
    }

    public void Hide()
    {
        target = null;
        currentText = null;
        bubble.Visibility = Visibility.Collapsed;
    }

    private void Show(FrameworkElement element, Func<string> describe, bool above)
    {
        target = element;
        currentText = describe;
        currentAbove = above;
        text.Text = describe();
        bubble.Visibility = Visibility.Visible;
        Position();
    }

    private void Position()
    {
        if (target is null || !target.IsVisible || PresentationSource.FromVisual(target) is null) return;
        bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = bubble.DesiredSize;
        Point origin;
        try
        {
            origin = target.TranslatePoint(new Point(0, 0), layer);
        }
        catch (InvalidOperationException)
        {
            // 按钮和说明层不在同一个窗口里（不会发生，保险起见）。
            return;
        }
        double x = origin.X + target.ActualWidth / 2 - size.Width / 2;
        double maxX = Math.Max(EdgeMargin, layer.ActualWidth - size.Width - EdgeMargin);
        x = Math.Clamp(x, EdgeMargin, maxX);
        double below = origin.Y + target.ActualHeight + Gap;
        double aboveY = origin.Y - size.Height - Gap;
        // 放不下时换到另一边。
        bool up = currentAbove ? aboveY >= 0 || below + size.Height > layer.ActualHeight : below + size.Height > layer.ActualHeight && aboveY >= 0;
        Canvas.SetLeft(bubble, x);
        Canvas.SetTop(bubble, up ? Math.Max(0, aboveY) : below);
    }
}
