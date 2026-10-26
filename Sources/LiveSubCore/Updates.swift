import Foundation

/// 版本号比较：只比数字段（“0.1.2”、“macos-0.1.10”），不认预发布后缀。
public enum AppVersion {
    public static func parse(_ text: String) -> [Int] {
        var s = text.trimmingCharacters(in: .whitespaces)
        if let first = s.firstIndex(where: { $0.isNumber }) { s = String(s[first...]) }  // 去掉 “macos-”、“v” 前缀
        s = String(s.split(separator: "+").first ?? "").split(separator: "-").first.map(String.init) ?? ""
        var parts = s.split(separator: ".").map { Int($0.filter(\.isNumber)) ?? 0 }
        while parts.count < 3 { parts.append(0) }
        return Array(parts.prefix(3))
    }

    /// 相等不算新：否则每次检查都会提示一次。
    public static func isNewer(_ remote: String, than local: String) -> Bool {
        parse(local).lexicographicallyPrecedes(parse(remote))
    }
}

/// GitHub 上的一个发布。
public struct ReleaseInfo: Equatable, Sendable {
    public var version: String
    public var notes: String
    public var assetURL: URL?
    public var size: Int
    public var page: URL?
}

/// 读 GitHub Releases 的回复（`/repos/…/releases`）：挑出本平台最新的一版。
/// macOS 和 Windows 的标签分开（`macos-0.1.2`、`windows-0.1.0`），所以按标签前缀筛。
public enum ReleaseFeed {
    public static func latest(from data: Data, tagPrefix: String, assetSuffix: String) -> ReleaseInfo? {
        guard let list = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return nil }
        var best: ReleaseInfo?
        for item in list {
            guard item["draft"] as? Bool != true, item["prerelease"] as? Bool != true,
                  let tag = item["tag_name"] as? String, tag.hasPrefix(tagPrefix) else { continue }
            let version = String(tag.dropFirst(tagPrefix.count))
            if let best, !AppVersion.isNewer(version, than: best.version) { continue }
            let assets = item["assets"] as? [[String: Any]] ?? []
            let asset = assets.first { ($0["name"] as? String)?.hasSuffix(assetSuffix) == true }
            best = ReleaseInfo(
                version: version,
                notes: (item["body"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines),
                assetURL: (asset?["browser_download_url"] as? String).flatMap(URL.init(string:)),
                size: asset?["size"] as? Int ?? 0,
                page: (item["html_url"] as? String).flatMap(URL.init(string:)))
        }
        return best
    }
}

public enum UpdatePolicy {
    /// 要不要提示：比现在的版本新，并且用户没有选“跳过此版本”。
    public static func shouldOffer(_ release: ReleaseInfo, current: String, skipped: String) -> Bool {
        AppVersion.isNewer(release.version, than: current) && release.version != skipped
    }

    /// 该不该去问 GitHub：上次成功后过了一天；上次失败的话 30 分钟后再试。
    public static func checkDue(now: Date, lastSuccess: Date?, lastAttempt: Date?) -> Bool {
        guard let lastAttempt else { return true }
        if let lastSuccess, lastSuccess >= lastAttempt { return now.timeIntervalSince(lastSuccess) >= 24 * 3600 }
        return now.timeIntervalSince(lastAttempt) >= 30 * 60
    }
}
