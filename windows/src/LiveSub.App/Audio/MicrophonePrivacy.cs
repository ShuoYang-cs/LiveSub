using System.Diagnostics;
using LiveSub.App.Services;
using LiveSub.Core.Audio;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace LiveSub.App.Audio;

/// <summary>
/// Windows 隐私设置里的麦克风权限（设置 › 隐私和安全性 › 麦克风）。
/// 三个开关存在注册表 CapabilityAccessManager\ConsentStore\microphone 的 Value 里（Allow / Deny）：
/// “麦克风访问权限”在 HKLM，“允许应用访问你的麦克风”在 HKCU，“允许桌面应用访问你的麦克风”在 HKCU 的 NonPackaged 子键。
/// 有的电脑上读不到这些值，所以采集失败时还要结合异常和失败时机判断（<see cref="MicrophoneAccess.Classify"/>）。
/// </summary>
public static class MicrophonePrivacy
{
    private const string ConsentKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);

    /// <summary>隐私设置里有麦克风开关是“拒绝”。</summary>
    public static bool IsDenied() => MicrophoneAccess.IsDenied(ReadSwitches());

    /// <summary>写日志用：三个开关的值，读不到的写成“-”。</summary>
    public static string Describe()
    {
        var s = ReadSwitches();
        return $"device={s[0] ?? "-"} apps={s[1] ?? "-"} desktop={s[2] ?? "-"}";
    }

    private static string?[] ReadSwitches() =>
    [
        Read(Registry.LocalMachine, ConsentKey),
        Read(Registry.CurrentUser, ConsentKey),
        Read(Registry.CurrentUser, ConsentKey + @"\NonPackaged"),
    ];

    private static string? Read(RegistryKey hive, string path)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue("Value") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>系统拒绝访问麦克风（E_ACCESSDENIED），包括包在内层的异常。</summary>
    public static bool IsAccessDenied(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is UnauthorizedAccessException || e.HResult == E_ACCESSDENIED) return true;
        }
        return false;
    }

    /// <summary>采集报“设备已断开或硬件已重新配置”（权限被关闭时也是这个异常）。</summary>
    public static bool IsDeviceLost(Exception e) => e is AudioDeviceDisconnectedException;

    /// <summary>现在有默认录音设备。</summary>
    public static bool HasMicrophone()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }
        catch (Exception e)
        {
            AppLog.Error("audio: cannot check microphone", e);
            return false;
        }
    }

    /// <summary>打开 Windows 设置的麦克风隐私页。</summary>
    public static void OpenSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "ms-settings:privacy-microphone", UseShellExecute = true });
        }
        catch (Exception e)
        {
            AppLog.Error("open microphone settings failed", e);
        }
    }
}
