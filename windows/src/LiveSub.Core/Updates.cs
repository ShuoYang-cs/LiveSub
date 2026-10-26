using System.Text.Json;

namespace LiveSub.Core;

/// <summary>版本号比较：只比数字段（“0.1.2”、“windows-0.1.10”），不认预发布后缀（与 macOS 的 AppVersion 相同）。</summary>
public static class AppVersion
{
    public static int[] Parse(string text)
    {
        var s = (text ?? "").Trim();
        int first = s.IndexOfAny("0123456789".ToCharArray());
        s = first < 0 ? "" : s[first..];
        s = s.Split('+')[0].Split('-')[0];
        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(new string(p.Where(char.IsDigit).ToArray()), out var n) ? n : 0).ToList();
        while (parts.Count < 3) parts.Add(0);
        return parts.Take(3).ToArray();
    }

    /// <summary>相等不算新：否则每次检查都会提示一次。</summary>
    public static bool IsNewer(string remote, string local)
    {
        var r = Parse(remote);
        var l = Parse(local);
        for (int i = 0; i < 3; i++)
        {
            if (r[i] != l[i]) return r[i] > l[i];
        }
        return false;
    }
}

/// <summary>GitHub 上的一个发布。</summary>
public sealed record ReleaseInfo(string Version, string Notes, string? AssetUrl, long Size, string? Page);

/// <summary>读 GitHub Releases 的回复（<c>/repos/…/releases</c>）：挑出本平台最新的一版。标签按前缀分平台（<c>windows-0.1.0</c>）。</summary>
public static class ReleaseFeed
{
    public static ReleaseInfo? Latest(string json, string tagPrefix, string assetSuffix)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            ReleaseInfo? best = null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (Flag(item, "draft") || Flag(item, "prerelease")) continue;
                var tag = Text(item, "tag_name");
                if (tag is null || !tag.StartsWith(tagPrefix, StringComparison.Ordinal)) continue;
                var version = tag[tagPrefix.Length..];
                if (best is not null && !AppVersion.IsNewer(version, best.Version)) continue;
                string? url = null;
                long size = 0;
                if (item.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        if (Text(asset, "name")?.EndsWith(assetSuffix, StringComparison.Ordinal) != true) continue;
                        url = Text(asset, "browser_download_url");
                        size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                        break;
                    }
                }
                best = new ReleaseInfo(version, (Text(item, "body") ?? "").Trim(), url, size, Text(item, "html_url"));
            }
            return best;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public static class UpdatePolicy
{
    /// <summary>要不要提示：比现在的版本新，并且用户没有选“跳过此版本”。</summary>
    public static bool ShouldOffer(ReleaseInfo release, string current, string skipped) =>
        AppVersion.IsNewer(release.Version, current) && release.Version != skipped;

    /// <summary>该不该去问 GitHub：上次成功后过了一天；上次失败的话 30 分钟后再试。</summary>
    public static bool CheckDue(DateTime now, DateTime? lastSuccess, DateTime? lastAttempt)
    {
        if (lastAttempt is not { } attempt) return true;
        if (lastSuccess is { } success && success >= attempt) return now - success >= TimeSpan.FromHours(24);
        return now - attempt >= TimeSpan.FromMinutes(30);
    }
}
