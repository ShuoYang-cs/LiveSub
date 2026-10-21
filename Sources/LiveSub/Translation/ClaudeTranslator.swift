import Foundation
import LiveSubCore

/// 通过本机 Claude Code CLI（使用用户自己的 Claude 登录额度）做翻译。
///
/// 保持一个长驻的 `claude -p --input-format stream-json` 进程：
/// - 省去每句话重新启动进程的时间；
/// - 同一会话里前面的句子自然成为上下文，术语前后一致；
/// - 每 `maxTurnsPerSession` 句换一个新进程，防止上下文无限变长拖慢速度，新会话第一句带上最近几句作为上下文。
final class ClaudeTranslator: Translator, @unchecked Sendable {
    struct Config {
        var executable: URL
        var model: String
        var systemPrompt: String
        var maxTurnsPerSession = 40
        var timeout: TimeInterval = 25
    }

    let name = "Claude"
    private let config: Config
    private let queue = DispatchQueue(label: "LiveSub.Claude")

    // 以下状态只在 queue 上访问。
    private var process: Process?
    private var stdin: FileHandle?
    private var lineBuffer = LineBuffer()
    private var stderrTail = ""
    private var turnsInSession = 0
    private var requestSerial = 0
    private var pending: Pending?

    private struct Pending {
        let id: Int
        var partial = ""
        var assistantText = ""
        let onPartial: @Sendable (String) -> Void
        let continuation: CheckedContinuation<String, Error>
    }

    init(config: Config) {
        self.config = config
    }

    /// 提前启动进程，第一句翻译就不用等进程启动。
    func warmUp() {
        queue.async { try? self.ensureProcess() }
    }

    func translate(_ text: String, context: ContextPairs, onPartial: @escaping @Sendable (String) -> Void) async throws -> String {
        try await request(newSessionMessage: Prompts.translationMessage(text: text, context: context),
                          ongoingMessage: text, onPartial: onPartial)
    }

    /// 通用的一问一答。会话是新开的（第一次，或进程重启、轮数用完换了新进程）时发 `newSessionMessage`，
    /// 否则发 `ongoingMessage`——新会话里 Claude 看不到之前的对话，需要把上下文补上。
    func request(newSessionMessage: String, ongoingMessage: String,
                 onPartial: @escaping @Sendable (String) -> Void) async throws -> String {
        try await withCheckedThrowingContinuation { continuation in
            queue.async {
                do {
                    if let old = self.pending {
                        // 理论上调用方是串行的；保险起见让旧请求失败而不是永远挂起。
                        self.pending = nil
                        old.continuation.resume(throwing: TranslatorError.timeout)
                    }
                    try self.ensureProcess()
                    let message = self.turnsInSession == 0 ? newSessionMessage : ongoingMessage
                    self.requestSerial += 1
                    let id = self.requestSerial
                    self.pending = Pending(id: id, onPartial: onPartial, continuation: continuation)
                    self.turnsInSession += 1
                    if ProcessInfo.processInfo.environment["LIVESUB_DEBUG_CLAUDE"] != nil {
                        FileHandle.standardError.write(Data("[claude #\(id) →] \(message)\n".utf8))
                    }
                    try self.stdin?.write(contentsOf: Data(ClaudeStream.userMessageLine(message).utf8))
                    self.queue.asyncAfter(deadline: .now() + self.config.timeout) {
                        guard self.pending?.id == id else { return }
                        self.finish(.failure(TranslatorError.timeout))
                        self.terminateProcess()
                    }
                } catch {
                    if self.pending?.id == self.requestSerial { self.pending = nil }
                    continuation.resume(throwing: error)
                    self.terminateProcess()
                }
            }
        }
    }

    func shutdown() {
        queue.sync {
            finish(.failure(CancellationError()))
            terminateProcess()
        }
    }

    // MARK: - 进程管理（queue 上）

