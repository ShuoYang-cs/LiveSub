using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LiveSub.App.Audio;
using LiveSub.App.Localization;
using LiveSub.App.Services;
using LiveSub.App.Translation;
using LiveSub.App.UI;
using LiveSub.Core.Chat;
using LiveSub.Core.Subtitles;

namespace LiveSub.App;

/// <summary>入口：单实例；再次启动时让已运行的实例把字幕显示出来。</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 更新：下载好的新版以这个参数启动，等旧进程退出后把文件拷进安装目录（见 UpdateService）。
        if (args.Length >= 3 && args[0] == "--apply-update" && int.TryParse(args[2], out var oldPid))
            return UpdateApplier.Apply(args[1], oldPid);

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

/// <summary>
/// 托盘程序：持有设置、控制器、悬浮字幕、AI 助手面板、托盘图标、快捷键和各个窗口，并提供菜单动作。
/// AI 助手面板跟着字幕一起显示和隐藏：字幕隐藏时它也隐藏，字幕再显示时，打开着的面板一起回来。
/// </summary>
public sealed class AppShell : IDisposable
{
    private readonly System.Windows.Application app;
    private OverlayWindow? overlay;
    /// <summary>第一次打开 AI 助手时才创建。</summary>
    private AssistantWindow? assistantWindow;
    private TrayIcon? tray;
    private GlobalHotKey? hotKey;
    private SettingsWindow? settingsWindow;
    private ModelServicesWindow? modelServicesWindow;
    private HistoryWindow? historyWindow;
    private RecordingsWindow? recordingsWindow;
    private DispatcherTimer? trayTimer;

    public SecretStore Secrets { get; }
    public SettingsService Settings { get; }
    public SubtitleController Controller { get; }
    public UpdateService Updates { get; }

    /// <summary>音源的选项（设置和托盘菜单共用）：托盘菜单用完整的名字，设置里的分段按钮用简短的名字（与 macOS 相同）。</summary>
    public static readonly IReadOnlyList<(AudioSourceMode Mode, string Title, string ShortTitle)> AudioSources =
        AudioSourceModes.All.Select(m => (m, m.Title(), m.ShortTitle())).ToList();

    public static readonly IReadOnlyList<(DisplayMode Mode, string Title)> DisplayModes =
    [
        (DisplayMode.Bilingual, Strings.Display_Bilingual),
        (DisplayMode.TranslationOnly, Strings.Display_TranslationOnly),
        (DisplayMode.SourceOnly, Strings.Display_SourceOnly),
    ];

    public AppShell(System.Windows.Application app)
    {
        this.app = app;
        // 先读密钥：迁移旧版设置时要把旧的 Anthropic API 密钥搬到新的服务名下。
        Secrets = new SecretStore();
        Settings = new SettingsService(Secrets);
        Controller = new SubtitleController(Settings, Secrets, app.Dispatcher);
        Updates = new UpdateService(Settings, app.Dispatcher);
    }

    public void Start()
    {
        // 先定好外观（字幕、AI 助手的颜色，设置类窗口的主题），再建窗口。
        appliedAppearance = Settings.Current.Appearance;
        Appearance.Apply(appliedAppearance);
        overlay = new OverlayWindow(Controller, Settings, new OverlayActions(
            ToggleRunning: ToggleRunning,
            ToggleRecording: ToggleRecording,
            Lock: ToggleLock,
            SelectAudioSource: SelectAudioSource,
            ToggleAssistant: ToggleAssistant,
            ShowSettings: ShowSettings,
            Quit: Quit,
            ModelMenu: ModelMenu,
            OpenMicrophoneSettings: MicrophonePrivacy.OpenSettings));
        tray = new TrayIcon(this);
        // 托盘提示文字与图标跟着状态变，但不必每次字幕变化都更新。
        trayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        trayTimer.Tick += (_, _) => tray?.Update();
        trayTimer.Start();

        ApplyHotKey();
        // 选了还没填 API 密钥的服务、还没下载的混元翻译：打开“模型服务”，滚到这一栏（与 macOS 相同）。
        Settings.ModelNeedsSetup += ShowModelServices;
        Settings.Changed += _ =>
        {
            if (Settings.Current.HotKey != registeredHotKey) ApplyHotKey();
            if (Settings.Current.Appearance != appliedAppearance)
            {
                appliedAppearance = Settings.Current.Appearance;
                Appearance.Apply(appliedAppearance);
            }
        };
        // secrets.bin 读不出来：这次运行不会写它（见 SecretStore.Unreadable），告诉用户密钥暂时不能用、也存不上。
        if (Secrets.Unreadable) Controller.ShowAlert(Strings.Error_SecretsUnreadable);

        overlay.ShowNoActivate();
        _ = DetectLocalServices();
        Updates.Start();
        if (Environment.GetEnvironmentVariable("LIVESUB_AUTOSTART") == "1") ToggleRunning();
    }

