import Foundation

/// Google 翻译的免费网页接口：不需要 API 密钥，也没有官方保证，短时间请求太多会被限流（HTTP 429）。
/// 有两个接口（`Endpoint`），一个被限流时换另一个（`GoogleEndpointRotation`）。
public enum GoogleTranslate {
    public enum Endpoint: CaseIterable, Sendable {
        /// translate.googleapis.com，client=gtx：回复里有逐段译文。
        case gtx
        /// clients5.google.com，Chrome 词典扩展用的接口（LiveCaptions-Translator 用的就是它）：回复只有译文。
        case dictionaryExtension
    }

    /// Google 的语言代码：简繁中文用地区写法，其余和 Apple Translation 相同。
    public static func languageCode(_ translationCode: String) -> String {
        switch translationCode {
        case "zh-Hans": "zh-CN"
        case "zh-Hant": "zh-TW"
        default: translationCode
        }
    }

    public static func requestURL(text: String, source: String, target: String, endpoint: Endpoint = .gtx) -> URL {
        let base: String
        var items: [URLQueryItem]
        switch endpoint {
        case .gtx:
            base = "https://translate.googleapis.com/translate_a/single"
            items = [URLQueryItem(name: "client", value: "gtx"), URLQueryItem(name: "dt", value: "t")]
        case .dictionaryExtension:
            base = "https://clients5.google.com/translate_a/t"
            items = [URLQueryItem(name: "client", value: "dict-chrome-ex")]
        }
        items += [
            URLQueryItem(name: "sl", value: languageCode(source)),
            URLQueryItem(name: "tl", value: languageCode(target)),
            URLQueryItem(name: "q", value: text),
        ]
        var components = URLComponents(string: base)!
        components.queryItems = items
        // URLComponents 不转义 “+”，服务器会把它当成空格。
        components.percentEncodedQuery = components.percentEncodedQuery?.replacingOccurrences(of: "+", with: "%2B")
        return components.url!
    }

    /// 这个状态码说明接口暂时不可用（限流或服务端出错），应该换接口，而不是放弃这句。
    public static func isTemporaryFailure(status: Int) -> Bool {
        status == 429 || status >= 500
    }

    public struct ParseError: Error {}

    public static func parse(_ data: Data, endpoint: Endpoint = .gtx) throws -> String {
        guard let root = try JSONSerialization.jsonObject(with: data) as? [Any] else { throw ParseError() }
        let text: String
        switch endpoint {
        case .gtx:
            // `[[[译文, 原文, …], …], …]`：第一项是逐段译文，拼起来就是整句。
            guard let segments = root.first as? [Any] else { throw ParseError() }
            text = segments.compactMap { ($0 as? [Any])?.first as? String }.joined()
        case .dictionaryExtension:
            // 指定了源语言时是 `["译文"]`，自动识别语言时是 `[["译文", "en"]]`。
            text = (root.first as? String) ?? ((root.first as? [Any])?.first as? String) ?? ""
        }
        guard !text.isEmpty else { throw ParseError() }
        return text.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

/// 两个 Google 接口轮换：被限流的接口冷却一段时间再用；都失败时等一会儿再试，等待时间按连续失败次数翻倍。
/// （其他项目只是报错或让用户换接口，2026-10-22 调研 LiveCaptions-Translator、Easydict、py-googletrans、pot-desktop。）
public struct GoogleEndpointRotation: Sendable {
    public var cooldown: TimeInterval = 60
    public var maxBackoff: TimeInterval = 4
    private var coolingUntil: [GoogleTranslate.Endpoint: Date] = [:]
    private var consecutiveFailures = 0

    public init() {}

    /// 这一次按什么顺序试：没在冷却的在前（gtx 优先），冷却中的按谁先结束排在后面（都在冷却时也只能试它们）。
    public func order(now: Date = Date()) -> [GoogleTranslate.Endpoint] {
        let all = GoogleTranslate.Endpoint.allCases
        let ready = all.filter { (coolingUntil[$0] ?? .distantPast) <= now }
        let cooling = all.filter { !ready.contains($0) }.sorted { coolingUntil[$0]! < coolingUntil[$1]! }
        return ready + cooling
    }

    public mutating func markLimited(_ endpoint: GoogleTranslate.Endpoint, now: Date = Date()) {
        coolingUntil[endpoint] = now.addingTimeInterval(cooldown)
    }

    public mutating func markSuccess(_ endpoint: GoogleTranslate.Endpoint) {
        coolingUntil[endpoint] = nil
        consecutiveFailures = 0
    }

    /// 所有接口都失败了一轮：返回重试前要等的秒数（1、2、4，之后一直是 4）。
    public mutating func nextBackoff() -> TimeInterval {
        consecutiveFailures += 1
        return min(maxBackoff, pow(2, Double(consecutiveFailures - 1)))
    }
}
