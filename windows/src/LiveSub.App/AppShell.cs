using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LiveSub.App.Localization;
using LiveSub.App.Services;
using LiveSub.App.Translation;
using LiveSub.App.UI;
using LiveSub.Core.Subtitles;

namespace LiveSub.App;

/// <summary>入口：单实例；再次启动时让已运行的实例把字幕显示出来。</summary>
public static class Program
{
    [STAThread]
    public static int Main()
    {
        // LIVESUB_UI_LANGUAGE=en 或 zh-Hans 可以不改系统语言就切换界面语言（测试用）。
        var uiLanguage = Environment.GetEnvironmentVariable("LIVESUB_UI_LANGUAGE");
        var culture = CultureInfo.CurrentUICulture;
        if (!string.IsNullOrWhiteSpace(uiLanguage))
        {
            try
            {
                culture = CultureInfo.GetCultureInfo(uiLanguage.Trim());
            }
            catch (CultureNotFoundException)
            {
            }
        }
        Strings.Initialize(culture);
        using var mutex = new Mutex(true, @"Local\LiveSub.Windows.Instance", out bool firstInstance);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LiveSub.Windows.Show");
        if (!firstInstance)
        {
            showSignal.Set();
            return 0;
        }

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppShell? shell = null;
        app.DispatcherUnhandledException += (_, e) =>
        {
            AppLog.Error("unhandled UI exception", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error("unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("unobserved task exception", e.Exception);
            e.SetObserved();
        };
        app.Startup += (_, _) =>
        {
            try
            {
                shell = new AppShell(app);
                shell.Start();
            }
            catch (Exception e)
            {
                // 启动失败时不能留下一个看不见、又占着单实例锁的进程。
                AppLog.Error("startup failed", e);
                System.Windows.MessageBox.Show($"{Strings.AppName}: {e.Message}", Strings.AppName,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                shell?.Dispose();
                shell = null;
                app.Shutdown(1);
                return;
            }
            var listener = new Thread(() =>
            {
                try
                {
                    while (showSignal.WaitOne()) app.Dispatcher.BeginInvoke(() => shell?.ShowOverlay());
                }
                catch (ObjectDisposedException)
                {
                    // 程序正在退出。
                }
            }) { IsBackground = true, Name = "LiveSub.ShowSignal" };
            listener.Start();
        };
        app.Exit += (_, _) => shell?.Dispose();
        AppLog.Info($"launch: LiveSub {typeof(Program).Assembly.GetName().Version} ui={Strings.Culture.Name}");
        return app.Run();
    }
}

/// <summary>托盘程序：持有设置、控制器、悬浮字幕、托盘图标、快捷键和各个窗口，并提供菜单动作。</summary>
public sealed class AppShell : IDisposable
{
    private readonly System.Windows.Application app;
    private OverlayWindow? overlay;
    private TrayIcon? tray;
    private GlobalHotKey? hotKey;
    private SettingsWindow? settingsWindow;
    private HistoryWindow? historyWindow;
    private DispatcherTimer? trayTimer;

    public SettingsService Settings { get; } = new();
    public SecretStore Secrets { get; } = new();
    public SubtitleController Controller { get; }

    public static readonly IReadOnlyList<(DisplayMode Mode, string Title)> DisplayModes =
    [
        (DisplayMode.Bilingual, Strings.Display_Bilingual),
        (DisplayMode.TranslationOnly, Strings.Display_TranslationOnly),
        (DisplayMode.SourceOnly, Strings.Display_SourceOnly),
    ];

    public AppShell(System.Windows.Application app)
    {
        this.app = app;
        Controller = new SubtitleController(Settings, Secrets, app.Dispatcher);
    }

    public void Start()
    {
        overlay = new OverlayWindow(Controller, Settings, new OverlayActions(
            ToggleRunning: ToggleRunning,
            CycleDisplayMode: CycleDisplayMode,
            Lock: ToggleLock,
            ShowHistory: ShowHistory,
            ShowSettings: ShowSettings,
            Hide: HideOverlay,
            ShowModelMenu: ShowModelMenu));
        tray = new TrayIcon(this);
        // 托盘提示文字与图标跟着状态变，但不必每次字幕变化都更新。
        trayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        trayTimer.Tick += (_, _) => tray?.Update();
        trayTimer.Start();

        ApplyHotKey();
        Settings.Changed += _ =>
        {
            if (Settings.Current.HotKey != registeredHotKey) ApplyHotKey();
        };

        overlay.ShowNoActivate();
        if (Environment.GetEnvironmentVariable("LIVESUB_AUTOSTART") == "1") ToggleRunning();
    }

    private HotKeyChoice? registeredHotKey;

    /// <summary>按设置注册显示/隐藏字幕的快捷键（设置里改了会重新注册）。</summary>
    private void ApplyHotKey()
    {
        hotKey?.Dispose();
        hotKey = null;
        var choice = Settings.Current.HotKey;
        registeredHotKey = choice;
        if (HotKeys.Text(choice) is not { } text) return;
        hotKey = new GlobalHotKey(HotKeys.Modifiers(choice), HotKeys.VirtualKeyL, ToggleOverlay);
        if (!hotKey.Registered) Controller.ShowAlert(Strings.Format(Strings.Error_HotKey, text));
    }

    public bool OverlayVisible => overlay?.IsVisible == true;

    public void ShowOverlay() => overlay?.ShowNoActivate();

    public void HideOverlay() => overlay?.Hide();

    public void ToggleOverlay()
    {
        if (OverlayVisible) HideOverlay();
        else ShowOverlay();
    }

    public async void ToggleRunning()
    {
        await Controller.Toggle();
        tray?.Update();
    }

    public void SelectAudioSource(AudioSourceMode mode)
    {
        if (Settings.Current.AudioSource == mode) return;
        Settings.Current.AudioSource = mode;
        Settings.Commit(SettingsChange.Recognition);
    }

    public void SelectModel(ModelChoice choice)
    {
        TranslationEngines.Select(Settings.Current, choice);
        Settings.Commit(SettingsChange.Translation);
    }

    public void SelectDisplayMode(DisplayMode mode)
    {
        Settings.Current.DisplayMode = mode;
        Settings.Commit(SettingsChange.Appearance);
    }

    private void CycleDisplayMode()
    {
        var modes = DisplayModes.Select(m => m.Mode).ToList();
        SelectDisplayMode(modes[(modes.IndexOf(Settings.Current.DisplayMode) + 1) % modes.Count]);
    }

    public void ToggleLock()
    {
        Settings.Current.ClickThrough = !Settings.Current.ClickThrough;
        Settings.Commit(SettingsChange.Appearance);
    }

    public void ResetOverlayPosition()
    {
        ShowOverlay();
        overlay?.ResetPosition();
    }

    /// <summary>状态条上点模型名称：弹出模型菜单。</summary>
    private void ShowModelMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        bool first = true;
        foreach (var group in TranslationEngines.Choices(Settings.Current, Secrets))
        {
            if (!first) menu.Items.Add(new Separator());
            first = false;
            menu.Items.Add(new MenuItem { Header = group.Title, IsEnabled = false });
            foreach (var choice in group.Choices)
            {
                var item = new MenuItem { Header = choice.Title, IsCheckable = false, IsChecked = choice.Selected, IsEnabled = choice.Enabled };
                item.Click += (_, _) => SelectModel(choice);
                menu.Items.Add(item);
            }
        }
        menu.IsOpen = true;
    }

    public void Export(SubtitleFileFormat format) => Controller.ExportSubtitles(format);

    public void ShowHistory()
    {
        if (historyWindow is null)
        {
            historyWindow = new HistoryWindow(Controller);
            historyWindow.Closed += (_, _) => historyWindow = null;
        }
        historyWindow.Show();
        historyWindow.Activate();
    }

    public void ShowSettings()
    {
        if (settingsWindow is null)
        {
            settingsWindow = new SettingsWindow(Settings, Secrets, this);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
        }
        settingsWindow.Show();
        if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
        settingsWindow.Activate();
    }

    public async void NewSession()
    {
        await Controller.NewSession();
        tray?.Update();
    }

    public void OpenTranscriptFolder()
    {
        var folder = Controller.TranscriptPath is { } path ? Path.GetDirectoryName(path)! : Settings.Current.TranscriptFolder;
        OpenInExplorer(folder);
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path) is false && File.Exists(path) is false) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception e)
        {
            AppLog.Error("open folder failed", e);
        }
    }

    public async void Quit()
    {
        AppLog.Info("quit");
        // 先把图标和字幕收起来，让用户立刻看到程序退出；收尾（识别完最后几段、写记录）最多几秒。
        tray?.Dispose();
        tray = null;
        trayTimer?.Stop();
        overlay?.Hide();
        try
        {
            await Controller.ShutdownAsync();
        }
        catch (Exception e)
        {
            AppLog.Error("quit: shutdown failed", e);
        }
        finally
        {
            app.Shutdown();
        }
    }

    public void Dispose()
    {
        trayTimer?.Stop();
        hotKey?.Dispose();
        tray?.Dispose();
        Controller.Dispose();
    }
}
