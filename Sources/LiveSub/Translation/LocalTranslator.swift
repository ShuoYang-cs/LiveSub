import Darwin
import Foundation
import LiveSubCore

/// 本机翻译模型的文件：~/Library/Application Support/LiveSub/LocalModel/ 里的模型和 llama.cpp 运行程序。
enum LocalModelFiles {
    /// “模型服务”里这一栏的 id（选了还没下载时滚到这里）。
    static let serviceID = UUID(uuidString: "D0000000-0000-4000-8000-000000000000")!

    static var folder: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("LiveSub/LocalModel", isDirectory: true)
    }

    static var runtime: LocalTranslationModel.Runtime {
        #if arch(arm64)
        LocalTranslationModel.runtime(arm64: true)
        #else
        LocalTranslationModel.runtime(arm64: false)
        #endif
    }

    static var model: URL { folder.appendingPathComponent(LocalTranslationModel.fileName) }
    static var server: URL { folder.appendingPathComponent(runtime.folder, isDirectory: true).appendingPathComponent("llama-server") }
    static var hasRuntime: Bool { FileManager.default.isExecutableFile(atPath: server.path) }
    static var hasModel: Bool {
        let size = (try? FileManager.default.attributesOfItem(atPath: model.path)[.size] as? NSNumber)?.int64Value
        return size == LocalTranslationModel.bytes
    }
    static var isInstalled: Bool { hasRuntime && hasModel }
}

/// 本机的 llama-server：第一次翻译时启动（2–8 秒），没人用或闲置 10 分钟后关掉，App 退出时也关掉。
/// 只监听 127.0.0.1 上随机的空闲端口。
final class LocalModelServer: @unchecked Sendable {
    static let shared = LocalModelServer()
    private let lock = NSLock()
    // 以下只在 lock 内访问。
    private var process: Process?
    private var starting: Task<URL, Error>?
    private var users = 0
    private var inFlight = 0
    private var lastUse = Date()
    /// 上次启动失败的时间和原因：一分钟内直接报同样的错，不再每句都重新启动一次。
    private var lastFailure: (at: Date, error: Error)?

