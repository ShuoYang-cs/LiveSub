using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiveSub.App.Localization;
using LiveSub.App.Services;
using LiveSub.Core;
using LiveSub.Core.Audio;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace LiveSub.App.UI;

/// <summary>
/// 录音记录（设置 › 记录 › 录音记录，与 macOS 的 RecordingsView 相同）：记录文件夹里录下的声音，最新的在最上面，
/// 每个显示时长和大小。“播放”用默认的播放器打开；有同一次的字幕记录时可以直接打开它；也可以在资源管理器中选中它。
/// 在后台读文件夹（记录文件夹可能在网络驱动器上，读时长也要打开每个文件），不卡界面；窗口回到前面时重新读一遍（可能刚录完一段）。
/// </summary>
public sealed class RecordingsWindow : Window
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly SolidColorBrush Hairline = new(Color.FromArgb(0x40, 0x80, 0x80, 0x80));

    private readonly SettingsService settings;
    private readonly StackPanel list = new();
    private readonly ScrollViewer scroller;
    private readonly TextBlock empty = new()
    {
        Text = Strings.Recordings_Empty,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        Opacity = 0.6,
        Margin = new Thickness(30),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock count = new() { FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>每次读文件夹加一：晚到的旧结果不再显示。</summary>
    private int scan;
    /// <summary>构造时已经读过一遍：窗口第一次显示时的 Activated 不用再读（否则打开窗口时整个文件夹读两遍）。</summary>
    private bool skipActivation = true;

    private sealed record Recording(string Path, DateTime Created, TimeSpan? Duration, long Bytes, string? Transcript);

    public RecordingsWindow(SettingsService settings)
    {
        this.settings = settings;
        Title = $"{Strings.AppName} · {Strings.Recordings_Title}";
        Width = 640;
        Height = 440;
        MinWidth = 520;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Appearance.Theme(this);
        // 内容窗口：保留最小化和最大化（与 macOS 相同）。
        WindowLevels.Raise(this, closeButtonOnly: false);
        // 小屏、高缩放时不超出屏幕（标题栏不能在屏幕外）。
        ScreenFit.Apply(this);

        scroller = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var openFolder = BarButton(Strings.Menu_OpenFolder, () => AppShell.OpenInExplorer(Folder));
        var refresh = BarButton(Strings.Button_Refresh, Reload);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(openFolder);
        buttons.Children.Add(refresh);
        var bar = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(count);
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
        body.Children.Add(scroller);
        body.Children.Add(empty);
        dock.Children.Add(body);
        Content = dock;

        Activated += (_, _) =>
        {
            if (skipActivation) skipActivation = false;
            else Reload();
        };
        Reload();
    }

    private string Folder => Environment.ExpandEnvironmentVariables(settings.Current.TranscriptFolder);

    /// <summary>记录文件夹写不进去时，字幕记录（和旁边的录音）放在这里（见 TranscriptStore）。</summary>
    private static string FallbackFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveSub", "Transcripts");

    private async void Reload()
    {
        int current = ++scan;
        var folders = new[] { Folder, FallbackFolder }.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string transcriptName = Strings.Transcript_Name, recordingName = Strings.Recording_Name;
        List<Recording> found;
        try
        {
            found = await Task.Run(() => Scan(folders, transcriptName, recordingName));
        }
        catch (Exception e)
        {
            AppLog.Error("recordings: scan failed", e);
            found = [];
        }
        if (current != scan) return;
        Show(found);
    }

    private static List<Recording> Scan(IEnumerable<string> folders, string transcriptName, string recordingName)
    {
        var result = new List<Recording>();
        bool mediaFoundation = true;
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*" + RecordingFiles.Extension).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                AppLog.Error($"recordings: cannot list {folder}", e);
                continue;
            }
            foreach (var path in files)
            {
                // 每个文件单独处理：一个坏文件（没写完、被占着、格式不对）不能让整个列表变空。
                try
                {
                    var info = new FileInfo(path);
                    result.Add(new Recording(path, info.CreationTime, mediaFoundation ? Duration(path, ref mediaFoundation) : null, info.Length,
                        RecordingFiles.TranscriptFor(path, transcriptName, recordingName, File.Exists)));
                }
                catch (Exception e)
                {
                    // 文件刚被删掉或被占着：跳过。
                    AppLog.Error($"recordings: skipped {Path.GetFileName(path)}", e);
                }
            }
        }
        return result.OrderByDescending(r => r.Created).ToList();
    }

    /// <summary>时长：用 Media Foundation 打开文件（正在录的、没写完的、损坏的文件打不开，这时不显示时长）。</summary>
    /// <param name="available">Media Foundation 不可用（Windows N 版本）时设为 false，后面的文件不再试。</param>
    private static TimeSpan? Duration(string path, ref bool available)
    {
        try
        {
            MediaFoundationApi.Startup();
            using var reader = new MediaFoundationReader(path);
            return reader.TotalTime;
        }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException)
        {
            available = false;
            return null;
        }
        catch (Exception)
        {
            // 正在录、没写完或损坏的文件：只是不显示时长，照样列出来。
            return null;
        }
    }

    private void Show(List<Recording> recordings)
    {
        list.Children.Clear();
        foreach (var recording in recordings) list.Children.Add(Row(recording));
        empty.Visibility = recordings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        scroller.Visibility = recordings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        count.Text = Strings.Format(Strings.Recordings_Count, recordings.Count);
    }

    private static FrameworkElement Row(Recording recording)
    {
        var icon = new TextBlock
        {
            Text = "\uE8D6", // Audio
            FontFamily = IconFont,
            FontSize = 14,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        var name = new TextBlock
        {
            Text = Path.GetFileNameWithoutExtension(recording.Path),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = recording.Path,
        };
        var parts = new List<string>();
        if (recording.Duration is { } d) parts.Add(RecordingFiles.Duration(d));
        parts.Add(TextUtil.FileSize(recording.Bytes));
        var details = new TextBlock { Text = string.Join(" · ", parts), FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 2, 0, 0) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(details);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (recording.Transcript is { } transcript)
        {
            buttons.Children.Add(BarButton(Strings.Label_History, () => Open(transcript)));
        }
        buttons.Children.Add(BarButton(Strings.Recordings_Play, () => Open(recording.Path)));
        var reveal = BarButton("\uE8B7", () => AppShell.RevealInExplorer(recording.Path)); // Folder
        reveal.FontFamily = IconFont;
        reveal.ToolTip = Strings.Assistant_ShowInExplorer;
        buttons.Children.Add(reveal);

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(buttons);
        row.Children.Add(text);
        return new Border
        {
            Child = row,
            Padding = new Thickness(12, 7, 12, 7),
            BorderBrush = Hairline,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private static Button BarButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>用默认程序打开（录音用播放器，字幕记录用文本编辑器）。</summary>
    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception e)
        {
            AppLog.Error("recordings: open failed", e);
        }
    }
}
