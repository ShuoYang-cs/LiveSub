using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using LiveSub.App.Localization;
using LiveSub.App.Services;
using LiveSub.App.Translation;
using LiveSub.Core.Subtitles;
using Microsoft.Win32;

namespace LiveSub.App.UI;

/// <summary>
/// 托盘图标与菜单。左键单击显示或隐藏字幕；右键弹出菜单（每次打开时按当前状态重建）。
/// 图标是运行时画的：正在听时实心，停止时空心；按任务栏深浅色选颜色。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppShell shell;
    private readonly NotifyIcon icon = new();
    private readonly ContextMenuStrip menu = new();
    private IntPtr iconHandle;
    private bool? drawnRunning;

    public TrayIcon(AppShell shell)
    {
        this.shell = shell;
        menu.Opening += (_, _) => BuildMenu();
        icon.ContextMenuStrip = menu;
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) shell.ToggleOverlay();
        };
        Update();
        icon.Visible = true;
    }

    /// <summary>按运行状态更新图标和提示文字。</summary>
    public void Update()
    {
        bool running = shell.Controller.IsRunning;
        if (drawnRunning != running)
        {
            drawnRunning = running;
            SetIcon(DrawIcon(running));
        }
        var tip = Strings.Format(Strings.Tip_Tray, Strings.AppName, shell.Controller.Status);
        icon.Text = tip.Length > 127 ? tip[..126] + "…" : tip;
    }

    private void BuildMenu()
    {
        var c = shell.Controller;
        var s = shell.Settings.Current;
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem(Shorten(c.Status)) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        bool active = c.IsRunning || c.IsStarting;
        menu.Items.Add(Item(active ? Strings.Menu_Stop : Strings.Menu_Start, shell.ToggleRunning, enabled: !c.IsBusy || c.IsStarting));

        var sourceMenu = new ToolStripMenuItem(Strings.Menu_AudioSource);
        sourceMenu.DropDownItems.Add(Item(Strings.Source_Microphone, () => shell.SelectAudioSource(AudioSourceMode.Microphone), s.AudioSource == AudioSourceMode.Microphone));
        sourceMenu.DropDownItems.Add(Item(Strings.Source_System, () => shell.SelectAudioSource(AudioSourceMode.System), s.AudioSource == AudioSourceMode.System));
        menu.Items.Add(sourceMenu);

        var modelMenu = new ToolStripMenuItem(Strings.Menu_Model);
        bool first = true;
        foreach (var group in TranslationEngines.Choices(s, shell.Secrets))
        {
            if (!first) modelMenu.DropDownItems.Add(new ToolStripSeparator());
            first = false;
            modelMenu.DropDownItems.Add(new ToolStripMenuItem(group.Title) { Enabled = false });
            foreach (var choice in group.Choices)
            {
                modelMenu.DropDownItems.Add(Item(choice.Title, () => shell.SelectModel(choice), choice.Selected, choice.Enabled));
            }
        }
        menu.Items.Add(modelMenu);

        var displayMenu = new ToolStripMenuItem(Strings.Menu_Display);
        foreach (var (mode, title) in AppShell.DisplayModes)
        {
            displayMenu.DropDownItems.Add(Item(title, () => shell.SelectDisplayMode(mode), s.DisplayMode == mode));
        }
        menu.Items.Add(displayMenu);
        menu.Items.Add(new ToolStripSeparator());

        var show = Item(shell.OverlayVisible ? Strings.Menu_HideOverlay : Strings.Menu_ShowOverlay, shell.ToggleOverlay);
        show.ShortcutKeyDisplayString = HotKeys.Text(s.HotKey) ?? "";
        menu.Items.Add(show);
        menu.Items.Add(Item(Strings.Menu_Lock, shell.ToggleLock, s.ClickThrough));
        menu.Items.Add(Item(Strings.Menu_ResetPosition, shell.ResetOverlayPosition));
        menu.Items.Add(new ToolStripSeparator());

        var export = new ToolStripMenuItem(Strings.Menu_Export) { Enabled = c.Book.Lines.Count > 0 };
        export.DropDownItems.Add(Item("SRT…", () => shell.Export(SubtitleFileFormat.Srt)));
        export.DropDownItems.Add(Item("WebVTT…", () => shell.Export(SubtitleFileFormat.Vtt)));
        menu.Items.Add(export);
        menu.Items.Add(Item(Strings.Menu_History, shell.ShowHistory));
        menu.Items.Add(Item(Strings.Menu_NewSession, shell.NewSession));
        menu.Items.Add(Item(Strings.Menu_OpenFolder, shell.OpenTranscriptFolder));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item(Strings.Menu_Settings, shell.ShowSettings));
        menu.Items.Add(Item(Strings.Menu_Quit, shell.Quit));
    }

    private static ToolStripMenuItem Item(string text, Action action, bool isChecked = false, bool enabled = true)
    {
        var item = new ToolStripMenuItem(text) { Checked = isChecked, Enabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static string Shorten(string s) => s.Length > 70 ? s[..69] + "…" : s;

    private void SetIcon(Icon newIcon)
    {
        var old = iconHandle;
        icon.Icon = newIcon;
        iconHandle = newIcon.Handle;
        if (old != IntPtr.Zero) NativeMethods.DestroyIcon(old);
    }

    /// <summary>字幕框图标：圆角矩形里两条横线。正在听时实心。</summary>
    private static Icon DrawIcon(bool running)
    {
        bool lightTaskbar = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0) is int v && v == 1;
        var fg = lightTaskbar ? Color.FromArgb(32, 32, 32) : Color.White;
        var bg = lightTaskbar ? Color.White : Color.FromArgb(32, 32, 32);
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var path = RoundedRect(new RectangleF(2.5f, 6.5f, 27, 19), 5);
            if (running)
            {
                using var fill = new SolidBrush(fg);
                g.FillPath(fill, path);
            }
            else
            {
                using var pen = new Pen(fg, 2.5f);
                g.DrawPath(pen, path);
            }
            using var line = new Pen(running ? bg : fg, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(line, 8, 13.5f, 24, 13.5f);
            g.DrawLine(line, 8, 19, 19, 19);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
        if (iconHandle != IntPtr.Zero) NativeMethods.DestroyIcon(iconHandle);
    }
}
