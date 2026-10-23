import Foundation
import LiveSubCore

/// Google 翻译（免费网页接口，不需要密钥，需要联网）。
final class GoogleTranslator: Translator, @unchecked Sendable {
    let name = L("Google 翻译")
    private let source: String
    private let target: String
    private let session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        // Google 平时 1.5 秒内就回；超时设短一点，一个接口卡住时能尽快换另一个。
        config.timeoutIntervalForRequest = 4
        return URLSession(configuration: config)
    }()
    private let lock = NSLock()
    /// 只在 lock 内访问。
    private var rotation = GoogleEndpointRotation()

    /// 一个接口的一次请求失败。`temporary`：限流或服务端出错，换接口、过一会儿再用它。
    private struct Failure: Error {
        let message: String
        let temporary: Bool
    }

    init(source: LanguageOption, target: LanguageOption) {
        self.source = source.translationCode
        self.target = target.translationCode
    }

    /// 每句单独翻译，不带上下文：试过把前两句原文一起发（LiveCaptions-Translator 的做法），
    /// 2026-10-22 实测 8 句里 7 句译文完全一样、1 句反而错了，Google 免费接口基本不利用上下文。
    ///
    /// 限流时：先换另一个接口（被限流的冷却 60 秒）；两个都不行就等 1–4 秒再各试一次，
    /// 还不行才算这句失败，由调用方改用 Apple 本地翻译（装了语言包时）。
    func translate(_ text: String, context: ContextPairs, onPartial: @escaping @Sendable (String) -> Void) async throws -> String {
        var last = Failure(message: L("无法识别的回复"), temporary: false)
        for attempt in 0..<2 {
            for endpoint in lock.withLock({ rotation.order() }) {
                do {
                    let result = try await request(text, endpoint: endpoint)
                    lock.withLock { rotation.markSuccess(endpoint) }
                    return result
                } catch let failure as Failure {
                    last = failure
                    if failure.temporary {
                        lock.withLock { rotation.markLimited(endpoint) }
                        appLog.notice("google \(String(describing: endpoint), privacy: .public) unavailable: \(failure.message, privacy: .public)")
                    }
                }
            }
            guard attempt == 0 else { break }
            let wait = lock.withLock { rotation.nextBackoff() }
            try await Task.sleep(for: .seconds(wait))
        }
        throw TranslatorError.googleFailed(last.message)
    }

    private func request(_ text: String, endpoint: GoogleTranslate.Endpoint) async throws -> String {
        // 测试用：LIVESUB_DEBUG_GOOGLE_FAIL=gtx（或 dictionaryExtension、all）假装这个接口被限流。
        if let fail = ProcessInfo.processInfo.environment["LIVESUB_DEBUG_GOOGLE_FAIL"],
           fail == "all" || fail == String(describing: endpoint) {
            throw Failure(message: "HTTP 429", temporary: true)
        }
        let url = GoogleTranslate.requestURL(text: text, source: source, target: target, endpoint: endpoint)
        let data: Data, response: URLResponse
        do {
            (data, response) = try await session.data(from: url)
        } catch {
            // 断网、超时：另一个接口多半也一样，但试一下不费事。
            throw Failure(message: error.localizedDescription, temporary: false)
        }
        if let http = response as? HTTPURLResponse, http.statusCode != 200 {
            throw Failure(message: "HTTP \(http.statusCode)", temporary: GoogleTranslate.isTemporaryFailure(status: http.statusCode))
        }
        do {
            return try GoogleTranslate.parse(data, endpoint: endpoint)
        } catch {
            throw Failure(message: L("无法识别的回复"), temporary: false)
        }
    }

    /// 先和两个接口都建立好连接（第一次请求要多花约 0.4 秒握手），第一句字幕、换接口时都不用等。
    func warmUp() {
        for endpoint in GoogleTranslate.Endpoint.allCases {
            Task { _ = try? await request("Hello", endpoint: endpoint) }
        }
    }

    func shutdown() { session.invalidateAndCancel() }
}