    private func ensureProcess() throws {
        if let process, process.isRunning { return }
        let p = Process()
        p.executableURL = config.executable
        p.arguments = [
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--model", config.model,
            "--tools", "",
            "--setting-sources", "",
            "--strict-mcp-config",
            "--disable-slash-commands",
            "--no-session-persistence",
            "--system-prompt", config.systemPrompt,
        ]
        p.environment = ClaudeCLI.environment()
        p.currentDirectoryURL = ClaudeCLI.workingDirectory()

        let inPipe = Pipe(), outPipe = Pipe(), errPipe = Pipe()
        p.standardInput = inPipe
        p.standardOutput = outPipe
        p.standardError = errPipe

        outPipe.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            if data.isEmpty { handle.readabilityHandler = nil } // EOF
            guard let self else { return }
            self.queue.async { self.handleStdout(data, from: p) }
        }
        errPipe.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            if data.isEmpty { handle.readabilityHandler = nil }
            guard let self, !data.isEmpty else { return }
            self.queue.async {
                self.stderrTail = String((self.stderrTail + String(decoding: data, as: UTF8.self)).suffix(600))
            }
        }
        p.terminationHandler = { [weak self] proc in
            outPipe.fileHandleForReading.readabilityHandler = nil
            errPipe.fileHandleForReading.readabilityHandler = nil
            guard let self else { return }
            self.queue.async {
                guard self.process === proc else { return }
                self.process = nil
                self.stdin = nil
                self.turnsInSession = 0
                self.finish(.failure(TranslatorError.processExited(self.stderrTail.trimmingCharacters(in: .whitespacesAndNewlines))))
            }
        }

        try p.run()
        process = p
        stdin = inPipe.fileHandleForWriting
        lineBuffer = LineBuffer()
        stderrTail = ""
        turnsInSession = 0
    }

    private func terminateProcess() {
        guard let p = process else { return }
        process = nil
        try? stdin?.close()
        stdin = nil
        turnsInSession = 0
        if p.isRunning { p.terminate() }
    }

    private func handleStdout(_ data: Data, from proc: Process) {
        guard process === proc, !data.isEmpty else { return }
        for line in lineBuffer.append(data) {
            guard let event = ClaudeStream.parse(line: line) else { continue }
            switch event {
            case .textDelta(let delta):
                guard pending != nil else { continue }
                pending!.partial += delta
                pending!.onPartial(pending!.partial)
            case .assistantText(let text):
                pending?.assistantText += text
            case .result(let text, let isError):
                if isError {
                    finish(.failure(TranslatorError.claudeFailed(text)))
                } else {
                    let best = [text, pending?.assistantText ?? "", pending?.partial ?? ""]
                        .first { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty } ?? ""
                    if ProcessInfo.processInfo.environment["LIVESUB_DEBUG_CLAUDE"] != nil {
                        FileHandle.standardError.write(Data("[claude #\(pending?.id ?? -1) ←] result=\(text.debugDescription) partial=\((pending?.partial ?? "").debugDescription)\n".utf8))
                    }
                    finish(.success(best.trimmingCharacters(in: .whitespacesAndNewlines)))
                }
                if turnsInSession >= config.maxTurnsPerSession {
                    terminateProcess()
                }
            case .initialized, .other:
                break
            }
        }
    }

    private func finish(_ result: Result<String, Error>) {
        guard let p = pending else { return }
        pending = nil
        p.continuation.resume(with: result)
    }
}

