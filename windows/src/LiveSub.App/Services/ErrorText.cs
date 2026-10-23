using LiveSub.App.Localization;

namespace LiveSub.App.Services;

/// <summary>
/// 系统异常 → 给用户看的说明（中英文）。.NET 自己的异常说明只有英文（“An error occurred while sending the request.”），
/// 中文界面上夹着一句英文，用户看不懂该怎么办；原文由调用方写进日志，排查时看日志。
/// </summary>
public static class ErrorText
{
    /// <summary>按 <see cref="HttpRequestException.HttpRequestError"/> 说明连不上的原因。</summary>
    public static string Network(HttpRequestException e) => e.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => Strings.Net_NameResolution,
        HttpRequestError.SecureConnectionError => Strings.Net_SecureConnection,
        HttpRequestError.ProxyTunnelError or HttpRequestError.UserAuthenticationError => Strings.Net_Proxy,
        HttpRequestError.ResponseEnded or HttpRequestError.InvalidResponse or HttpRequestError.HttpProtocolError => Strings.Net_Interrupted,
        _ => Strings.Net_ConnectionFailed,
    };

    /// <summary>下载、写文件出错：网络错误按种类说明；没有权限、磁盘满单独说明；其他用异常自己的说明。</summary>
    public static string Download(Exception e) => e switch
    {
        HttpRequestException h => Network(h),
        UnauthorizedAccessException => Strings.File_AccessDenied,
        IOException io when IsDiskFull(io) => Strings.File_DiskFull,
        _ => e.Message,
    };

    /// <summary>ERROR_DISK_FULL、ERROR_HANDLE_DISK_FULL（HRESULT 的低 16 位）。</summary>
    private static bool IsDiskFull(IOException e) => (e.HResult & 0xFFFF) is 0x70 or 0x27;
}
