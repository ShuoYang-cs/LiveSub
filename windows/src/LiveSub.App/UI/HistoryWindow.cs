using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LiveSub.App.Localization;
using LiveSub.App.Services;
using LiveSub.Core;
using LiveSub.Core.Subtitles;

namespace LiveSub.App.UI;

/// <summary>
/// “字幕记录”窗口（与 macOS 版 HistoryView 相同）：本次记录的全部字幕，每句显示时间（和标签、附注）、原文、
/// 纠正前的识别原文、译文，文字可以选中复制。下方是状态、“在资源管理器中显示记录”和“生成笔记”。
/// 停在底部时跟着新字幕滚动，往上翻看时不打扰。搜索留到以后的版本。
/// </summary>
public sealed class HistoryWindow : Window
{
    private static readonly SolidColorBrush NoteOrange = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x00)));
    private static readonly SolidColorBrush Hairline = Freeze(new SolidColorBrush(Color.FromArgb(0x1F, 0, 0, 0)));

    private readonly SubtitleController controller;
    private readonly SettingsService settingsService;
    private readonly ListBox list = new()
    {
        BorderThickness = new Thickness(0),
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    private readonly ObservableCollection<Row> items = [];
    private readonly TextBlock empty = new() { Text = Strings.History_Empty, Margin = new Thickness(16), Opacity = 0.6 };
    private readonly TextBlock status = new()
    {
        FontSize = 11,
        Foreground = SystemColors.GrayTextBrush,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Button reveal = BarButton(Strings.History_ShowInExplorer);
    private readonly Button notes = BarButton(Strings.Menu_GenerateNotes);
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(0.5) };
    private ScrollViewer? scroller;
    /// <summary>停在最新一句：有新字幕时跟着滚到底；往上翻看时为 false，不打扰。</summary>
    private bool following = true;

    public HistoryWindow(SubtitleController controller, SettingsService settingsService)
    {
        this.controller = controller;
        this.settingsService = settingsService;
        Title = $"{Strings.AppName} · {Strings.History_Title}";
        Width = 640;
        Height = 560;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // 行只放文字，不要列表的选中底色；列表很长时只创建看得见的行，按像素滚动。
        var row = new FrameworkElementFactory(typeof(RowView));
        list.ItemTemplate = new DataTemplate { VisualTree = row };
        var container = new Style(typeof(ListBoxItem));
        container.Setters.Add(new Setter(TemplateProperty, new ControlTemplate(typeof(ListBoxItem))
        {
            VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)),
        }));
        container.Setters.Add(new Setter(FocusableProperty, false));
        list.ItemContainerStyle = container;
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        list.ItemsSource = items;
        // 文字是只读文本框，它们会吞掉滚轮：转交给列表自己的滚动区，滚轮总是滚动整个列表。
        list.PreviewMouseWheel += (_, e) =>
        {
            if (Scroller() is not { } sv || e.Handled) return;
            e.Handled = true;
            sv.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sv });
        };
        list.Loaded += (_, _) => Scroller();

        reveal.Click += (_, _) =>
        {
            if (controller.TranscriptPath is { } p) AppShell.RevealInExplorer(p);
        };
        notes.Click += (_, _) =>
        {
            controller.GenerateNotes();
            RefreshBar();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        buttons.Children.Add(reveal);
        buttons.Children.Add(notes);
        var bar = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(status);
        var barBorder = new Border
        {
            Child = bar,
            Padding = new Thickness(10),
            BorderBrush = Hairline,
            BorderThickness = new Thickness(0, 1, 0, 0),
        };

        var dock = new DockPanel();
        DockPanel.SetDock(barBorder, Dock.Bottom);
        dock.Children.Add(barBorder);
        var body = new Grid();
        body.Children.Add(list);
        body.Children.Add(empty);
        dock.Children.Add(body);
        Content = dock;

        refreshTimer.Tick += (_, _) => Refresh();
        refreshTimer.Start();
        Closed += (_, _) => refreshTimer.Stop();
        Refresh();
    }

    private static Button BarButton(string text) => new()
    {
        Content = new TextBlock { Text = text },
        Padding = new Thickness(12, 4, 12, 4),
        Margin = new Thickness(8, 0, 0, 0),
    };

    private void Refresh()
    {
        var lines = controller.Book.Lines;
        var sourceFont = TextFonts.For(settingsService.Current.Source);
        var targetFont = TextFonts.For(settingsService.Current.Target);
        // 逐项更新而不是整体替换（翻译完成、纠正原文时只换那一行），往上翻看时滚动位置不会被重置。
        bool changed = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var fresh = MakeRow(lines[i], sourceFont, targetFont);
            if (i >= items.Count)
            {
                items.Add(fresh);
                changed = true;
            }
            else if (items[i] != fresh)
            {
                items[i] = fresh;
                changed = true;
            }
        }
        while (items.Count > lines.Count)
        {
            items.RemoveAt(items.Count - 1);
            changed = true;
        }
        empty.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        list.Visibility = lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        // 每次都找一下滚动区：列表刚从收起变为显示时，要等排版后才找得到。
        var sv = Scroller();
        if (changed && following) sv?.ScrollToEnd();
        RefreshBar();
    }

    private void RefreshBar()
    {
        status.Text = controller.Status;
        status.ToolTip = controller.Status;
        reveal.Visibility = controller.TranscriptPath is { } p && File.Exists(p) ? Visibility.Visible : Visibility.Collapsed;
        bool generating = controller.IsGeneratingNotes;
        ((TextBlock)notes.Content).Text = generating ? Strings.History_Generating : Strings.Menu_GenerateNotes;
        notes.IsEnabled = !generating && controller.Book.Lines.Count > 0;
    }

    /// <summary>
    /// 列表自己的滚动区。列表还没显示过（没有字幕时是收起的）时模板还没生成，找不到，下次再找。
    /// 第一次找到时滚到底，显示最新的字幕。
    /// </summary>
    private ScrollViewer? Scroller()
    {
        if (scroller is not null) return scroller;
        scroller = Find<ScrollViewer>(list);
        if (scroller is null) return null;
        scroller.ScrollChanged += OnScrollChanged;
        scroller.ScrollToEnd();
        return scroller;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (scroller is null) return;
        bool atEnd = scroller.VerticalOffset >= scroller.ScrollableHeight - 8;
        // 往上滚就不再跟随，回到底部时重新跟随。只按“往上滚”判断：只创建看得见的行时，行高边滚边修正，
        // 程序滚到底的那一下可能还差一点，不能因此停止跟随。
        if (atEnd) following = true;
        else if (e.VerticalChange < 0) following = false;
        // 新字幕、行高修正、改窗口大小以后，原来停在底部的继续贴底。
        if (following && !atEnd) scroller.ScrollToEnd();
    }

    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (Find<T>(child) is { } found) return found;
        }
        return null;
    }

    private static Row MakeRow(SubtitleLine line, FontFamily sourceFont, FontFamily targetFont)
    {
        bool pending = line.State is LineState.Waiting or LineState.Translating;
        return new Row(
            line.Id,
            TranscriptFormat.TimeStamp(line.Time),
            line.SourceLabel,
            line.Note,
            line.Source,
            line.OriginalSource is { } o && o != line.Source ? Strings.Format(Strings.History_Original, o) : null,
            // 还没翻译出字时显示“…”；翻译失败的句子不加别的（与 macOS 相同）。
            line.Translation.Length > 0 ? line.Translation : pending ? "…" : null,
            pending && line.Translation.Length == 0,
            sourceFont,
            targetFont);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>一句字幕在列表里显示的内容。内容不变时不重建这一行。</summary>
    private sealed record Row(
        Guid Id,
        string Time,
        string? Label,
        string? Note,
        string Source,
        string? Original,
        string? Translation,
        bool Waiting,
        FontFamily SourceFont,
        FontFamily TargetFont);

    /// <summary>列表里的一行。列表回收行时换 DataContext，这里跟着更新。</summary>
    private sealed class RowView : Border
    {
        private readonly TextBlock time = Caption(SystemColors.GrayTextBrush);
        private readonly TextBlock label = Caption(SystemColors.GrayTextBrush);
        private readonly TextBlock note = Caption(NoteOrange);
        private readonly TextBox source = Selectable(13);
        private readonly TextBox original = Selectable(11);
        private readonly TextBox translation = Selectable(14);

        public RowView()
        {
            Padding = new Thickness(12, 6, 12, 7);
            BorderBrush = Hairline;
            BorderThickness = new Thickness(0, 0, 0, 1);
            Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
            original.Foreground = SystemColors.GrayTextBrush;
            // 文本框的文字左边有约 2 像素的内边距，标题行对齐它。
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 2) };
            header.Children.Add(time);
            header.Children.Add(label);
            header.Children.Add(note);
            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(source);
            stack.Children.Add(original);
            stack.Children.Add(translation);
            Child = stack;
            DataContextChanged += (_, _) => Update(DataContext as Row);
        }

        private static TextBlock Caption(Brush brush) => new() { FontSize = 11, Foreground = brush, Margin = new Thickness(0, 0, 6, 0) };

        private static TextBox Selectable(double size) => new()
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
        };

        private void Update(Row? row)
        {
            if (row is null) return;
            time.Text = row.Time;
            Show(label, row.Label);
            Show(note, row.Note);
            source.Text = row.Source;
            source.FontFamily = row.SourceFont;
            Show(original, row.Original);
            original.FontFamily = row.SourceFont;
            Show(translation, row.Translation);
            translation.FontFamily = row.TargetFont;
            translation.FontWeight = row.Waiting ? FontWeights.Normal : FontWeights.Medium;
            translation.Foreground = row.Waiting ? SystemColors.GrayTextBrush : SystemColors.ControlTextBrush;
        }

        private static void Show(TextBlock block, string? text)
        {
            block.Text = text ?? "";
            block.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        }

        private static void Show(TextBox box, string? text)
        {
            box.Text = text ?? "";
            box.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
