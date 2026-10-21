using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
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
/// 设置（一页，改了自动生效）：翻译、Claude Code、Anthropic API、OpenAI 兼容接口、语音识别、字幕、记录。
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly SettingsService settings;
    private readonly SecretStore secrets;
    private readonly AppShell shell;
    private AppSettings S => settings.Current;
    private bool loading = true;

    private readonly StackPanel body = new() { Margin = new Thickness(20, 8, 20, 20) };
    private readonly ComboBox engineBox = new();
    private readonly TextBlock claudeStatus = Note();
    private readonly ComboBox anthropicModelBox = new() { IsEditable = true };
    private readonly TextBlock anthropicStatus = Note();
    private readonly ListBox providerList = new() { Height = 96 };
    private readonly StackPanel providerDetails = new();
    private readonly TextBox providerName = new();
    private readonly TextBox providerUrl = new();
    private readonly PasswordBox providerKey = new();
    private readonly ComboBox providerModel = new() { IsEditable = true };
    private readonly TextBlock providerStatus = Note();
    private readonly TextBlock testStatus = Note();
    private readonly TextBlock modelStatus = Note();
    private readonly Button downloadButton = new();
    private CancellationTokenSource? downloadCancel;
    private ProviderConfig? editingProvider;
    /// <summary>延迟保存当前服务密钥的计时器；切换服务前要先保存，否则密钥会存到别的服务名下或丢失。</summary>
    private DispatcherTimer? providerKeyTimer;

    public SettingsWindow(SettingsService settings, SecretStore secrets, AppShell shell)
    {
        this.settings = settings;
        this.secrets = secrets;
        this.shell = shell;
        Title = $"{Strings.AppName} · {Strings.Settings_Title}";
        Width = 600;
        Height = 760;
        MinWidth = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        BuildTranslation();
        BuildClaudeCode();
        BuildAnthropic();
        BuildProviders();
        BuildRecognition();
        BuildOverlay();
        BuildRecords();
        loading = false;
        Closed += (_, _) => downloadCancel?.Cancel();
    }

    private void Commit(SettingsChange change)
    {
        if (!loading) settings.Commit(change);
    }

    // MARK: - 翻译

    private void BuildTranslation()
    {
        Section(Strings.Section_Translation);
        var source = Combo(LanguageOption.Sources.Select(LanguageName), LanguageOption.Sources.ToList().FindIndex(o => o.Id == S.SourceLanguage),
            i => { S.SourceLanguage = LanguageOption.Sources[i].Id; Commit(SettingsChange.Recognition | SettingsChange.Translation); });
        Row(Strings.Label_SourceLanguage, source);
        var target = Combo(LanguageOption.Targets.Select(LanguageName), LanguageOption.Targets.ToList().FindIndex(o => o.Id == S.TargetLanguage),
            i => { S.TargetLanguage = LanguageOption.Targets[i].Id; Commit(SettingsChange.Translation | SettingsChange.Appearance); });
        Row(Strings.Label_TargetLanguage, target);
        var audio = Combo([Strings.Source_Microphone, Strings.Source_System], S.AudioSource == AudioSourceMode.Microphone ? 0 : 1,
            i => { S.AudioSource = i == 0 ? AudioSourceMode.Microphone : AudioSourceMode.System; Commit(SettingsChange.Recognition); });
        Row(Strings.Label_AudioSource, audio);

        RefreshEngineBox();
        engineBox.SelectionChanged += (_, _) =>
        {
            if (loading || engineBox.SelectedItem is not ComboBoxItem { Tag: EngineChoice choice }) return;
            S.Engine = choice.Kind;
            if (choice.ProviderId is not null) S.ActiveProviderId = choice.ProviderId;
            Commit(SettingsChange.Translation);
        };
        Row(Strings.Label_Engine, engineBox);
        var test = new Button { Content = Strings.Button_Test, Padding = new Thickness(12, 3, 12, 3), HorizontalAlignment = HorizontalAlignment.Left };
        test.Click += async (_, _) => await TestTranslation(test);
        Row("", Stack(test, testStatus));
    }

    private sealed record EngineChoice(EngineKind Kind, string? ProviderId);

    private void RefreshEngineBox()
    {
        bool wasLoading = loading;
        loading = true;
        engineBox.Items.Clear();
        engineBox.Items.Add(new ComboBoxItem { Content = Strings.Group_ClaudeCode, Tag = new EngineChoice(EngineKind.ClaudeCode, null) });
        engineBox.Items.Add(new ComboBoxItem { Content = Strings.Group_Anthropic, Tag = new EngineChoice(EngineKind.Anthropic, null) });
        foreach (var p in S.Providers)
        {
            engineBox.Items.Add(new ComboBoxItem { Content = p.Name, Tag = new EngineChoice(EngineKind.OpenAICompatible, p.Id) });
        }
        engineBox.SelectedIndex = S.Engine switch
        {
            EngineKind.ClaudeCode => 0,
            EngineKind.Anthropic => 1,
            _ => Math.Max(0, 2 + S.Providers.FindIndex(p => p.Id == S.ActiveProviderId)),
        };
        loading = wasLoading;
    }

    private async Task TestTranslation(Button button)
    {
        button.IsEnabled = false;
        testStatus.Text = Strings.Status_Testing;
        try
        {
            using var session = TranslationEngines.Create(S, secrets,
                Prompts.TranslatorSystemPrompt(S.Source, S.Target));
            const string sample = "Hello everyone, can you hear me clearly?";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var raw = await session.RequestAsync(sample, sample, null, timeout.Token);
            testStatus.Text = Strings.Format(Strings.Status_TestOk, TranslationOutput.Split(raw, sample, S.Target.IsCJK).Translation);
        }
        catch (Exception e)
        {
            testStatus.Text = TranslationEngines.Describe(e);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    // MARK: - Claude Code

    private void BuildClaudeCode()
    {
        Section(Strings.Group_ClaudeCode);
        var models = TranslationEngines.ClaudeCodeModels;
        Row(Strings.Label_Model, Combo(models.Select(m => char.ToUpperInvariant(m[0]) + m[1..]), Math.Max(0, models.ToList().IndexOf(S.ClaudeCodeModel)),
            i => { S.ClaudeCodeModel = models[i]; Commit(SettingsChange.Translation); }));
        var path = new TextBox { Text = S.ClaudePath, ToolTip = Strings.Tip_ClaudePath };
        path.TextChanged += (_, _) =>
        {
            S.ClaudePath = path.Text.Trim();
            Commit(SettingsChange.Translation);
            UpdateClaudeStatus();
        };
        var browse = SmallButton(Strings.Button_Browse, () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "claude|claude.exe;claude.cmd|*.*|*.*" };
            if (dialog.ShowDialog(this) == true) path.Text = dialog.FileName;
        });
        Row(Strings.Label_ClaudePath, WithButtons(path, browse), Strings.Tip_ClaudePath);
        Row("", claudeStatus);
        UpdateClaudeStatus();
    }

    private void UpdateClaudeStatus()
    {
        var launch = ClaudeLocator.Locate(S.ClaudePath);
        claudeStatus.Text = launch is null ? Strings.Status_ClaudeMissing : Strings.Format(Strings.Status_ClaudeFound, launch.DisplayPath);
    }

    // MARK: - Anthropic API

    private void BuildAnthropic()
    {
        Section(Strings.Group_Anthropic);
        var key = new PasswordBox { Password = secrets.Get(SecretStore.AnthropicKey) ?? "", ToolTip = Strings.Tip_ApiKey };
        var saveTimer = Debounce(() =>
        {
            secrets.Set(SecretStore.AnthropicKey, key.Password);
            anthropicStatus.Text = key.Password.Length > 0 ? Strings.Status_KeySaved : "";
            Commit(SettingsChange.Translation);
        });
        key.PasswordChanged += (_, _) => Restart(saveTimer);
        Closing += (_, _) => Flush(saveTimer);
        Row(Strings.Label_ApiKey, key, Strings.Tip_ApiKey);

        FillAnthropicModels();
        anthropicModelBox.Text = S.AnthropicModel;
        anthropicModelBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            var model = anthropicModelBox.Text.Trim();
            if (loading || model.Length == 0 || model == S.AnthropicModel) return;
            S.AnthropicModel = model;
            Commit(SettingsChange.Translation);
        }));
        var fetch = SmallButton(Strings.Button_FetchModels, async () =>
        {
            Flush(saveTimer);
            if (secrets.Get(SecretStore.AnthropicKey) is not { } apiKey)
            {
                anthropicStatus.Text = Strings.Format(Strings.Error_MissingKey, "Anthropic API");
                return;
            }
            anthropicStatus.Text = Strings.Status_Fetching;
            try
            {
                var list = await AnthropicTransport.ListModelsAsync(apiKey, CancellationToken.None);
                S.AnthropicModels = list;
                Commit(SettingsChange.None);
                FillAnthropicModels();
                anthropicStatus.Text = Strings.Format(Strings.Status_FetchedModels, list.Count);
            }
            catch (Exception e)
            {
                anthropicStatus.Text = TranslationEngines.Describe(e);
            }
        });
        Row(Strings.Label_Model, WithButtons(anthropicModelBox, fetch));
        Row("", anthropicStatus);
    }

    private void FillAnthropicModels()
    {
        var text = anthropicModelBox.Text;
        anthropicModelBox.Items.Clear();
        foreach (var m in AnthropicModelPolicy.KnownModels.Concat(S.AnthropicModels).Distinct()) anthropicModelBox.Items.Add(m);
        anthropicModelBox.Text = text;
    }

    // MARK: - OpenAI 兼容接口

    private void BuildProviders()
    {
        Section(Strings.Section_Providers);
        providerList.DisplayMemberPath = nameof(ProviderConfig.Name);
        providerList.ItemsSource = S.Providers;
        providerList.SelectionChanged += (_, _) => ShowProvider(providerList.SelectedItem as ProviderConfig);

        var presets = new ComboBox { MinWidth = 140 };
        foreach (var p in ProviderPreset.All) presets.Items.Add(new ComboBoxItem { Content = Strings.IsChinese ? p.ChineseName : p.Name, Tag = p });
        presets.Items.Add(new ComboBoxItem { Content = Strings.Preset_Custom, Tag = null });
        presets.SelectedIndex = 0;
        var add = SmallButton(Strings.Button_AddProvider, () =>
        {
            var preset = (presets.SelectedItem as ComboBoxItem)?.Tag as ProviderPreset;
            var config = new ProviderConfig
            {
                Name = preset is null ? Strings.Preset_Custom : Strings.IsChinese ? preset.ChineseName : preset.Name,
                BaseUrl = preset?.BaseUrl ?? "",
            };
            S.Providers.Add(config);
            S.ActiveProviderId ??= config.Id;
            Commit(SettingsChange.None);
            providerList.Items.Refresh();
            providerList.SelectedItem = config;
            RefreshEngineBox();
        });
        var remove = SmallButton(Strings.Button_RemoveProvider, () =>
        {
            if (providerList.SelectedItem is not ProviderConfig p) return;
            providerKeyTimer?.Stop(); // 删掉的服务不要再保存密钥
            S.Providers.Remove(p);
            secrets.Set(SecretStore.ProviderKey(p.Id), null);
            if (S.ActiveProviderId == p.Id) S.ActiveProviderId = S.Providers.FirstOrDefault()?.Id;
            if (S.Engine == EngineKind.OpenAICompatible && S.Providers.Count == 0) S.Engine = EngineKind.ClaudeCode;
            Commit(SettingsChange.Translation);
            providerList.Items.Refresh();
            ShowProvider(null);
            RefreshEngineBox();
        });
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        addRow.Children.Add(presets);
        add.Margin = new Thickness(6, 0, 0, 0);
        remove.Margin = new Thickness(6, 0, 0, 0);
        addRow.Children.Add(add);
        addRow.Children.Add(remove);
        Row("", Stack(providerList, addRow));

        // 选中的服务
        var detailsGrid = NewGrid();
        providerDetails.Children.Add(detailsGrid);
        providerName.TextChanged += (_, _) =>
        {
            if (editingProvider is null || loading) return;
            editingProvider.Name = providerName.Text.Trim();
            Commit(SettingsChange.Translation);
            providerList.Items.Refresh();
            RefreshEngineBox();
        };
        AddRow(detailsGrid, Strings.Label_ProviderName, providerName, null);
        providerUrl.TextChanged += (_, _) =>
        {
            if (editingProvider is null || loading) return;
            editingProvider.BaseUrl = providerUrl.Text.Trim();
            Commit(SettingsChange.Translation);
        };
        AddRow(detailsGrid, Strings.Label_BaseUrl, providerUrl, Strings.Tip_BaseUrl);
        var keyTimer = providerKeyTimer = Debounce(() =>
        {
            if (editingProvider is null) return;
            secrets.Set(SecretStore.ProviderKey(editingProvider.Id), providerKey.Password);
            providerStatus.Text = providerKey.Password.Length > 0 ? Strings.Status_KeySaved : "";
            Commit(SettingsChange.Translation);
        });
        providerKey.ToolTip = Strings.Tip_ApiKey;
        providerKey.PasswordChanged += (_, _) =>
        {
            if (!loading) Restart(keyTimer);
        };
        Closing += (_, _) => Flush(keyTimer);
        AddRow(detailsGrid, Strings.Label_ApiKey, providerKey, Strings.Tip_ApiKey);
        providerModel.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            if (editingProvider is null || loading) return;
            editingProvider.Model = providerModel.Text.Trim();
            Commit(SettingsChange.Translation);
        }));
        var fetch = SmallButton(Strings.Button_FetchModels, async () =>
        {
            if (editingProvider is not { } p) return;
            Flush(keyTimer);
            providerStatus.Text = Strings.Status_Fetching;
            try
            {
                var transport = new OpenAICompatibleTransport(TranslationEngines.Http, p.BaseUrl,
                    secrets.Get(SecretStore.ProviderKey(p.Id)), "");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                p.Models = await transport.ListModelsAsync(timeout.Token);
                Commit(SettingsChange.None);
                FillProviderModels(p);
                providerStatus.Text = Strings.Format(Strings.Status_FetchedModels, p.Models.Count);
            }
            catch (Exception e)
            {
                providerStatus.Text = TranslationEngines.Describe(e);
            }
        });
        AddRow(detailsGrid, Strings.Label_Model, WithButtons(providerModel, fetch), null);
        AddRow(detailsGrid, "", providerStatus, null);
        body.Children.Add(providerDetails);
        ShowProvider(S.ActiveProvider ?? S.Providers.FirstOrDefault());
        if (editingProvider is not null) providerList.SelectedItem = editingProvider;
    }

    private void ShowProvider(ProviderConfig? p)
    {
        // 先把上一个服务还没保存的密钥存到它自己名下。
        if (providerKeyTimer is not null) Flush(providerKeyTimer);
        bool wasLoading = loading;
        loading = true;
        editingProvider = p;
        providerDetails.Visibility = p is null ? Visibility.Collapsed : Visibility.Visible;
        if (p is not null)
        {
            providerName.Text = p.Name;
            providerUrl.Text = p.BaseUrl;
            providerKey.Password = secrets.Get(SecretStore.ProviderKey(p.Id)) ?? "";
            FillProviderModels(p);
            providerModel.Text = p.Model;
            providerStatus.Text = "";
        }
        loading = wasLoading;
    }

    private void FillProviderModels(ProviderConfig p)
    {
        bool wasLoading = loading;
        loading = true;
        var text = providerModel.Text;
        providerModel.Items.Clear();
        foreach (var m in p.Models) providerModel.Items.Add(m);
        providerModel.Text = text;
        loading = wasLoading;
    }

    // MARK: - 语音识别

    private void BuildRecognition()
    {
        Section(Strings.Section_Recognition);
        var models = WhisperModel.All;
        var modelBox = Combo(models.Select(m => $"{m.Id} · {m.ApproxMegabytes} MB"), Math.Max(0, models.ToList().FindIndex(m => m.Id == S.WhisperModel)),
            i => { S.WhisperModel = models[i].Id; Commit(SettingsChange.Recognition); UpdateModelStatus(); });
        downloadButton.Content = Strings.Button_Download;
        downloadButton.Padding = new Thickness(10, 2, 10, 2);
        downloadButton.Click += async (_, _) => await DownloadModel();
        Row(Strings.Label_WhisperModel, WithButtons(modelBox, downloadButton), Strings.Tip_WhisperModel);
        Row("", modelStatus);
        UpdateModelStatus();

        Row(Strings.Label_UseGpu, Check(S.UseGpu, v => { S.UseGpu = v; Commit(SettingsChange.None); }, Strings.Tip_UseGpu), Strings.Tip_UseGpu);
        Row(Strings.Label_LivePreview, Check(S.LivePreview, v => { S.LivePreview = v; Commit(SettingsChange.None); }, Strings.Tip_LivePreview), Strings.Tip_LivePreview);
        Row(Strings.Label_FilterChinese, Check(S.FilterChinese, v => { S.FilterChinese = v; Commit(SettingsChange.Recognition); }, Strings.Tip_FilterChinese), Strings.Tip_FilterChinese);
        var mirror = new TextBox { Text = S.ModelMirror, ToolTip = Strings.Tip_ModelMirror };
        mirror.TextChanged += (_, _) => { S.ModelMirror = mirror.Text.Trim(); Commit(SettingsChange.None); };
        Row(Strings.Label_ModelMirror, mirror, Strings.Tip_ModelMirror);
    }

    private void UpdateModelStatus()
    {
        bool downloaded = WhisperModels.IsDownloaded(WhisperModel.Find(S.WhisperModel));
        modelStatus.Text = downloaded ? Strings.Model_Downloaded : Strings.Model_NotDownloaded;
        downloadButton.IsEnabled = !downloaded && downloadCancel is null;
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
            modelStatus.Text = Strings.Format(Strings.Error_ModelDownload, e.Message);
            downloadCancel = null;
            downloadButton.IsEnabled = true;
            return;
        }
        downloadCancel = null;
        UpdateModelStatus();
    }

    // MARK: - 字幕

    private void BuildOverlay()
    {
        Section(Strings.Section_Overlay);
        var modes = AppShell.DisplayModes;
        Row(Strings.Label_DisplayMode, Combo(modes.Select(m => m.Title), modes.Select(m => m.Mode).ToList().IndexOf(S.DisplayMode),
            i => { S.DisplayMode = modes[i].Mode; Commit(SettingsChange.Appearance); }));
        Row(Strings.Label_TranslationFontSize, SliderWithValue(14, 48, 1, S.TranslationFontSize, "0", v => { S.TranslationFontSize = v; Commit(SettingsChange.Appearance); }));
        Row(Strings.Label_SourceFontSize, SliderWithValue(10, 32, 1, S.SourceFontSize, "0", v => { S.SourceFontSize = v; Commit(SettingsChange.Appearance); }));
        Row(Strings.Label_Opacity, SliderWithValue(0, 1, 0.05, S.BackgroundOpacity, "0%", v => { S.BackgroundOpacity = v; Commit(SettingsChange.Appearance); }));
        Row(Strings.Label_VisibleLines, Combo(Enumerable.Range(1, 6).Select(n => n.ToString()), Math.Clamp(S.VisibleLines, 1, 6) - 1,
            i => { S.VisibleLines = i + 1; Commit(SettingsChange.Appearance); }));
        Row(Strings.Label_ShowStatusBar, Check(S.ShowStatusBar, v => { S.ShowStatusBar = v; Commit(SettingsChange.Appearance); }, Strings.Tip_ShowStatusBar), Strings.Tip_ShowStatusBar);
        Row(Strings.Label_Lock, Check(S.ClickThrough, v => { S.ClickThrough = v; Commit(SettingsChange.Appearance); }, Strings.Tip_Lock), Strings.Tip_Lock);
        var keys = HotKeys.All;
        var hotKey = Combo(keys.Select(k => HotKeys.Text(k) ?? Strings.HotKey_None), Math.Max(0, keys.ToList().IndexOf(S.HotKey)),
            i => { S.HotKey = keys[i]; Commit(SettingsChange.Appearance); });
        hotKey.ToolTip = Strings.Tip_HotKey;
        Row(Strings.Label_HotKey, hotKey, Strings.Tip_HotKey);
        var reset = new Button { Content = Strings.Button_ResetOverlay, Padding = new Thickness(12, 3, 12, 3), HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) => shell.ResetOverlayPosition();
        Row("", reset);
    }

    // MARK: - 记录

    private void BuildRecords()
    {
        Section(Strings.Section_Records);
        var folder = new TextBox { Text = S.TranscriptFolder };
        folder.TextChanged += (_, _) => { S.TranscriptFolder = folder.Text.Trim(); Commit(SettingsChange.None); };
        var browse = SmallButton(Strings.Button_Browse, () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(S.TranscriptFolder) ? S.TranscriptFolder : "" };
            if (dialog.ShowDialog(this) == true) folder.Text = dialog.FolderName;
        });
        var open = SmallButton(Strings.Button_Open, () => AppShell.OpenInExplorer(S.TranscriptFolder));
        Row(Strings.Label_TranscriptFolder, WithButtons(folder, browse, open));
        Row(Strings.Label_KeepOriginal, Check(S.KeepOriginalText, v => { S.KeepOriginalText = v; Commit(SettingsChange.None); }, Strings.Tip_KeepOriginal), Strings.Tip_KeepOriginal);
    }

    // MARK: - 布局小工具

    private static string LanguageName(LanguageOption o) => Strings.IsChinese ? o.DisplayName : o.EnglishName;

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

    private static Grid NewGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private void Row(string label, UIElement control, string? tip = null) => AddRow(currentGrid!, label, control, tip);

    private static void AddRow(Grid grid, string label, UIElement control, string? tip)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4), ToolTip = tip };
        Grid.SetRow(text, row);
        grid.Children.Add(text);
        if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
    }

    private ComboBox Combo(IEnumerable<string> items, int selected, Action<int> onChange)
    {
        var box = new ComboBox();
        foreach (var item in items) box.Items.Add(item);
        box.SelectedIndex = Math.Max(0, selected);
        box.SelectionChanged += (_, _) =>
        {
            if (!loading && box.SelectedIndex >= 0) onChange(box.SelectedIndex);
        };
        return box;
    }

    private CheckBox Check(bool value, Action<bool> onChange, string? tip)
    {
        var box = new CheckBox { IsChecked = value, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
        box.Checked += (_, _) => { if (!loading) onChange(true); };
        box.Unchecked += (_, _) => { if (!loading) onChange(false); };
        return box;
    }

    private FrameworkElement SliderWithValue(double min, double max, double step, double value, string format, Action<double> onChange)
    {
        var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, Value = value };
        var label = new TextBlock { Width = 48, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = value.ToString(format) };
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

    private static Button SmallButton(string text, Action action)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 2, 10, 2) };
        b.Click += (_, _) => action();
        return b;
    }

    private static FrameworkElement WithButtons(FrameworkElement main, params Button[] buttons)
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

    private static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var c in children) panel.Children.Add(c);
        return panel;
    }

    private static TextBlock Note() => new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };

    private static DispatcherTimer Debounce(Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Tag = action;
        return timer;
    }

    private static void Restart(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    /// <summary>窗口关闭或需要立即使用时，把还没保存的改动保存掉。</summary>
    private static void Flush(DispatcherTimer timer)
    {
        if (!timer.IsEnabled) return;
        timer.Stop();
        (timer.Tag as Action)?.Invoke();
    }
}
