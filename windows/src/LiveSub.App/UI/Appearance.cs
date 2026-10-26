using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using LiveSub.App.Services;
using LiveSub.Core;
using Microsoft.Win32;

namespace LiveSub.App.UI;

/// <summary>
/// 悬浮窗口（字幕、AI 助手）的颜色（与 macOS 的 OverlayPalette 相同）：深色外观黑底白字，浅色外观白底黑字。
/// 颜色放在程序的资源里，部件用 <see cref="FrameworkElement.SetResourceReference"/> 引用，外观一变所有部件跟着变，不用逐个重画。
/// 键名后面的数字是前景色的不透明度（Fg60 = 60% 的白色或黑色）。
/// </summary>
internal static class OverlayPalette
{
    public const string Fg = "LiveSub.Fg";
    public const string Fg85 = "LiveSub.Fg85";
    public const string Fg80 = "LiveSub.Fg80";
    /// <summary>双语时原文的颜色。</summary>
    public const string Fg72 = "LiveSub.Fg72";
    public const string Fg60 = "LiveSub.Fg60";
    public const string Fg45 = "LiveSub.Fg45";
    public const string Fg30 = "LiveSub.Fg30";
    public const string Fg20 = "LiveSub.Fg20";
    /// <summary>音源标签的底色。</summary>
    public const string Fg18 = "LiveSub.Fg18";
    public const string Fg15 = "LiveSub.Fg15";
    public const string Fg12 = "LiveSub.Fg12";
    public const string Fg10 = "LiveSub.Fg10";
    public const string Fg08 = "LiveSub.Fg08";
    /// <summary>背景色本身（“开始翻译”按钮上的字）。</summary>
    public const string Bg = "LiveSub.Bg";
    /// <summary>快捷按钮一排的底色（macOS 为 80%）。</summary>
    public const string Bg80 = "LiveSub.Bg80";
    public const string Bg70 = "LiveSub.Bg70";
    /// <summary>按钮说明的底色。</summary>
    public const string Bg92 = "LiveSub.Bg92";
    /// <summary>字幕文字的投影：深色外观压暗，浅色外观提亮，叠在复杂背景上也看得清。</summary>
    public const string Shadow = "LiveSub.Shadow";

    private static readonly (string Key, double Alpha)[] FgKeys =
    [
        (Fg, 1), (Fg85, 0.85), (Fg80, 0.8), (Fg72, 0.72), (Fg60, 0.6), (Fg45, 0.45), (Fg30, 0.3), (Fg20, 0.2), (Fg18, 0.18),
        (Fg15, 0.15), (Fg12, 0.12), (Fg10, 0.1), (Fg08, 0.08),
    ];
    private static readonly (string Key, double Alpha)[] BgKeys = [(Bg, 1), (Bg80, 0.8), (Bg70, 0.7), (Bg92, 0.92)];

    /// <summary>链接、悬停时的蓝色（与 macOS 的 .blue 相近，深浅两种外观都看得清）。</summary>
    public static readonly SolidColorBrush Blue = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));
    /// <summary>录音中的红色。</summary>
    public static readonly SolidColorBrush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));
    /// <summary>警告、出错的橙色（两种外观相同，与 macOS 的 .orange 相近）。</summary>
    public static readonly SolidColorBrush Orange = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)));
    /// <summary>AI 助手打开着时按钮的颜色：深色外观黄色，浅色外观橙色（与 macOS 相同）。</summary>
    public static SolidColorBrush AssistantTint => IsDark ? Yellow : Orange;
    private static readonly SolidColorBrush Yellow = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)));

    public static bool IsDark { get; private set; } = true;
    public static Color FgColor => IsDark ? Colors.White : Colors.Black;
    public static Color BgColor => IsDark ? Colors.Black : Colors.White;

    /// <summary>外观变了（已经换好资源里的颜色）；需要按外观另算颜色的地方（字幕背景的不透明度等）在这里重画。</summary>
    public static event Action? Changed;

    public static void Apply(bool dark)
    {
        IsDark = dark;
        var resources = System.Windows.Application.Current.Resources;
        foreach (var (key, alpha) in FgKeys) resources[key] = FgBrush(alpha);
        foreach (var (key, alpha) in BgKeys) resources[key] = BgBrush(alpha);
        resources[Shadow] = Freeze(new DropShadowEffect
        {
            Color = dark ? Colors.Black : Colors.White,
            Opacity = dark ? 0.6 : 0.8,
            BlurRadius = 3,
            ShadowDepth = 1,
            Direction = 270,
        });
        Changed?.Invoke();
    }

    public static SolidColorBrush FgBrush(double alpha) => Freeze(new SolidColorBrush(WithAlpha(FgColor, alpha)));

    public static SolidColorBrush BgBrush(double alpha) => Freeze(new SolidColorBrush(WithAlpha(BgColor, alpha)));

    private static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// <summary>
