using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
using LiveSub.Core.Audio;
using LiveSub.Core.Subtitles;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace LiveSub.App.UI;

/// <summary>悬浮字幕上的快捷按钮和状态条的回调（由托盘程序提供）。</summary>
public sealed record OverlayActions(
    Action ToggleRunning,
    Action ToggleRecording,
    Action Lock,
    Action<AudioSourceMode> SelectAudioSource,
    Action ToggleAssistant,
    Action ShowSettings,
    Action Quit,
    Func<ContextMenu> ModelMenu,
    Action OpenMicrophoneSettings);

/// <summary>
/// 悬浮字幕窗口：无边框、透明背景上一块圆角半透明面板（深色外观黑底白字，浅色外观白底黑字，见 <see cref="OverlayPalette"/>）；
/// 一直在最上层、不进任务栏、不抢焦点（WS_EX_NOACTIVATE）。新用户的字幕在屏幕右上角，面积是屏幕的 1/16（与 macOS 相同）。
/// 窗口大小只随设置变化，不随字幕内容忽高忽低：高度默认按“显示句数”算，字幕在框里滚动，可往上翻看历史；
/// “正在说”放在滚动区下面，至少占一行（“两者”模式下两个音源同时在说时是两行，窗口高度不变，滚动区让出位置）。
/// “两者”模式下每句原文和“正在说”前面有小标签，标出来自麦克风还是系统音频（与 macOS 相同）。
/// 鼠标移上去出现快捷按钮（与 macOS 相同：开始/停止、录音、锁定、音源、AI 助手、设置、退出），停在按钮上立刻显示它的名字。
/// 没开始时中间是“开始翻译”按钮。收起字幕用快捷键或托盘菜单。
/// 按住拖动；拖边框和四角调整大小（拖上下边框改成自己想要的高度，双击上下边框恢复按句数算的高度）；
/// 锁定后点击穿透（WS_EX_TRANSPARENT），点右上角的锁或在托盘菜单里解锁。
/// </summary>
public sealed class OverlayWindow : Window
{
    private static readonly SolidColorBrush Orange = OverlayPalette.Orange;
    private static readonly SolidColorBrush Green = Freeze(new SolidColorBrush(Color.FromArgb(0xD9, 0x4C, 0xD9, 0x64)));
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    /// <summary>AI 助手按钮的 ✨（与标题栏相同，单色字形）。</summary>
    private static readonly FontFamily SymbolFont = new("Segoe UI Emoji, Segoe UI Symbol");
    /// <summary>音源标签（界面文字，“麦克风”或 “Microphone”）的字体。</summary>
    private static readonly FontFamily TagFont = new("Segoe UI, Microsoft YaHei UI");
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
    /// 下边距小一些：下面紧接着“正在说”一行（总高度与把“正在说”放在滚动区里时相同）。
    /// </summary>
    private readonly ScrollViewer scroller = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(18, 12, 18, 6),
        Focusable = false,
    };
    private readonly Grid scrollArea = new();
    private readonly Border scrollThumb = new()
    {
        Width = 4,
        CornerRadius = new CornerRadius(2),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock placeholder = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
    /// <summary>
    /// “正在说”（与 macOS 的 liveFooter 相同）：滚动区下面每个正在说的音源一行，至少占一行高，它的变化不会把上面的字幕往上推；
    /// 没有文字时也占着这一行，滚动区的高度不变。“两者”模式下两个音源同时在说时有两行。
    /// </summary>
    private readonly Border liveFooter = new() { Padding = new Thickness(18, 0, 18, 0), ClipToBounds = true };
    private readonly StackPanel liveRows = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly Dictionary<Guid, LineView> views = [];
    private readonly StatusBarView statusBar;
    /// <summary>麦克风被隐私设置挡住时，字幕下方的提示和“打开麦克风设置”按钮。不受“显示状态条”影响。</summary>
    private readonly Border microphoneNotice;
    private readonly TextBlock microphoneNoticeText = new()
    {
        Foreground = Orange,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly StackPanel toolbarButtons = new() { Orientation = Orientation.Horizontal };
    private readonly Border toolbar;
    /// <summary>鼠标停在快捷按钮上时立刻显示的名字（与 macOS 相同）。</summary>
    private readonly InstantTip tips;
    private Button? runButton;
    private Button? recordButton;
    private Button? sourceButton;
    private Button? assistantButton;
    /// <summary>没开始时字幕中间的“开始翻译”按钮（与 macOS 相同），下面一行是音源和语言，出错时是原因。</summary>
    private readonly Grid startLayer = new();
    private readonly Border startDim = new() { CornerRadius = new CornerRadius(14), IsHitTestVisible = false };
    private Button? startButton;
    private readonly StackPanel startContent = new() { Orientation = Orientation.Horizontal };
    /// <summary>不接收鼠标：按在这行字上也是拖动字幕。</summary>
    private readonly TextBlock startDetails = new()
    {
        FontSize = 11,
        TextTrimming = TextTrimming.CharacterEllipsis,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(18, 8, 18, 0),
        IsHitTestVisible = false,
    };
    /// <summary>第一次使用时开始按钮下面的提示（基础翻译够用，更准的翻译、AI 助手和纠正口音要换模型）。</summary>
    private readonly TextBlock startHint = new()
    {
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(24, 4, 24, 0),
        IsHitTestVisible = false,
    };
    private string startSignature = "";
    /// <summary>锁定后鼠标移到字幕上时右上角的锁，点一下解锁。第一次用到时才创建。</summary>
    private LockBadgeWindow? lockBadge;
    private readonly Button backToLive;
    private readonly DispatcherTimer topmostTimer;
    /// <summary>
    /// 要一直在字幕上面的窗口（打开着的 AI 助手面板），由托盘程序设置。定时提到最上层时先提字幕、再提它：
    /// 两个窗口重叠时（1080p 屏幕上默认位置就会重叠），面板的输入框不会被字幕盖住。
    /// </summary>
    public Window? KeepAbove { get; set; }
    /// <summary>锁定后窗口收不到鼠标事件（MouseEnter 不再触发）：改为定时看鼠标在不在字幕上。</summary>
    private readonly DispatcherTimer lockedHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    /// <summary>
    /// 正在显示的菜单（音源或状态条上的模型菜单）。打开着时快捷按钮不消失，鼠标移到菜单上、离开字幕也一样（macOS 的 menuOpen）。
    /// </summary>
    private ContextMenu? openMenu;
    private bool hovering;

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

        linesPanel.SetResourceReference(EffectProperty, OverlayPalette.Shadow);
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, OverlayPalette.Fg);
        scrollThumb.SetResourceReference(Border.BackgroundProperty, OverlayPalette.Fg45);
        // 浅色外观下白底叠在白色网页上看不出边缘，加一圈很淡的描边（与 macOS 相同）。
        panel.BorderThickness = new Thickness(1);
        panel.SetResourceReference(Border.BorderBrushProperty, OverlayPalette.Fg08);
        linesPanel.Children.Add(placeholder);
        scroller.Content = linesPanel;
        scrollArea.Children.Add(scroller);
        scrollArea.Children.Add(scrollThumb);
        liveRows.SetResourceReference(EffectProperty, OverlayPalette.Shadow);
        liveFooter.Child = liveRows;
        // 宽度变了，“正在说”能放下的字数也变了。
        liveFooter.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged) RequestRender();
        };

        // 从下往上：状态条、麦克风提示、“正在说”，剩下的是字幕滚动区。
        statusBar = new StatusBarView(el => OpenMenu(actions.ModelMenu(), el, PlacementMode.Top, () => statusBar!.ResetEngineHover()));
        DockPanel.SetDock(statusBar, Dock.Bottom);
        layout.Children.Add(statusBar);
        microphoneNotice = BuildMicrophoneNotice();
        DockPanel.SetDock(microphoneNotice, Dock.Bottom);
        layout.Children.Add(microphoneNotice);
        DockPanel.SetDock(liveFooter, Dock.Bottom);
        layout.Children.Add(liveFooter);
        layout.Children.Add(scrollArea);
        panel.Child = layout;
        root.Children.Add(panel);
        BuildStartButton();
        root.Children.Add(startLayer);

        toolbar = new Border
        {
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(4),
            Margin = new Thickness(6),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Child = toolbarButtons,
        };
        toolbar.SetResourceReference(Border.BackgroundProperty, OverlayPalette.Bg80);
        toolbar.SetResourceReference(Border.BorderBrushProperty, OverlayPalette.Fg10);
        tips = new InstantTip(root);
        BuildToolbar();
        root.Children.Add(toolbar);

        lockedHoverTimer.Tick += (_, _) => UpdateLockedBadge();

        backToLive = FlatButton(Strings.Overlay_BackToLive, null);
        backToLive.FontSize = 12;
        backToLive.FontWeight = FontWeights.SemiBold;
        backToLive.Padding = new Thickness(10, 4, 10, 4);
        backToLive.SetResourceReference(BackgroundProperty, OverlayPalette.Bg70);
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

        root.MouseEnter += (_, _) =>
        {
            hovering = true;
            UpdateToolbar();
        };
        root.MouseLeave += (_, _) =>
        {
            hovering = false;
            UpdateToolbar();
        };
        // 字幕被收起（快捷键、托盘菜单）时，开着的菜单一起关掉。
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible && openMenu is not null) openMenu.IsOpen = false;
        };
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
        settingsService.Changed += OnSettingsChanged;
        // 外观变了：字幕背景按新的颜色和不透明度重画（其他颜色由资源自动换）。
        OverlayPalette.Changed += ApplySettings;

        // 全屏视频等其他置顶窗口出现后，定期把字幕重新提到最上层（不激活），再把 AI 助手提到字幕上面。
        // 只用这一个计时器：两个窗口各自定时提会轮流盖住对方。锁归字幕窗口所有，系统让它总在字幕上面，不用另外提。
        topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        topmostTimer.Tick += (_, _) =>
        {
            // 系统对话框开着时不提，否则会盖住对话框的按钮（见 SystemDialogs）。
            if (!IsVisible || SystemDialogs.IsOpen) return;
            BringToTop(this);
            if (KeepAbove is { IsVisible: true } above) BringToTop(above);
            // 用 LiveSub 时设置、模型服务等窗口在字幕和 AI 助手上面（与 macOS 相同）。
            WindowLevels.Update();
        };
        topmostTimer.Start();

        ApplySettings();
        var (left, top) = OverlayLayout.TopRight(ToScreenRect(SystemParameters.WorkArea), Width);
        Left = left;
        Top = top;
    }

    private void OnSettingsChanged(SettingsChange change)
    {
        if (change.HasFlag(SettingsChange.Appearance) || change.HasFlag(SettingsChange.Translation)) ApplySettings();
        // 录音、AI 助手开关等不影响布局，只更新快捷按钮。
        else RequestRender();
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
            lockedHoverTimer.Start();
        }
        else
        {
            NativeMethods.SetExStyle(hwnd, 0, NativeMethods.WS_EX_TRANSPARENT);
            lockedHoverTimer.Stop();
        }
        // 锁定（或点锁解锁）的那一刻鼠标多半就在字幕上，直接按鼠标位置更新一次。
        hovering = CursorInside();
        UpdateToolbar();
        UpdateLockedBadge();
    }

    /// <summary>鼠标在字幕上或菜单开着时显示快捷按钮；锁定时不显示。</summary>
    private void UpdateToolbar() =>
        toolbar.Visibility = !S.ClickThrough && (hovering || openMenu is not null) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>鼠标在不在字幕窗口上。鼠标位置和窗口范围都用屏幕像素，不受缩放比例影响；锁定后收不到鼠标事件时也能用。</summary>
    private bool CursorInside() =>
        IsVisible && NativeMethods.GetCursorPos(out var p) && NativeMethods.GetWindowRect(this.Handle(), out var r)
        && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    private void UpdateLockedBadge()
    {
        bool inside = S.ClickThrough && CursorInside();
        if (!inside)
        {
            lockBadge?.Hide();
            return;
        }
        // 点一下解锁：和托盘菜单里取消“锁定字幕”一样。
        lockBadge ??= new LockBadgeWindow(actions.Lock) { Owner = this };
        // 放在快捷按钮的位置（右上角，边距 6）。
        lockBadge.Left = Left + ActualWidth - 6 - lockBadge.Width;
        lockBadge.Top = Top + 6;
        if (!lockBadge.IsVisible) lockBadge.Show();
    }

    /// <summary>显示但不激活。</summary>
    public void ShowNoActivate()
    {
        Show();
        RequestRender();
    }

    /// <summary>不改设置的状态变了（例如打开、收起 AI 助手）：更新快捷按钮。</summary>
    public void Refresh() => RequestRender();

    /// <summary>提到置顶窗口的最上层，不激活。</summary>
    private static void BringToTop(Window window) => NativeMethods.SetWindowPos(window.Handle(), NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

    // MARK: - 设置

    private void ApplySettings()
    {
        panel.Background = OverlayPalette.BgBrush(S.BackgroundOpacity);
        Width = Math.Max(MinSize.Width, S.OverlayWidth);
        Height = Math.Max(MinSize.Height, S.OverlayHeight > 0 ? S.OverlayHeight : AutoHeight());
        placeholder.FontSize = S.SourceFontSize;
        placeholder.FontFamily = TextFonts.For(S.Source);
        liveFooter.MinHeight = LiveFooterHeight();
        statusBar.Visibility = Visibility.Collapsed;
        foreach (var view in views.Values) view.Invalidate();
        ApplyClickThrough();
        RequestRender();
    }

    /// <summary>
    /// 没拖过上下边框时的高度：正好放下最近 N 句（每句按一行原文、一行译文算），加上“正在说”一行和状态条。
    /// 只随设置变化，字幕生成时窗口不会忽高忽低；句子折成多行时，放不下的部分往上滚出去。
    /// </summary>
    private double AutoHeight()
    {
        var mode = S.DisplayMode;
        double translationLine = S.TranslationFontSize * LineSpacing(TextFonts.For(S.Target));
        double sentence = (mode == DisplayMode.TranslationOnly ? 0 : SourceLineHeight())
            + (mode == DisplayMode.SourceOnly ? 0 : translationLine + 2)
            + LineGap;
        double lines = scroller.Padding.Top + Math.Clamp(S.VisibleLines, 1, 6) * sentence + scroller.Padding.Bottom;
        return Math.Ceiling(lines + LiveFooterHeight() + (S.ShowStatusBar ? StatusBarHeight : 0));
    }

    private double SourceLineHeight() => S.SourceFontSize * LineSpacing(TextFonts.For(S.Source));

    /// <summary>“正在说”至少占的高度：一行原文加 6（与 macOS 相同）。</summary>
    private double LiveFooterHeight() => SourceLineHeight() + 6;

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
                int index = olderThanShown ? linesPanel.Children.IndexOf(firstExisting!) : linesPanel.Children.Count;
                linesPanel.Children.Insert(Math.Max(0, index), view);
                view.Update(line, mode, S.SourceFontSize, S.TranslationFontSize, sourceFont, targetFont);
                FadeIn(view);
            }
            else
            {
                view.Update(line, mode, S.SourceFontSize, S.TranslationFontSize, sourceFont, targetFont);
            }
        }

        // 每个正在说的音源一行；噪音有时被识别成一个“.”，不显示（与 macOS 相同）。
        var live = controller.LiveRows;
        UpdateLiveRows(live, sourceFont);
        var notice = controller.MicrophoneNotice;
        microphoneNotice.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
        microphoneNoticeText.Text = notice ?? "";
        // 没开始时中间是“开始翻译”按钮，这里不再写一遍（与 macOS 相同）。
        placeholder.Visibility = controller.IsRunning && visible.Count == 0 && live.Count == 0 && notice is null
            ? Visibility.Visible : Visibility.Collapsed;
        placeholder.Text = S.AudioSource switch
        {
            // 写明在听哪里的声音：对着麦克风说话、音源却只选了系统音频时不会出字幕。
            AudioSourceMode.Microphone => Strings.Overlay_ListeningMicrophone,
            AudioSourceMode.Both => Strings.Overlay_ListeningBoth,
            _ => Strings.Overlay_ListeningSystem,
        };
        UpdateStartButton(visible.Count > 0);

        bool showStatus = S.ShowStatusBar && controller.IsRunning;
        statusBar.Visibility = showStatus ? Visibility.Visible : Visibility.Collapsed;
        if (showStatus) statusBar.Update(controller);
        UpdateToolButtons();

        if (atBottom)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => scroller.ScrollToEnd());
        }
    }

    /// <summary>“正在说”的各行：行数跟着正在说的音源数变（最多两行），已有的行复用。</summary>
    private void UpdateLiveRows(List<LiveRow> rows, FontFamily font)
    {
        while (liveRows.Children.Count < rows.Count)
        {
            liveRows.Children.Add(new LiveRowView { Margin = new Thickness(0, liveRows.Children.Count == 0 ? 0 : 4, 0, 0) });
        }
        while (liveRows.Children.Count > rows.Count) liveRows.Children.RemoveAt(liveRows.Children.Count - 1);
        double width = liveFooter.ActualWidth - liveFooter.Padding.Left - liveFooter.Padding.Right;
        for (int i = 0; i < rows.Count; i++)
        {
            ((LiveRowView)liveRows.Children[i]).Update(rows[i], S.SourceFontSize, font, width);
        }
    }

    /// <summary>音源标签（与 macOS 相同）：10 号中等粗细的字，半透明胶囊底。</summary>
    private static Border SourceTag(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontFamily = TagFont,
            FontSize = 10,
            FontWeight = FontWeights.Medium,
            FontStyle = FontStyles.Normal,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, OverlayPalette.Fg80);
        var tag = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
        };
        tag.SetResourceReference(Border.BackgroundProperty, OverlayPalette.Fg18);
        return tag;
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

    private Border BuildMicrophoneNotice()
    {
        var open = FlatButton(Strings.Overlay_OpenMicrophoneSettings, actions.OpenMicrophoneSettings);
        open.FontSize = 12;
        open.FontWeight = FontWeights.SemiBold;
        open.Padding = new Thickness(10, 4, 10, 4);
        open.SetResourceReference(BackgroundProperty, OverlayPalette.Fg18);
        open.Margin = new Thickness(12, 0, 0, 0);
        open.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(open, Dock.Right);
        row.Children.Add(open);
        row.Children.Add(microphoneNoticeText);
        return new Border { Child = row, Margin = new Thickness(18, 4, 18, 8), Visibility = Visibility.Collapsed };
    }

    // MARK: - 快捷按钮和菜单

    /// <summary>
    /// 与 macOS 相同的七个：开始/停止、录音、锁定、音源（菜单）、AI 助手、设置、退出。字幕一直在最上层，没有“置顶”开关；
    /// 字幕记录在设置 › 记录里；收起字幕用快捷键或托盘菜单。鼠标停在按钮上立刻在下面显示它的名字（名字随状态变）。
    /// </summary>
    private void BuildToolbar()
    {
        runButton = ToolButton("\uE768", () => controller.IsRunning || controller.IsStarting ? Strings.Tool_Stop : Strings.Tool_Start,
            actions.ToggleRunning); // Play / Pause
        // 录音：打开后，字幕运行期间把听到的声音存进记录文件夹（和字幕记录同名）。
        recordButton = ToolButton("\uE7C8", () => controller.RecordButtonOn ? Strings.Tool_StopRecording : Strings.Tool_Record, actions.ToggleRecording);
        // 没锁定时显示开口的锁（锁定后右上角是闭合的锁，见 LockBadgeWindow）。
        var lockButton = ToolButton("\uE785", () => Strings.Tool_Lock, actions.Lock);
        sourceButton = ToolButton("", () => Strings.Format(Strings.Tool_Source, S.AudioSource.Title()), () => ShowSourceMenu(sourceButton!));
        assistantButton = ToolButton("\u2728", () => S.AssistantVisible ? Strings.Menu_HideAssistant : Strings.Menu_ShowAssistant,
            actions.ToggleAssistant);
        assistantButton.FontFamily = SymbolFont;
        var settings = ToolButton("\uE713", () => Strings.Tool_Settings, actions.ShowSettings); // Setting
        var quit = ToolButton("\uE711", () => Strings.Format(Strings.Tool_Quit, Strings.AppName), actions.Quit); // Cancel
        foreach (var b in new[] { runButton, recordButton, lockButton, sourceButton, assistantButton, settings, quit })
        {
            toolbarButtons.Children.Add(b);
        }
        UpdateToolButtons();
    }

    /// <summary>快捷按钮跟着状态变：开始/停止、录音时变红、音源的图标、AI 助手打开时变黄（浅色外观为橙色）。</summary>
    private void UpdateToolButtons()
    {
        if (runButton is null || recordButton is null || sourceButton is null || assistantButton is null) return;
        runButton.Content = controller.IsRunning || controller.IsStarting ? "\uE769" : "\uE768";
        // 按实际是否在录：录音出错停下时不再显示成红色。
        SetTint(recordButton, controller.RecordButtonOn ? OverlayPalette.Red : null);
        SetTint(assistantButton, S.AssistantVisible ? OverlayPalette.AssistantTint : null);
        if (sourceButton.Tag is not AudioSourceMode shown || shown != S.AudioSource)
        {
            sourceButton.Tag = S.AudioSource;
            sourceButton.Content = SourceIcon(S.AudioSource);
        }
        tips.Refresh();
    }

    /// <summary>按钮的颜色：<paramref name="tint"/> 为 null 时用前景色（随外观变）。</summary>
    private static void SetTint(Button button, Brush? tint)
    {
        if (tint is null) button.SetResourceReference(ForegroundProperty, OverlayPalette.Fg);
        else button.Foreground = tint;
    }

    /// <summary>音源的图标：麦克风、扬声器；“两者”时两个小图标并排（相当于 macOS 的 waveform.badge.mic）。</summary>
    private static object SourceIcon(AudioSourceMode mode)
    {
        if (mode != AudioSourceMode.Both) return mode == AudioSourceMode.Microphone ? "\uE720" : "\uE767"; // Microphone / Volume
        var both = new StackPanel { Orientation = Orientation.Horizontal };
        both.Children.Add(new TextBlock { Text = "\uE720", FontFamily = IconFont, FontSize = 9 });
        both.Children.Add(new TextBlock { Text = "\uE767", FontFamily = IconFont, FontSize = 9, Margin = new Thickness(1, 0, 0, 0) });
        return both;
    }

    /// <summary>音源菜单：麦克风、系统音频、两者（当前的打勾）。</summary>
    private void ShowSourceMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu();
        foreach (var (mode, title, _) in AppShell.AudioSources)
        {
            menu.Items.Add(PopupMenus.Item(title, () => actions.SelectAudioSource(mode), S.AudioSource == mode));
        }
        OpenMenu(menu, anchor, PlacementMode.Bottom);
    }

    /// <summary>弹出字幕上的菜单（不激活字幕窗口），记下它开着：这期间快捷按钮不消失。</summary>
    /// <param name="onClosed">菜单关上时（例如复原状态条上模型名的悬停颜色）。</param>
    private void OpenMenu(ContextMenu menu, FrameworkElement anchor, PlacementMode placement, Action? onClosed = null)
    {
        if (openMenu is not null) openMenu.IsOpen = false;
        openMenu = menu;
        // 菜单打开期间收不到鼠标移出，先收起按钮的名字。
        tips.Hide();
        menu.Closed += (_, _) =>
        {
            onClosed?.Invoke();
            if (!ReferenceEquals(openMenu, menu)) return;
            openMenu = null;
            // 菜单开着时鼠标可能离开了字幕（MouseLeave 已经来过），也可能停在字幕上。
            hovering = CursorInside();
            UpdateToolbar();
        };
        PopupMenus.Open(menu, anchor, placement);
        UpdateToolbar();
    }

    /// <summary>快捷按钮：鼠标停在上面时底下出现浅色圆形、图标略微放大（与 macOS 相同），并立刻显示名字。</summary>
    private Button ToolButton(string glyph, Func<string> name, Action action)
    {
        var b = IconButton(glyph, action);
        b.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        b.RenderTransform = scale;
        b.MouseEnter += (_, _) => scale.ScaleX = scale.ScaleY = 1.15;
        b.MouseLeave += (_, _) => scale.ScaleX = scale.ScaleY = 1;
        tips.Attach(b, name);
        return b;
    }

    private static Button IconButton(string glyph, Action action)
    {
        var b = FlatButton(glyph, action);
        b.FontFamily = IconFont;
        b.FontSize = 12;
        b.FontWeight = FontWeights.SemiBold;
        b.Width = 26;
        b.Height = 24;
        b.Margin = new Thickness(1, 0, 1, 0);
        return b;
    }

    /// <summary>
    /// 无边框的扁平按钮；鼠标移上去时盖上一层淡淡的前景色（不改按钮自己的底色，底色可以引用外观资源）。
    /// 文字颜色默认是前景色，跟着外观变。
    /// </summary>
    internal static Button FlatButton(string content, Action? action)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Background)) { RelativeSource = RelativeSource.TemplatedParent });
        var hover = new FrameworkElementFactory(typeof(Border), "hover");
        hover.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        hover.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        hover.SetBinding(Border.PaddingProperty, new Binding(nameof(Padding)) { RelativeSource = RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        hover.AppendChild(presenter);
        border.AppendChild(hover);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var trigger = new Trigger { Property = IsMouseOverProperty, Value = true };
        trigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(OverlayPalette.Fg15), "hover"));
        template.Triggers.Add(trigger);

        var b = new Button
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Focusable = false,
            Cursor = Cursors.Hand,
            Template = template,
        };
        b.SetResourceReference(ForegroundProperty, OverlayPalette.Fg);
        if (action is not null) b.Click += (_, _) => action();
        return b;
    }

    /// <summary>
    /// “开始翻译”（与 macOS 相同）：醒目的胶囊按钮，鼠标停上去略微放大；准备中（下载、加载识别模型）时不可点。
    /// 下面一行是音源和语言；上次出错时是原因；准备中时是进度。上次的字幕压暗，开始按钮更醒目。
    /// 空白处不接收鼠标：按住那里照样拖动字幕。
    /// </summary>
    private void BuildStartButton()
    {
        var pill = new FrameworkElementFactory(typeof(Border));
        pill.SetValue(Border.CornerRadiusProperty, new CornerRadius(22));
        pill.SetValue(Border.PaddingProperty, new Thickness(22, 10, 22, 10));
        pill.SetBinding(Border.BackgroundProperty, new Binding(nameof(Background)) { RelativeSource = RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        pill.AppendChild(presenter);
        var shadow = new DropShadowEffect { Color = Colors.Black, Opacity = 0.35, BlurRadius = 8, ShadowDepth = 2, Direction = 270 };
        var scale = new ScaleTransform(1, 1);
        var button = new Button
        {
            Content = startContent,
            BorderThickness = new Thickness(0),
            Focusable = false,
            Cursor = Cursors.Hand,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Template = new ControlTemplate(typeof(Button)) { VisualTree = pill },
            HorizontalAlignment = HorizontalAlignment.Center,
            Effect = shadow,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
        };
        button.SetResourceReference(BackgroundProperty, OverlayPalette.Fg);
        button.SetResourceReference(ForegroundProperty, OverlayPalette.Bg);
        button.MouseEnter += (_, _) =>
        {
            if (!button.IsEnabled) return;
            scale.ScaleX = scale.ScaleY = 1.06;
            shadow.Opacity = 0.5;
            shadow.BlurRadius = 12;
        };
        button.MouseLeave += (_, _) =>
        {
            scale.ScaleX = scale.ScaleY = 1;
            shadow.Opacity = 0.35;
            shadow.BlurRadius = 8;
        };
        button.Click += (_, _) => actions.ToggleRunning();
        startButton = button;

        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(button);
        center.Children.Add(startDetails);
        startHint.SetResourceReference(TextBlock.ForegroundProperty, OverlayPalette.Fg60);
        center.Children.Add(startHint);
        startLayer.Children.Add(startDim);
        startLayer.Children.Add(center);
    }

    /// <param name="hasLines">已经有字幕：压暗它们，开始按钮更醒目。</param>
    private void UpdateStartButton(bool hasLines)
    {
        bool show = !controller.IsRunning;
        startLayer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || startButton is null) return;
        bool starting = controller.IsStarting;
        startButton.IsEnabled = !starting;
        startDim.Background = hasLines ? OverlayPalette.BgBrush(0.5) : null;
        var src = Strings.IsChinese ? S.Source.DisplayName : S.Source.EnglishName;
        var dst = Strings.IsChinese ? S.Target.DisplayName : S.Target.EnglishName;
        // 准备中显示进度（下载识别模型可能要一会儿），上次出错时显示原因，否则显示音源和语言。
        var (details, error) = starting ? (controller.Status, false)
            : controller.Alert is { } alert ? (alert, true)
            : ($"{S.AudioSource.Title()} · {src} → {dst}", false);
        startDetails.Text = details;
        startHint.Text = Strings.Overlay_FirstRunHint;
        startHint.Visibility = S.HasStartedOnce != true && !starting && controller.Alert is null && !S.TranslationModel.IsLanguageModel
            ? Visibility.Visible : Visibility.Collapsed;
        if (error) startDetails.Foreground = Orange;
        else startDetails.SetResourceReference(TextBlock.ForegroundProperty, OverlayPalette.Fg60);
        var signature = starting ? "starting" : "start";
        if (signature == startSignature) return;
        startSignature = signature;
        startContent.Children.Clear();
        if (starting)
        {
            startContent.Children.Add(Spinner());
            startContent.Children.Add(new TextBlock { Text = Strings.Status_Preparing, Margin = new Thickness(8, 0, 0, 0) });
        }
        else
        {
            startContent.Children.Add(new TextBlock
            {
                Text = "\uE768", // Play
                FontFamily = IconFont,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 8, 0),
            });
            startContent.Children.Add(new TextBlock { Text = Strings.Overlay_StartTranslating });
        }
    }

    /// <summary>转圈的小圆弧（准备中）。只在显示着的时候转：一直运行的动画会让 WPF 不停地重绘。</summary>
    private static FrameworkElement Spinner()
    {
        var spinner = new System.Windows.Shapes.Ellipse
        {
            Width = 14,
            Height = 14,
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 9, 7 },
            RenderTransformOrigin = new Point(0.5, 0.5),
            VerticalAlignment = VerticalAlignment.Center,
        };
        spinner.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, OverlayPalette.Bg);
        var rotate = new RotateTransform();
        spinner.RenderTransform = rotate;
        spinner.Loaded += (_, _) => rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        spinner.Unloaded += (_, _) => rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        return spinner;
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
                    // 双击上/下边框：恢复默认高度（设置里的“显示最近 N 句”2026-10-23 按用户决定去掉了）。
                    if ((edges & (Edges.Top | Edges.Bottom)) != 0)
                    {
                        S.OverlayHeight = AppSettings.DefaultOverlaySize.Height;
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

    /// <summary>
    /// 改了字号、句数等设置使窗口变高时，底边不动、向上生长；超出屏幕时推回屏幕内
    /// （默认放在右上角，往上长就会出界；与 macOS 的 fit 相同）。
    /// </summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!positioned || resizing is not null || !e.HeightChanged || e.PreviousSize.Height <= 0) return;
        Top += e.PreviousSize.Height - e.NewSize.Height;
        KeepOnScreen();
    }

    private void KeepOnScreen()
    {
        var (left, top) = OverlayLayout.Clamp(new ScreenRect(Left, Top, ActualWidth, ActualHeight), ToScreenRect(CurrentWorkArea()));
        if (Math.Abs(left - Left) > 0.5) Left = left;
        if (Math.Abs(top - Top) > 0.5) Top = top;
    }

    private void RestorePosition()
    {
        double height = ActualHeight > 0 ? ActualHeight : Height;
        if (S.OverlayLeft is { } left && S.OverlayBottom is { } bottom
            && left > SystemParameters.VirtualScreenLeft - 50
            && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50
            && bottom > SystemParameters.VirtualScreenTop + 50
            && bottom < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 50)
        {
            // 存下的底边可能是窗口变高之前的（例如双击边框换成按句数算的高度、被推回屏幕内之后没有再存），
            // 照着放会伸出屏幕上边（2026-10-23 Windows 实测 Top = -307），所以上边不超出虚拟屏幕。
            Left = left;
            Top = Math.Max(bottom - height, SystemParameters.VirtualScreenTop);
        }
        else
        {
            // 默认放在主屏幕右上角，离边缘留一点距离（与 macOS 相同）。
            PlaceTopRightOfPrimary();
        }
        positioned = true;
    }

    /// <summary>
    /// 放到主屏幕右上角，按物理像素放：几块屏幕缩放不同、字幕在另一块屏幕上时，Left、Top 按字幕当前所在屏幕的缩放换算，
    /// 用主屏幕的坐标去设会放偏（甚至放到别的屏幕上）。先把窗口挪到主屏幕上（系统按主屏幕的缩放调整它的大小），排好版再放到右上角。
    /// </summary>
    /// <param name="moved">已经挪过一次（窗口大部分落在旁边的屏幕上时，系统仍认为它在那块屏幕上：不再挪，直接放）。</param>
    private void PlaceTopRightOfPrimary(bool moved = false)
    {
        var hwnd = this.Handle();
        var primary = NativeMethods.PrimaryMonitor();
        if (hwnd == IntPtr.Zero || NativeMethods.WorkArea(primary) is not { } work)
        {
            (Left, Top) = OverlayLayout.TopRight(ToScreenRect(SystemParameters.WorkArea), Width);
            return;
        }
        const uint flags = NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;
        if (!moved && NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST) != primary)
        {
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, work.Left, work.Top, 0, 0, flags);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => PlaceTopRightOfPrimary(moved: true));
            return;
        }
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var (left, top) = OverlayLayout.TopRight(new ScreenRect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top),
            r.Right - r.Left, OverlayLayout.Margin * scale);
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(left), (int)Math.Round(top), 0, 0, flags);
    }

    /// <summary>“恢复默认”：调用方已经把大小、位置设回默认（<see cref="AppSettings.ResetOverlay"/>）；等按新的大小排好版再放回右上角。</summary>
    public void ResetPosition() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RestorePosition);

    private static ScreenRect ToScreenRect(Rect r) => new(r.Left, r.Top, r.Width, r.Height);

    /// <summary>字幕所在屏幕的工作区（与设备无关的像素）。</summary>
    private Rect CurrentWorkArea()
    {
        var hwnd = this.Handle();
        var screen = hwnd != IntPtr.Zero ? System.Windows.Forms.Screen.FromHandle(hwnd) : System.Windows.Forms.Screen.PrimaryScreen;
        if (screen is null) return SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var r = screen.WorkingArea;
        return new Rect(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY, r.Width / dpi.DpiScaleX, r.Height / dpi.DpiScaleY);
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
        lockedHoverTimer.Stop();
        if (openMenu is not null) openMenu.IsOpen = false;
        lockBadge?.Close();
        controller.Changed -= RequestRender;
        settingsService.Changed -= OnSettingsChanged;
        OverlayPalette.Changed -= ApplySettings;
        base.OnClosed(e);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    // MARK: - 部件

    /// <summary>
    /// 锁定时右上角的锁：单独的小窗口，只有它能点，字幕窗口本身仍然点击穿透（WS_EX_TRANSPARENT 作用于整个窗口）。
    /// 归字幕窗口所有（Owner），总在字幕上面；同样不抢焦点。
    /// </summary>
    private sealed class LockBadgeWindow : Window
    {
        public LockBadgeWindow(Action onUnlock)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            // 和快捷按钮一样大：按钮 26×24，加上 4 的内边距和 1 的描边。
            Width = 38;
            Height = 34;
            var unlock = IconButton("\uE72E", onUnlock); // Lock
            unlock.ToolTip = Strings.Tool_Unlock;
            var badge = new Border
            {
                CornerRadius = new CornerRadius(15),
                Padding = new Thickness(4),
                BorderThickness = new Thickness(1),
                Child = unlock,
            };
            badge.SetResourceReference(Border.BackgroundProperty, OverlayPalette.Bg80);
            badge.SetResourceReference(Border.BorderBrushProperty, OverlayPalette.Fg10);
            Content = badge;
            SourceInitialized += (_, _) =>
            {
                var hwnd = this.Handle();
                NativeMethods.SetExStyle(hwnd, NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW, 0);
                HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            };
        }
    }

    /// <summary>一句字幕：原文（小字）+ 译文（大字）。</summary>
    private sealed class LineView : StackPanel
    {
        private readonly TextBlock source = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock translation = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        private string signature = "";
        private string sourceSignature = "";

        public DateTime Time { get; private set; }

        public LineView()
        {
            Margin = new Thickness(0, 0, 0, 8);
            source.SetResourceReference(TextBlock.ForegroundProperty, OverlayPalette.Fg);
            Children.Add(source);
            Children.Add(translation);
        }

        public void Invalidate() => signature = sourceSignature = "";

        public void Update(SubtitleLine line, DisplayMode mode, double sourceSize, double translationSize, FontFamily sourceFont, FontFamily targetFont)
        {
            Time = line.Time;
            var sig = $"{line.SourceLabel}\u0001{line.Source}\u0001{line.Translation}\u0001{line.State}\u0001{mode}\u0001{sourceSize}\u0001{translationSize}\u0001{sourceFont}\u0001{targetFont}";
            if (sig == signature) return;
            signature = sig;

            source.Visibility = mode == DisplayMode.TranslationOnly ? Visibility.Collapsed : Visibility.Visible;
            // 原文只在它自己变了时重排（译文逐字出现时不动）。
            var sourceSig = $"{line.SourceLabel}\u0001{line.Source}\u0001{mode}\u0001{sourceSize}\u0001{sourceFont}";
            if (sourceSig != sourceSignature)
            {
                sourceSignature = sourceSig;
                // “两者”模式下原文前面是音源标签（与 macOS 相同），折行时接着排在标签下面。
                // 双语时的淡色只加在文字上，标签保持自己的颜色。
                source.Inlines.Clear();
                if (line.SourceLabel is { } label)
                {
                    source.Inlines.Add(new InlineUIContainer(SourceTag(label)) { BaselineAlignment = BaselineAlignment.Center });
                }
                var run = new Run(line.Source);
                run.SetResourceReference(TextElement.ForegroundProperty, mode == DisplayMode.SourceOnly ? OverlayPalette.Fg : OverlayPalette.Fg72);
                source.Inlines.Add(run);
                source.FontSize = sourceSize;
                source.FontFamily = sourceFont;
            }

            translation.Visibility = mode == DisplayMode.SourceOnly ? Visibility.Collapsed : Visibility.Visible;
            translation.FontFamily = targetFont;
            switch (line.State)
            {
                case LineState.Waiting:
                    Set(translation, "…", translationSize, FontWeights.Normal, 0.4, null);
                    break;
                case LineState.Translating:
                    Set(translation, line.Translation.Length == 0 ? "…" : line.Translation, translationSize, FontWeights.SemiBold, 0.85, null);
                    break;
                case LineState.Done:
                    var text = line.Note is null ? line.Translation : $"{line.Translation}  · {line.Note}";
                    Set(translation, text, translationSize, FontWeights.SemiBold, 1, null);
                    if (line.Translation.Length == 0) translation.Visibility = Visibility.Collapsed;
                    break;
                case LineState.Failed:
                    Set(translation, Strings.Overlay_TranslationFailed, translationSize * 0.7, FontWeights.Normal, 0.9, Orange);
                    break;
            }
        }

        /// <param name="brush">null 时用前景色（随外观变）。</param>
        private static void Set(TextBlock t, string text, double size, FontWeight weight, double opacity, Brush? brush)
        {
            t.Text = text;
            t.FontSize = size;
            t.FontWeight = weight;
            t.Opacity = opacity;
            if (brush is null) t.SetResourceReference(TextBlock.ForegroundProperty, OverlayPalette.Fg);
            else t.Foreground = brush;
        }
    }

    /// <summary>“正在说”的一行：“两者”模式下前面是音源标签，后面是这个音源正在说的文字。</summary>
    private sealed class LiveRowView : DockPanel
    {
        private readonly Border tag = SourceTag("");
        private readonly LiveLineView text = new();

        public LiveRowView()
        {
            LastChildFill = true;
            SetDock(tag, Dock.Left);
            Children.Add(tag);
            Children.Add(text);
        }

        /// <param name="width">一行能用的宽度（包括标签）；还没排版时为 0。</param>
        public void Update(LiveRow row, double size, FontFamily font, double width)
        {
            if (row.Label is { } label)
            {
                ((TextBlock)tag.Child).Text = label;
                tag.Visibility = Visibility.Visible;
                // DesiredSize 已经包括右边的间距。
                tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                width -= tag.DesiredSize.Width;
            }
            else
            {
                tag.Visibility = Visibility.Collapsed;
            }
            text.Update(row.Text, size, font, width);
        }
    }

    /// <summary>
    /// 正在说、还没送去翻译的文字（斜体、较淡）。只占一行，太长时省略前面、显示最新的词（macOS 的 truncationMode(.head)）。
    /// </summary>
    private sealed class LiveLineView : TextBlock
    {
        private string signature = "";

        public LiveLineView()
        {
            TextWrapping = TextWrapping.NoWrap;
            FontStyle = FontStyles.Italic;
            SetResourceReference(ForegroundProperty, OverlayPalette.Fg);
            Opacity = 0.55;
            VerticalAlignment = VerticalAlignment.Top;
        }

        /// <param name="width">一行能用的宽度；还没排版时为 0。</param>
        public void Update(string text, double size, FontFamily font, double width)
        {
            var sig = $"{text}\u0001{size}\u0001{font}\u0001{Math.Round(width)}";
            if (sig == signature) return;
            signature = sig;
            FontSize = size;
            FontFamily = font;
            var typeface = new Typeface(font, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            // 留 2 像素余量：测量和实际排版可能差零点几个像素，差了最后一个字会被裁掉。
            Text = LiveTail.Fit(text, width - 2, s => new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, size, Brushes.White, pixelsPerDip).WidthIncludingTrailingWhitespace);
        }
    }

    /// <summary>一小行状态：有没有收到声音、识别是否清晰、是否在录音、翻译模型、待翻译句数、最近的错误。</summary>
    private sealed class StatusBarView : DockPanel
    {
        private readonly Rectangle[] meter = new Rectangle[5];
        private readonly TextBlock sound = new();
        private readonly TextBlock recognition = new();
        private readonly TextBlock recordingSeparator = Separator();
        /// <summary>正在录音（与 macOS 相同：红色的录音图标和“录音”）。</summary>
        private readonly StackPanel recording = new() { Orientation = Orientation.Horizontal };
        private readonly TextBlock engine = new();
        private readonly TextBlock chevron = new()
        {
            Text = "\uE70D", // ChevronDown
            FontFamily = IconFont,
            FontSize = 8,
            Margin = new Thickness(3, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        /// <summary>
        /// 模型名和后面的小箭头（与 macOS 相同），点击弹出模型菜单；鼠标移上去文字变蓝、底下出现淡淡的胶囊
        /// （和 AI 助手的模型菜单一样，2026-10-22 用户要求）。背景透明，两者之间的空隙也能点。
        /// </summary>
        private readonly Border engineButton = new()
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(-6, 0, -6, 0),
            Cursor = Cursors.Hand,
            ToolTip = Strings.Tip_ModelMenu,
        };
        private readonly TextBlock backlogSeparator = Separator();
        private readonly TextBlock backlog = new() { Foreground = Orange };
        private readonly TextBlock alert = new() { Foreground = Orange, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
        private bool menuOpen;

        public StatusBarView(Action<FrameworkElement> onEngineClick)
        {
            Margin = new Thickness(18, 0, 18, 7);
            LastChildFill = true;
            TextElement.SetFontSize(this, 11);
            SetResourceReference(TextElement.ForegroundProperty, OverlayPalette.Fg60);

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
            recording.Children.Add(new TextBlock
            {
                Text = "\uE7C8", // Record
                FontFamily = IconFont,
                FontSize = 10,
                Foreground = OverlayPalette.Red,
                Margin = new Thickness(0, 1, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            recording.Children.Add(new TextBlock { Text = Strings.Overlay_Recording, Foreground = OverlayPalette.Red });
            left.Children.Add(recordingSeparator);
            left.Children.Add(recording);
            left.Children.Add(Separator());
            var engineContent = new StackPanel { Orientation = Orientation.Horizontal };
            engineContent.Children.Add(engine);
            engineContent.Children.Add(chevron);
            engineButton.Child = engineContent;
            left.Children.Add(engineButton);
            left.Children.Add(backlogSeparator);
            left.Children.Add(backlog);
            engineButton.MouseEnter += (_, _) => SetEngineHover(true);
            engineButton.MouseLeave += (_, _) =>
            {
                // 菜单打开期间鼠标在菜单上，保持蓝色，关掉菜单时复原（ResetEngineHover）。
                if (!menuOpen) SetEngineHover(false);
            };
            engineButton.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                menuOpen = true;
                onEngineClick(engineButton);
            };
            engineButton.MouseLeftButtonDown += (_, e) => e.Handled = true; // 不要触发拖动
            SetDock(left, Dock.Left);
            Children.Add(left);
            Children.Add(alert);
        }

        /// <summary>模型菜单关上了：鼠标已经不在模型名上时复原颜色。</summary>
        public void ResetEngineHover()
        {
            menuOpen = false;
            SetEngineHover(engineButton.IsMouseOver);
        }

        private void SetEngineHover(bool hovered)
        {
            if (hovered)
            {
                engine.Foreground = chevron.Foreground = OverlayPalette.Blue;
                engineButton.Background = OverlayPalette.FgBrush(0.12);
            }
            else
            {
                engine.ClearValue(TextBlock.ForegroundProperty);
                chevron.ClearValue(TextBlock.ForegroundProperty);
                engineButton.Background = Brushes.Transparent;
            }
        }

        private static TextBlock Separator() => new() { Text = "·", Margin = new Thickness(6, 0, 6, 0), Opacity = 0.6 };

        public void Update(SubtitleController c)
        {
            bool hasInput = c.SilentFor < 3; // 超过 3 秒没收到任何音频数据：采集断了（和“安静”不同）。
            for (int i = 0; i < meter.Length; i++)
            {
                if (i < c.InputLevel * 5) meter[i].Fill = Green;
                else meter[i].SetResourceReference(System.Windows.Shapes.Shape.FillProperty, OverlayPalette.Fg20);
            }
            sound.Text = c.MicrophoneBlock != MicrophoneBlock.None ? Strings.Overlay_MicrophoneBlocked
                : !hasInput ? Strings.Overlay_NoInput
                : c.InputLevel > 0 ? Strings.Overlay_Receiving : Strings.Overlay_Quiet;
            if (hasInput && c.MicrophoneBlock == MicrophoneBlock.None) sound.ClearValue(TextBlock.ForegroundProperty);
            else sound.Foreground = Orange;
            // 识别困难但没打开“自动纠正口音”时只提示，告诉用户在哪里打开；打开了但没有能纠正的模型（用 Google 翻译、混元翻译，
            // 本机又没有 Claude Code）时说明需要 Claude Code（与 macOS 相同）。
            (recognition.Text, recognition.ToolTip) = c.Correcting ? (Strings.Overlay_RecognitionCorrecting, Strings.Tip_RecognitionCorrecting)
                : c.RecognitionDifficult ? (Strings.Overlay_RecognitionHard, c.CorrectionUnavailable ? Strings.Tip_RecognitionNeedsClaude : Strings.Tip_RecognitionHard)
                : (Strings.Overlay_RecognitionClear, Strings.Tip_RecognitionClear);
            recording.Visibility = recordingSeparator.Visibility = c.IsRecording ? Visibility.Visible : Visibility.Collapsed;
            engine.Text = c.EngineLabel;
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

/// <summary>字幕、状态条和 AI 助手上弹出的菜单（音源、模型菜单）共用的做法。</summary>
internal static class PopupMenus
{
    /// <summary>菜单项。标题用 TextBlock 原样显示：Header 是字符串时下划线会被当成快捷键标记（模型名里常有 _）。</summary>
    public static MenuItem Item(string title, Action action, bool isChecked = false)
    {
        var item = new MenuItem { Header = new TextBlock { Text = title }, IsChecked = isChecked };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// 在 <paramref name="anchor"/> 旁边打开菜单。所在的窗口不激活，焦点留在用户正在用的程序上。
    /// 程序不在前台时，WPF 菜单收不到点在别的程序上的鼠标，不会自己关上：菜单开着时定时看鼠标键，
    /// 按在别的程序的窗口上就关掉菜单。点在本程序的窗口上（菜单、子菜单、字幕）由 WPF 自己处理。
    /// </summary>
    public static void Open(ContextMenu menu, FrameworkElement anchor, PlacementMode placement)
    {
        menu.PlacementTarget = anchor;
        menu.Placement = placement;
        var watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        watch.Tick += (_, _) =>
        {
            if (!menu.IsOpen) watch.Stop();
            else if (NativeMethods.MouseDownInOtherProcess()) menu.IsOpen = false;
        };
        menu.Closed += (_, _) => watch.Stop();
        menu.IsOpen = true;
        watch.Start();
    }
}
