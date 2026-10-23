using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LiveSub.App.Localization;
using LiveSub.App.Services;
using LiveSub.App.Translation;
using LiveSub.Core;
using LiveSub.Core.Chat;
using Ellipse = System.Windows.Shapes.Ellipse;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace LiveSub.App.UI;

/// <summary>
/// AI 助手面板（与 macOS 版 AssistantPanel 相同）：无边框、置顶、不进任务栏的圆角深色面板，可以打字提问。
/// 第一次打开时放在字幕旁边；按住标题栏可以单独拖到别处，拖边框和四角调整大小，位置和大小都会记住。
/// 字幕窗口移动、改大小时不跟着走。
/// 显示时不抢焦点；只有点输入框或对话内容（要打字、选中复制）时才激活窗口，点标题栏、按钮和拖边框时不激活。
/// </summary>
public sealed class AssistantWindow : Window
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    /// <summary>标题栏的 ✨。WPF 不画彩色表情，单色字形染成黄色，与 macOS 的 sparkles 图标相近。</summary>
    private static readonly FontFamily SymbolFont = new("Segoe UI Emoji, Segoe UI Symbol");
    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");
    private static readonly SolidColorBrush Orange = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x3D)));
    private static readonly SolidColorBrush Yellow = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)));
    private static readonly SolidColorBrush Bubble = Freeze(new SolidColorBrush(Color.FromArgb(0xBF, 0x0A, 0x84, 0xFF)));
    private static readonly SolidColorBrush SendBlue = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));
    private static readonly SolidColorBrush Hairline = Freeze(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush Faint = Freeze(new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush Dim = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush Capsule = Freeze(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush InputBackground = Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush CodeBackground = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));

    public static readonly Size MinSize = new(280, 220);
    /// <summary>调整大小的边框宽度（四角是它的两倍）。</summary>
    private const double ResizeEdge = 6;

    private readonly AssistantSession assistant;
    private readonly SettingsService settingsService;
    private readonly Window overlay;
    private readonly Action onClose;
    private AppSettings S => settingsService.Current;

    private readonly Grid root = new();
    private readonly Border panel = new()
    {
        CornerRadius = new CornerRadius(14),
        BorderThickness = new Thickness(1),
        BorderBrush = Hairline,
    };
    private readonly TextBlock modelName = new()
    {
        FontSize = 12,
        MaxWidth = 160,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Button modelButton;
    private readonly StackPanel messagesPanel = new();
    /// <summary>
    /// 右边让出调整大小的边框（宽 <see cref="ResizeEdge"/>，减去面板 1 像素的边框）：滚动条不被它盖住，最右边也能点到。
    /// 内边距相应减小，对话内容离右边的距离仍是 12。
    /// 背景透明但能点到：点在消息之间的空白处也算点到对话内容（见 WantsActivation）。
    /// </summary>
    private readonly ScrollViewer scroller = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Background = Brushes.Transparent,
        Margin = new Thickness(0, 0, ResizeEdge - 1, 0),
        Padding = new Thickness(12, 10, 12 - (ResizeEdge - 1), 4),
        Focusable = false,
    };
    private readonly TextBlock emptyText = new()
    {
        Text = Strings.Assistant_Empty,
        FontSize = 13,
        Foreground = Dim,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly Dictionary<Guid, MessageView> views = [];
    private readonly List<Button> quickButtons = [];
    private readonly TextBox input = new()
    {
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = Brushes.White,
        CaretBrush = Brushes.White,
        FontSize = 13,
        Padding = new Thickness(0),
        VerticalContentAlignment = VerticalAlignment.Center,
        AcceptsReturn = false,
    };
    private readonly TextBlock placeholder = new()
    {
        Text = Strings.Assistant_Placeholder,
        FontSize = 13,
        Foreground = Faint,
        IsHitTestVisible = false,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(2, 0, 0, 0),
    };
    private readonly Border inputBox;
    private readonly Button sendButton;
    /// <summary>正在显示的模型菜单：面板隐藏时一起关掉。</summary>
    private ContextMenu? openMenu;

    private ResizeDrag? resizing;
    private bool renderQueued;
    private int shownCount = -1;
    private string shownLastText = "";

    /// <param name="overlay">字幕窗口：第一次打开时放在它旁边。</param>
    /// <param name="onClose">“收起”（和托盘菜单里取消“AI 助手”相同）。</param>
    public AssistantWindow(AssistantSession assistant, SettingsService settingsService, Window overlay, Action onClose)
    {
        this.assistant = assistant;
        this.settingsService = settingsService;
        this.overlay = overlay;
        this.onClose = onClose;

        Title = Strings.Assistant_Title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        UseLayoutRounding = true;
        Width = Math.Max(MinSize.Width, S.AssistantWidth);
        Height = Math.Max(MinSize.Height, S.AssistantHeight);

        modelButton = BuildModelButton();
        var header = BuildHeader();
        var divider = new Border { Height = 1, Background = Hairline };
        inputBox = new Border
        {
            Background = InputBackground,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 7),
        };
        sendButton = IconButton("\uE724", Strings.Assistant_Send, Send); // Send
        var inputBar = BuildInputBar();
        var quickBar = BuildQuickQuestions();

        messagesPanel.Children.Add(emptyText);
        scroller.Content = messagesPanel;
        // 对话内容是只读文本框，不让它们吞掉滚轮：滚轮总是滚动整个对话。
        scroller.PreviewMouseWheel += (_, e) =>
        {
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta * 0.4);
            e.Handled = true;
        };

        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(divider, Dock.Top);
        DockPanel.SetDock(inputBar, Dock.Bottom);
        DockPanel.SetDock(quickBar, Dock.Bottom);
        layout.Children.Add(header);
        layout.Children.Add(divider);
        layout.Children.Add(inputBar);
        layout.Children.Add(quickBar);
        layout.Children.Add(scroller);
        panel.Child = layout;
        root.Children.Add(panel);
        AddResizeHandles();
        Content = root;

        input.TextChanged += (_, _) =>
        {
            if (assistant.Draft != input.Text) assistant.Draft = input.Text;
            UpdateInputState();
        };
        input.PreviewKeyDown += (_, e) =>
        {
            // 输入法正在组字时按回车不会到这里（Key 是 ImeProcessed）。
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Send();
        };

        SourceInitialized += OnSourceInitialized;
        // 定期提到最上层由字幕窗口统一做：先提字幕再提面板（OverlayWindow.KeepAbove），两个窗口重叠时面板总在上面。
        // 面板被收起（“收起”、快捷键、托盘菜单）时，开着的模型菜单一起关掉。
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible && openMenu is not null) openMenu.IsOpen = false;
        };
        assistant.Changed += RequestRender;
        settingsService.Changed += OnSettingsChanged;
    }

    /// <summary>显示但不激活：放到记住的位置，没拖过（或那里已不在任何屏幕上）就放在字幕旁边。</summary>
    public void ShowNoActivate()
    {
        Place();
        Show();
        RequestRender();
    }

    /// <summary>“字幕移回屏幕底部”：调用方已清掉记住的位置；等字幕移好之后再放回它旁边。</summary>
    public void ResetPosition()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (IsVisible) Place();
        });
    }

    // MARK: - 窗口样式

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = this.Handle();
        // 不进 Alt+Tab。不设 WS_EX_NOACTIVATE：点输入框时要能激活窗口打字（见 WndProc）。
        NativeMethods.SetExStyle(hwnd, NativeMethods.WS_EX_TOOLWINDOW, 0);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            // 点输入框、对话内容时照常激活（要打字、选中复制）；点标题栏、按钮时不激活，焦点留在用户正在用的程序上。
            if (WantsActivation()) return IntPtr.Zero;
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }
        if (msg == NativeMethods.WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == NativeMethods.SC_CLOSE)
        {
            // Alt+F4：和“收起”一样只是隐藏。
            handled = true;
            Dispatcher.BeginInvoke(onClose);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 鼠标下面的部件是输入框，或在对话内容（含滚动条）里面时才激活。按实际点到的部件判断而不是按范围：
    /// 左右调整大小的边框压在对话内容上，点到的是边框，从那里拖边框不激活面板、不抢焦点。
    /// </summary>
    private bool WantsActivation()
    {
        if (!NativeMethods.GetCursorPos(out var p) || PresentationSource.FromVisual(root) is null) return false;
        // 鼠标位置是屏幕像素坐标，换算成面板里的坐标。
        var point = root.PointFromScreen(new Point(p.X, p.Y));
        return VisualTreeHelper.HitTest(root, point)?.VisualHit is Visual hit
            && (hit.IsDescendantOf(inputBox) || hit.IsDescendantOf(scroller));
    }

    // MARK: - 部件

    private FrameworkElement BuildHeader()
    {
        // 图标和标题不接收鼠标，按在上面也是拖动面板。
        var title = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock
        {
            Text = "\u2728",
            FontFamily = SymbolFont,
            FontSize = 13,
            Foreground = Yellow,
            VerticalAlignment = VerticalAlignment.Center,
        });
        title.Children.Add(new TextBlock
        {
            Text = Strings.Assistant_Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(modelButton);
        buttons.Children.Add(IconButton("\uE70F", Strings.Assistant_NewConversation, () => // Edit
        {
            assistant.NewConversation();
            assistant.WarmUp();
        }));
        buttons.Children.Add(IconButton("\uE711", Strings.Assistant_Close, onClose)); // Cancel

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(title);
        // 背景透明但要能点到：空白处按住也是拖动。
        var header = new Border { Background = Brushes.Transparent, Padding = new Thickness(12, 6, 10, 6), Child = row };
        header.MouseLeftButtonDown += OnHeaderMouseDown;
        return header;
    }

    /// <summary>回答使用的模型：分组与翻译模型相同。</summary>
    private Button BuildModelButton()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(modelName);
        content.Children.Add(new TextBlock
        {
            Text = "\uE70D", // ChevronDown
            FontFamily = IconFont,
            FontSize = 8,
            Margin = new Thickness(5, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var button = OverlayWindow.FlatButton("", null);
        button.Content = content;
        button.Padding = new Thickness(8, 3, 7, 3);
        button.Margin = new Thickness(0, 0, 4, 0);
        button.Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
        button.ToolTip = Strings.Assistant_ModelTip;
        button.Click += (_, _) => ShowModelMenu(button);
        return button;
    }

    private void ShowModelMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu();
        bool first = true;
        foreach (var group in TranslationEngines.AssistantMenu(S))
        {
            if (!first) menu.Items.Add(new Separator());
            first = false;
            menu.Items.Add(new MenuItem { Header = group.Title, IsEnabled = false });
            foreach (var entry in group.Items)
            {
                menu.Items.Add(PopupMenus.Item(entry.Title, () => SelectModel(entry.Choice), entry.Selected));
            }
        }
        if (openMenu is not null) openMenu.IsOpen = false;
        openMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(openMenu, menu)) openMenu = null;
        };
        // 点标题栏不激活面板，菜单要靠 PopupMenus 才能在点到别的程序时关上。
        PopupMenus.Open(menu, anchor, PlacementMode.Bottom);
    }

    private void SelectModel(ModelChoice choice)
    {
        if (S.AssistantModel == choice) return;
        // 只保存：下次提问时按新模型开新会话，并把本次对话以来的全部字幕重新带上。
        S.AssistantModel = choice;
        settingsService.Commit(SettingsChange.None);
    }

    /// <summary>快捷问题：发给模型的是中文原文，按钮上显示界面语言的文字。放不下时折行（Windows 上不方便横向滚动）。</summary>
    private FrameworkElement BuildQuickQuestions()
    {
        string[] labels = [Strings.Quick_Summarize, Strings.Quick_Concepts, Strings.Quick_LastSentence];
        var bar = new WrapPanel { Margin = new Thickness(10, 6, 4, 2) };
        for (int i = 0; i < AssistantPrompt.QuickQuestions.Count; i++)
        {
            var question = AssistantPrompt.QuickQuestions[i];
            var button = OverlayWindow.FlatButton(i < labels.Length ? labels[i] : question, () => assistant.Ask(question));
            button.FontSize = 11.5;
            button.Padding = new Thickness(9, 4, 9, 4);
            button.Margin = new Thickness(0, 0, 6, 6);
            button.Background = Capsule;
            button.Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
            quickButtons.Add(button);
            bar.Children.Add(button);
        }
        return bar;
    }

    private FrameworkElement BuildInputBar()
    {
        var field = new Grid();
        field.Children.Add(input);
        field.Children.Add(placeholder);
        inputBox.Child = field;
        // 点在输入框的内边距上也开始输入。
        inputBox.MouseLeftButtonDown += (_, _) => input.Focus();
        sendButton.FontSize = 16;
        sendButton.Width = 32;
        sendButton.Height = 32;
        sendButton.Margin = new Thickness(6, 0, 0, 0);
        var bar = new DockPanel { LastChildFill = true, Margin = new Thickness(10, 0, 10, 10) };
        DockPanel.SetDock(sendButton, Dock.Right);
        bar.Children.Add(sendButton);
        bar.Children.Add(inputBox);
        return bar;
    }

    private static Button IconButton(string glyph, string tip, Action action)
    {
        var b = OverlayWindow.FlatButton(glyph, action);
        b.FontFamily = IconFont;
        b.FontSize = 12;
        b.Width = 26;
        b.Height = 22;
        b.ToolTip = tip;
        b.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        return b;
    }

    private void Send()
    {
        assistant.Send();
        RequestRender();
    }

    private void UpdateInputState()
    {
        placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool canSend = assistant.CanSend;
        sendButton.IsEnabled = canSend;
        sendButton.Foreground = canSend ? SendBlue : Faint;
    }

    // MARK: - 渲染

    private void OnSettingsChanged(SettingsChange change) => RequestRender();

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
        // 背景比字幕深一些，长段文字也看得清。
        double opacity = Math.Clamp(Math.Max(S.BackgroundOpacity, 0.8), 0, 1);
        panel.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), 0, 0, 0));
        modelName.Text = S.DisplayName(S.AssistantModel);

        var messages = assistant.Messages;
        var ids = messages.Select(m => m.Id).ToHashSet();
        foreach (var (id, view) in views.Where(kv => !ids.Contains(kv.Key)).ToList())
        {
            views.Remove(id);
            messagesPanel.Children.Remove(view);
        }
        var font = TextFonts.For(S.Target);
        for (int i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (!views.TryGetValue(message.Id, out var view))
            {
                view = new MessageView(message.Role);
                views[message.Id] = view;
                messagesPanel.Children.Add(view);
            }
            view.Update(message, answering: assistant.IsAnswering && i == messages.Count - 1, font);
        }
        emptyText.Visibility = messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        bool answering = assistant.IsAnswering;
        foreach (var button in quickButtons)
        {
            button.IsEnabled = !answering;
            button.Opacity = answering ? 0.45 : 1;
        }
        if (input.Text != assistant.Draft) input.Text = assistant.Draft;
        UpdateInputState();

        // 有新消息、回答变长、出现保存的文件时滚到底部（与 macOS 相同）。
        var lastText = messages.Count > 0 ? $"{messages[^1].Text}\u0001{messages[^1].WritingFile}\u0001{messages[^1].Files.Count}" : "";
        if (messages.Count != shownCount || lastText != shownLastText)
        {
            shownCount = messages.Count;
            shownLastText = lastText;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => scroller.ScrollToEnd());
        }
    }

    // MARK: - 拖动

    /// <summary>按住标题栏拖动面板。只有用户拖过才记住位置（程序自己摆放时不记）。</summary>
    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        e.Handled = true;
        double left = Left, top = Top;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已经松开。
            return;
        }
        if (Math.Abs(Left - left) > 0.5 || Math.Abs(Top - top) > 0.5) SaveFrame();
    }

    private void SaveFrame()
    {
        S.AssistantWidth = Math.Round(Width);
        S.AssistantHeight = Math.Round(Height);
        S.AssistantLeft = Math.Round(Left);
        S.AssistantTop = Math.Round(Top);
        settingsService.Commit(SettingsChange.None);
    }

    // MARK: - 调整大小（与字幕窗口的做法相同）

    [Flags]
    private enum Edges { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    private sealed record ResizeDrag(Edges Edges, Point StartCursor, Rect StartFrame);

    private void AddResizeHandles()
    {
        const double edge = ResizeEdge, corner = ResizeEdge * 2;
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
                e.Handled = true;
                resizing = new ResizeDrag(edges, CursorDip(), new Rect(Left, Top, ActualWidth, ActualHeight));
                r.CaptureMouse();
            };
            void EndResize()
            {
                if (resizing is null) return;
                resizing = null;
                if (r.IsMouseCaptured) r.ReleaseMouseCapture();
                SaveFrame();
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
        var work = WorkArea(this);
        double width = f.Width, height = f.Height;
        if (drag.Edges.HasFlag(Edges.Left)) width = f.Width - dx;
        if (drag.Edges.HasFlag(Edges.Right)) width = f.Width + dx;
        if (drag.Edges.HasFlag(Edges.Top)) height = f.Height - dy;
        if (drag.Edges.HasFlag(Edges.Bottom)) height = f.Height + dy;
        width = Math.Clamp(width, MinSize.Width, Math.Max(MinSize.Width, work.Width));
        height = Math.Clamp(height, MinSize.Height, Math.Max(MinSize.Height, work.Height));
        // 被限制大小时，保持对边不动。
        Left = drag.Edges.HasFlag(Edges.Left) ? f.Right - width : f.Left;
        Top = drag.Edges.HasFlag(Edges.Top) ? f.Bottom - height : f.Top;
        Width = width;
        Height = height;
    }

    private Point CursorDip()
    {
        NativeMethods.GetCursorPos(out var p);
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY);
    }

    // MARK: - 位置

    /// <summary>放到用户上次拖到的位置；没拖过，或那里的标题栏已经不在任何屏幕上（换了显示器），就放在字幕旁边。</summary>
    private void Place()
    {
        var screen = WorkArea(overlay);
        double width = Math.Clamp(S.AssistantWidth, MinSize.Width, Math.Max(MinSize.Width, screen.Width));
        double height = Math.Clamp(S.AssistantHeight, MinSize.Height, Math.Max(MinSize.Height, screen.Height));
        double ow = overlay.ActualWidth > 0 ? overlay.ActualWidth : overlay.Width;
        double oh = overlay.ActualHeight > 0 ? overlay.ActualHeight : overlay.Height;
        var o = double.IsNaN(ow) || double.IsNaN(oh) || double.IsNaN(overlay.Left) || double.IsNaN(overlay.Top)
            ? new ScreenRect(screen.Right, screen.Bottom, 0, 0)
            : new ScreenRect(overlay.Left, overlay.Top, ow, oh);
        var (left, top) = AssistantPlacement.Place(o, ToScreenRect(screen), WorkAreas(overlay).Select(ToScreenRect), width, height,
            S.AssistantLeft, S.AssistantTop);
        Width = width;
        Height = height;
        Left = left;
        Top = top;
    }

    private static ScreenRect ToScreenRect(Rect r) => new(r.Left, r.Top, r.Width, r.Height);

    /// <summary>窗口所在屏幕的工作区（与设备无关的像素）。</summary>
    private static Rect WorkArea(Window window)
    {
        var hwnd = window.Handle();
        var screen = hwnd != IntPtr.Zero ? System.Windows.Forms.Screen.FromHandle(hwnd) : System.Windows.Forms.Screen.PrimaryScreen;
        return screen is null ? SystemParameters.WorkArea : ToDip(screen.WorkingArea, VisualTreeHelper.GetDpi(window));
    }

    /// <summary>所有屏幕的工作区，按 <paramref name="reference"/> 的缩放比例换算成与设备无关的像素。</summary>
    private static List<Rect> WorkAreas(Visual reference)
    {
        var dpi = VisualTreeHelper.GetDpi(reference);
        return System.Windows.Forms.Screen.AllScreens.Select(s => ToDip(s.WorkingArea, dpi)).ToList();
    }

    private static Rect ToDip(System.Drawing.Rectangle r, DpiScale dpi) =>
        new(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY, r.Width / dpi.DpiScaleX, r.Height / dpi.DpiScaleY);

    protected override void OnClosed(EventArgs e)
    {
        if (openMenu is not null) openMenu.IsOpen = false;
        assistant.Changed -= RequestRender;
        settingsService.Changed -= OnSettingsChanged;
        base.OnClosed(e);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    // MARK: - 一条消息

    /// <summary>
    /// 提问：右侧的蓝色气泡。回答：左侧的文字，显示粗体、斜体、行内代码，失败时为橙色；还没有文字时显示“正在回答…”。
    /// 文字可以选中复制（只读文本框）。回答里有要保存的文件时，写文件期间显示“正在保存文件…”，
    /// 之后每个文件一行“已保存：文件名”（点一下在资源管理器中选中它）或橙色的“保存失败：原因”。
    /// </summary>
    private sealed class MessageView : Border
    {
        private readonly TextBox? question;
        /// <summary>回答的各部分：文字（或“正在回答…”）、“正在保存文件…”、保存的文件。</summary>
        private readonly StackPanel? body;
        private RichTextBox? reply;
        private Paragraph? paragraph;
        private FrameworkElement? waiting;
        private FrameworkElement? saving;
        private IReadOnlyList<AssistantSavedFile>? shownFiles;
        private List<FrameworkElement> fileRows = [];
        private string signature = "";

        public MessageView(AssistantRole role)
        {
            Margin = new Thickness(0, 0, 0, 12);
            if (role != AssistantRole.User)
            {
                body = new StackPanel();
                return;
            }
            question = new TextBox
            {
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(0),
                FontSize = 13,
            };
            HorizontalAlignment = HorizontalAlignment.Right;
            Margin = new Thickness(40, 0, 0, 12);
            Background = Bubble;
            CornerRadius = new CornerRadius(10);
            Padding = new Thickness(10, 7, 10, 7);
            Child = question;
        }

        /// <param name="answering">这是正在回答的一条（还没有文字、也没有文件时显示“正在回答…”）。</param>
        public void Update(AssistantMessage message, bool answering, FontFamily font)
        {
            var sig = $"{message.Text}\u0001{message.Failed}\u0001{answering}\u0001{font}\u0001{message.WritingFile}\u0001{message.Files.Count}";
            if (sig == signature) return;
            signature = sig;
            if (question is not null)
            {
                question.Text = message.Text;
                return;
            }
            var parts = new List<FrameworkElement>();
            if (message.Text.Length > 0)
            {
                parts.Add(Reply(message, font));
            }
            else if (answering && !message.WritingFile && message.Files.Count == 0)
            {
                parts.Add(waiting ??= Progress(Strings.Assistant_Answering));
            }
            if (message.WritingFile) parts.Add(saving ??= Progress(Strings.Assistant_SavingFile));
            if (!ReferenceEquals(shownFiles, message.Files))
            {
                shownFiles = message.Files;
                fileRows = message.Files.Select(SavedFile).ToList();
            }
            parts.AddRange(fileRows);
            SetParts(parts);
        }

        /// <summary>
        /// 换成这些部分（与 macOS 的 VStack 相同，间隔 6）。只增删变了的部分：正在显示的回答文字不重新挂上，选中的文字不丢；
        /// 转圈只在显示着的时候转（见 <see cref="Progress"/>），所以不用的部分要拿掉而不是隐藏。
        /// </summary>
        private void SetParts(List<FrameworkElement> parts)
        {
            for (int i = body!.Children.Count - 1; i >= 0; i--)
            {
                if (!parts.Contains((FrameworkElement)body.Children[i])) body.Children.RemoveAt(i);
            }
            for (int i = 0; i < parts.Count; i++)
            {
                parts[i].Margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0);
                if (i < body.Children.Count && ReferenceEquals(body.Children[i], parts[i])) continue;
                if (body.Children.Contains(parts[i])) body.Children.Remove(parts[i]);
                body.Children.Insert(i, parts[i]);
            }
            Child = parts.Count > 0 ? body : null;
        }

        private RichTextBox Reply(AssistantMessage message, FontFamily font)
        {
            if (reply is null || paragraph is null)
            {
                paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 21 };
                reply = new RichTextBox(new FlowDocument(paragraph) { PagePadding = new Thickness(0), FontSize = 13.5 })
                {
                    IsReadOnly = true,
                    IsReadOnlyCaretVisible = false,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = Brushes.White,
                    Padding = new Thickness(0),
                };
            }
            reply.Document.FontFamily = font;
            paragraph.Foreground = message.Failed ? Orange : Brushes.White;
            paragraph.Inlines.Clear();
            paragraph.Inlines.AddRange(Inlines(message.Text));
            return reply;
        }

        /// <summary>替用户保存的文件：点一下在资源管理器中选中它（文件已被删掉时打开所在的文件夹）。保存失败时是橙色的原因。</summary>
        private static FrameworkElement SavedFile(AssistantSavedFile file)
        {
            bool saved = file.Path is not null;
            var brush = saved ? SendBlue : Orange;
            var row = new DockPanel();
            var icon = new TextBlock
            {
                Text = saved ? "\uE8A5" : "\uE7BA", // Document / Warning
                FontFamily = IconFont,
                FontSize = 12.5,
                Foreground = brush,
                Margin = new Thickness(0, 2, 6, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(new TextBlock
            {
                Text = saved
                    ? Strings.Format(Strings.Assistant_Saved, file.Name)
                    : Strings.Format(Strings.Assistant_SaveFailed, file.Error ?? file.Name),
                FontSize = 12.5,
                Foreground = brush,
                TextWrapping = TextWrapping.Wrap,
            });
            if (file.Path is not { } path) return row;

            // 没有边框和底色的按钮（与 macOS 的 .plain 相同）；透明背景让图标和文字之间的空隙也能点到。
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            var link = new Button
            {
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Left,
                Focusable = false,
                Cursor = Cursors.Hand,
                ToolTip = Strings.Assistant_ShowInExplorer,
                Template = new ControlTemplate(typeof(Button)) { VisualTree = border },
            };
            link.Click += (_, _) =>
            {
                if (File.Exists(path)) AppShell.RevealInExplorer(path);
                else AppShell.OpenInExplorer(Path.GetDirectoryName(path)!);
            };
            return link;
        }

        /// <summary>行内 Markdown → 文字片段；换行用 LineBreak。</summary>
        private static List<Inline> Inlines(string text)
        {
            var inlines = new List<Inline>();
            foreach (var piece in InlineMarkdown.Parse(text.Replace("\r\n", "\n")))
            {
                var lines = piece.Text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0) inlines.Add(new LineBreak());
                    if (lines[i].Length == 0) continue;
                    var run = new Run(lines[i]);
                    if (piece.Style.HasFlag(InlineStyle.Bold)) run.FontWeight = FontWeights.Bold;
                    if (piece.Style.HasFlag(InlineStyle.Italic)) run.FontStyle = FontStyles.Italic;
                    if (piece.Style.HasFlag(InlineStyle.Code))
                    {
                        run.FontFamily = CodeFont;
                        run.FontSize = 12.5;
                        run.Background = CodeBackground;
                    }
                    inlines.Add(run);
                }
            }
            return inlines;
        }

        /// <summary>转圈的小圆弧 + 文字（“正在回答…”“正在保存文件…”）。</summary>
        private static FrameworkElement Progress(string text)
        {
            var spinner = new Ellipse
            {
                Width = 12,
                Height = 12,
                Stroke = Dim,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 9, 7 },
                RenderTransformOrigin = new Point(0.5, 0.5),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var rotate = new RotateTransform();
            spinner.RenderTransform = rotate;
            // 只在显示着的时候转：一直运行的动画即使不在界面上，也会让 WPF 不停地重绘。
            spinner.Loaded += (_, _) => rotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
            spinner.Unloaded += (_, _) => rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(spinner);
            row.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = Dim,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            return row;
        }
    }
}