/// 外观：跟随系统、浅色、深色（设置 › 字幕 › 外观，与 macOS 相同）。字幕和 AI 助手用 <see cref="OverlayPalette"/>；
/// 设置、模型服务、字幕记录、录音记录窗口用 WPF 自带的 Windows 11 风格主题（Fluent，<see cref="Window.ThemeMode"/>），
/// 浅色、深色都有，标题栏也跟着变。弹出菜单和托盘菜单仍是系统的浅色样式。
/// “跟随系统”看的是“设置 › 个性化 › 颜色 › 选择默认应用模式”，改了马上跟着变。
/// </summary>
internal static class Appearance
{
    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static readonly List<Window> themed = [];
    private static AppearanceMode mode = AppearanceMode.System;
    private static bool listening;
    private static bool themeFailureLogged;

    public static bool IsDark => mode == AppearanceMode.Dark || (mode == AppearanceMode.System && SystemUsesDarkApps());

    /// <summary>启动时（建窗口之前）调用一次，之后外观设置变了再调用。</summary>
    public static void Apply(AppearanceMode newMode)
    {
        mode = newMode;
        if (!listening)
        {
            listening = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        OverlayPalette.Apply(IsDark);
        foreach (var window in themed.ToList()) ApplyTheme(window);
    }

    public static void Shutdown()
    {
        if (!listening) return;
        listening = false;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    /// <summary>
    /// 设置类窗口跟着外观变。在窗口的构造函数开头、建控件之前调用：控件按主题的样式建（例如分段按钮找切换按钮的样式）。
    /// </summary>
    public static void Theme(Window window)
    {
        themed.Add(window);
        window.Closed += (_, _) => themed.Remove(window);
        ApplyTheme(window);
        // 主题里窗口的样式按类型 Window 找，派生的窗口类（SettingsWindow 等）不会自动套用，明确引用它
        // （找不到时退回系统主题里 Window 的样式，与不设时相同）。
        window.SetResourceReference(FrameworkElement.StyleProperty, typeof(Window));
        // 主题的窗口背景是透明的，靠系统的 Mica 背景（Windows 11 22H2 起才有）。更早的系统上背景是黑的，
        // 浅色主题的文字、没勾上的复选框都看不见（Windows 11 21H2 实测），所以改用主题里不透明的背景色。
        if (!SupportsSystemBackdrop) window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ApplicationBackgroundBrush");
    }

    private static bool SupportsSystemBackdrop => Environment.OSVersion.Version.Build >= 22621;

    private static void ApplyTheme(Window window)
    {
        try
        {
            // Fluent 主题在 .NET 10 里仍标为实验性（WPF0001），接口以后可能会变；这里只在设置类窗口上用。
#pragma warning disable WPF0001
            window.ThemeMode = mode switch
            {
                AppearanceMode.Light => ThemeMode.Light,
                AppearanceMode.Dark => ThemeMode.Dark,
                _ => ThemeMode.System,
            };
#pragma warning restore WPF0001
        }
        catch (Exception e)
        {
            // 主题出错时窗口照常用系统的浅色样式。
            if (!themeFailureLogged) AppLog.Error("appearance: window theme unavailable", e);
            themeFailureLogged = true;
        }
    }

    /// <summary>“选择默认应用模式”是不是深色（读不到时当作浅色，与 Windows 的默认值相同）。</summary>
    private static bool SystemUsesDarkApps()
    {
        try
        {
            return Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1) is int v && v == 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || mode != AppearanceMode.System) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (mode == AppearanceMode.System && IsDark != OverlayPalette.IsDark) OverlayPalette.Apply(IsDark);
        });
    }
}

/// <summary>
/// 设置类窗口（设置、模型服务、字幕记录、录音记录）的层级（与 macOS 的 setWindowLevels 相同）：用 LiveSub 时（前台是本程序的窗口）
/// 显示在字幕和 AI 助手上面，打开设置不用再把字幕拖开；切到别的程序时回到普通层，不挡别的程序的窗口。
/// 字幕和 AI 助手每 2 秒被提到最上层一次（见 OverlayWindow），之后调用 <see cref="Update"/> 把这些窗口再提上去。
/// </summary>
internal static class WindowLevels
{
    private static readonly List<Window> raised = [];

