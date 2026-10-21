using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using LiveSub.App.Localization;
using LiveSub.App.Services;
using LiveSub.Core;
using LiveSub.Core.Subtitles;

namespace LiveSub.App.UI;

/// <summary>本次记录的全部字幕（原文 + 译文），可以打开记录文件。搜索留到以后的版本。</summary>
public sealed class HistoryWindow : Window
{
    private readonly SubtitleController controller;
    private readonly ListBox list = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<string> items = [];
    private readonly TextBlock empty = new() { Text = Strings.History_Empty, Margin = new Thickness(16), Opacity = 0.6 };
    private readonly DispatcherTimer refreshTimer;
    private int shownCount = -1;
    private int shownDone = -1;

    public HistoryWindow(SubtitleController controller)
    {
        this.controller = controller;
        Title = $"{Strings.AppName} · {Strings.History_Title}";
        Width = 640;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding("."));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(MarginProperty, new Thickness(4, 6, 4, 6));
        list.ItemTemplate = new DataTemplate { VisualTree = text };
        list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        list.ItemsSource = items;

        var openFile = new Button { Content = Strings.Button_OpenTranscript, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        openFile.Click += (_, _) =>
        {
            if (controller.TranscriptPath is { } p) AppShell.OpenInExplorer(p);
        };
        var openFolder = new Button { Content = Strings.Menu_OpenFolder, Padding = new Thickness(12, 4, 12, 4) };
        openFolder.Click += (_, _) =>
        {
            if (controller.TranscriptPath is { } p) AppShell.OpenInExplorer(Path.GetDirectoryName(p)!);
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        buttons.Children.Add(openFile);
        buttons.Children.Add(openFolder);

        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        var body = new Grid();
        body.Children.Add(list);
        body.Children.Add(empty);
        dock.Children.Add(body);
        Content = dock;

        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        refreshTimer.Tick += (_, _) => Refresh();
        refreshTimer.Start();
        Closed += (_, _) => refreshTimer.Stop();
        Refresh();
    }

    private void Refresh()
    {
        var lines = controller.Book.Lines;
        int done = lines.Count(l => l.State is LineState.Done or LineState.Failed);
        if (lines.Count == shownCount && done == shownDone) return;
        bool atEnd = items.Count == 0 || IsScrolledToEnd();
        shownCount = lines.Count;
        shownDone = done;
        // 逐项更新而不是整体替换，往上翻看时滚动位置不会被重置。
        var fresh = lines.Select(Format).ToList();
        for (int i = 0; i < Math.Min(items.Count, fresh.Count); i++)
        {
            if (items[i] != fresh[i]) items[i] = fresh[i];
        }
        while (items.Count > fresh.Count) items.RemoveAt(items.Count - 1);
        for (int i = items.Count; i < fresh.Count; i++) items.Add(fresh[i]);
        empty.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        list.Visibility = lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (atEnd && items.Count > 0) list.ScrollIntoView(items[^1]);
    }

    private bool IsScrolledToEnd()
    {
        if (VisualTreeHelperFind<ScrollViewer>(list) is not { } sv) return true;
        return sv.VerticalOffset >= sv.ScrollableHeight - 2;
    }

    private static T? VisualTreeHelperFind<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (VisualTreeHelperFind<T>(child) is { } found) return found;
        }
        return null;
    }

    private static string Format(SubtitleLine line)
    {
        var translation = line.State switch
        {
            LineState.Failed => Strings.Overlay_TranslationFailed,
            LineState.Done => line.Translation,
            _ => line.Translation.Length > 0 ? line.Translation : "…",
        };
        var original = line.OriginalSource is { } o && o != line.Source ? $"\n    ({o})" : "";
        return $"[{TranscriptFormat.TimeStamp(line.Time)}] {line.Source}{original}\n    {translation}";
    }
}