    private static let idleLimit: TimeInterval = 600
    static var logFile: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/LiveSub/llama-server.log")
    }

    /// 有翻译器在用：用完（`release`）且没有别的翻译器时关掉，省下约 1.4 GB 内存。
    func acquire() { lock.withLock { users += 1 } }

    func release() {
        let idle = lock.withLock { users = max(0, users - 1); return users == 0 }
        if idle { stop() }
    }

    /// 服务地址；还没启动时启动并等它加载好模型。
    func baseURL() async throws -> URL {
        let task = try lock.withLock { () throws -> Task<URL, Error> in
            if let starting, process?.isRunning ?? true { return starting }
            if let lastFailure, Date().timeIntervalSince(lastFailure.at) < 60 { throw lastFailure.error }
            let task = Task { try await self.launch() }
            starting = task
            return task
        }
        do {
            let url = try await task.value
            lock.withLock { lastFailure = nil }
            return url
        } catch {
            // 进程一启动就退出时，退出回调可能已经先把 `starting` 清掉了，所以不管它还在不在都记下失败。
            lock.withLock {
                if starting == task { starting = nil }
                if !(error is CancellationError) { lastFailure = (Date(), error) }
            }
            throw error
        }
    }

    /// 下载好、或用户点“测试”时重新试，不等一分钟。
    func clearFailure() { lock.withLock { lastFailure = nil } }

    /// 一次请求的开始和结束：有请求在进行时不会因为闲置被关掉。
    func begin() { lock.withLock { inFlight += 1; lastUse = Date() } }
    func end() { lock.withLock { inFlight -= 1; lastUse = Date() } }

    func stop() {
        let old = lock.withLock { () -> Process? in
            let old = process
            process = nil
            starting = nil
            return old
        }
        guard let old, old.isRunning else { return }
        old.terminate()
        appLog.notice("local model server stopped")
    }

    private func launch() async throws -> URL {
        guard LocalModelFiles.isInstalled else { throw TranslatorError.localModelUnavailable(L("还没有下载混元翻译模型。")) }
        let port = try Self.freePort()
        let runtime = LocalModelFiles.runtime
        let p = Process()
        // 通过 sh 守着：LiveSub 崩溃或被强制退出时（来不及 `stop`），两秒内把 llama-server 也关掉，
        // 不让它在后台一直占着 1.4 GB 内存。`terminate()` 发给 sh，由它转给 llama-server。
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.currentDirectoryURL = LocalModelFiles.folder
        // 两个槽位：Google、Apple、本机模型都是同时翻两句（`SubtitleController.runWorker`）。
        p.arguments = ["-c", Self.guardScript, "sh", LocalModelFiles.server.path,
                       "-m", LocalModelFiles.model.path, "--host", "127.0.0.1", "--port", String(port),
                       "-c", "4096", "-np", "2", "--jinja", "--no-webui", "-ngl", runtime.usesGPU ? "99" : "0"]
        try? FileManager.default.createDirectory(at: Self.logFile.deletingLastPathComponent(), withIntermediateDirectories: true)
        FileManager.default.createFile(atPath: Self.logFile.path, contents: nil)
        if let log = try? FileHandle(forWritingTo: Self.logFile) {
            p.standardOutput = log
            p.standardError = log
        }
        p.terminationHandler = { [weak self] proc in
            appLog.notice("local model server exited (\(proc.terminationStatus))")
            self?.lock.withLock {
                if self?.process === proc {
                    self?.process = nil
                    self?.starting = nil
                }
            }
        }
        try p.run()
        lock.withLock { process = p; lastUse = Date() }
        appLog.notice("local model server starting on port \(port)")

        let base = URL(string: "http://127.0.0.1:\(port)")!
        let started = Date()
        // 加载模型时 /health 返回 503，加载好了才是 200。
        while Date().timeIntervalSince(started) < 90 {
            guard p.isRunning else {
                throw TranslatorError.localModelUnavailable(Self.logTail())
            }
            var request = URLRequest(url: base.appendingPathComponent("health"), timeoutInterval: 2)
            request.httpMethod = "GET"
            if let (_, response) = try? await URLSession.shared.data(for: request),
               (response as? HTTPURLResponse)?.statusCode == 200 {
                appLog.notice("local model server ready in \(String(format: "%.1f", Date().timeIntervalSince(started)), privacy: .public)s")
                watchIdle(p)
                return base
            }
            try await Task.sleep(for: .milliseconds(250))
        }
        p.terminate()
        throw TranslatorError.localModelUnavailable(L("启动超时。"))
    }

    /// 闲置太久（例如停了字幕没退出 App）就关掉；下次翻译时再启动。
    private func watchIdle(_ p: Process) {
        Task.detached { [weak self] in
            while true {
                try? await Task.sleep(for: .seconds(60))
                guard let self else { return }
                let (current, idle) = self.lock.withLock {
                    (self.process === p, self.inFlight == 0 && Date().timeIntervalSince(self.lastUse) > Self.idleLimit)
                }
                guard current else { return }
                if idle {
                    appLog.notice("local model server idle")
                    self.stop()
                    return
                }
            }
        }
    }

    /// 启动 llama-server（"$@"），LiveSub（$PPID）不在了、或收到 TERM 时关掉它；llama-server 自己退出时 sh 也退出。
    private static let guardScript = """
        "$@" &
        child=$!
        trap 'kill $child 2>/dev/null; wait $child; exit 0' TERM INT
        while kill -0 $PPID 2>/dev/null && kill -0 $child 2>/dev/null; do sleep 2; done
        kill $child 2>/dev/null
        wait $child
        """

    private static func freePort() throws -> Int {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { throw TranslatorError.localModelUnavailable(L("没有可用的端口。")) }
        defer { close(fd) }
        var addr = sockaddr_in()
        addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = 0
        addr.sin_addr.s_addr = inet_addr("127.0.0.1")
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let ok = withUnsafeMutablePointer(to: &addr) { ptr in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(fd, $0, length) == 0 && getsockname(fd, $0, &length) == 0
            }
        }
        guard ok else { throw TranslatorError.localModelUnavailable(L("没有可用的端口。")) }
        return Int(UInt16(bigEndian: addr.sin_port))
    }

    /// 启动失败时日志的最后几行（排查用）。
    private static func logTail() -> String {
        let text = (try? String(contentsOf: logFile, encoding: .utf8)) ?? ""
        return text.split(whereSeparator: \.isNewline).suffix(3).joined(separator: " ")
    }
}

/// 用本机的混元翻译模型翻译：每句带上前两句原文作背景，流式输出。
final class LocalTranslator: Translator, @unchecked Sendable {
    let name = L("混元翻译")
    private let source: LanguageOption
    private let target: LanguageOption
    private let server = LocalModelServer.shared
    private let lock = NSLock()
    private var released = false

    init(source: LanguageOption, target: LanguageOption) {
        self.source = source
        self.target = target
        server.acquire()
    }

    /// 提前启动服务、加载模型，第一句不用等。
    func warmUp() {
        let server = server
        Task.detached { _ = try? await server.baseURL() }
    }

    /// 等服务启动好（测试按钮计时前用）。
    func prepare() async throws {
        _ = try await server.baseURL()
    }

    func translate(_ text: String, context: ContextPairs, onPartial: @escaping @Sendable (String) -> Void) async throws -> String {
        let base = try await server.baseURL()
        server.begin()
        defer { server.end() }
        var request = URLRequest(url: base.appendingPathComponent("v1/chat/completions"), timeoutInterval: 20)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let prompt = LocalTranslationModel.prompt(text, context: context.map(\.source), source: source, target: target)
        request.httpBody = LocalTranslationModel.requestBody(prompt: prompt)

        let (bytes, response) = try await URLSession.shared.bytes(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 200 else {
            var body = Data()
            for try await byte in bytes.prefix(4096) { body.append(byte) }
            throw TranslatorError.localModelFailed(ChatAPI.errorMessage(fromBody: body, status: status))
        }
        var result = ""
        for try await line in bytes.lines {
            try Task.checkCancellation()
            switch ChatAPI.parse(line: line, format: .openAI) {
            case .text(let delta):
                result += delta
                onPartial(result)
            case .done:
                return result.trimmingCharacters(in: .whitespacesAndNewlines)
            case .error(let message):
                throw TranslatorError.localModelFailed(message)
            case nil:
                continue
            }
        }
        return result.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    func shutdown() {
        let first = lock.withLock { () -> Bool in
            defer { released = true }
            return !released
        }
        if first { server.release() }
    }
}
