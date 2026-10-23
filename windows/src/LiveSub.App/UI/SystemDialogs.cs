using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace LiveSub.App.UI;

/// <summary>
/// 选择文件夹、导出字幕等系统对话框。字幕和 AI 助手总在最上层，并且每 2 秒重新提到最上层，会挡住对话框的按钮；
/// 对话框又是模态的，挡住它的窗口也拖不开，看起来像程序卡住了（macOS 版 2026-10-21 发生过）。
/// 所以对话框开着时不重新置顶（<see cref="IsOpen"/>），并让对话框的所有者临时置顶，对话框就在它们上面。
/// </summary>
internal static class SystemDialogs
{
    private static int open;

    /// <summary>有系统对话框开着：不要把字幕和 AI 助手重新提到最上层。</summary>
    public static bool IsOpen => open > 0;

    /// <param name="owner">对话框所属的窗口；没有时临时建一个看不见的置顶窗口当所有者。</param>
    public static bool? Show(CommonDialog dialog, Window? owner = null)
    {
        Window? temporary = null;
        if (owner is null)
        {
            var work = SystemParameters.WorkArea;
            temporary = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize,
                Width = 1,
                Height = 1,
                Left = work.Left + work.Width / 2,
                Top = work.Top + work.Height / 3,
            };
            temporary.Show();
            owner = temporary;
        }
        bool wasTopmost = owner.Topmost;
        open++;
        try
        {
            owner.Topmost = true;
            return dialog.ShowDialog(owner);
        }
        finally
        {
            open--;
            if (temporary is not null) temporary.Close();
            else owner.Topmost = wasTopmost;
        }
    }
}
