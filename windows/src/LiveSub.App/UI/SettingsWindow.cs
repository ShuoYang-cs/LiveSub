using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LiveSub.App.Localization;
using LiveSub.App.Services;
using LiveSub.App.Speech;
using LiveSub.App.Translation;
using LiveSub.Core;
using LiveSub.Core.Chat;
using LiveSub.Core.Speech;
using LiveSub.Core.Subtitles;

namespace LiveSub.App.UI;

/// <summary>
/// 设置（一页，改了自动生效），布局与 macOS 版相同：翻译、字幕、记录三组全部直接列出（2026-10-22 用户要求去掉可折叠的“更多”），
/// Windows 版特有的识别设置（识别模型、显卡加速、实时预览、模型下载地址）放在最后单独的“识别”一组，快捷键在“字幕”一组。
/// 行尾不放说明文字，说明在悬停提示里。Claude 路径和每家服务用哪个具体模型在“模型服务”窗口里。
/// 只有关闭按钮；用 LiveSub 时显示在字幕和 AI 助手上面（<see cref="WindowLevels"/>）。
/// 托盘菜单、字幕上的按钮、模型服务窗口改了设置时，这里跟着更新。
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly SettingsService settings;
    private readonly AppShell shell;
    private AppSettings S => settings.Current;
    /// <summary>正在按设置更新控件：这时控件的改动事件不当成用户的操作。</summary>
    private bool loading = true;
    private bool refreshQueued;

    private readonly StackPanel body = new() { Margin = new Thickness(20, 8, 20, 20) };
    /// <summary>按当前设置重新显示控件（值、显示/隐藏、跟着设置变的文字）。</summary>
    private readonly List<Action> refreshers = [];
    private readonly ComboBox modelBox = new();
    /// <summary>模型列表上次的内容；没变时不重建，免得打断正在打开的下拉列表。</summary>
    private string modelBoxSignature = "";
    private readonly TextBlock modelStatus = Note();
    private readonly Button downloadButton = new();
    private CancellationTokenSource? downloadCancel;

    public SettingsWindow(SettingsService settings, AppShell shell)
    {
        this.settings = settings;
        this.shell = shell;
        Title = $"{Strings.AppName} · {Strings.Settings_Title}";
        Width = 560;
        Height = 760;
        MinWidth = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // 先套上外观（浅色、深色）再建控件：分段按钮要找主题里的切换按钮样式。
        Appearance.Theme(this);
        WindowLevels.Raise(this, closeButtonOnly: true);
        // 小屏、高缩放时不超出屏幕（标题栏不能在屏幕外）。
        ScreenFit.Apply(this);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        BuildTranslation();
        BuildOverlay();
        BuildUpdates();
        BuildRecords();
        BuildRecognition();
        Refresh();
        loading = false;

        settings.Changed += OnSettingsChanged;
        shell.Updates.Changed += OnUpdatesChanged;
        Closed += (_, _) =>
        {
            settings.Changed -= OnSettingsChanged;
            shell.Updates.Changed -= OnUpdatesChanged;
            downloadCancel?.Cancel();
        };
    }

    private void Commit(SettingsChange change)
    {
        if (!loading) settings.Commit(change);
    }

    /// <summary>
    /// 设置改了（不管是这里还是别处改的）：稍后按设置更新一遍控件。
    /// 放到事件处理之后再做，不在控件自己的改动事件里重建它；值没变的控件不动，正在输入的文字不会被打断。
    /// </summary>
    private void OnSettingsChanged(SettingsChange change)
    {
        if (refreshQueued) return;
        refreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            refreshQueued = false;
            Refresh();
        });
    }

    private void Refresh()
    {
        bool wasLoading = loading;
        loading = true;
        try
        {
            foreach (var refresh in refreshers) refresh();
        }
        finally
        {
            loading = wasLoading;
        }
    }

    // MARK: - 翻译

    private void BuildTranslation()
    {
        Section(Strings.Section_Translation);
        var sources = LanguageOption.Sources;
        var source = Combo(sources.Select(LanguageName), () => IndexOf(sources, o => o.Id == S.SourceLanguage),
            i => { S.SourceLanguage = sources[i].Id; Commit(SettingsChange.Recognition | SettingsChange.Translation); });
        var targets = LanguageOption.Targets;
        var target = Combo(targets.Select(LanguageName), () => IndexOf(targets, o => o.Id == S.TargetLanguage),
            i => { S.TargetLanguage = targets[i].Id; Commit(SettingsChange.Translation | SettingsChange.Appearance); });
        Row(Strings.Label_Language, LanguagePair(source, target));

        var audioSources = AppShell.AudioSources;
        Row(Strings.Label_AudioSource, Segmented(audioSources.Select(a => a.ShortTitle), () => IndexOf(audioSources, a => a.Mode == S.AudioSource),
            i => { S.AudioSource = audioSources[i].Mode; Commit(SettingsChange.Recognition); }));

        modelBox.ToolTip = Strings.Tip_TranslationModel;
        AutomationProperties.SetName(modelBox, Strings.Label_TranslationModel);
        modelBox.SelectionChanged += (_, _) =>
        {
            if (loading || modelBox.SelectedItem is not ComboBoxItem { Tag: ModelChoice choice } || choice == S.TranslationModel) return;
            S.TranslationModel = choice;
            Commit(SettingsChange.Translation);
        };
        refreshers.Add(RefreshModelBox);
        Row(Strings.Label_TranslationModel, modelBox, Strings.Tip_TranslationModel);

        var services = Note();
        services.VerticalAlignment = VerticalAlignment.Center;
        services.Margin = new Thickness(0);
        // 能用的模型服务：本地的，或者已经填了 API 密钥的（默认列出的那几个没填密钥不算；与 macOS 相同）。
        refreshers.Add(() =>
        {
            int configured = S.Endpoints.Count(e => e.IsLocal || shell.Secrets.Has(SecretStore.EndpointKey(e.Id)));
            services.Text = configured == 0 ? Strings.ModelServices_None : Strings.Format(Strings.ModelServices_Count, configured);
        });
        Row(Strings.ModelServices_Title, WithButtons(services, SmallButton(Strings.Button_Manage, shell.ShowModelServices)));

        // 只在中文界面、讲话语言不是中文时显示（见 AppSettings.FilterChineseOffered）。
        var filter = Row(Strings.Label_FilterChinese,
            Check(() => S.FilterChinese, v => { S.FilterChinese = v; Commit(SettingsChange.Recognition); }, Strings.Tip_FilterChinese),
            Strings.Tip_FilterChinese);
        refreshers.Add(() => filter.SetVisible(S.FilterChineseOffered));

        // 自动纠正只对云端模型有效（与 macOS 相同）：用 Google 翻译、混元翻译时开关置灰并自动关掉，下面有一行说明。
        var autoCorrect = Row(Strings.Label_AutoCorrect, Check(() => S.AutoCorrect, v =>
            {
                S.AutoCorrect = v;
                // 纠正会改原文：只看译文时看不出来，打开时从“仅译文”切到“双语”。
                bool switchedDisplay = v && S.DisplayMode == DisplayMode.TranslationOnly;
                if (switchedDisplay) S.DisplayMode = DisplayMode.Bilingual;
                Commit(switchedDisplay ? SettingsChange.Translation | SettingsChange.Appearance : SettingsChange.Translation);
            }, Strings.Tip_AutoCorrect),
            Strings.Tip_AutoCorrect);
        var autoCorrectNote = Note();
        autoCorrectNote.Text = Strings.Note_AutoCorrectCloudOnly;
        autoCorrectNote.FontSize = 11;
        autoCorrectNote.Margin = new Thickness(0, 0, 0, 4);
        Row("", autoCorrectNote);
        // 打开“自动纠正口音”后才出现，缩进表示从属于它。
        var keepOriginal = Row(Strings.Label_KeepOriginal,
            Check(() => S.KeepOriginalText, v =>
            {
                S.KeepOriginalText = v;
                // 保留识别原文时“仅译文”不能选：已经在“仅译文”就切到“双语”（Commit 里的 SyncModels 改显示模式）。
                Commit(SettingsChange.Appearance);
            }, Strings.Tip_KeepOriginal),
            Strings.Tip_KeepOriginal);
        keepOriginal.Label.Margin = new Thickness(16, 4, 12, 4);
        refreshers.Add(() =>
        {
            bool cloud = S.TranslationModel.IsLanguageModel;
            autoCorrect.Control.IsEnabled = cloud;
            autoCorrect.Label.Opacity = cloud ? 1 : 0.5;
            keepOriginal.SetVisible(S.AutoCorrect);
            keepOriginal.Control.IsEnabled = cloud;
            keepOriginal.Label.Opacity = cloud ? 1 : 0.5;
        });
    }

    /// <summary>翻译模型：“基础”“云端模型”“本地模型”三组，组名不可选，每家服务一项（与 macOS 的分组菜单相同）。</summary>
    private void RefreshModelBox()
    {
        var groups = TranslationEngines.TranslationMenu(S);
        // 每家服务只列一项，服务名不变时它用的具体模型也可能变了：模型也算进去。
        var signature = string.Join('\u0001', groups.SelectMany(g => g.Items.Select(i => $"{i.Title}\u0002{i.Choice}").Prepend("#" + g.Title)));
        if (signature != modelBoxSignature)
        {
            modelBoxSignature = signature;
            modelBox.Items.Clear();
            foreach (var group in groups)
            {
                modelBox.Items.Add(new ComboBoxItem { Content = group.Title, IsEnabled = false, FontWeight = FontWeights.SemiBold });
                foreach (var item in group.Items)
                {
                    modelBox.Items.Add(new ComboBoxItem { Content = item.Title, Tag = item.Choice, Padding = new Thickness(16, 2, 4, 2) });
                }
            }
        }
        // 所选模型已不在列表里（例如从服务的模型里删掉了）时不选任何一项，与 macOS 相同。
        modelBox.SelectedItem = modelBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is ModelChoice c && c == S.TranslationModel);
    }

    // MARK: - 字幕

    private void BuildOverlay()
    {
        Section(Strings.Section_Overlay);
        var modes = AppShell.DisplayModes;
        // 开着自动纠正又保留识别原文时，“仅译文”置灰、不能选（与 macOS 相同）。
        Row(Strings.Label_DisplayMode, Segmented(modes.Select(m => m.Title), () => IndexOf(modes, m => m.Mode == S.DisplayMode),
            i => { S.DisplayMode = modes[i].Mode; Commit(SettingsChange.Appearance); },
            i => !(modes[i].Mode == DisplayMode.TranslationOnly && S.TranslationOnlyLocked)));
        // 外观：字幕、AI 助手和各个窗口一起变（与 macOS 相同）。
        var appearances = new[]
        {
            (Mode: AppearanceMode.System, Title: Strings.Appearance_System),
            (Mode: AppearanceMode.Light, Title: Strings.Appearance_Light),
            (Mode: AppearanceMode.Dark, Title: Strings.Appearance_Dark),
        };
        Row(Strings.Label_Appearance, Segmented(appearances.Select(a => a.Title), () => IndexOf(appearances, a => a.Mode == S.Appearance),
            i => { S.Appearance = appearances[i].Mode; Commit(SettingsChange.Appearance); }));
        // 一个滑块同时调译文和原文字号（原文约为译文的 0.62 倍）。
        Row(Strings.Label_FontSize, SliderWithValue(14, 56, 1, () => S.TranslationFontSize, "0", v =>
        {
            S.TranslationFontSize = Math.Round(v, MidpointRounding.AwayFromZero);
            S.SourceFontSize = Math.Round(v * 0.62, MidpointRounding.AwayFromZero);
            Commit(SettingsChange.Appearance);
        }));
        Row(Strings.Label_Opacity, SliderWithValue(0, 1, 0.05, () => S.BackgroundOpacity, "0%",
            v => { S.BackgroundOpacity = v; Commit(SettingsChange.Appearance); }));

        // 只有“恢复默认”按钮：“拖动字幕边框调整”这句说明挪进了按钮的悬停提示（2026-10-22 用户要求）。
        var reset = SmallButton(Strings.Button_ResetSize, shell.ResetOverlay);
        reset.ToolTip = Strings.Tip_ResetSize;
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        Row(Strings.Label_Size, reset, Strings.Tip_ResetSize);

        Row(Strings.Label_ShowStatusBar, Check(() => S.ShowStatusBar, v => { S.ShowStatusBar = v; Commit(SettingsChange.Appearance); }, Strings.Tip_ShowStatusBar),
            Strings.Tip_ShowStatusBar);
        Row(Strings.Label_Lock, Check(() => S.ClickThrough, v => { S.ClickThrough = v; Commit(SettingsChange.Appearance); }, Strings.Tip_Lock), Strings.Tip_Lock);

        var keys = HotKeys.All;
        var hotKey = Combo(keys.Select(k => HotKeys.Text(k) ?? Strings.HotKey_None), () => IndexOf(keys, k => k == S.HotKey),
            i => { S.HotKey = keys[i]; Commit(SettingsChange.Appearance); });
        hotKey.ToolTip = Strings.Tip_HotKey;
        Row(Strings.Label_HotKey, hotKey, Strings.Tip_HotKey);
    }

    // MARK: - 更新

    private void OnUpdatesChanged() => Dispatcher.BeginInvoke(Refresh);

    /// <summary>自动检查更新的开关、当前版本，以及“检查更新”（有新版本时是“更新”）。</summary>
    private void BuildUpdates()
    {
        var updates = shell.Updates;
        Section(Strings.Section_Update);
        Row(Strings.Label_AutoUpdate, Check(() => S.AutoCheckUpdates, v => { S.AutoCheckUpdates = v; Commit(SettingsChange.None); }, null));
        var status = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
        var check = SmallButton(Strings.Button_CheckUpdate, updates.CheckNow);
        var install = SmallButton(Strings.Update_Install, updates.Install);
        Row(Strings.Format(Strings.Label_CurrentVersion, UpdateService.CurrentVersion), WithButtons(status, check, install));
        refreshers.Add(() =>
        {
            bool available = updates.Phase == UpdatePhase.Available && updates.Release is not null;
            status.Text = available ? Strings.Format(Strings.Update_Available, updates.Release!.Version) : updates.StatusText;
            install.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            check.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
            check.IsEnabled = !updates.Checking;
        });
    }

    // MARK: - 记录

    /// <summary>字幕记录、录音记录（都只有“查看…”按钮，2026-10-22 用户要求去掉行尾说明）、记录文件夹。</summary>
    private void BuildRecords()
    {
        Section(Strings.Section_Records);
        Row(Strings.Label_History, LeftButton(SmallButton(Strings.Button_View, shell.ShowHistory)));
        Row(Strings.Label_Recordings, LeftButton(SmallButton(Strings.Button_View, shell.ShowRecordings)));

        var folder = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
        refreshers.Add(() =>
        {
            folder.Text = S.TranscriptFolder;
            folder.ToolTip = S.TranscriptFolder;
        });
        var changeFolder = SmallButton(Strings.Button_Change, () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(S.TranscriptFolder) ? S.TranscriptFolder : "" };
            if (SystemDialogs.Show(dialog, this) != true) return;
            S.TranscriptFolder = dialog.FolderName;
            Commit(SettingsChange.None);
        });
        Row(Strings.Label_TranscriptFolder, WithButtons(folder, changeFolder));
    }

    private static Button LeftButton(Button button)
    {
        button.HorizontalAlignment = HorizontalAlignment.Left;
        return button;
    }

    // MARK: - 识别（Windows 版特有）

    /// <summary>Windows 版特有：Whisper 识别模型、显卡加速、实时预览、模型下载地址。</summary>
    private void BuildRecognition()
    {
        Section(Strings.Section_Recognition);
        var models = WhisperModel.All;
        var modelBox = Combo(models.Select(m => $"{m.Id} · {m.ApproxMegabytes} MB"), () => IndexOf(models, m => m.Id == S.WhisperModel),
            i => { S.WhisperModel = models[i].Id; Commit(SettingsChange.Recognition); });
        downloadButton.Content = Strings.Button_Download;
        downloadButton.Padding = new Thickness(10, 2, 10, 2);
        downloadButton.Click += async (_, _) => await DownloadModel();
        Row(Strings.Label_WhisperModel, WithButtons(modelBox, downloadButton), Strings.Tip_WhisperModel);
        Row("", modelStatus);
        refreshers.Add(UpdateModelStatus);

        Row(Strings.Label_UseGpu, Check(() => S.UseGpu, v => { S.UseGpu = v; Commit(SettingsChange.None); }, Strings.Tip_UseGpu), Strings.Tip_UseGpu);
        Row(Strings.Label_LivePreview, Check(() => S.LivePreview, v => { S.LivePreview = v; Commit(SettingsChange.None); }, Strings.Tip_LivePreview),
            Strings.Tip_LivePreview);
        var mirror = new TextBox { ToolTip = Strings.Tip_ModelMirror };
        refreshers.Add(() =>
        {
            if (mirror.Text.Trim() != S.ModelMirror) mirror.Text = S.ModelMirror;
        });
        mirror.TextChanged += (_, _) =>
        {
            if (loading) return;
            S.ModelMirror = mirror.Text.Trim();
            Commit(SettingsChange.None);
        };
        Row(Strings.Label_ModelMirror, mirror, Strings.Tip_ModelMirror);
    }

    private void UpdateModelStatus()
    {
        // 正在下载时状态里是进度，不要覆盖。
        if (downloadCancel is not null) return;
        bool downloaded = WhisperModels.IsDownloaded(WhisperModel.Find(S.WhisperModel));
        modelStatus.Text = downloaded ? Strings.Model_Downloaded : Strings.Model_NotDownloaded;
        downloadButton.IsEnabled = !downloaded;
    }

    private async Task DownloadModel()
    {
        var model = WhisperModel.Find(S.WhisperModel);
        downloadCancel = new CancellationTokenSource();
        downloadButton.IsEnabled = false;
        try
        {
            var progress = new Progress<int>(p => modelStatus.Text = Strings.Format(Strings.Status_DownloadingModel, model.Id, p));
            await WhisperModels.EnsureAsync(model, S.ModelMirror, progress, downloadCancel.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            AppLog.Error("model download failed", e);
            downloadCancel = null;
            modelStatus.Text = Strings.Format(Strings.Error_ModelDownload, e.Message);
            downloadButton.IsEnabled = true;
            return;
        }
        downloadCancel = null;
        UpdateModelStatus();
    }

    // MARK: - 布局小工具（模型服务窗口也用）

    private static string LanguageName(LanguageOption o) => Strings.IsChinese ? o.DisplayName : o.EnglishName;

    private static int IndexOf<T>(IReadOnlyList<T> items, Func<T, bool> match)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (match(items[i])) return i;
        }
        return -1;
    }

    private Grid? currentGrid;

    private void Section(string title)
    {
        body.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 6),
        });
        currentGrid = NewGrid();
        body.Children.Add(currentGrid);
    }

    /// <summary>两列：标签、控件。</summary>
    internal static Grid NewGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    /// <summary>一行的标签和控件，用来整行显示或隐藏。</summary>
    internal sealed record RowParts(TextBlock Label, UIElement Control)
    {
        public void SetVisible(bool visible)
        {
            var v = visible ? Visibility.Visible : Visibility.Collapsed;
            Label.Visibility = v;
            Control.Visibility = v;
        }
    }

    private RowParts Row(string label, UIElement control, string? tip = null) => AddRow(currentGrid!, label, control, tip);

    internal static RowParts AddRow(Grid grid, string label, UIElement control, string? tip)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // 英文标签较长，放不下时折行，不截断。
        var text = new TextBlock
        {
            Text = label,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 12, 4),
            ToolTip = tip,
        };
        Grid.SetRow(text, row);
        grid.Children.Add(text);
        if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return new RowParts(text, control);
    }

    private ComboBox Combo(IEnumerable<string> items, Func<int> selected, Action<int> onChange)
    {
        var box = new ComboBox();
        foreach (var item in items) box.Items.Add(item);
        refreshers.Add(() => box.SelectedIndex = Math.Max(0, selected()));
        box.SelectionChanged += (_, _) =>
        {
            if (!loading && box.SelectedIndex >= 0) onChange(box.SelectedIndex);
        };
        return box;
    }

    /// <summary>讲话语言 → 字幕语言，放在同一行。</summary>
    private static FrameworkElement LanguagePair(ComboBox source, ComboBox target)
    {
        source.ToolTip = Strings.Label_SourceLanguage;
        target.ToolTip = Strings.Label_TargetLanguage;
        AutomationProperties.SetName(source, Strings.Label_SourceLanguage);
        AutomationProperties.SetName(target, Strings.Label_TargetLanguage);
        var arrow = new TextBlock { Text = "→", Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(arrow, 1);
        Grid.SetColumn(target, 2);
        grid.Children.Add(source);
        grid.Children.Add(arrow);
        grid.Children.Add(target);
        return grid;
    }

    /// <summary>分段选择（相当于 macOS 的 segmented picker）：一排按钮样式的单选按钮。</summary>
    /// <param name="enabled">某一段能不能选（例如开着保留识别原文时“仅译文”置灰）；null 表示都能选。</param>
    private FrameworkElement Segmented(IEnumerable<string> titles, Func<int> selected, Action<int> onChange, Func<int, bool>? enabled = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        // 单选按钮套用切换按钮的系统样式，看起来像一组按钮；找不到样式时就是普通的单选按钮。
        var style = TryFindResource(typeof(ToggleButton)) as Style;
        var buttons = new List<RadioButton>();
        foreach (var title in titles)
        {
            int index = buttons.Count;
            var button = new RadioButton
            {
                Content = title,
                Padding = new Thickness(12, 3, 12, 3),
                MinWidth = 64,
                Margin = new Thickness(0, 0, style is null ? 12 : -1, 0),
            };
            if (style is not null) button.Style = style;
            button.Checked += (_, _) =>
            {
                if (!loading) onChange(index);
            };
            buttons.Add(button);
            panel.Children.Add(button);
        }
        refreshers.Add(() =>
        {
            int i = selected();
            for (int k = 0; k < buttons.Count; k++)
            {
                buttons[k].IsChecked = k == i;
                if (enabled is not null) buttons[k].IsEnabled = enabled(k);
            }
        });
        return panel;
    }

    private CheckBox Check(Func<bool> value, Action<bool> onChange, string? tip)
    {
        var box = new CheckBox { ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
        refreshers.Add(() => box.IsChecked = value());
        box.Checked += (_, _) => { if (!loading) onChange(true); };
        box.Unchecked += (_, _) => { if (!loading) onChange(false); };
        return box;
    }

    private FrameworkElement SliderWithValue(double min, double max, double step, Func<double> value, string format, Action<double> onChange)
    {
        var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true };
        var label = new TextBlock { Width = 48, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        refreshers.Add(() =>
        {
            var v = value();
            if (Math.Abs(slider.Value - v) > 1e-6) slider.Value = v;
            label.Text = v.ToString(format);
        });
        slider.ValueChanged += (_, e) =>
        {
            label.Text = e.NewValue.ToString(format);
            if (!loading) onChange(Math.Round(e.NewValue, 2));
        };
        var dock = new DockPanel();
        DockPanel.SetDock(label, Dock.Right);
        dock.Children.Add(label);
        dock.Children.Add(slider);
        return dock;
    }

    internal static Button SmallButton(string text, Action action)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 2, 10, 2) };
        b.Click += (_, _) => action();
        return b;
    }

    internal static FrameworkElement WithButtons(FrameworkElement main, params Button[] buttons)
    {
        var dock = new DockPanel { LastChildFill = true };
        foreach (var b in buttons.Reverse())
        {
            b.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(b, Dock.Right);
            dock.Children.Add(b);
        }
        dock.Children.Add(main);
        return dock;
    }

    internal static TextBlock Note() => new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };

    /// <summary>输入框为空时显示的灰色提示（WPF 的输入框没有自带的占位文字）。</summary>
    internal static FrameworkElement WithPlaceholder(TextBox box, string hint)
    {
        var text = HintText();
        text.Text = hint;
        void Update() => text.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Update();
        Update();
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>密码框为空时显示 <paramref name="hint"/>（提示文字由调用方随时修改）。</summary>
    internal static FrameworkElement WithPlaceholder(PasswordBox box, TextBlock hint)
    {
        void Update() => hint.Visibility = box.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.PasswordChanged += (_, _) => Update();
        Update();
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(hint);
        return grid;
    }

    internal static TextBlock HintText() => new()
    {
        Opacity = 0.45,
        IsHitTestVisible = false,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(5, 0, 5, 0),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
}
