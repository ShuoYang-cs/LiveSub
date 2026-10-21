using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using LiveSub.App.Localization;
using LiveSub.App.Services;
using LiveSub.Core;
using LiveSub.Core.Subtitles;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace LiveSub.App.UI;

/// <summary>悬浮字幕上的快捷按钮回调（由托盘程序提供）。</summary>
public sealed record OverlayActions(
    Action ToggleRunning,
    Action CycleDisplayMode,
    Action Lock,
    Action ShowHistory,
    Action ShowSettings,
    Action Hide,
    Action<FrameworkElement> ShowModelMenu);

/// <summary>
/// 悬浮字幕窗口：无边框、透明背景上一块圆角半透明深色面板；置顶、不进任务栏、不抢焦点（WS_EX_NOACTIVATE）。
/// 窗口大小只随设置变化，不随字幕内容忽高忽低：高度默认按“显示句数”算，字幕在框里滚动，可往上翻看历史。
/// 按住拖动；拖边框和四角调整大小（拖上下边框改成自己想要的高度，双击上下边框恢复按句数算的高度）；
/// 锁定后点击穿透（WS_EX_TRANSPARENT），在托盘菜单里解锁。
/// </summary>
public sealed class OverlayWindow : Window
{
    private static readonly SolidColorBrush Orange = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x3D)));
    private static readonly SolidColorBrush Green = Freeze(new SolidColorBrush(Color.FromArgb(0xD9, 0x4C, 0xD9, 0x64)));
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public const double DefaultWidth = 900;
    private const int ScrollbackLines = 300;
    private static readonly Size MinSize = new(320, 70);
    /// <summary>每句字幕下面的间距（LineView 的下边距）。</summary>
    private const double LineGap = 8;
    /// <summary>状态条一行 11 号字加下边距。</summary>
    private const double StatusBarHeight = 22;

    private readonly SubtitleController controller;
    private readonly SettingsService settingsService;
    private readonly OverlayActions actions;
    private AppSettings S => settingsService.Current;

    private readonly Grid root = new();
    private readonly Border panel = new() { CornerRadius = new CornerRadius(14) };
    private readonly DockPanel layout = new() { LastChildFill = true };
    private readonly StackPanel linesPanel = new();
    /// <summary>
    /// 系统滚动条在深色字幕上太显眼：隐藏起来，往上翻看时用右边的细位置条（scrollThumb）表示位置。
    /// 不拿焦点：ScrollViewer 拿到焦点时会把鼠标按下标记为已处理，按住字幕就拖不动窗口了。
    /// </summary>
    private readonly ScrollViewer scroller = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(18, 12, 18, 12),
        Focusable = false,
    };
    private readonly Grid scrollArea = new();
    private readonly Border scrollThumb = new()
    {
        Width = 4,
        CornerRadius = new CornerRadius(2),
        Background = new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF)),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock placeholder = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Foreground = Brushes.White };
    private readonly LiveLineView liveView = new();
    private readonly Dictionary<Guid, LineView> views = [];
    private readonly StatusBarView statusBar;
    private readonly StackPanel toolbarButtons = new() { Orientation = Orientation.Horizontal };
    private readonly Border toolbar;
    private readonly Button backToLive;
    private Button? runButton;
    private Button? hideButton;
    private readonly DispatcherTimer topmostTimer;

    private bool atBottom = true;
    private bool dragging;
    private Point dragStart;
    private ResizeDrag? resizing;
    private bool renderQueued;
    private bool positioned;

    public OverlayWindow(SubtitleController controller, SettingsService settingsService, OverlayActions actions)
    {
        this.controller = controller;
        this.settingsService = settingsService;
        this.actions = actions;

        Title = Strings.AppName;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        UseLayoutRounding = true;

        linesPanel.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.6, BlurRadius = 3, ShadowDepth = 1, Direction = 270 };
        linesPanel.Children.Add(placeholder);
        linesPanel.Children.Add(liveView);
        scroller.Content = linesPanel;
        scrollArea.Children.Add(scroller);
        scrollArea.Children.Add(scrollThumb);

        statusBar = new StatusBarView(el => actions.ShowModelMenu(el));
        DockPanel.SetDock(statusBar, Dock.Bottom);
        layout.Children.Add(statusBar);
        layout.Children.Add(scrollArea);
        panel.Child = layout;
        root.Children.Add(panel);

        toolbar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(4),
            Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Child = toolbarButtons,
        };
        BuildToolbar();
        root.Children.Add(toolbar);

        backToLive = FlatButton(Strings.Overlay_BackToLive, null);
        backToLive.FontSize = 12;
        backToLive.FontWeight = FontWeights.SemiBold;
        backToLive.Padding = new Thickness(10, 4, 10, 4);
        backToLive.Background = new SolidColorBrush(Color.FromArgb(0xB3, 0, 0, 0));
        backToLive.HorizontalAlignment = HorizontalAlignment.Right;
        backToLive.VerticalAlignment = VerticalAlignment.Bottom;
        backToLive.Margin = new Thickness(0, 0, 24, 30);
        backToLive.Visibility = Visibility.Collapsed;
        backToLive.Click += (_, _) =>
        {
            atBottom = true;
            scroller.ScrollToEnd();
            backToLive.Visibility = Visibility.Collapsed;
            scrollThumb.Visibility = Visibility.Collapsed;
        };
        root.Children.Add(backToLive);

        AddResizeHandles();
        Content = root;

        root.MouseEnter += (_, _) => toolbar.Visibility = S.ClickThrough ? Visibility.Collapsed : Visibility.Visible;
        root.MouseLeave += (_, _) => toolbar.Visibility = Visibility.Collapsed;
        panel.MouseLeftButtonDown += OnPanelMouseDown;
        panel.MouseMove += OnPanelMouseMove;
        panel.MouseLeftButtonUp += OnPanelMouseUp;
        panel.LostMouseCapture += (_, _) =>
        {
            if (!dragging) return;
            dragging = false;
            SavePosition();
        };
        scroller.ScrollChanged += OnScrollChanged;
        SizeChanged += OnSizeChanged;
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => RestorePosition();

        controller.Changed += RequestRender;
        settingsService.Changed += change =>
        {
            if (change.HasFlag(SettingsChange.Appearance) || change.HasFlag(SettingsChange.Translation)) ApplySettings();
        };

        // 全屏视频等其他置顶窗口出现后，定期把字幕重新提到最上层（不激活）。
        topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        topmostTimer.Tick += (_, _) =>
        {
            if (IsVisible) NativeMethods.SetWindowPos(this.Handle(), NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        };
        topmostTimer.Start();

        ApplySettings();
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Bottom - 190;
    }

    // MARK: - 窗口样式

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = this.Handle();
        NativeMethods.SetExStyle(hwnd, NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW, 0);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        ApplyClickThrough();
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 点击字幕时不激活窗口，焦点留在用户正在用的程序上。
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    private void ApplyClickThrough()
    {
        var hwnd = this.Handle();
        if (hwnd == IntPtr.Zero) return;
        if (S.ClickThrough)
        {
            NativeMethods.SetExStyle(hwnd, NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED, 0);
            toolbar.Visibility = Visibility.Collapsed;
        }
        else
        {
            NativeMethods.SetExStyle(hwnd, 0, NativeMethods.WS_EX_TRANSPARENT);
        }
    }

    /// <summary>显示但不激活。</summary>
    public void ShowNoActivate()
    {
        Show();
        RequestRender();
    }

    // MARK: - 设置

    private void ApplySettings()
    {
        panel.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(S.BackgroundOpacity, 0, 1) * 255), 0, 0, 0));
        Width = Math.Max(MinSize.Width, S.OverlayWidth);
        Height = Math.Max(MinSize.Height, S.OverlayHeight > 0 ? S.OverlayHeight : AutoHeight());
        placeholder.FontSize = S.SourceFontSize;
        placeholder.FontFamily = TextFonts.For(S.Source);
        statusBar.Visibility = Visibility.Collapsed;
        foreach (var view in views.Values) view.Invalidate();
        ApplyClickThrough();
        RequestRender();
    }

    /// <summary>
    /// 没拖过上下边框时的高度：正好放下最近 N 句（每句按一行原文、一行译文算），加上正在说的一行和状态条。
    /// 只随设置变化，字幕生成时窗口不会忽高忽低；句子折成多行时，放不下的部分往上滚出去。
    /// </summary>
    private double AutoHeight()
    {
        var mode = S.DisplayMode;
        double sourceLine = S.SourceFontSize * LineSpacing(TextFonts.For(S.Source));
        double translationLine = S.TranslationFontSize * LineSpacing(TextFonts.For(S.Target));
        double sentence = (mode == DisplayMode.TranslationOnly ? 0 : sourceLine)
            + (mode == DisplayMode.SourceOnly ? 0 : translationLine + 2)
            + LineGap;
        double lines = scroller.Padding.Top + Math.Clamp(S.VisibleLines, 1, 6) * sentence + sourceLine + scroller.Padding.Bottom;
        return Math.Ceiling(lines + (S.ShowStatusBar ? StatusBarHeight : 0));
    }

    private static double LineSpacing(FontFamily font) => font.LineSpacing > 1 ? font.LineSpacing : 1.33;

    // MARK: - 渲染

    private void RequestRender()
    {
        if (renderQueued) return;
        renderQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, Render);
    }

    private void Render()
    {
        renderQueued = false;
        if (!IsVisible) return;
        var lines = controller.Book.Lines;
        var mode = S.DisplayMode;
        int start = Math.Max(0, lines.Count - ScrollbackLines);
        var visible = new List<SubtitleLine>(lines.Count - start);
        for (int i = start; i < lines.Count; i++) visible.Add(lines[i]);
        var visibleIds = visible.Select(l => l.Id).ToHashSet();

        // 被挤出去的句子淡出后移除。
        foreach (var (id, view) in views.Where(kv => !visibleIds.Contains(kv.Key)).ToList())
        {
            views.Remove(id);
            FadeOutAndRemove(view);
        }

        var sourceFont = TextFonts.For(S.Source);
        var targetFont = TextFonts.For(S.Target);
        LineView? firstExisting = views.Count > 0 ? views.Values.MinBy(v => v.Time) : null;
        foreach (var line in visible)
        {
            if (!views.TryGetValue(line.Id, out var view))
            {
                view = new LineView();
                views[line.Id] = view;
                bool olderThanShown = firstExisting is not null && line.Time < firstExisting.Time;
                int index = olderThanShown ? linesPanel.Children.IndexOf(firstExisting!) : linesPanel.Children.IndexOf(liveView);
                linesPanel.Children.Insert(Math.Max(0, index), view);
                view.Update(line, mode, S.SourceFontSize, S.TranslationFontSize, sourceFont, targetFont);
                FadeIn(view);
            }
            else
            {
                view.Update(line, mode, S.SourceFontSize, S.TranslationFontSize, sourceFont, targetFont);
            }
        }

        var live = controller.LiveText;
        liveView.Update(live, mode, S.SourceFontSize, sourceFont);
        placeholder.Visibility = visible.Count == 0 && live.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        placeholder.Text = controller.IsRunning
            ? Strings.Overlay_Listening
            : Strings.Format(Strings.Overlay_Ready, Strings.AppName, controller.Status);

        bool showStatus = S.ShowStatusBar && controller.IsRunning;
        statusBar.Visibility = showStatus ? Visibility.Visible : Visibility.Collapsed;
        if (showStatus) statusBar.Update(controller);
        if (runButton is not null)
        {
            bool active = controller.IsRunning || controller.IsStarting;
            runButton.Content = active ? "\uE769" : "\uE768";
            runButton.ToolTip = active ? Strings.Tool_Stop : Strings.Tool_Start;
        }
        if (hideButton is not null)
        {
            hideButton.ToolTip = HotKeys.Text(S.HotKey) is { } key ? Strings.Format(Strings.Tool_Hide, key) : Strings.Tool_HideNoKey;
        }

        if (atBottom)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => scroller.ScrollToEnd());
        }
    }

    private static void FadeIn(FrameworkElement element)
    {
        var transform = new TranslateTransform(0, 10);
        element.RenderTransform = transform;
        element.Opacity = 0;
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    private void FadeOutAndRemove(FrameworkElement element)
    {
        var fade = new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => linesPanel.Children.Remove(element);
        element.BeginAnimation(OpacityProperty, fade);
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 只看用户自己的滚动（内容和窗口大小没变时）；停在底部时新字幕自动跟随，往上翻看时不打扰。
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            atBottom = scroller.VerticalOffset >= scroller.ScrollableHeight - 24;
            backToLive.Visibility = atBottom ? Visibility.Collapsed : Visibility.Visible;
        }
        else if (atBottom)
        {
            scroller.ScrollToEnd();
        }
        UpdateScrollThumb();
    }

    /// <summary>细的滚动位置条：只在往上翻看时出现。</summary>
    private void UpdateScrollThumb()
    {
        double extent = scroller.ExtentHeight, viewport = scroller.ViewportHeight;
        if (atBottom || extent <= viewport + 1 || scroller.ActualHeight <= 0)
        {
            scrollThumb.Visibility = Visibility.Collapsed;
            return;
        }
        double track = scroller.ActualHeight - 8;
        double thumb = Math.Max(24, track * viewport / extent);
        double progress = Math.Clamp(scroller.VerticalOffset / (extent - viewport), 0, 1);
        scrollThumb.Height = thumb;
        scrollThumb.Margin = new Thickness(0, 4 + (track - thumb) * progress, 4, 0);
        scrollThumb.Visibility = Visibility.Visible;
    }

    // MARK: - 工具栏

    private void BuildToolbar()
    {
        runButton = IconButton("\uE768", Strings.Tool_Start, actions.ToggleRunning);
        toolbarButtons.Children.Add(runButton);
        toolbarButtons.Children.Add(IconButton("\uE8C1", Strings.Tool_CycleDisplay, actions.CycleDisplayMode));
        toolbarButtons.Children.Add(IconButton("\uE72E", Strings.Tool_Lock, actions.Lock));
        toolbarButtons.Children.Add(IconButton("\uE81C", Strings.Tool_History, actions.ShowHistory));
        toolbarButtons.Children.Add(IconButton("\uE713", Strings.Tool_Settings, actions.ShowSettings));
        hideButton = IconButton("\uE921", Strings.Tool_HideNoKey, actions.Hide);
        toolbarButtons.Children.Add(hideButton);
    }

    private static Button IconButton(string glyph, string tip, Action action)
    {
        var b = FlatButton(glyph, action);
        b.FontFamily = IconFont;
        b.FontSize = 12;
        b.Width = 26;
        b.Height = 22;
        b.ToolTip = tip;
        return b;
    }

    /// <summary>无边框的扁平按钮；鼠标移上去时背景稍亮。</summary>
    internal static Button FlatButton(string content, Action? action)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Background)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new Binding(nameof(Padding)) { RelativeSource = RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var normal = Brushes.Transparent;
        var b = new Button
        {
            Content = content,
            Foreground = Brushes.White,
            Background = normal,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Focusable = false,
            Cursor = Cursors.Hand,
            Template = new ControlTemplate(typeof(Button)) { VisualTree = border },
        };
        Brush? resting = null;
        b.MouseEnter += (_, _) =>
        {
            resting = b.Background;
            b.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        };
        b.MouseLeave += (_, _) => b.Background = resting ?? normal;
        if (action is not null) b.Click += (_, _) => action();
        return b;
    }

    // MARK: - 拖动

    private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (S.ClickThrough || e.ClickCount > 1) return;
        dragging = true;
        dragStart = e.GetPosition(this);
        panel.CaptureMouse();
        e.Handled = true;
    }

    private void OnPanelMouseMove(object sender, MouseEventArgs e)
    {
        if (!dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // 松开鼠标的消息丢了（例如被别的窗口抢走捕获）：结束拖动。
            dragging = false;
            panel.ReleaseMouseCapture();
            SavePosition();
            return;
        }
        var p = e.GetPosition(this);
        Left += p.X - dragStart.X;
        Top += p.Y - dragStart.Y;
    }

    private void OnPanelMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        panel.ReleaseMouseCapture();
        SavePosition();
    }

    // MARK: - 调整大小

    [Flags]
    private enum Edges { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    private sealed record ResizeDrag(Edges Edges, Point StartCursor, Rect StartFrame);

    private void AddResizeHandles()
    {
        const double edge = 6, corner = 12;
        (Edges, HorizontalAlignment, VerticalAlignment, double, double, Thickness, Cursor)[] handles =
        [
            (Edges.Left, HorizontalAlignment.Left, VerticalAlignment.Stretch, edge, double.NaN, new Thickness(0, corner, 0, corner), Cursors.SizeWE),
            (Edges.Right, HorizontalAlignment.Right, VerticalAlignment.Stretch, edge, double.NaN, new Thickness(0, corner, 0, corner), Cursors.SizeWE),
            (Edges.Top, HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, edge, new Thickness(corner, 0, corner, 0), Cursors.SizeNS),
            (Edges.Bottom, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, edge, new Thickness(corner, 0, corner, 0), Cursors.SizeNS),
            (Edges.Top | Edges.Left, HorizontalAlignment.Left, VerticalAlignment.Top, corner, corner, new Thickness(0), Cursors.SizeNWSE),
            (Edges.Bottom | Edges.Right, HorizontalAlignment.Right, VerticalAlignment.Bottom, corner, corner, new Thickness(0), Cursors.SizeNWSE),
            (Edges.Top | Edges.Right, HorizontalAlignment.Right, VerticalAlignment.Top, corner, corner, new Thickness(0), Cursors.SizeNESW),
            (Edges.Bottom | Edges.Left, HorizontalAlignment.Left, VerticalAlignment.Bottom, corner, corner, new Thickness(0), Cursors.SizeNESW),
        ];
        foreach (var (edges, h, v, w, ht, margin, cursor) in handles)
        {
            // 完全透明的像素收不到鼠标，用 1/255 的不透明度。
            var r = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                HorizontalAlignment = h,
                VerticalAlignment = v,
                Width = w,
                Height = ht,
                Margin = margin,
                Cursor = cursor,
            };
            r.MouseLeftButtonDown += (_, e) =>
            {
                if (S.ClickThrough) return;
                e.Handled = true;
                if (e.ClickCount == 2)
                {
                    // 双击上/下边框：恢复按“显示句数”算的高度。
                    if ((edges & (Edges.Top | Edges.Bottom)) != 0)
                    {
                        S.OverlayHeight = 0;
                        settingsService.Commit(SettingsChange.Appearance);
                    }
                    return;
                }
                resizing = new ResizeDrag(edges, CursorDip(), new Rect(Left, Top, ActualWidth, ActualHeight));
                r.CaptureMouse();
            };
            void EndResize()
            {
                if (resizing is not { } drag) return;
                resizing = null;
                if (r.IsMouseCaptured) r.ReleaseMouseCapture();
                S.OverlayWidth = Math.Round(Width);
                if ((drag.Edges & (Edges.Top | Edges.Bottom)) != 0) S.OverlayHeight = Math.Round(Height);
                SavePosition();
                settingsService.Commit(SettingsChange.Appearance);
            }
            r.MouseMove += (_, e) =>
            {
                if (resizing is not { } drag) return;
                // 松开鼠标的消息丢了：结束调整。
                if (e.LeftButton != MouseButtonState.Pressed) EndResize();
                else ResizeTo(drag);
            };
            r.MouseLeftButtonUp += (_, _) => EndResize();
            r.LostMouseCapture += (_, _) => EndResize();
            root.Children.Add(r);
        }
    }

    private void ResizeTo(ResizeDrag drag)
    {
        var cursor = CursorDip();
        double dx = cursor.X - drag.StartCursor.X, dy = cursor.Y - drag.StartCursor.Y;
        var f = drag.StartFrame;
        var work = SystemParameters.WorkArea;
        double width = f.Width, height = f.Height, left = f.Left, top = f.Top;
        if (drag.Edges.HasFlag(Edges.Left)) width = f.Width - dx;
        if (drag.Edges.HasFlag(Edges.Right)) width = f.Width + dx;
        if (drag.Edges.HasFlag(Edges.Top)) height = f.Height - dy;
        if (drag.Edges.HasFlag(Edges.Bottom)) height = f.Height + dy;
        width = Math.Clamp(width, MinSize.Width, Math.Max(MinSize.Width, work.Width));
        height = Math.Clamp(height, MinSize.Height, Math.Max(MinSize.Height, work.Height));
        // 被限制大小时，保持对边不动。
        if (drag.Edges.HasFlag(Edges.Left)) left = f.Right - width;
        if (drag.Edges.HasFlag(Edges.Top)) top = f.Bottom - height;

        if ((drag.Edges & (Edges.Top | Edges.Bottom)) != 0)
        {
            // 拖上下边框：改成自己想要的高度（松开时记到设置里）。
            Height = height;
            Top = top;
        }
        Width = width;
        Left = left;
    }

    private Point CursorDip()
    {
        NativeMethods.GetCursorPos(out var p);
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY);
    }

    // MARK: - 位置

    /// <summary>改了字号、句数等设置使窗口变高时，底边不动、向上生长（字幕贴着屏幕底部）。</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!positioned || resizing is not null || !e.HeightChanged || e.PreviousSize.Height <= 0) return;
        Top += e.PreviousSize.Height - e.NewSize.Height;
    }

    private void RestorePosition()
    {
        var work = SystemParameters.WorkArea;
        double height = ActualHeight > 0 ? ActualHeight : 120;
        if (S.OverlayLeft is { } left && S.OverlayBottom is { } bottom
            && left > SystemParameters.VirtualScreenLeft - 50
            && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50
            && bottom > SystemParameters.VirtualScreenTop + 50
            && bottom < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 50)
        {
            Left = left;
            Top = bottom - height;
        }
        else
        {
            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Bottom - 70 - height;
        }
        positioned = true;
    }

    public void ResetPosition()
    {
        S.OverlayLeft = null;
        S.OverlayBottom = null;
        S.OverlayWidth = DefaultWidth;
        S.OverlayHeight = 0;
        settingsService.Commit(SettingsChange.Appearance);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RestorePosition);
    }

    private void SavePosition()
    {
        S.OverlayLeft = Math.Round(Left);
        S.OverlayBottom = Math.Round(Top + ActualHeight);
        settingsService.Commit(SettingsChange.None);
    }

    protected override void OnClosed(EventArgs e)
    {
        topmostTimer.Stop();
        controller.Changed -= RequestRender;
        base.OnClosed(e);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    // MARK: - 部件

    /// <summary>一句字幕：原文（小字）+ 译文（大字）。</summary>
    private sealed class LineView : StackPanel
    {
        private readonly TextBlock source = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
        private readonly TextBlock translation = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Margin = new Thickness(0, 2, 0, 0) };
        private string signature = "";

        public DateTime Time { get; private set; }

        public LineView()
        {
            Margin = new Thickness(0, 0, 0, 8);
            Children.Add(source);
            Children.Add(translation);
        }

        public void Invalidate() => signature = "";

        public void Update(SubtitleLine line, DisplayMode mode, double sourceSize, double translationSize, FontFamily sourceFont, FontFamily targetFont)
        {
            Time = line.Time;
            var sig = $"{line.Source}\u0001{line.Translation}\u0001{line.State}\u0001{mode}\u0001{sourceSize}\u0001{translationSize}\u0001{sourceFont}\u0001{targetFont}";
            if (sig == signature) return;
            signature = sig;

            source.Visibility = mode == DisplayMode.TranslationOnly ? Visibility.Collapsed : Visibility.Visible;
            source.Text = line.SourceLabel is null ? line.Source : $"[{line.SourceLabel}] {line.Source}";
            source.FontSize = sourceSize;
            source.FontFamily = sourceFont;
            source.Opacity = mode == DisplayMode.SourceOnly ? 1 : 0.72;

            translation.Visibility = mode == DisplayMode.SourceOnly ? Visibility.Collapsed : Visibility.Visible;
            translation.FontFamily = targetFont;
            switch (line.State)
            {
                case LineState.Waiting:
                    Set(translation, "…", translationSize, FontWeights.Normal, 0.4, Brushes.White);
                    break;
                case LineState.Translating:
                    Set(translation, line.Translation.Length == 0 ? "…" : line.Translation, translationSize, FontWeights.SemiBold, 0.85, Brushes.White);
                    break;
                case LineState.Done:
                    var text = line.Note is null ? line.Translation : $"{line.Translation}  · {line.Note}";
                    Set(translation, text, translationSize, FontWeights.SemiBold, 1, Brushes.White);
                    if (line.Translation.Length == 0) translation.Visibility = Visibility.Collapsed;
                    break;
                case LineState.Failed:
                    Set(translation, Strings.Overlay_TranslationFailed, translationSize * 0.7, FontWeights.Normal, 0.9, Orange);
                    break;
            }
        }

        private static void Set(TextBlock t, string text, double size, FontWeight weight, double opacity, Brush brush)
        {
            t.Text = text;
            t.FontSize = size;
            t.FontWeight = weight;
            t.Opacity = opacity;
            t.Foreground = brush;
        }
    }

    /// <summary>正在说、还没送去翻译的一行（斜体、较淡）。</summary>
    private sealed class LiveLineView : TextBlock
    {
        public LiveLineView()
        {
            TextWrapping = TextWrapping.Wrap;
            FontStyle = FontStyles.Italic;
            Foreground = Brushes.White;
            Opacity = 0.55;
            Visibility = Visibility.Collapsed;
        }

        public void Update(string text, DisplayMode mode, double size, FontFamily font)
        {
            Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            Text = text;
            FontSize = size;
            FontFamily = font;
        }
    }

    /// <summary>一小行状态：有没有收到声音、识别是否清晰、翻译模型、待翻译句数、最近的错误。</summary>
    private sealed class StatusBarView : DockPanel
    {
        private readonly Rectangle[] meter = new Rectangle[5];
        private readonly TextBlock sound = new();
        private readonly TextBlock recognition = new();
        private readonly TextBlock engine = new() { Cursor = Cursors.Hand, ToolTip = Strings.Tip_ModelMenu };
        private readonly TextBlock backlogSeparator = Separator();
        private readonly TextBlock backlog = new() { Foreground = Orange };
        private readonly TextBlock alert = new() { Foreground = Orange, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };

        public StatusBarView(Action<FrameworkElement> onEngineClick)
        {
            Margin = new Thickness(18, 0, 18, 7);
            LastChildFill = true;
            TextElement.SetFontSize(this, 11);
            TextElement.SetForeground(this, new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)));

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var meterPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 6, 2) };
            for (int i = 0; i < meter.Length; i++)
            {
                meter[i] = new Rectangle { Width = 2.5, Height = 4 + i * 1.6, RadiusX = 1, RadiusY = 1, Margin = new Thickness(0, 0, 1.5, 0), VerticalAlignment = VerticalAlignment.Bottom };
                meterPanel.Children.Add(meter[i]);
            }
            left.Children.Add(meterPanel);
            left.Children.Add(sound);
            left.Children.Add(Separator());
            left.Children.Add(recognition);
            left.Children.Add(Separator());
            left.Children.Add(engine);
            left.Children.Add(backlogSeparator);
            left.Children.Add(backlog);
            engine.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                onEngineClick(engine);
            };
            engine.MouseLeftButtonDown += (_, e) => e.Handled = true; // 不要触发拖动
            SetDock(left, Dock.Left);
            Children.Add(left);
            Children.Add(alert);
        }

        private static TextBlock Separator() => new() { Text = "·", Margin = new Thickness(6, 0, 6, 0), Opacity = 0.6 };

        public void Update(SubtitleController c)
        {
            bool hasInput = c.SilentFor < 3; // 超过 3 秒没收到任何音频数据：采集断了（和“安静”不同）。
            for (int i = 0; i < meter.Length; i++)
            {
                meter[i].Fill = i < c.InputLevel * 5 ? Green : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            }
            sound.Text = !hasInput ? Strings.Overlay_NoInput : c.InputLevel > 0 ? Strings.Overlay_Receiving : Strings.Overlay_Quiet;
            if (hasInput) sound.ClearValue(TextBlock.ForegroundProperty);
            else sound.Foreground = Orange;
            recognition.Text = c.RecognitionDifficult ? Strings.Overlay_RecognitionHard : Strings.Overlay_RecognitionClear;
            recognition.ToolTip = c.RecognitionDifficult ? Strings.Tip_RecognitionHard : Strings.Tip_RecognitionClear;
            engine.Text = c.EngineName;
            int waiting = c.Book.Backlog;
            backlog.Text = waiting > 0 ? Strings.Format(Strings.Overlay_Backlog, waiting) : "";
            backlog.Visibility = backlogSeparator.Visibility = waiting > 0 ? Visibility.Visible : Visibility.Collapsed;
            alert.Text = c.Alert ?? "";
            alert.ToolTip = c.Alert;
        }
    }
}

/// <summary>按语言选字体：Segoe UI 显示拉丁字母，中日韩文字用对应的系统字体，避免日文汉字显示成中文字形。</summary>
internal static class TextFonts
{
    private static readonly Dictionary<string, FontFamily> Cache = [];

    public static FontFamily For(LanguageOption language)
    {
        var key = language.TranslationCode;
        if (Cache.TryGetValue(key, out var f)) return f;
        var families = key switch
        {
            "ja" => "Segoe UI, Yu Gothic UI, Meiryo UI, Microsoft YaHei UI",
            "ko" => "Segoe UI, Malgun Gothic, Microsoft YaHei UI",
            "zh-Hant" => "Segoe UI, Microsoft JhengHei UI, Microsoft YaHei UI",
            _ => "Segoe UI, Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic",
        };
        f = new FontFamily(families);
        Cache[key] = f;
        return f;
    }
}
