using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using LiveSub.App.Localization;
using LiveSub.App.Services;
using LiveSub.App.Translation;
using LiveSub.Core;
using LiveSub.Core.Chat;
using LiveSub.Core.Translation;

namespace LiveSub.App.UI;

/// <summary>
/// “模型服务”（与 macOS 的 ModelServicesView 相同，顺序和文字也一样）：
/// Claude Code（翻译、AI 助手各用哪个型号，Claude 路径）、混元翻译（下载到本机），以及默认列出的云端服务和自己添加的服务
/// （OpenAI 兼容或 Anthropic 的 API，或 Ollama、LM Studio 等本地服务），每家都能“测试”。
/// 模型列表里每家服务只列一项，具体用哪个模型在这里为翻译和 AI 助手分别选。
/// 改了自动保存；API 密钥用 Windows 数据保护加密后保存在本机（<see cref="SecretStore"/>）。
/// 外观跟着设置变，只有关闭按钮；用 LiveSub 时显示在字幕和 AI 助手上面。
/// 只有正在被翻译模型（或 AI 助手）使用的服务改动时才换翻译会话。
/// </summary>
public sealed class ModelServicesWindow : Window
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    /// <summary>出错提示用橙色（在浅色背景上要看得清）。</summary>
    private static readonly SolidColorBrush ErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC2, 0x5B, 0x00)));
    private static readonly SolidColorBrush DeleteBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)));
    /// <summary>卡片的边框：灰色半透明，浅色、深色外观下都看得出来。</summary>
    private static readonly SolidColorBrush CardBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x4D, 0x80, 0x80, 0x80)));
    /// <summary>“测试”翻译的句子（与 macOS 相同）。</summary>
    private const string TestSentence = "Welcome, everyone. Let's get started.";

    private readonly SettingsService settings;
    private readonly SecretStore secrets;
    private AppSettings S => settings.Current;
    private readonly ScrollViewer scroll;
    private readonly StackPanel body = new() { Margin = new Thickness(20, 16, 20, 20) };
    private readonly StackPanel cards = new();
    private readonly LocalModelCard localCard;
    /// <summary>窗口关闭时取消还在进行的获取列表和测试。</summary>
    private readonly CancellationTokenSource lifetime = new();

    public ModelServicesWindow(SettingsService settings, SecretStore secrets)
    {
        this.settings = settings;
        this.secrets = secrets;
        Title = $"{Strings.AppName} · {Strings.ModelServices_Title}";
        Width = 600;
        Height = 720;
        MinWidth = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // 外观跟着设置变；只有关闭按钮；用 LiveSub 时显示在字幕和 AI 助手上面（与设置窗口相同）。
        Appearance.Theme(this);
        WindowLevels.Raise(this, closeButtonOnly: true);
        // 小屏、高缩放时不超出屏幕（标题栏不能在屏幕外）。
        ScreenFit.Apply(this);

        body.Children.Add(new TextBlock { Text = Strings.ModelServices_Intro, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
        if (secrets.Unreadable)
        {
            body.Children.Add(new TextBlock
            {
                Text = Strings.Error_SecretsUnreadable,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ErrorBrush,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }
        body.Children.Add(BuildAddButton());
        body.Children.Add(new ClaudeCard(this));
        // 打开窗口、回到这个窗口时按磁盘重新判断混元翻译下没下载（文件可能被删掉、被安全软件隔离）。
        LocalModelStore.Shared.Refresh();
        Activated += (_, _) => LocalModelStore.Shared.Refresh();
        localCard = new LocalModelCard(this);
        body.Children.Add(localCard);
        body.Children.Add(cards);
        scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = scroll;

        foreach (var endpoint in S.Endpoints) cards.Children.Add(new EndpointCard(this, endpoint));
        LocalModelStore.Shared.Changed += localCard.Refresh;
        LocalModelStore.Shared.InstalledChanged += localCard.OnInstalledChanged;
        Closed += (_, _) =>
        {
            LocalModelStore.Shared.Changed -= localCard.Refresh;
            LocalModelStore.Shared.InstalledChanged -= localCard.OnInstalledChanged;
            lifetime.Cancel();
        };
    }

    /// <summary>
    /// 滚到这家服务，并说明怎么开始用（选了还没填 API 密钥的服务、还没下载的混元翻译时；与 macOS 相同）。
    /// </summary>
    /// <param name="serviceId">服务的 Id；混元翻译是 <see cref="LocalModelFiles.ServiceId"/>。</param>
    public void FocusService(string serviceId)
    {
        Card? card = serviceId == LocalModelFiles.ServiceId
            ? localCard
            : cards.Children.OfType<EndpointCard>().FirstOrDefault(c => c.Endpoint.Id == serviceId);
        if (card is null) return;
        card.ShowNotice(card == localCard ? Strings.Notice_DownloadToUse : Strings.Notice_EnterKey, isError: false);
        // 等排好版才知道位置；滚到这一栏的顶上（BringIntoView 只滚到刚好露出来）。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!card.IsVisible) return;
            var top = card.TransformToAncestor(body).Transform(new Point(0, 0)).Y;
            scroll.ScrollToVerticalOffset(Math.Max(0, top - 8));
        });
    }

    // MARK: - 添加、删除

    /// <summary>“添加模型服务”：常用服务的模板（默认列出的服务在前，删掉后可以从这里加回来），最后是“自定义”。</summary>
    private Button BuildAddButton()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = Strings.Button_AddService });
        content.Children.Add(new TextBlock
        {
            Text = "\uE70D", // ChevronDown
            FontFamily = IconFont,
            FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 2, 0, 0),
        });
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(12, 3, 12, 3),
            Margin = new Thickness(0, 12, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var preset in ChatEndpointPreset.All)
        {
            var name = Strings.IsChinese ? preset.ChineseName : preset.Name;
            menu.Items.Add(MenuItem(name, () => Add(new ModelEndpoint
            {
                Name = name,
                Format = preset.Format,
                BaseUrl = preset.BaseUrl,
                Models = [.. preset.ExampleModels],
            })));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(Strings.Preset_Custom, () => Add(new ModelEndpoint
        {
            Name = Strings.Service_CustomName,
            Format = ChatApiFormat.OpenAI,
            BaseUrl = "https://",
        })));
        button.Click += (_, _) => menu.IsOpen = true;
        return button;
    }

    private static MenuItem MenuItem(string title, Action action)
    {
        // 用 TextBlock 原样显示：字符串标题里的下划线会被当成快捷键标记。
        var item = new MenuItem { Header = new TextBlock { Text = title } };
        item.Click += (_, _) => action();
        return item;
    }

    private void Add(ModelEndpoint endpoint)
    {
        S.Endpoints.Add(endpoint);
        settings.Commit(SettingsChange.None);
        var card = new EndpointCard(this, endpoint);
        cards.Children.Add(card);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => card.BringIntoView());
    }

    /// <summary>
    /// 删除服务和它的密钥；正在用它的翻译模型换回 Google 翻译，AI 助手换回 Claude。
    /// 删掉的默认服务不再自动加回来（<see cref="AppSettings.RemoveEndpoint"/>）。
    /// </summary>
    private void Remove(EndpointCard card)
    {
        var endpoint = card.Endpoint;
        bool used = UsedByModels(endpoint);
        secrets.Set(SecretStore.EndpointKey(endpoint.Id), null);
        S.RemoveEndpoint(endpoint);
        cards.Children.Remove(card);
        settings.Commit(used ? SettingsChange.Translation : SettingsChange.None);
    }

    private bool UsedByModels(ModelEndpoint endpoint) =>
        S.TranslationModel.EndpointId == endpoint.Id || S.AssistantModel.EndpointId == endpoint.Id;

    /// <summary>保存一个服务的改动。翻译模型（或 AI 助手）正在用它时才需要换会话（例如改了密钥、换了“翻译用”的模型）。</summary>
    private void Changed(ModelEndpoint endpoint) =>
        settings.Commit(UsedByModels(endpoint) ? SettingsChange.Translation : SettingsChange.None);

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    // MARK: - 每一栏共用

    /// <summary>一栏：标题、内容，下面一行是“测试”按钮、转圈和结果（Claude Code、混元翻译和每个服务都一样）。</summary>
    private abstract class Card : Border
    {
        protected readonly ModelServicesWindow Owner;
        protected readonly TextBlock Header = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        protected readonly StackPanel Panel = new();
        protected readonly Button TestButton;
        /// <summary>测试按钮这一行（每个服务的这一行右边还有“删除”）。</summary>
        protected readonly DockPanel Actions = new() { LastChildFill = true, Margin = new Thickness(0, 8, 0, 0) };
        private readonly ProgressBar spinner = new()
        {
            IsIndeterminate = true,
            Width = 48,
            Height = 6,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        private readonly TextBlock notice = new()
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        };
        protected bool Busy { get; private set; }

        protected Card(ModelServicesWindow owner)
        {
            Owner = owner;
            Margin = new Thickness(0, 14, 0, 0);
            Padding = new Thickness(14, 10, 14, 10);
            BorderThickness = new Thickness(1);
            BorderBrush = CardBorder;
            CornerRadius = new CornerRadius(6);
            TestButton = SettingsWindow.SmallButton(Strings.Button_Test, async () => await Test());
            TestButton.ToolTip = Strings.Tip_Test;
            ToolTipService.SetShowOnDisabled(TestButton, true);
            DockPanel.SetDock(TestButton, Dock.Left);
            DockPanel.SetDock(spinner, Dock.Left);
            Actions.Children.Add(TestButton);
            Actions.Children.Add(spinner);
            Panel.Children.Add(Header);
            Child = Panel;
        }

        /// <summary>内容都加好之后调用：把测试这一行放到最后，结果的文字占满剩下的宽度。</summary>
        protected void FinishLayout()
        {
            Actions.Children.Add(notice);
            Panel.Children.Add(Actions);
            UpdateButtons();
        }

        protected static TextBlock Intro(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 4) };

        /// <summary>这家现在能不能测试（例如服务至少要有一个模型、混元翻译要先下载）。</summary>
        protected virtual bool CanTest => true;

        protected virtual void UpdateButtons()
        {
            TestButton.IsEnabled = !Busy && CanTest;
            spinner.Visibility = Busy ? Visibility.Visible : Visibility.Collapsed;
        }

        protected void SetBusy(bool value)
        {
            Busy = value;
            UpdateButtons();
        }

        public void ShowNotice(string text, bool isError)
        {
            notice.Text = text;
            notice.ToolTip = text.Length == 0 ? null : text;
            if (isError)
            {
                notice.Foreground = ErrorBrush;
                notice.Opacity = 1;
            }
            else
            {
                notice.ClearValue(TextBlock.ForegroundProperty);
                notice.Opacity = 0.7;
            }
        }

        protected string NoticeText => notice.Text;

        protected abstract Task Test();

        /// <summary>
        /// 用“翻译用”的模型翻译一句话，确认服务（地址和密钥、Claude Code 的登录、本机模型）和模型都可用，并显示用时（与 macOS 相同）。
        /// 和翻译时一样由 <see cref="TranslationEngines.Create"/> 创建会话。Claude Code 的用时包括启动命令行；
        /// 混元翻译不算启动服务、加载模型的时间（只在第一句时有）。
        /// </summary>
        protected async Task RunTest(string name, Func<ITranslator> make)
        {
            SetBusy(true);
            ShowNotice("", isError: false);
            try
            {
                using var translator = make();
                if (translator is LocalTranslator local) await local.PrepareAsync(Owner.lifetime.Token);
                var watch = Stopwatch.StartNew();
                var raw = await translator.TranslateAsync(TestSentence, [], null, Owner.lifetime.Token);
                var seconds = watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
                var translation = TranslationOutput.Split(raw, TestSentence, Owner.S.Target.IsCJK).Translation;
                ShowNotice(Strings.Format(Strings.Status_TestOk, name, seconds, translation), isError: false);
            }
            catch (OperationCanceledException) when (Owner.lifetime.IsCancellationRequested)
            {
                // 窗口已关闭。
            }
            catch (Exception e)
            {
                ShowNotice(TranslationEngines.Describe(e), isError: true);
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>大模型会话做的翻译器：系统提示词和翻译时一样，只翻一句（测试用）。</summary>
        protected ITranslator Session(ModelChoice choice, TimeSpan timeout)
        {
            var s = Owner.S;
            return new ChatTranslator(TranslationEngines.Create(choice, s, Owner.secrets,
                Prompts.TranslatorSystemPrompt(s.Source, s.Target), maxTurns: 1, timeout: timeout));
        }

        /// <summary>从几个模型里选一个（翻译用、AI 助手用）。</summary>
        protected static ComboBox ModelPicker(Action<string> onChange)
        {
            var box = new ComboBox();
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is ComboBoxItem { Tag: string model }) onChange(model);
            };
            return box;
        }
    }

    // MARK: - Claude Code

    /// <summary>本机 Claude Code：模型列表里只有一项“Claude”，型号在这里分别为翻译和 AI 助手选；Claude 路径也在这里（原来在设置里）。</summary>
    private sealed class ClaudeCard : Card
    {
        private readonly TextBlock claudeStatus = SettingsWindow.Note();

        public ClaudeCard(ModelServicesWindow owner) : base(owner)
        {
            Header.Text = "Claude Code";
            Panel.Children.Add(Intro(Strings.Claude_Intro));
            var grid = SettingsWindow.NewGrid();
            var s = owner.S;
            SettingsWindow.AddRow(grid, Strings.Label_ForTranslation, ClaudeModels(s.ClaudeTranslationModel, model =>
            {
                if (owner.S.ClaudeTranslationModel == model) return;
                owner.S.ClaudeTranslationModel = model;
                // 正在用 Claude 翻译：跟着换型号（保存时同步，见 AppSettings.SyncModels）。
                owner.settings.Commit(owner.S.TranslationModel.Kind == ModelKind.ClaudeCode ? SettingsChange.Translation : SettingsChange.None);
            }), null);
            SettingsWindow.AddRow(grid, Strings.Label_ForAssistant, ClaudeModels(s.ClaudeAssistantModel, model =>
            {
                if (owner.S.ClaudeAssistantModel == model) return;
                owner.S.ClaudeAssistantModel = model;
                // AI 助手下一问按新型号开新会话。
                owner.settings.Commit(SettingsChange.None);
            }), null);

            var path = new TextBox { Text = s.ClaudePath, ToolTip = Strings.Tip_ClaudePath };
            path.TextChanged += (_, _) =>
            {
                owner.S.ClaudePath = path.Text.Trim();
                owner.settings.Commit(SettingsChange.Translation);
                UpdateClaudeStatus();
            };
            var browse = SettingsWindow.SmallButton(Strings.Button_Browse, () =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "claude|claude.exe;claude.cmd|*.*|*.*" };
                if (SystemDialogs.Show(dialog, owner) == true) path.Text = dialog.FileName;
            });
            SettingsWindow.AddRow(grid, Strings.Label_ClaudePath,
                SettingsWindow.WithButtons(SettingsWindow.WithPlaceholder(path, Strings.Hint_ClaudePath), browse), Strings.Tip_ClaudePath);
            SettingsWindow.AddRow(grid, "", claudeStatus, null);
            Panel.Children.Add(grid);
            UpdateClaudeStatus();
            FinishLayout();
        }

        private static ComboBox ClaudeModels(string selected, Action<string> onChange)
        {
            var box = ModelPicker(onChange);
            (string Model, string Title)[] options =
            [
                (ModelCatalog.Haiku, Strings.Claude_Haiku),
                (ModelCatalog.Sonnet, Strings.Claude_Sonnet),
                (ModelCatalog.Opus, Strings.Claude_Opus),
            ];
            foreach (var (model, title) in options)
            {
                var item = new ComboBoxItem { Content = title, Tag = model };
                box.Items.Add(item);
                if (model == selected) box.SelectedItem = item;
            }
            return box;
        }

        private void UpdateClaudeStatus()
        {
            var launch = ClaudeLocator.Locate(Owner.S.ClaudePath);
            claudeStatus.Text = launch is null ? Strings.Status_ClaudeMissing : Strings.Format(Strings.Status_ClaudeFound, launch.DisplayPath);
        }

        protected override Task Test()
        {
            var model = Owner.S.ClaudeTranslationModel;
            return RunTest("Claude " + ModelCatalog.ClaudeModelName(model), () => Session(ModelChoice.ClaudeCode(model), TimeSpan.FromSeconds(60)));
        }
    }

    // MARK: - 混元翻译

    /// <summary>下载到本机的混元翻译模型：只能翻译，在翻译模型的“本地模型”一组里。</summary>
    private sealed class LocalModelCard : Card
    {
        private static LocalModelStore Store => LocalModelStore.Shared;
        private readonly StackPanel fileRow = new() { Orientation = Orientation.Horizontal };
        private readonly ProgressBar progress = new()
        {
            Width = 120,
            Height = 8,
            Maximum = LocalModelStore.Total,
            VerticalAlignment = VerticalAlignment.Center,
        };
        private readonly TextBlock progressText = Note("");
        /// <summary>正在显示的是不是下载进度：下载中每 2 MB 刷新一次，只改进度条和数字，不重建这一行。</summary>
        private bool showingProgress;

        public LocalModelCard(ModelServicesWindow owner) : base(owner)
        {
            Header.Text = Strings.Local_Title;
            Panel.Children.Add(Intro(Strings.Local_Intro));
            var grid = SettingsWindow.NewGrid();
            SettingsWindow.AddRow(grid, Strings.Label_ModelFile, fileRow, null);
            Panel.Children.Add(grid);
            FinishLayout();
            Refresh();
        }

        protected override bool CanTest => Store.Installed;

        /// <summary>下载好了：去掉“下载后就能使用”（正在运行的字幕换成混元翻译由 SubtitleController 处理）。</summary>
        public void OnInstalledChanged()
        {
            if (NoticeText == Strings.Notice_DownloadToUse) ShowNotice("", isError: false);
        }

        /// <summary>“模型文件”一行：没下载时是大小和“下载”；下载中是进度和“取消”；校验中；失败时是原因和“重试”；已下载时是“删除”。</summary>
        public void Refresh()
        {
            bool downloading = Store.State == LocalModelStore.Phase.Downloading;
            if (downloading)
            {
                progress.Value = Store.Received;
                progressText.Text = $"{TextUtil.FileSize(Store.Received)} / {TextUtil.FileSize(LocalModelStore.Total)}";
                if (showingProgress) return;
            }
            showingProgress = downloading;
            fileRow.Children.Clear();
            switch (Store.State)
            {
                case LocalModelStore.Phase.Downloading:
                    fileRow.Children.Add(progress);
                    fileRow.Children.Add(progressText);
                    fileRow.Children.Add(RowButton(Strings.Button_Cancel, Store.Cancel));
                    break;
                case LocalModelStore.Phase.Verifying:
                    fileRow.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 48, Height = 6, VerticalAlignment = VerticalAlignment.Center });
                    fileRow.Children.Add(Note(Strings.Local_Verifying));
                    break;
                case LocalModelStore.Phase.Failed:
                    var error = Note(Store.Error ?? Strings.Local_DownloadFailed);
                    error.Foreground = ErrorBrush;
                    error.Opacity = 1;
                    error.MaxWidth = 300;
                    error.TextWrapping = TextWrapping.Wrap;
                    error.Margin = new Thickness(0, 0, 8, 0);
                    fileRow.Children.Add(error);
                    fileRow.Children.Add(RowButton(Strings.Button_Retry, Download));
                    break;
                default:
                    if (Store.Installed)
                    {
                        fileRow.Children.Add(Note(Strings.Model_Downloaded));
                        var delete = RowButton(Strings.Button_Delete, Delete);
                        delete.Foreground = DeleteBrush;
                        fileRow.Children.Add(delete);
                    }
                    else
                    {
                        fileRow.Children.Add(Note(Strings.Format(Strings.Local_SizeNote, TextUtil.FileSize(LocalModelStore.Total))));
                        fileRow.Children.Add(RowButton(Strings.Button_Download, Download));
                    }
                    break;
            }
            UpdateButtons();
        }

        private static TextBlock Note(string text) => new()
        {
            Text = text,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        };

        private static Button RowButton(string text, Action action)
        {
            var button = SettingsWindow.SmallButton(text, action);
            button.VerticalAlignment = VerticalAlignment.Center;
            return button;
        }

        /// <summary>和 Whisper 模型一样，填了“模型下载地址”时先从那里下。</summary>
        private void Download() => Store.Download(Owner.S.ModelMirror);

        /// <summary>删掉整个文件夹；正在用混元翻译时换回 Google 翻译。</summary>
        private void Delete()
        {
            Store.Delete();
            ShowNotice("", isError: false);
            if (Owner.S.TranslationModel.Kind != ModelKind.LocalModel) return;
            Owner.S.TranslationModel = ModelChoice.Google;
            Owner.settings.Commit(SettingsChange.Translation);
        }

        /// <summary>用户点“测试”：上次启动失败的话马上重试，不等一分钟（与 macOS 相同）。</summary>
        protected override Task Test()
        {
            LocalModelServer.Shared.ClearFailure();
            return RunTest(Strings.Engine_LocalModel, () => new LocalTranslator(Owner.S.Source, Owner.S.Target, () => Owner.S.UseGpu));
        }
    }

    // MARK: - 一个服务

    /// <summary>
    /// 一个模型服务：名称、接口格式、接口地址、API 密钥、可用模型（获取列表），翻译用、AI 助手用，以及测试、删除。
    /// </summary>
    private sealed class EndpointCard : Card
    {
        private readonly PasswordBox key = new();
        private readonly TextBlock keyHint = SettingsWindow.HintText();
        private readonly TextBox models = new();
        private readonly Button fetch;
        private readonly ComboBox translationPicker;
        private readonly ComboBox assistantPicker;
        private readonly SettingsWindow.RowParts translationRow;
        private readonly SettingsWindow.RowParts assistantRow;
        /// <summary>程序在改模型输入框的文字或选择框（获取列表之后），不当成用户输入。</summary>
        private bool settingModels;

        public ModelEndpoint Endpoint { get; }

        public EndpointCard(ModelServicesWindow owner, ModelEndpoint endpoint) : base(owner)
        {
            Endpoint = endpoint;
            var grid = SettingsWindow.NewGrid();
            var name = new TextBox { Text = endpoint.Name };
            name.TextChanged += (_, _) =>
            {
                endpoint.Name = name.Text.Trim();
                UpdateHeader();
                owner.Changed(endpoint);
            };
            SettingsWindow.AddRow(grid, Strings.Label_ServiceName, name, null);

            var format = new ComboBox();
            format.Items.Add(Strings.Format_OpenAI);
            format.Items.Add(Strings.Format_Anthropic);
            format.SelectedIndex = endpoint.Format == ChatApiFormat.Anthropic ? 1 : 0;
            format.SelectionChanged += (_, _) =>
            {
                endpoint.Format = format.SelectedIndex == 1 ? ChatApiFormat.Anthropic : ChatApiFormat.OpenAI;
                owner.Changed(endpoint);
            };
            SettingsWindow.AddRow(grid, Strings.Label_ApiFormat, format, null);

            var url = new TextBox { Text = endpoint.BaseUrl };
            url.TextChanged += (_, _) =>
            {
                endpoint.BaseUrl = url.Text.Trim();
                UpdateHeader();
                owner.Changed(endpoint);
            };
            SettingsWindow.AddRow(grid, Strings.Label_BaseUrl, url, null);

            key.Password = owner.secrets.Get(SecretStore.EndpointKey(endpoint.Id)) ?? "";
            key.PasswordChanged += (_, _) =>
            {
                // 没写进文件（密钥文件读不出来、写入失败）：本次运行照样能用，但要告诉用户退出后会丢。
                if (!owner.secrets.Set(SecretStore.EndpointKey(endpoint.Id), key.Password)) ShowNotice(Strings.Error_KeyNotSaved, isError: true);
                else if (NoticeText == Strings.Error_KeyNotSaved || NoticeText == Strings.Notice_EnterKey) ShowNotice("", isError: false);
                owner.Changed(endpoint);
            };
            SettingsWindow.AddRow(grid, Strings.Label_ApiKey, SettingsWindow.WithPlaceholder(key, keyHint), null);

            // 输入框保留用户正在输入的逗号；模型列表按输入框的文字解析。
            models.Text = ModelCatalog.JoinModels(endpoint.Models);
            models.TextChanged += (_, _) =>
            {
                if (settingModels) return;
                endpoint.Models = ModelCatalog.ParseModels(models.Text);
                RefreshPickers();
                UpdateButtons();
                owner.Changed(endpoint);
            };
            fetch = SettingsWindow.SmallButton(Strings.Button_FetchList, async () => await FetchModels());
            SettingsWindow.AddRow(grid, Strings.Label_AvailableModels,
                SettingsWindow.WithButtons(SettingsWindow.WithPlaceholder(models, Strings.Hint_Models), fetch), null);

            // 模型列表里这家只列一项：翻译、AI 助手各用这家的哪个模型在这里选（与 macOS 相同）。
            translationPicker = ModelPicker(model => SetModel(model, ModelPurpose.Translation));
            assistantPicker = ModelPicker(model => SetModel(model, ModelPurpose.Assistant));
            translationRow = SettingsWindow.AddRow(grid, Strings.Label_ForTranslation, translationPicker, null);
            assistantRow = SettingsWindow.AddRow(grid, Strings.Label_ForAssistant, assistantPicker, null);
            Panel.Children.Add(grid);

            var delete = SettingsWindow.SmallButton(Strings.Button_Delete, () => owner.Remove(this));
            delete.Foreground = DeleteBrush;
            DockPanel.SetDock(delete, Dock.Right);
            Actions.Children.Add(delete);
            FinishLayout();
            UpdateHeader();
            RefreshPickers();
        }

        protected override bool CanTest => Endpoint.Models.Count > 0;

        protected override void UpdateButtons()
        {
            base.UpdateButtons();
            fetch.IsEnabled = !Busy;
        }

        /// <summary>标题是服务名（本地服务加“· 本地”）；密钥提示随地址变化：本地服务无需填写，云服务必填。</summary>
        private void UpdateHeader()
        {
            bool local = Endpoint.IsLocal;
            Header.Text = local ? Strings.Format(Strings.Service_Local, Endpoint.Name) : Endpoint.Name;
            keyHint.Text = local ? Strings.Hint_KeyLocal : Strings.Hint_KeyRequired;
        }

        /// <summary>“翻译用”“AI 助手用”的选项跟着可用模型变；没有模型时这两行不显示。</summary>
        private void RefreshPickers()
        {
            settingModels = true;
            try
            {
                Fill(translationPicker, ModelPurpose.Translation);
                Fill(assistantPicker, ModelPurpose.Assistant);
            }
            finally
            {
                settingModels = false;
            }
            bool any = Endpoint.Models.Count > 0;
            translationRow.SetVisible(any);
            assistantRow.SetVisible(any);
        }

        private void Fill(ComboBox box, ModelPurpose purpose)
        {
            box.Items.Clear();
            var selected = Endpoint.ModelFor(purpose);
            foreach (var model in Endpoint.Models)
            {
                // 用 TextBlock 原样显示：模型名里的下划线会被当成快捷键标记。
                var item = new ComboBoxItem { Content = new TextBlock { Text = model }, Tag = model };
                box.Items.Add(item);
                if (model == selected) box.SelectedItem = item;
            }
        }

        private void SetModel(string model, ModelPurpose purpose)
        {
            if (settingModels || Endpoint.ModelFor(purpose) == model) return;
            Endpoint.SetModel(model, purpose);
            // 正在用这家的翻译、AI 助手跟着换（保存时同步，见 AppSettings.SyncModels）。
            Owner.Changed(Endpoint);
        }

        /// <summary>“获取列表”：已经填写、服务里也有的模型排在前面，其余按名称排序，方便挑选后删减。</summary>
        private async Task FetchModels()
        {
            SetBusy(true);
            try
            {
                var list = await TranslationEngines.ListModelsAsync(Endpoint, Owner.secrets, Owner.lifetime.Token);
                if (!Owner.S.Endpoints.Contains(Endpoint)) return; // 等待期间被删除了
                if (list.Count == 0)
                {
                    ShowNotice(Strings.Status_NoModelList, isError: true);
                    return;
                }
                var merged = ModelCatalog.MergeFetched(Endpoint.Models, list);
                Endpoint.Models = merged;
                settingModels = true;
                try
                {
                    models.Text = ModelCatalog.JoinModels(merged);
                }
                finally
                {
                    settingModels = false;
                }
                RefreshPickers();
                Owner.Changed(Endpoint);
                ShowNotice(Strings.Format(Strings.Status_FetchedModels, list.Count), isError: false);
            }
            catch (OperationCanceledException) when (Owner.lifetime.IsCancellationRequested)
            {
                // 窗口已关闭。
            }
            catch (Exception e)
            {
                ShowNotice(TranslationEngines.Describe(e), isError: true);
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>“测试”：用“翻译用”的模型翻译一句话（最多 30 秒）。</summary>
        protected override Task Test() =>
            Endpoint.ModelFor(ModelPurpose.Translation) is { } model
                ? RunTest(model, () => Session(ModelChoice.Endpoint(Endpoint.Id, model), TimeSpan.FromSeconds(30)))
                : Task.CompletedTask;
    }
}
