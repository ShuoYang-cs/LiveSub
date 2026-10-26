import Foundation

/// 更新的“动手”部分：下载解压，以及新版自己来装自己时的换文件。
enum UpdateApplier {
    struct Failure: LocalizedError {
        let message: String
        var errorDescription: String? { message }
        init(_ message: String) { self.message = message }
    }

    static var cacheFolder: URL {
        FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0].appendingPathComponent("LiveSub/update")
    }

    static var logURL: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/LiveSub/update.log")
    }

    /// 这个 App 能不能就地更新：必须是装好的 .app，所在的文件夹要能写（在磁盘映像里直接运行的不行）。
    static func installProblem(target: URL) -> String? {
        guard target.pathExtension == "app" else { return L("当前不是从“应用程序”里运行的 App。") }
        let folder = target.deletingLastPathComponent().path
        guard FileManager.default.isWritableFile(atPath: folder) else {
            return L("“%@”没有写入权限（如果是在磁盘映像里直接打开的，请先拖进“应用程序”）。", folder)
        }
        return nil
    }

    // MARK: 下载

    /// 下载 zip，解压，核对签名，返回新的 LiveSub.app（在缓存目录里）。
    static func download(_ url: URL, version: String, progress: @escaping @Sendable (Double) -> Void) async throws -> URL {
        let fm = FileManager.default
        let folder = cacheFolder.appendingPathComponent(version)
        try? fm.removeItem(at: folder)
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        let zip = folder.appendingPathComponent("LiveSub.zip")

        // 自检：LIVESUB_TEST_UPDATE_ZIP=本地 zip 代替下载（测换文件那一步）。
        let source = ProcessInfo.processInfo.environment["LIVESUB_TEST_UPDATE_ZIP"].map { URL(fileURLWithPath: $0) } ?? url
        let (bytes, response) = try await URLSession.shared.bytes(from: source)
        if !source.isFileURL { guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw Failure(L("下载失败。")) } }
        let total = Double(response.expectedContentLength)
        fm.createFile(atPath: zip.path, contents: nil)
        let handle = try FileHandle(forWritingTo: zip)
        defer { try? handle.close() }
        var buffer = Data(), received = 0.0
        for try await byte in bytes {
            buffer.append(byte)
            if buffer.count >= 64 * 1024 {
                try handle.write(contentsOf: buffer)
                received += Double(buffer.count)
                buffer.removeAll(keepingCapacity: true)
                if total > 0 { progress(min(received / total, 1)) }
            }
        }
        try handle.write(contentsOf: buffer)
        try handle.close()

        // 用 ditto 解（保留签名和扩展属性），不用 zip 库。
        let extracted = folder.appendingPathComponent("extracted")
        try run("/usr/bin/ditto", ["-x", "-k", zip.path, extracted.path], failure: L("解压失败。"))
        let app = extracted.appendingPathComponent("LiveSub.app")
        guard fm.fileExists(atPath: app.path) else { throw Failure(L("下载的文件里没有 LiveSub.app。")) }
        try run("/usr/bin/codesign", ["--verify", "--strict", app.path], failure: L("新版的签名无效。"))
        // 签名身份必须和现在的一样：换了证书，系统会把新版当成另一个 App，麦克风、录音权限要重新授权。
        if designatedRequirement(Bundle.main.bundleURL) != designatedRequirement(app) {
            throw Failure(L("新版的签名和现在的不一致，不能自动更新。"))
        }
        return app
    }

    private static func designatedRequirement(_ app: URL) -> String {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/codesign")
        p.arguments = ["-d", "-r-", app.path]
        let out = Pipe()
        p.standardOutput = out
        p.standardError = FileHandle.nullDevice
        try? p.run()
        let data = out.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return String(decoding: data, as: UTF8.self).split(separator: "\n").first { $0.contains("designated") }.map(String.init) ?? "?"
    }

    private static func run(_ tool: String, _ arguments: [String], failure: String) throws {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: tool)
        p.arguments = arguments
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        try p.run()
        p.waitUntilExit()
        if p.terminationStatus != 0 { throw Failure(failure) }
    }

    // MARK: 换文件（新版的程序以 --apply-update 启动时跑）

    /// `target` 是要换掉的旧 .app，`pid` 是旧进程；自己所在的 .app（缓存目录里）就是新版。返回退出码。
    static func apply(target: URL, waitFor pid: Int32) -> Int32 {
        let fm = FileManager.default
        let staged = Bundle.main.bundleURL
        let old = URL(fileURLWithPath: target.path + ".old")
        func say(_ text: String) {
            try? fm.createDirectory(at: logURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            let line = "\(Date()) \(text)\n"
            if let handle = try? FileHandle(forWritingTo: logURL) {
                handle.seekToEndOfFile(); handle.write(Data(line.utf8)); try? handle.close()
            } else {
                try? Data(line.utf8).write(to: logURL)
            }
        }
        say("apply-update: \(staged.path) -> \(target.path), waiting for pid \(pid)")
        // 等旧进程退出（最多 30 秒）。
        for _ in 0..<300 where kill(pid, 0) == 0 { usleep(100_000) }
        if kill(pid, 0) == 0 { say("old process still running, giving up"); return 1 }

        try? fm.removeItem(at: old)
        do {
            try fm.moveItem(at: target, to: old)
        } catch {
            say("cannot move old app aside: \(error.localizedDescription)")
            reopen(target)
            return 1
        }
        do {
            try fm.moveItem(at: staged, to: target)
        } catch {
            say("cannot move new app into place: \(error.localizedDescription); rolling back")
            try? fm.removeItem(at: target)
            try? fm.moveItem(at: old, to: target)
            reopen(target)
            return 1
        }
        // 清掉可能带上的隔离标记。
        _ = try? run("/usr/bin/xattr", ["-cr", target.path], failure: "")
        say("installed, launching")
        reopen(target)
        return 0
    }

    private static func reopen(_ app: URL) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        p.arguments = [app.path]
        try? p.run()
        p.waitUntilExit()
    }
}
