import CryptoKit
import Foundation
import LiveSubCore
import Observation

/// 下载、删除本机翻译模型（“模型服务”里的“混元翻译”一栏）。
/// 先下 llama.cpp 运行程序（十几 MB），再下模型（1.1 GB，Hugging Face 不通时换镜像），都按 SHA-256 校验。
@MainActor @Observable
final class LocalModelStore {
    static let shared = LocalModelStore()

    enum Phase: Equatable {
        case idle, downloading, verifying
        case failed(String)
    }

    private(set) var phase: Phase = .idle
    private(set) var installed = LocalModelFiles.isInstalled
    /// 已经下载的字节数（运行程序 + 模型）。
    private(set) var received: Int64 = 0
    var total: Int64 { LocalModelFiles.runtime.bytes + LocalTranslationModel.bytes }
    @ObservationIgnored private var task: Task<Void, Never>?

    func download() {
        guard task == nil else { return }
        phase = .downloading
        received = 0
        task = Task {
            do {
                try await install()
                installed = LocalModelFiles.isInstalled
                phase = .idle
                LocalModelServer.shared.clearFailure()
                appLog.notice("local model installed")
            } catch is CancellationError {
                phase = .idle
            } catch {
                phase = .failed(error.localizedDescription)
                appLog.error("local model download failed: \(error.localizedDescription, privacy: .public)")
            }
            task = nil
        }
    }

    func cancel() {
        task?.cancel()
    }

    /// 按磁盘上的文件重新判断（文件可能被手动删掉、被安全软件隔离）。
    func refresh() {
        guard task == nil else { return }
        installed = LocalModelFiles.isInstalled
    }

    func delete() {
        cancel()
        LocalModelServer.shared.stop()
        try? FileManager.default.removeItem(at: LocalModelFiles.folder)
        installed = false
        phase = .idle
        received = 0
    }

    private func install() async throws {
        let folder = LocalModelFiles.folder
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let runtime = LocalModelFiles.runtime
        if !LocalModelFiles.hasRuntime {
            let archive = try await fetch(runtime.url, sha256: runtime.sha256, offset: 0)
            defer { try? FileManager.default.removeItem(at: archive) }
            try await Self.extract(archive, into: folder)
            guard LocalModelFiles.hasRuntime else { throw Failure(L("运行程序解压后找不到 llama-server。")) }
        }
        received = runtime.bytes
        guard !LocalModelFiles.hasModel else { return }
        var lastError: Error = Failure(L("下载失败。"))
        for url in LocalTranslationModel.downloadURLs {
            do {
                let file = try await fetch(url, sha256: LocalTranslationModel.sha256, offset: runtime.bytes)
                try? FileManager.default.removeItem(at: LocalModelFiles.model)
                try FileManager.default.moveItem(at: file, to: LocalModelFiles.model)
                return
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                lastError = error
                appLog.notice("local model download from \(url.host ?? "", privacy: .public) failed: \(error.localizedDescription, privacy: .public)")
                received = runtime.bytes
                phase = .downloading
            }
        }
        throw lastError
    }

    /// 下载到记录文件夹旁边的临时文件，校验通过才返回。
    private func fetch(_ url: URL, sha256: String, offset: Int64) async throws -> URL {
        let target = LocalModelFiles.folder.appendingPathComponent(".download-" + url.lastPathComponent)
        let downloader = FileDownloader(target: target) { [weak self] written in
            Task { @MainActor in
                guard let self, self.phase == .downloading else { return }
                self.received = offset + written
            }
        }
        try await withTaskCancellationHandler {
            try await downloader.run(url)
        } onCancel: {
            downloader.cancel()
        }
        try Task.checkCancellation()
        phase = .verifying
        let digest = try await Task.detached(priority: .userInitiated) { try Self.sha256(of: target) }.value
        guard digest == sha256 else {
            try? FileManager.default.removeItem(at: target)
            throw Failure(L("下载的文件校验失败，请重试。"))
        }
        phase = .downloading
        return target
    }

    private nonisolated static func sha256(of url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 8 << 20), !chunk.isEmpty {
            hasher.update(data: chunk)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    private nonisolated static func extract(_ archive: URL, into folder: URL) async throws {
        try await Task.detached {
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/usr/bin/tar")
            p.arguments = ["-xzf", archive.path, "-C", folder.path]
            try p.run()
            p.waitUntilExit()
            guard p.terminationStatus == 0 else { throw Failure(L("运行程序解压失败。")) }
        }.value
    }

    struct Failure: LocalizedError {
        let message: String
        init(_ message: String) { self.message = message }
        var errorDescription: String? { message }
    }
}

/// 一个文件的下载：报告进度，完成后移到 `target`。
private final class FileDownloader: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
    private let target: URL
    private let onProgress: @Sendable (Int64) -> Void
    private let lock = NSLock()
    // 以下只在 lock 内访问。
    private var continuation: CheckedContinuation<Void, Error>?
    private var session: URLSession?
    private var cancelled = false

    init(target: URL, onProgress: @escaping @Sendable (Int64) -> Void) {
        self.target = target
        self.onProgress = onProgress
    }

    func run(_ url: URL) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            let session = URLSession(configuration: .default, delegate: self, delegateQueue: nil)
            let start = lock.withLock { () -> Bool in
                guard !cancelled else { return false }
                self.continuation = continuation
                self.session = session
                return true
            }
            guard start else {
                continuation.resume(throwing: CancellationError())
                return
            }
            session.downloadTask(with: url).resume()
        }
    }

    func cancel() {
        let session = lock.withLock { () -> URLSession? in
            cancelled = true
            return self.session
        }
        session?.invalidateAndCancel()
    }

    private func finish(_ result: Result<Void, Error>) {
        let (continuation, session) = lock.withLock { () -> (CheckedContinuation<Void, Error>?, URLSession?) in
            defer { self.continuation = nil }
            return (self.continuation, self.session)
        }
        session?.finishTasksAndInvalidate()
        continuation?.resume(with: result)
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                    totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        onProgress(totalBytesWritten)
    }

    /// 临时文件只在这个回调里有效，必须马上移走。
    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        let status = (downloadTask.response as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 200 else {
            finish(.failure(LocalModelStore.Failure(L("下载失败（HTTP %d）。", status))))
            return
        }
        do {
            try? FileManager.default.removeItem(at: target)
            try FileManager.default.moveItem(at: location, to: target)
            finish(.success(()))
        } catch {
            finish(.failure(error))
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let error else { return }
        let cancelled = lock.withLock { self.cancelled }
        finish(.failure(cancelled ? CancellationError() : error))
    }
}
