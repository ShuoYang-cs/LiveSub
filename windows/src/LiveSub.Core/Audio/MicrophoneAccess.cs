namespace LiveSub.Core.Audio;

/// <summary>麦克风采集失败是不是因为 Windows 隐私设置关闭了麦克风。</summary>
public enum MicrophoneBlock
{
    /// <summary>不是权限问题，或者看不出来。</summary>
    None,
    /// <summary>采集刚开始就被系统断开，而麦克风还在：多半是权限被关闭（隐私开关读不到时只能这样推断）。</summary>
    Likely,
    /// <summary>隐私设置里有开关是“拒绝”，或者系统直接拒绝访问。</summary>
    Certain,
}

/// <summary>
/// 判断麦克风是否被隐私设置挡住。关闭“允许桌面应用访问你的麦克风”后，WASAPI 照样能打开麦克风，
/// 约 3 秒后才报“设备已断开”（2026-10-21 在 Windows 上实测），所以只看异常分不出是权限还是设备问题。
/// </summary>
public static class MicrophoneAccess
{
    /// <summary>采集开始后这么短时间内就被断开，才按权限问题提示；用了很久才断开，多半是设备或格式变了，重新打开就好。</summary>
    public static readonly TimeSpan EarlyFailure = TimeSpan.FromSeconds(10);

    /// <summary>隐私设置里的麦克风开关（注册表 ConsentStore 的 Value）有一个是 Deny 就是被关闭；读不到的开关不算。</summary>
    public static bool IsDenied(IEnumerable<string?> switches) =>
        switches.Any(v => string.Equals(v?.Trim(), "Deny", StringComparison.OrdinalIgnoreCase));

    /// <param name="switchDenied">隐私设置里有开关是“拒绝”。</param>
    /// <param name="accessDenied">打开麦克风时系统返回“拒绝访问”。</param>
    /// <param name="deviceLost">采集时报“设备已断开或硬件已重新配置”。</param>
    /// <param name="ranFor">这次采集从开始到出错用了多久。</param>
    /// <param name="microphonePresent">出错后仍然有默认录音设备（区分“权限被关”和“麦克风被拔掉”）。</param>
    public static MicrophoneBlock Classify(bool switchDenied, bool accessDenied, bool deviceLost, TimeSpan ranFor, bool microphonePresent)
    {
        if (switchDenied || accessDenied) return MicrophoneBlock.Certain;
        if (deviceLost && microphonePresent && ranFor < EarlyFailure) return MicrophoneBlock.Likely;
        return MicrophoneBlock.None;
    }
}
