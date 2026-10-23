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

namespace LiveSub.App.UI;

/// <summary>
/// “模型服务”：添加、编辑自己的 API（OpenAI 兼容或 Anthropic），或 Ollama、LM Studio 等本地服务（与 macOS 的 ModelServicesView 相同）。
/// 改了自动保存；API 密钥用 Windows 数据保护加密后保存在本机（<see cref="SecretStore"/>）。
/// 只有正在被翻译模型（或 AI 助手）使用的服务改动时才换翻译会话。
/// </summary>
public sealed class ModelServicesWindow : Window
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    /// <summary>出错提示用橙色（在浅色背景上要看得清）。</summary>
    private static readonly SolidColorBrush ErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC2, 0x5B, 0x00)));
    private static readonly SolidColorBrush DeleteBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)));
    private static readonly SolidColorBrush CardBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)));
    /// <summary>“测试”翻译的句子（与 macOS 相同）。</summary>
    private const string TestSentence = "Welcome, everyone. Let's get started.";

    private readonly SettingsService settings;
    private readonly SecretStore secrets;
    private AppSettings S => settings.Current;
    private readonly StackPanel cards = new();
    /// <summary>窗口关闭时取消还在进行的获取列表和测试。</summary>
    private readonly CancellationTokenSource lifetime = new();

    public ModelServicesWindow(SettingsService settings, SecretStore secrets)
    {
        this.settings = settings;
        this.secrets = secrets;
        Title = $"{Strings.AppName} · {Strings.ModelServices_Title}";
        Width = 600;
        Height = 680;
        MinWidth = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var body = new StackPanel { Margin = new Thickness(20, 16, 20, 20) };
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
        body.Children.Add(cards);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        foreach (var endpoint in S.Endpoints) cards.Children.Add(new EndpointCard(this, endpoint));
        Closed += (_, _) => lifetime.Cancel();
    }

    // MARK: - 添加、删除

    /// <summary>“添加模型服务”：常用服务的模板，最后是“自定义”。</summary>
    private Button BuildAddButton()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = Strings.Button_AddService });
        content.Children.Add(new TextBlock
        {
            Text = "", // ChevronDown
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

    /// <summary>删除服务和它的密钥；正在用它的翻译模型换回 Claude Haiku（AI 助手换回 Claude Sonnet）。</summary>
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

    /// <summary>保存一个服务的改动。翻译模型正在用它时才需要换翻译会话（例如改了密钥）。</summary>
    private void Changed(ModelEndpoint endpoint) =>
        settings.Commit(UsedByModels(endpoint) ? SettingsChange.Translation : SettingsChange.None);

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    // MARK: - 一个服务

    /// <summary>一个模型服务：名称、接口格式、接口地址、API 密钥、模型，以及获取列表、测试、删除。</summary>
    private sealed class EndpointCard : Border
    {
        private readonly ModelServicesWindow owner;
        private readonly TextBlock header = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        private readonly PasswordBox key = new();
        private readonly TextBlock keyHint = SettingsWindow.HintText();
        private readonly TextBox models = new();
        private readonly Button fetch;
        private readonly Button test;
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
        private bool busy;
        /// <summary>程序在改模型输入框的文字（获取列表之后），不当成用户输入。</summary>
        private bool settingModels;

        public ModelEndpoint Endpoint { get; }

        public EndpointCard(ModelServicesWindow owner, ModelEndpoint endpoint)
        {
            this.owner = owner;
            Endpoint = endpoint;
            Margin = new Thickness(0, 14, 0, 0);
            Padding = new Thickness(14, 10, 14, 10);
            BorderThickness = new Thickness(1);
            BorderBrush = CardBorder;
            CornerRadius = new CornerRadius(6);

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
                else if (notice.Text == Strings.Error_KeyNotSaved) ShowNotice("", isError: false);
                owner.Changed(endpoint);
            };
            SettingsWindow.AddRow(grid, Strings.Label_ApiKey, SettingsWindow.WithPlaceholder(key, keyHint), null);

            // 输入框保留用户正在输入的逗号；模型列表按输入框的文字解析。
            models.Text = ModelCatalog.JoinModels(endpoint.Models);
            models.TextChanged += (_, _) =>
            {
                if (settingModels) return;
                endpoint.Models = ModelCatalog.ParseModels(models.Text);
                UpdateButtons();
                owner.Changed(endpoint);
            };
            fetch = SettingsWindow.SmallButton(Strings.Button_FetchList, async () => await FetchModels());
            SettingsWindow.AddRow(grid, Strings.Label_Model,
                SettingsWindow.WithButtons(SettingsWindow.WithPlaceholder(models, Strings.Hint_Models), fetch), null);

            test = SettingsWindow.SmallButton(Strings.Button_Test, async () => await Test());
            var delete = SettingsWindow.SmallButton(Strings.Button_Delete, () => owner.Remove(this));
            delete.Foreground = DeleteBrush;
            var actions = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(test, Dock.Left);
            DockPanel.SetDock(spinner, Dock.Left);
            DockPanel.SetDock(delete, Dock.Right);
            actions.Children.Add(test);
            actions.Children.Add(spinner);
            actions.Children.Add(delete);
            actions.Children.Add(notice);

            var panel = new StackPanel();
            panel.Children.Add(header);
            panel.Children.Add(grid);
            panel.Children.Add(actions);
            Child = panel;
            UpdateHeader();
            UpdateButtons();
        }

        /// <summary>标题是服务名（本地服务加“· 本地”）；密钥提示随地址变化：本地服务无需填写，云服务必填。</summary>
        private void UpdateHeader()
        {
            bool local = Endpoint.IsLocal;
            header.Text = local ? Strings.Format(Strings.Service_Local, Endpoint.Name) : Endpoint.Name;
            keyHint.Text = local ? Strings.Hint_KeyLocal : Strings.Hint_KeyRequired;
        }

        private void UpdateButtons()
        {
            fetch.IsEnabled = !busy;
            test.IsEnabled = !busy && Endpoint.Models.Count > 0;
            spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetBusy(bool value)
        {
            busy = value;
            UpdateButtons();
        }

        private void ShowNotice(string text, bool isError)
        {
            notice.Text = text;
            notice.ToolTip = text;
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

        /// <summary>“获取列表”：已经填写、服务里也有的模型排在前面，其余按名称排序，方便挑选后删减。</summary>
        private async Task FetchModels()
        {
            SetBusy(true);
            try
            {
                var list = await TranslationEngines.ListModelsAsync(Endpoint, owner.secrets, owner.lifetime.Token);
                if (!owner.S.Endpoints.Contains(Endpoint)) return; // 等待期间被删除了
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
                owner.Changed(Endpoint);
                ShowNotice(Strings.Format(Strings.Status_FetchedModels, list.Count), isError: false);
            }
            catch (OperationCanceledException) when (owner.lifetime.IsCancellationRequested)
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

        /// <summary>“测试”：用第一个模型翻译一句话（最多 30 秒），确认地址、密钥和模型都可用，并显示用时。</summary>
        private async Task Test()
        {
            if (Endpoint.Models.FirstOrDefault() is not { } model) return;
            SetBusy(true);
            try
            {
                var s = owner.S;
                using var session = TranslationEngines.Create(ModelChoice.Endpoint(Endpoint.Id, model), s, owner.secrets,
                    Prompts.TranslatorSystemPrompt(s.Source, s.Target), timeout: TimeSpan.FromSeconds(30));
                var watch = Stopwatch.StartNew();
                var raw = await session.RequestAsync(Prompts.TranslationMessage(TestSentence, []), TestSentence, null, owner.lifetime.Token);
                var seconds = watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
                var translation = TranslationOutput.Split(raw, TestSentence, s.Target.IsCJK).Translation;
                ShowNotice(Strings.Format(Strings.Status_TestOk, model, seconds, translation), isError: false);
            }
            catch (OperationCanceledException) when (owner.lifetime.IsCancellationRequested)
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
    }
}
