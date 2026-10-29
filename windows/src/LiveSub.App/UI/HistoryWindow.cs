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
/// 纠正前的识别原文、译文，文字可以选中复制。下方是状态、“在资源管理器中显示记录”、“导出字幕”、“开始新的记录”和“生成笔记”
/// （快捷按钮里去掉“⋯”菜单后，记录相关的操作都在这里，与 macOS 相同）。
/// 停在底部时跟着新字幕滚动，往上翻看时不打扰。搜索留到以后的版本。外观跟着设置变；用 LiveSub 时显示在字幕上面。
/// 左上角的菜单可以换成记录文件夹里以前的某一份（只读，可以生成笔记）：以前只显示本次的，重新打开程序后窗口是空的，
/// 文件夹里明明有记录（2026-10-28 macOS 上用户提出）。打开时本次还没有字幕，先显示最近的一份，本次有了字幕再换回来。
/// </summary>
public sealed class HistoryWindow : Window
{
    private static readonly SolidColorBrush NoteOrange = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x00)));
    /// <summary>分隔线：灰色半透明，浅色、深色外观下都看得出来。</summary>
    private static readonly SolidColorBrush Hairline = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)));
    /// <summary>次要文字（时间、标签、识别原文）用半透明的前景色，跟着外观变。</summary>
    private const double Secondary = 0.6;

    private readonly SubtitleController controller;
    private readonly SettingsService settingsService;
    private readonly AppShell shell;
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
        Opacity = Secondary,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Button reveal = BarButton(Strings.History_ShowInExplorer);
    private readonly Button export = BarButton(Strings.Menu_Export + " \u25BE");
    private readonly Button newSession = BarButton(Strings.Menu_NewSession);
    private readonly Button notes = BarButton(Strings.Menu_GenerateNotes);
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(0.5) };
    private ScrollViewer? scroller;
    /// <summary>停在最新一句：有新字幕时跟着滚到底；往上翻看时为 false，不打扰。</summary>
    private bool following = true;
    private readonly ComboBox records = new() { MinWidth = 180, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Border recordsBar;
    /// <summary>记录文件夹里以前的记录（不含本次的），最新的在前面。</summary>
    private List<string> files = [];
    /// <summary>正在看的以前那份记录（null 是本次）和它的字幕。</summary>
    private string? pastFile;
    private List<TranscriptFormat.ParsedEntry> pastEntries = [];
    /// <summary>打开时本次还没有字幕、自动换成了最近的一份：本次有了字幕就换回来。</summary>
    private bool autoSelected;
    /// <summary>程序在改菜单的内容或选中项，不当成用户选的。</summary>
    private bool updatingRecords;
    private int lastLineCount;

    public HistoryWindow(SubtitleController controller, SettingsService settingsService, AppShell shell)
    {
        this.controller = controller;
        this.settingsService = settingsService;
        this.shell = shell;
        Title = $"{Strings.AppName} · {Strings.History_Title}";
        Width = 720;
        Height = 560;
        MinWidth = 600;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Appearance.Theme(this);
        // 内容窗口：保留最小化和最大化（与 macOS 相同）。
        WindowLevels.Raise(this, closeButtonOnly: false);
        // 小屏、高缩放时不超出屏幕（标题栏不能在屏幕外）。
        ScreenFit.Apply(this);

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

        // 有本次的记录文件就在资源管理器里选中它，否则打开记录文件夹；看以前的记录时选中那一份。
        reveal.Click += (_, _) =>
        {
            if (pastFile is not null) AppShell.RevealInExplorer(pastFile);
            else shell.OpenTranscriptFolder();
        };
        export.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = export, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
            menu.Items.Add(PopupMenus.Item("SRT…", () => shell.Export(SubtitleFileFormat.Srt, this)));
            menu.Items.Add(PopupMenus.Item("WebVTT…", () => shell.Export(SubtitleFileFormat.Vtt, this)));
            menu.IsOpen = true;
        };
        newSession.Click += (_, _) =>
        {
            shell.NewSession();
            RefreshBar();
        };
        notes.Click += (_, _) =>
        {
            if (pastFile is not null)
            {
                var day = DateTime.Today;
                controller.GenerateNotes(pastEntries.Select(e => (day.AddSeconds(e.Seconds), e.Source, e.Translation)).ToList(), pastFile);
            }
            else
            {
                controller.GenerateNotes();
            }
            RefreshBar();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        buttons.Children.Add(reveal);
        buttons.Children.Add(export);
        buttons.Children.Add(newSession);
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

        var recordsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        recordsPanel.Children.Add(new TextBlock { Text = Strings.History_Records, VerticalAlignment = VerticalAlignment.Center });
        recordsPanel.Children.Add(records);
        records.SelectionChanged += (_, _) =>
        {
            if (!updatingRecords) SelectRecord((records.SelectedItem as ComboBoxItem)?.Tag as string, auto: false);
        };
        recordsBar = new Border
        {
            Child = recordsPanel,
            Padding = new Thickness(10, 6, 10, 6),
            BorderBrush = Hairline,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Visibility = Visibility.Collapsed,
        };

        var dock = new DockPanel();
        DockPanel.SetDock(barBorder, Dock.Bottom);
        dock.Children.Add(barBorder);
        DockPanel.SetDock(recordsBar, Dock.Top);
        dock.Children.Add(recordsBar);
        var body = new Grid();
        body.Children.Add(list);
        body.Children.Add(empty);
        dock.Children.Add(body);
        Content = dock;

        refreshTimer.Tick += (_, _) => Refresh();
        refreshTimer.Start();
        Closed += (_, _) => refreshTimer.Stop();
        // 窗口回到前面时可能已经多了一份记录（开始了新的记录、重新打开过程序）。
        Activated += (_, _) => ReloadRecords();
        Refresh();
        ReloadRecords();
    }

    /// <summary>在后台读记录文件夹（同录音记录窗口）。</summary>
    private async void ReloadRecords()
    {
        var folder = Environment.ExpandEnvironmentVariables(settingsService.Current.TranscriptFolder);
        var current = controller.TranscriptPath;
        var found = await Task.Run(() => ScanRecords(folder));
        files = found.Where(f => current is null || !string.Equals(Path.GetFullPath(f), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)).ToList();
        if (pastFile is not null && !files.Contains(pastFile)) SelectRecord(null, auto: false);
        else if (pastFile is null && controller.Book.Lines.Count == 0 && files.Count > 0) SelectRecord(files[0], auto: true);
        UpdateRecordsMenu();
    }

    /// <summary>只列有字幕的记录（开始后没说话就停了的只有标题）；英文界面下写的文件名是“Transcript”。按文件名倒排就是最新的在前面。</summary>
    private static List<string> ScanRecords(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.md")
                .Where(f => Path.GetFileName(f) is var name && (name.Contains("字幕记录") || name.Contains("Transcript")) && HasLines(f))
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool HasLines(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[4096];
            int n = stream.Read(buffer, 0, buffer.Length);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, n).Contains("\n**[");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>菜单里的名字：文件名去掉“字幕记录”，剩下开始时间。</summary>
    private static string RecordTitle(string path) =>
        Path.GetFileNameWithoutExtension(path).Replace("字幕记录", "").Replace("Transcript", "").Trim();

    private void UpdateRecordsMenu()
    {
        updatingRecords = true;
        records.Items.Clear();
        records.Items.Add(new ComboBoxItem { Content = Strings.History_Current });
        foreach (var f in files) records.Items.Add(new ComboBoxItem { Content = RecordTitle(f), Tag = f });
        records.SelectedIndex = pastFile is null ? 0 : files.IndexOf(pastFile) + 1;
        updatingRecords = false;
        recordsBar.Visibility = files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private async void SelectRecord(string? file, bool auto)
    {
        autoSelected = auto && file is not null;
        if (file == pastFile) return;
        pastFile = file;
        pastEntries = [];
        items.Clear();
        if (!updatingRecords && records.Items.Count > 0)
        {
            updatingRecords = true;
            records.SelectedIndex = file is null ? 0 : files.IndexOf(file) + 1;
            updatingRecords = false;
        }
        if (file is null)
        {
            // 回到本次：从头重新填，停在最新一句。
            following = true;
            Refresh();
            return;
        }
        var entries = await Task.Run(() =>
        {
            try { return TranscriptFormat.Parse(File.ReadAllText(file)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        });
        if (pastFile != file) return;
        pastEntries = entries;
        var sourceFont = TextFonts.For(settingsService.Current.Source);
        var targetFont = TextFonts.For(settingsService.Current.Target);
        foreach (var e in entries)
        {
            var time = TimeSpan.FromSeconds(e.Seconds);
            items.Add(new Row(Guid.NewGuid(), $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}", e.Label, null, e.Source,
                e.Original is { } o && o != e.Source ? Strings.Format(Strings.History_Original, o) : null,
                e.Translation.Length > 0 ? e.Translation : null, false, sourceFont, targetFont));
        }
        // 以前的记录从头看。
        following = false;
        Scroller()?.ScrollToTop();
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
        // 本次有了第一句字幕：自动显示的以前那份换回本次；开始了新的记录（本次清空）：刚才那份成了以前的记录。
        if (autoSelected && lines.Count > 0) SelectRecord(null, auto: false);
        if (lines.Count == 0 && lastLineCount > 0) ReloadRecords();
        lastLineCount = lines.Count;
        if (pastFile is not null)
        {
            empty.Visibility = Visibility.Collapsed;
            list.Visibility = Visibility.Visible;
            RefreshBar();
            return;
        }
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
        empty.Text = files.Count > 0 ? Strings.History_EmptyWithPast : Strings.History_Empty;
        empty.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        list.Visibility = lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        // 每次都找一下滚动区：列表刚从收起变为显示时，要等排版后才找得到。
        var sv = Scroller();
        if (changed && following) sv?.ScrollToEnd();
        RefreshBar();
    }

    private void RefreshBar()
    {
        bool generating = controller.IsGeneratingNotes;
        bool past = pastFile is not null;
        // 以前的记录没有音频时间，不能导出字幕；也不是本次，不能“开始新的记录”。
        status.Text = past && !generating ? Strings.Format(Strings.History_LineCount, pastEntries.Count) : controller.Status;
        status.ToolTip = status.Text;
        bool hasLines = controller.Book.Lines.Count > 0;
        export.Visibility = newSession.Visibility = past ? Visibility.Collapsed : Visibility.Visible;
        export.IsEnabled = hasLines;
        newSession.IsEnabled = hasLines;
        ((TextBlock)notes.Content).Text = generating ? Strings.History_Generating : Strings.Menu_GenerateNotes;
        notes.IsEnabled = !generating && (past ? pastEntries.Count > 0 : hasLines);
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
        // 以前的记录不会再长，不跟随到底。
        if (scroller is null || pastFile is not null) return;
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
        private readonly TextBlock time = Caption(null);
        private readonly TextBlock label = Caption(null);
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
            original.Opacity = Secondary;
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

        /// <param name="brush">null 时用半透明的前景色（跟着外观变）。</param>
        private static TextBlock Caption(Brush? brush)
        {
            var caption = new TextBlock { FontSize = 11, Margin = new Thickness(0, 0, 6, 0) };
            if (brush is null) caption.Opacity = Secondary;
            else caption.Foreground = brush;
            return caption;
        }

        /// <summary>只读文本框（可以选中复制）。最小高度清零：Windows 11 风格的主题给文本框设了 32 的最小高度。</summary>
        private static TextBox Selectable(double size) => new()
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            MinHeight = 0,
            MinWidth = 0,
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            Template = PlainTextTemplate,
        };

        /// <summary>
        /// 只有文字区域（PART_ContentHost）的最简模板：Fluent 主题的文本框模板在鼠标悬停、点击时画输入框的边框和底色，
        /// 还会改内边距，只读的字幕行看起来像输入框，行高也跟着跳。所有行共用一个模板。
        /// </summary>
        private static readonly ControlTemplate PlainTextTemplate = MakePlainTextTemplate();

        private static ControlTemplate MakePlainTextTemplate()
        {
            var template = new ControlTemplate(typeof(TextBox)) { VisualTree = new FrameworkElementFactory(typeof(Decorator), "PART_ContentHost") };
            template.Seal();
            return template;
        }

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
            translation.Opacity = row.Waiting ? Secondary : 1;
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