    /// <param name="closeButtonOnly">设置、模型服务：只留关闭按钮，没有最小化、最大化（与 macOS 的设置类窗口相同）；仍然可以拖边框改大小。</param>
    public static void Raise(Window window, bool closeButtonOnly)
    {
        raised.Add(window);
        window.Closed += (_, _) => raised.Remove(window);
        window.Activated += (_, _) => Update();
        // 失去激活时前台窗口可能还没换好，稍后再看：换到的是本程序的另一个窗口（例如选文件夹的对话框）时不降下来。
        window.Deactivated += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Background, Update);
        if (closeButtonOnly)
        {
            window.SourceInitialized += (_, _) =>
                NativeMethods.SetStyle(window.Handle(), 0, NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX);
        }
    }

    /// <summary>按前台是不是本程序，把这些窗口提到最上层（不激活），或放回普通层。正在用的那个放在最上面。</summary>
    public static void Update()
    {
        // 系统对话框开着（导出字幕、选文件夹、添加附件）：不动层级。否则对话框一打开，它的所有者失去激活，
        // 这里把设置类窗口重新提到最上层，盖住对话框；对话框是模态的，用户点不到它，像是程序卡住了。
        if (raised.Count == 0 || SystemDialogs.IsOpen) return;
        bool ours = NativeMethods.ForegroundIsOurs();
        var foreground = NativeMethods.GetForegroundWindow();
        foreach (var window in raised.Where(w => w.IsVisible).OrderBy(w => w.Handle() == foreground).ToList())
        {
            if (!ours)
            {
                var hwnd = window.Handle();
                // 按实际的扩展样式判断：WPF 的 Topmost 已经是 false，窗口却可能被系统带进了置顶层（见下面）。
                if (!window.Topmost && !NativeMethods.IsTopmost(hwnd)) continue;
                window.Topmost = false;
                if (NativeMethods.IsTopmost(hwnd))
                {
                    NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                }
                // 取消置顶时系统把窗口放到所有普通窗口的最上面，会盖住用户刚切过去的程序：放回它的下面。
                // 前台是置顶窗口（别的程序的“总在最前”）时不放：插到置顶窗口下面会被带进置顶层，一直挡在别人上面。
                if (foreground != IntPtr.Zero && !NativeMethods.IsTopmost(foreground))
                {
                    NativeMethods.SetWindowPos(hwnd, foreground, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                }
                continue;
            }
            if (!window.Topmost) window.Topmost = true;
            NativeMethods.SetWindowPos(window.Handle(), NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }
}

/// <summary>
/// 设置类窗口放进屏幕的工作区（见 <see cref="WindowFit"/>）：打开时的大小按主屏幕的工作区限制；
/// 显示以后（居中到了鼠标所在的屏幕）再按那块屏幕检查一次，标题栏不能在屏幕外。
/// </summary>
internal static class ScreenFit
{
    /// <summary>在构造函数里、设好 Width、Height、MinWidth、MinHeight 之后调用。</summary>
    public static void Apply(Window window)
    {
        var work = SystemParameters.WorkArea;
        window.Width = WindowFit.Size(window.Width, work.Width);
        window.Height = WindowFit.Size(window.Height, work.Height);
        window.MinWidth = Math.Min(window.MinWidth, window.Width);
        window.MinHeight = Math.Min(window.MinHeight, window.Height);
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => KeepInWorkArea(window));
    }

    /// <summary>按窗口所在屏幕的工作区（物理像素）缩小、挪回屏幕内；多块屏幕缩放不同时也对。</summary>
    public static void KeepInWorkArea(Window window)
    {
        var hwnd = window.Handle();
        if (hwnd == IntPtr.Zero || window.WindowState != WindowState.Normal || !NativeMethods.GetWindowRect(hwnd, out var r)) return;
        if (NativeMethods.WorkArea(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST)) is not { } w) return;
        var frame = new ScreenRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        var fit = WindowFit.Fit(frame, new ScreenRect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top));
        if (fit == frame) return;
        AppLog.Info($"window: moved {window.GetType().Name} into the work area");
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, (int)fit.Left, (int)fit.Top, (int)fit.Width, (int)fit.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }
}
