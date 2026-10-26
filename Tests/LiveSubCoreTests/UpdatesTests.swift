import Foundation
import Testing
@testable import LiveSubCore

@Suite struct UpdatesTests {
    @Test func versionComparison() {
        #expect(AppVersion.parse("macos-0.1.2") == [0, 1, 2])
        #expect(AppVersion.parse("v1.10") == [1, 10, 0])
        #expect(AppVersion.isNewer("0.1.10", than: "0.1.9"))
        #expect(AppVersion.isNewer("0.2.0", than: "0.1.99"))
        #expect(!AppVersion.isNewer("0.1.2", than: "0.1.2"))
        #expect(!AppVersion.isNewer("0.1.1", than: "0.1.2"))
        #expect(AppVersion.isNewer("1.0.0", than: "0.9.9"))
    }

    private let feed = Data("""
    [
     {"tag_name":"windows-0.1.0","assets":[{"name":"LiveSub-0.1.0-Windows-x64.zip","browser_download_url":"https://x/w.zip","size":5}]},
     {"tag_name":"macos-0.1.1","body":"old","assets":[]},
     {"tag_name":"macos-0.1.2","body":" new things ","html_url":"https://x/page","assets":[
        {"name":"LiveSub-0.1.2-macOS.dmg","browser_download_url":"https://x/a.dmg","size":1},
        {"name":"LiveSub-0.1.2-macOS.zip","browser_download_url":"https://x/a.zip","size":2}]},
     {"tag_name":"macos-0.1.10","draft":true,"assets":[]},
     {"tag_name":"macos-0.1.3","prerelease":true,"assets":[]}
    ]
    """.utf8)

    @Test func picksNewestReleaseOfThisPlatform() {
        let r = ReleaseFeed.latest(from: feed, tagPrefix: "macos-", assetSuffix: "-macOS.zip")
        #expect(r?.version == "0.1.2")
        #expect(r?.assetURL?.absoluteString == "https://x/a.zip")
        #expect(r?.notes == "new things")
        #expect(r?.size == 2)
        #expect(ReleaseFeed.latest(from: feed, tagPrefix: "windows-", assetSuffix: "-Windows-x64.zip")?.version == "0.1.0")
        #expect(ReleaseFeed.latest(from: Data("nope".utf8), tagPrefix: "macos-", assetSuffix: ".zip") == nil)
    }

    @Test func offerRespectsSkippedVersion() {
        let r = ReleaseInfo(version: "0.1.3", notes: "", assetURL: nil, size: 0, page: nil)
        #expect(UpdatePolicy.shouldOffer(r, current: "0.1.2", skipped: ""))
        #expect(!UpdatePolicy.shouldOffer(r, current: "0.1.2", skipped: "0.1.3"))
        #expect(UpdatePolicy.shouldOffer(r, current: "0.1.2", skipped: "0.1.2"))
        #expect(!UpdatePolicy.shouldOffer(r, current: "0.1.3", skipped: ""))
    }

    @Test func checkSchedule() {
        let now = Date()
        #expect(UpdatePolicy.checkDue(now: now, lastSuccess: nil, lastAttempt: nil))
        #expect(!UpdatePolicy.checkDue(now: now, lastSuccess: now.addingTimeInterval(-3600), lastAttempt: now.addingTimeInterval(-3600)))
        #expect(UpdatePolicy.checkDue(now: now, lastSuccess: now.addingTimeInterval(-25 * 3600), lastAttempt: now.addingTimeInterval(-25 * 3600)))
        // 上次失败：30 分钟后再试
        #expect(!UpdatePolicy.checkDue(now: now, lastSuccess: now.addingTimeInterval(-3600), lastAttempt: now.addingTimeInterval(-600)))
        #expect(UpdatePolicy.checkDue(now: now, lastSuccess: now.addingTimeInterval(-3600), lastAttempt: now.addingTimeInterval(-2000)))
    }
}
