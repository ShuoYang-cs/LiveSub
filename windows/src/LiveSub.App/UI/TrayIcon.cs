using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;
using LiveSub.App.Audio;
using LiveSub.App.Localization;
using LiveSub.App.Services;
using LiveSub.App.Translation;
using LiveSub.Core.Audio;
using Microsoft.Win32;

namespace LiveSub.App.UI;

/// <summary>
/// 托盘图标与菜单（与 macOS 的菜单栏图标相同）：左键单击时，字幕收起着就把它显示出来，否则弹出菜单；右键总是弹出菜单。
/// 菜单每次打开时按当前状态重建。图标是运行时画的：正在听时实心，停止时空心；按任务栏深浅色选颜色。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>
    /// NotifyIcon 右键弹菜单用的私有方法：先把程序提到前台再弹出，点菜单外面时菜单才会关上。
    /// 左键也用它，与右键的菜单表现一致；找不到时（以后的 .NET 改了名字）直接弹出。
    /// </summary>
    private static readonly MethodInfo? ShowContextMenuMethod =
        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes);

    private readonly AppShell shell;
    private readonly NotifyIcon icon = new();
    private readonly ContextMenuStrip menu = new();
    private IntPtr iconHandle;
    private bool? drawnRunning;
    /// <summary>菜单开着时再点图标：按下时菜单已经关上，松开时不要又把它打开。</summary>
    private DateTime menuClosedAt = DateTime.MinValue;

    public TrayIcon(AppShell shell)
    {
        this.shell = shell;
        menu.Opening += (_, _) => BuildMenu();
        menu.Closed += (_, _) => menuClosedAt = DateTime.UtcNow;
        icon.ContextMenuStrip = menu;
        icon.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (!shell.OverlayVisible) shell.ShowOverlay();
            else if (DateTime.UtcNow - menuClosedAt > TimeSpan.FromMilliseconds(300)) ShowMenu();
        };
        Update();
        icon.Visible = true;
    }

    private void ShowMenu()
    {
        if (ShowContextMenuMethod is not null) ShowContextMenuMethod.Invoke(icon, null);
        else menu.Show(Cursor.Position);
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

    /// <summary>
    /// 菜单和字幕上的快捷按钮对齐（macOS 去掉了菜单栏图标，Windows 上托盘图标是常规做法，保留）：
    /// 状态；开始/停止、录音、音源、翻译模型；显示/收起字幕、锁定、AI 助手；字幕记录、录音记录；设置、退出。
    /// 导出字幕、开始新的记录、生成笔记、打开记录文件夹在“字幕记录”窗口里，“字幕移回原位”并进了设置里的“恢复默认”（与 macOS 相同）。
    /// </summary>
    private void BuildMenu()
    {
        var c = shell.Controller;
        var s = shell.Settings.Current;
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem(Shorten(c.Status)) { Enabled = false });
        // 字幕锁定后点不到提示上的按钮，托盘菜单里也放一个。
        if (c.MicrophoneBlock != MicrophoneBlock.None) menu.Items.Add(Item(Strings.Overlay_OpenMicrophoneSettings, MicrophonePrivacy.OpenSettings));
        menu.Items.Add(new ToolStripSeparator());

        bool active = c.IsRunning || c.IsStarting;
        menu.Items.Add(Item(active ? Strings.Menu_Stop : Strings.Menu_Start, shell.ToggleRunning, enabled: !c.IsBusy || c.IsStarting));
        menu.Items.Add(Item(Strings.Menu_Record, shell.ToggleRecording, c.RecordButtonOn));

        var sourceMenu = new ToolStripMenuItem(Strings.Menu_AudioSource);
        foreach (var (mode, title, _) in AppShell.AudioSources)
        {
            sourceMenu.DropDownItems.Add(Item(title, () => shell.SelectAudioSource(mode), s.AudioSource == mode));
        }
        menu.Items.Add(sourceMenu);

        // 翻译模型：“基础”“云端模型”“本地模型”三组（组名不可点），最后是“管理模型服务…”。
        var modelMenu = new ToolStripMenuItem(Strings.Menu_Model);
        bool first = true;
        foreach (var group in TranslationEngines.TranslationMenu(s))
        {
            if (!first) modelMenu.DropDownItems.Add(new ToolStripSeparator());
            first = false;
            modelMenu.DropDownItems.Add(new ToolStripMenuItem(group.Title) { Enabled = false });
            foreach (var item in group.Items)
            {
                modelMenu.DropDownItems.Add(Item(item.Title, () => shell.SelectModel(item.Choice), item.Selected));
            }
        }
        modelMenu.DropDownItems.Add(new ToolStripSeparator());
        modelMenu.DropDownItems.Add(Item(Strings.Menu_ManageModels, shell.ShowModelServices));
        menu.Items.Add(modelMenu);
        menu.Items.Add(new ToolStripSeparator());

        var show = Item(shell.OverlayVisible ? Strings.Menu_HideOverlay : Strings.Menu_ShowOverlay, shell.ToggleOverlay);
        show.ShortcutKeyDisplayString = HotKeys.Text(s.HotKey) ?? "";
        menu.Items.Add(show);
        menu.Items.Add(Item(Strings.Menu_Lock, shell.ToggleLock, s.ClickThrough));
        menu.Items.Add(Item(Strings.Assistant_Title, shell.ToggleAssistantFromMenu, shell.AssistantVisible));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(Item(Strings.Menu_History, shell.ShowHistory));
        menu.Items.Add(Item(Strings.Menu_Recordings, shell.ShowRecordings));
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
