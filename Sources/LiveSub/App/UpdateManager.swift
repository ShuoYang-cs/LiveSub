import AppKit
import Foundation
import LiveSubCore

/// 检查更新、下载并安装新版本（和 neu-helper 一样：让下载好的新版自己来装自己，不用脚本）。
///
/// 流程：问 GitHub 有没有比现在新的发布 → 用户点“更新” → 下载 zip、用 ditto 解到缓存目录、核对签名 →
/// 启动新版的程序 `--apply-update <装在哪> <本进程 pid>`，本进程退出 → 新进程等旧进程退出，
/// 把旧的 .app 改名成 .old、新的挪到位，再打开它（`UpdateApplier`）。失败就把旧的改回来。
/// 下载用 URLSession，文件不带“从网上下载”的隔离标记，所以更新之后不用再点“仍要打开”。
@MainActor @Observable
final class UpdateManager {
    static let shared = UpdateManager()

    enum Phase: Equatable {
        case idle
        case available(ReleaseInfo)
        case downloading(Double)
        case installing
        case failed(String)
    }

    private(set) var phase: Phase = .idle
    /// 用户手动点了“检查更新”后的结果（“已是最新版本”之类）；自动检查不写。
    private(set) var statusText = ""
    private(set) var checking = false

    @ObservationIgnored private let defaults = UserDefaults.standard
    var autoCheck: Bool { didSet { defaults.set(autoCheck, forKey: "autoCheckUpdates") } }
    private var skippedVersion: String { get { defaults.string(forKey: "skippedVersion") ?? "" } set { defaults.set(newValue, forKey: "skippedVersion") } }
    @ObservationIgnored private var timer: Timer?

    static let repo = "ShuoYang-cs/LiveSub"
    static var currentVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
    }

    private init() {
        autoCheck = defaults.object(forKey: "autoCheckUpdates") as? Bool ?? true
    }

    /// 启动时调用：先核对上次更新成没成，再按计划检查；之后每小时看一眼是不是该检查了（电脑睡眠也不会漏）。
    func start() {
        verifyPreviousUpdate()
        cleanUpLeftovers()
        checkIfDue()
        timer = Timer.scheduledTimer(withTimeInterval: 3600, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.checkIfDue() }
        }
    }

    private func checkIfDue() {
        guard autoCheck, !checking, phase == .idle else { return }
        let last = { (key: String) -> Date? in
            let t = self.defaults.double(forKey: key)
            return t > 0 ? Date(timeIntervalSince1970: t) : nil
        }
        guard UpdatePolicy.checkDue(now: Date(), lastSuccess: last("lastUpdateSuccess"), lastAttempt: last("lastUpdateAttempt")) else { return }
        Task { await check(manual: false) }
    }

    func checkNow() { Task { await check(manual: true) } }

    private func check(manual: Bool) async {
        guard !checking else { return }
        if case .downloading = phase { return }
        if phase == .installing { return }
        checking = true
        if manual { statusText = L("正在检查…") }
        defaults.set(Date().timeIntervalSince1970, forKey: "lastUpdateAttempt")
        defer { checking = false }
        var request = URLRequest(url: URL(string: "https://api.github.com/repos/\(Self.repo)/releases?per_page=30")!, timeoutInterval: 20)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("LiveSub/\(Self.currentVersion)", forHTTPHeaderField: "User-Agent")
        guard let (data, response) = try? await URLSession.shared.data(for: request),
              (response as? HTTPURLResponse)?.statusCode == 200 else {
            if manual { statusText = L("无法连接 GitHub，请稍后再试。") }
            return
        }
        defaults.set(Date().timeIntervalSince1970, forKey: "lastUpdateSuccess")
        guard let release = ReleaseFeed.latest(from: data, tagPrefix: "macos-", assetSuffix: "-macOS.zip") else {
            if manual { statusText = L("已是最新版本") }
            return
        }
        let newer = AppVersion.isNewer(release.version, than: Self.currentVersion)
        if manual {
            // 手动检查：用户主动问了，跳过过的版本也告诉他。
            if newer { skippedVersion = ""; phase = .available(release); statusText = "" }
            else { statusText = L("已是最新版本") }
        } else if UpdatePolicy.shouldOffer(release, current: Self.currentVersion, skipped: skippedVersion) {
            phase = .available(release)
        }
        // 自检：不点界面直接装（LIVESUB_TEST_AUTOUPDATE=1）。
        if ProcessInfo.processInfo.environment["LIVESUB_TEST_AUTOUPDATE"] == "1", case .available = phase { install() }
    }

    func later() { if case .available = phase { phase = .idle } }

    func skip() {
        if case .available(let release) = phase { skippedVersion = release.version }
        phase = .idle
    }

    func dismissFailure() { if case .failed = phase { phase = .idle } }

    func install() {
        guard case .available(let release) = phase else { return }
        Task { await install(release) }
    }

    private func install(_ release: ReleaseInfo) async {
        guard let asset = release.assetURL else { return openPage(release, reason: L("这一版没有可以直接安装的文件。")) }
        let target = Bundle.main.bundleURL
        if let problem = UpdateApplier.installProblem(target: target) { return openPage(release, reason: problem) }
        phase = .downloading(0)
        do {
            let staged = try await UpdateApplier.download(asset, version: release.version) { [weak self] fraction in
                Task { @MainActor in if case .downloading = self?.phase { self?.phase = .downloading(fraction) } }
            }
            phase = .installing
            let helper = staged.appendingPathComponent("Contents/MacOS/LiveSub")
            let process = Process()
            process.executableURL = helper
            process.arguments = ["--apply-update", target.path, String(ProcessInfo.processInfo.processIdentifier)]
            process.standardOutput = FileHandle.nullDevice
            process.standardError = FileHandle.nullDevice
            defaults.set(release.version, forKey: "updatePendingVersion")
            defaults.synchronize()
            try process.run()
            appLog.notice("update: handing over to \(release.version, privacy: .public)")
            // 给安装进程一点时间起来，然后退出，让它换文件。
            try? await Task.sleep(for: .milliseconds(500))
            NSApp.terminate(nil)
        } catch {
            defaults.removeObject(forKey: "updatePendingVersion")
            appLog.error("update failed: \(error.localizedDescription, privacy: .public)")
            phase = .failed(L("更新失败：%@", error.localizedDescription))
        }
    }

    /// 装不上（装在只读位置、签名对不上……）时退回手动下载：打开发布页。
    private func openPage(_ release: ReleaseInfo, reason: String) {
        if let page = release.page { NSWorkspace.shared.open(page) }
        phase = .failed(L("%@ 已为你打开下载页，请手动下载安装。", reason))
    }

    /// 上一次点了“更新”，这次启动的版本还是旧的：照实告诉用户。
    private func verifyPreviousUpdate() {
        guard let pending = defaults.string(forKey: "updatePendingVersion") else { return }
        defaults.removeObject(forKey: "updatePendingVersion")
        if AppVersion.isNewer(pending, than: Self.currentVersion) {
            phase = .failed(L("上次更新到 %@ 没有成功，现在还是 %@。日志在 ~/Library/Logs/LiveSub/update.log，可以到下载页手动安装。", pending, Self.currentVersion))
        }
    }

    /// 更新留下的旧版和缓存。
    private func cleanUpLeftovers() {
        let old = URL(fileURLWithPath: Bundle.main.bundlePath + ".old")
        DispatchQueue.global(qos: .utility).async {
            try? FileManager.default.removeItem(at: old)
            try? FileManager.default.removeItem(at: UpdateApplier.cacheFolder)
        }
    }
}