    private HotKeyChoice? registeredHotKey;
    private AppearanceMode appliedAppearance;

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

    /// <summary>AI 助手面板是否打开（字幕隐藏时它也隐藏，但仍算打开，字幕再显示时一起回来）。</summary>
    public bool AssistantVisible => Settings.Current.AssistantVisible;

    /// <summary>显示字幕，以及打开着的 AI 助手。</summary>
    public void ShowOverlay()
    {
        overlay?.ShowNoActivate();
        if (Settings.Current.AssistantVisible) ShowAssistant();
    }

    /// <summary>字幕和 AI 助手一起隐藏（快捷按钮、快捷键、托盘菜单）。</summary>
    public void HideOverlay()
    {
        assistantWindow?.Hide();
        overlay?.Hide();
    }

    public void ToggleOverlay()
    {
        if (OverlayVisible) HideOverlay();
        else ShowOverlay();
    }

    /// <summary>打开 AI 助手面板；这时才启动它的会话（不用就不占资源）。</summary>
    private void ShowAssistant()
    {
        if (overlay is null) return;
        if (assistantWindow is null)
        {
            assistantWindow = new AssistantWindow(Controller.Assistant, Settings, overlay, ToggleAssistant);
            assistantWindow.Closed += (_, _) =>
            {
                assistantWindow = null;
                if (overlay is not null) overlay.KeepAbove = null;
            };
            // 字幕定时提到最上层时把面板提到它上面（面板自己不再定时提）。
            overlay.KeepAbove = assistantWindow;
        }
        assistantWindow.ShowNoActivate();
        Controller.Assistant.WarmUp();
    }

    /// <summary>打开或收起 AI 助手（面板上的“收起”和字幕上的快捷按钮）。字幕隐藏着时只记下状态。</summary>
    public void ToggleAssistant()
    {
        var s = Settings.Current;
        s.AssistantVisible = !s.AssistantVisible;
        if (s.AssistantVisible && OverlayVisible) ShowAssistant();
        else assistantWindow?.Hide();
        // 快捷按钮里的 AI 助手按钮跟着变色。
        overlay?.Refresh();
    }

    /// <summary>托盘菜单里的“AI 助手”：字幕隐藏着时先把字幕显示出来。</summary>
    public void ToggleAssistantFromMenu()
    {
        if (!OverlayVisible) overlay?.ShowNoActivate();
        ToggleAssistant();
    }

    /// <summary>启动时检测本机的 Claude Code、Ollama、LM Studio；新加了服务就保存。</summary>
    private async Task DetectLocalServices()
    {
        try
        {
            if (await LocalServiceDetector.RunAsync(Settings.Current)) Settings.Commit(SettingsChange.None);
        }
        catch (Exception e)
        {
            AppLog.Error("local service detection failed", e);
        }
    }

    public async void ToggleRunning()
    {
        // 与 macOS 相同：从托盘菜单开始字幕时，收起的字幕（和打开着的 AI 助手）也显示出来。
        if (!OverlayVisible && !Controller.IsRunning && !Controller.IsStarting) ShowOverlay();
        await Controller.Toggle();
        tray?.Update();
    }

    /// <summary>录音按钮（快捷按钮和托盘菜单）：字幕运行中也可以随时打开或关掉。</summary>
    public void ToggleRecording()
    {
        Controller.ToggleRecording();
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
        if (Settings.Current.TranslationModel == choice) return;
        Settings.Current.TranslationModel = choice;
        Settings.Commit(SettingsChange.Translation);
    }

    public void ToggleLock()
    {
        Settings.Current.ClickThrough = !Settings.Current.ClickThrough;
        Settings.Commit(SettingsChange.Appearance);
    }

    /// <summary>
    /// 设置里的“恢复默认”（与 macOS 相同）：字幕恢复默认大小（面积是屏幕的 1/16、宽高比 3:1）、移回屏幕右上角；
    /// AI 助手也忘掉拖到的位置，回到字幕旁边。
    /// </summary>
    public void ResetOverlay()
    {
        Settings.Current.ResetOverlay();
        Settings.Commit(SettingsChange.Appearance);
        ShowOverlay();
        overlay?.ResetPosition();
        assistantWindow?.ResetPosition();
    }

