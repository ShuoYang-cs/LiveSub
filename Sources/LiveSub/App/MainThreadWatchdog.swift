import Foundation

/// 主线程卡住时自动留证据：后台每秒“敲”一下主线程，超过 5 秒没回应就记日志，
/// 并用系统自带的 /usr/bin/sample 把各线程调用栈存到 ~/Library/Logs/LiveSub/。
final class MainThreadWatchdog: @unchecked Sendable {
    static let shared = MainThreadWatchdog()

    private let queue = DispatchQueue(label: "LiveSub.watchdog")
    // 以下只在 queue 上访问。
    private var lastPong = Date()
    private var lastCheck = Date()
    private var reported = false
    private var timer: DispatchSourceTimer?

    func start() {
        queue.async {
            guard self.timer == nil else { return }
            let timer = DispatchSource.makeTimerSource(queue: self.queue)
            timer.schedule(deadline: .now() + 1, repeating: 1)
            timer.setEventHandler { [weak self] in self?.check() }
            timer.resume()
            self.timer = timer
        }
    }

    private func check() {
        // 电脑睡眠/合盖时所有线程都停了；醒来后这次检查会距离上次很久，不能当成主线程卡住。
        let now = Date()
        if now.timeIntervalSince(lastCheck) > 3 {
            lastPong = now
            reported = false
        }
        lastCheck = now
        DispatchQueue.main.async { [weak self] in
            self?.queue.async {
                self?.lastPong = Date()
                self?.reported = false
            }
        }
        let stalled = Date().timeIntervalSince(lastPong)
        guard stalled > 5, !reported else { return }
        reported = true
        appLog.fault("main thread stalled for \(Int(stalled), privacy: .public)s, sampling")
        let folder = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/LiveSub")
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd HH.mm.ss"
        let stamp = formatter.string(from: Date())
        let out = folder.appendingPathComponent("hang-\(stamp).txt")
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/sample")
        p.arguments = [String(ProcessInfo.processInfo.processIdentifier), "3", "-file", out.path]
        try? p.run()
    }
}