/// 定位和调用本机 claude 命令的公共逻辑。
enum ClaudeCLI {
    static func locate(customPath: String) -> URL? {
        let fm = FileManager.default
        let custom = (customPath as NSString).expandingTildeInPath
        if !custom.isEmpty, fm.isExecutableFile(atPath: custom) {
            return URL(fileURLWithPath: custom)
        }
        let home = fm.homeDirectoryForCurrentUser.path
        let candidates = [
            "\(home)/.local/bin/claude",
            "/opt/homebrew/bin/claude",
            "/usr/local/bin/claude",
            "\(home)/.claude/local/claude",
            "\(home)/.npm-global/bin/claude",
        ]
        if let hit = candidates.first(where: { fm.isExecutableFile(atPath: $0) }) {
            return URL(fileURLWithPath: hit)
        }
        // 最后用登录 shell 查找（GUI 程序的 PATH 很短）。
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/zsh")
        p.arguments = ["-lc", "command -v claude"]
        let out = Pipe()
        p.standardOutput = out
        p.standardError = FileHandle.nullDevice
        guard (try? p.run()) != nil else { return nil }
        p.waitUntilExit()
        let path = String(decoding: out.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return fm.isExecutableFile(atPath: path) ? URL(fileURLWithPath: path) : nil
    }

    static func environment() -> [String: String] {
        // 用户在终端里通过环境变量登录（例如 ~/.zshrc 里的 CLAUDE_CODE_OAUTH_TOKEN）时，补上这些变量。
        var env = ShellEnvironment.merge(ProcessInfo.processInfo.environment, shell: shellEnvironment())
        // 如果本应用是从 Claude Code 的终端里启动的，去掉嵌套会话的标记。
        // 只去掉这几个；其他 CLAUDE_CODE_* 是用户自己的配置（例如通过 Bedrock、Vertex 使用），要保留。
        for key in ShellEnvironment.sessionMarkers {
            env.removeValue(forKey: key)
        }
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let extra = ["\(home)/.local/bin", "/opt/homebrew/bin", "/usr/local/bin"]
        env["PATH"] = (extra + [env["PATH"] ?? "/usr/bin:/bin"]).joined(separator: ":")
        env["MAX_THINKING_TOKENS"] = "0"
        env["DISABLE_AUTOUPDATER"] = "1"
        env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1"
        return env
    }

    private static let shellLock = NSLock()
    private static var cachedShellEnvironment: [String: String]?

    /// 用户终端（登录、交互式 shell）里的环境变量，读一次后缓存。读取最多等 5 秒，失败时为空。
    /// 会阻塞调用线程，不要在主线程上第一次调用。
    static func shellEnvironment() -> [String: String] {
        shellLock.withLock {
            if let cachedShellEnvironment { return cachedShellEnvironment }
            let env = captureShellEnvironment()
            cachedShellEnvironment = env
            return env
        }
    }

    /// 用户可能刚改了终端配置（例如刚加上登录令牌），下次启动 claude 时重新读取。
    static func reloadShellEnvironment() {
        shellLock.withLock { cachedShellEnvironment = nil }
    }

    private static func captureShellEnvironment() -> [String: String] {
        let fm = FileManager.default
        let shell = ProcessInfo.processInfo.environment["SHELL"].flatMap { fm.isExecutableFile(atPath: $0) ? $0 : nil } ?? "/bin/zsh"
        let p = Process()
        p.executableURL = URL(fileURLWithPath: shell)
        // 交互式才会读 ~/.zshrc。
        p.arguments = ["-l", "-i", "-c", ShellEnvironment.command]
        p.environment = ProcessInfo.processInfo.environment.filter { !ShellEnvironment.sessionMarkers.contains($0.key) }
        p.currentDirectoryURL = fm.homeDirectoryForCurrentUser
        let out = Pipe()
        p.standardInput = FileHandle.nullDevice
        p.standardOutput = out
        p.standardError = FileHandle.nullDevice
        guard (try? p.run()) != nil else { return [:] }
        // 终端配置里启动的后台程序可能一直占着输出，读取放到别的线程上，超时就放弃。
        let done = DispatchSemaphore(value: 0)
        nonisolated(unsafe) var data = Data()
        DispatchQueue.global(qos: .utility).async {
            data = out.fileHandleForReading.readDataToEndOfFile()
            done.signal()
        }
        if done.wait(timeout: .now() + 5) == .timedOut {
            p.terminate()
            appLog.notice("claude: reading the login shell environment timed out")
            return [:]
        }
        p.waitUntilExit()
        return ShellEnvironment.relevant(ShellEnvironment.parse(data))
    }

    /// 在空目录里运行，避免读到任何项目的 CLAUDE.md。
    static func workingDirectory() -> URL {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("LiveSub/claude-workdir", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }
}