    /// <summary>状态条上点模型名称时的菜单（与托盘菜单里的“翻译模型”相同），由字幕窗口弹出。</summary>
    private ContextMenu ModelMenu()
    {
        var menu = new ContextMenu();
        bool first = true;
        foreach (var group in TranslationEngines.TranslationMenu(Settings.Current))
        {
            if (!first) menu.Items.Add(new Separator());
            first = false;
            menu.Items.Add(new MenuItem { Header = group.Title, IsEnabled = false });
            foreach (var entry in group.Items)
            {
                menu.Items.Add(PopupMenus.Item(entry.Title, () => SelectModel(entry.Choice), entry.Selected));
            }
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(PopupMenus.Item(Strings.Menu_ManageModels, ShowModelServices));
        return menu;
    }

    /// <param name="owner">点导出的窗口（字幕记录窗口）：保存对话框显示在它上面，不被设置类窗口挡住。</param>
    public void Export(SubtitleFileFormat format, Window? owner = null) => Controller.ExportSubtitles(format, owner);

    public void GenerateNotes() => Controller.GenerateNotes();

    public void ShowHistory()
    {
        if (historyWindow is null)
        {
            historyWindow = new HistoryWindow(Controller, Settings, this);
            historyWindow.Closed += (_, _) => historyWindow = null;
        }
        historyWindow.Show();
        if (historyWindow.WindowState == WindowState.Minimized) historyWindow.WindowState = WindowState.Normal;
        historyWindow.Activate();
    }

    /// <summary>录音记录（设置 › 记录 › 录音记录，托盘菜单）。</summary>
    public void ShowRecordings()
    {
        if (recordingsWindow is null)
        {
            recordingsWindow = new RecordingsWindow(Settings);
            recordingsWindow.Closed += (_, _) => recordingsWindow = null;
        }
        recordingsWindow.Show();
        if (recordingsWindow.WindowState == WindowState.Minimized) recordingsWindow.WindowState = WindowState.Normal;
        recordingsWindow.Activate();
    }

    public void ShowSettings()
    {
        if (settingsWindow is null)
        {
            settingsWindow = new SettingsWindow(Settings, this);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
        }
        settingsWindow.Show();
        if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
        settingsWindow.Activate();
    }

    /// <summary>“模型服务”窗口：只开一个，已经打开时提到前面。</summary>
    public void ShowModelServices() => ShowModelServices(null);

    /// <param name="focusServiceId">要滚到的服务（还没填 API 密钥、还没下载混元翻译时），并说明怎么开始用。</param>
    public void ShowModelServices(string? focusServiceId)
    {
        if (modelServicesWindow is null)
        {
            modelServicesWindow = new ModelServicesWindow(Settings, Secrets);
            modelServicesWindow.Closed += (_, _) => modelServicesWindow = null;
        }
        modelServicesWindow.Show();
        if (modelServicesWindow.WindowState == WindowState.Minimized) modelServicesWindow.WindowState = WindowState.Normal;
        modelServicesWindow.Activate();
        if (focusServiceId is not null) modelServicesWindow.FocusService(focusServiceId);
    }

    public async void NewSession()
    {
        await Controller.NewSession();
        // 新的记录也是新的对话；面板开着时提前启动它的会话。
        if (AssistantVisible && OverlayVisible) Controller.Assistant.WarmUp();
        tray?.Update();
    }

    /// <summary>打开记录文件夹；已经有记录文件时选中它（与 macOS 相同）。</summary>
    public void OpenTranscriptFolder()
    {
        if (Controller.TranscriptPath is { } path && File.Exists(path))
        {
            RevealInExplorer(path);
            return;
        }
        OpenInExplorer(Controller.TranscriptPath is { } p ? Path.GetDirectoryName(p)! : Settings.Current.TranscriptFolder);
    }

    /// <summary>在资源管理器中打开文件所在的文件夹，并选中这个文件。</summary>
    public static void RevealInExplorer(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        }
        catch (Exception e)
        {
            AppLog.Error("reveal file failed", e);
        }
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
        assistantWindow?.Hide();
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
        Appearance.Shutdown();
        trayTimer?.Stop();
        hotKey?.Dispose();
        tray?.Dispose();
        Controller.Dispose();
        // 正在下载混元翻译时取消（下了一半的临时文件下次重新下）。
        LocalModelStore.Shared.Cancel();
    }
}
