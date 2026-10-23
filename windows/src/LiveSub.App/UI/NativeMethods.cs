using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LiveSub.App.Services;

namespace LiveSub.App.UI;

internal static partial class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WM_HOTKEY = 0x0312;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;
    public const int WM_SYSCOMMAND = 0x0112;
    public const int SC_CLOSE = 0xF060;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;
    public const int VK_MBUTTON = 0x04;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr hIcon);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll")]
    public static partial IntPtr WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>鼠标键正按在别的程序的窗口上（看的是物理按键，本程序不在前台时也能用）。</summary>
    public static bool MouseDownInOtherProcess()
    {
        bool down = GetAsyncKeyState(VK_LBUTTON) < 0 || GetAsyncKeyState(VK_RBUTTON) < 0 || GetAsyncKeyState(VK_MBUTTON) < 0;
        if (!down || !GetCursorPos(out var p)) return false;
        var hwnd = WindowFromPoint(p);
        if (hwnd == IntPtr.Zero) return true;
        GetWindowThreadProcessId(hwnd, out uint processId);
        return processId != (uint)Environment.ProcessId;
    }

    public static void SetExStyle(IntPtr hwnd, int add, int remove)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style = (style | (long)add) & ~(long)remove;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }
}

/// <summary>
/// 全局快捷键（RegisterHotKey，不需要管理员权限）。挂在一个只收消息的隐藏窗口上。
/// </summary>
internal sealed class GlobalHotKey : IDisposable
{
    private const int Id = 0x4C53; // 'LS'
    private readonly HwndSource source;
    private readonly Action action;

    public bool Registered { get; }

    /// <param name="modifiers">MOD_ALT、MOD_CONTROL、MOD_WIN 的组合（不含 MOD_NOREPEAT，这里自动加上）。</param>
    public GlobalHotKey(uint modifiers, uint virtualKey, Action action)
    {
        this.action = action;
        source = new HwndSource(new HwndSourceParameters("LiveSubHotKey")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0,
        });
        source.AddHook(WndProc);
        Registered = NativeMethods.RegisterHotKey(source.Handle, Id, modifiers | NativeMethods.MOD_NOREPEAT, virtualKey);
        if (!Registered) AppLog.Error($"hotkey: RegisterHotKey failed ({Marshal.GetLastWin32Error()})");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == Id)
        {
            handled = true;
            Dispatcher.CurrentDispatcher.BeginInvoke(action);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (Registered) NativeMethods.UnregisterHotKey(source.Handle, Id);
        source.RemoveHook(WndProc);
        source.Dispose();
    }
}

internal static class WindowExtensions
{
    public static IntPtr Handle(this Window window) => new WindowInteropHelper(window).Handle;
}
