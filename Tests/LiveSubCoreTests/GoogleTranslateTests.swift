import Foundation
import Testing
@testable import LiveSubCore

@Suite struct GoogleTranslateTests {
    @Test func languageCodes() {
        #expect(GoogleTranslate.languageCode("zh-Hans") == "zh-CN")
        #expect(GoogleTranslate.languageCode("zh-Hant") == "zh-TW")
        #expect(GoogleTranslate.languageCode("en") == "en")
    }

    @Test func requestURLEscapesText() {
        let url = GoogleTranslate.requestURL(text: "1 + 1 = 2 & more?", source: "en", target: "zh-Hans")
        let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
        #expect(items.first { $0.name == "q" }?.value == "1 + 1 = 2 & more?")
        #expect(items.first { $0.name == "tl" }?.value == "zh-CN")
        #expect(url.absoluteString.contains("%2B"))
    }

    @Test func parseJoinsSegments() throws {
        let data = Data(#"[[["你好。","Hello.",null,null,10],["世界","World",null,null,10]],null,"en"]"#.utf8)
        #expect(try GoogleTranslate.parse(data) == "你好。世界")
    }

    @Test func dictionaryExtensionURLAndReplies() throws {
        let url = GoogleTranslate.requestURL(text: "Hi", source: "en", target: "zh-Hans", endpoint: .dictionaryExtension)
        #expect(url.absoluteString.hasPrefix("https://clients5.google.com/translate_a/t?client=dict-chrome-ex"))
        #expect(url.absoluteString.contains("tl=zh-CN"))
        #expect(try GoogleTranslate.parse(Data(#"["大家好。你今天怎么样？"]"#.utf8), endpoint: .dictionaryExtension) == "大家好。你今天怎么样？")
        #expect(try GoogleTranslate.parse(Data(#"[["大家好。","en"]]"#.utf8), endpoint: .dictionaryExtension) == "大家好。")
        #expect(throws: GoogleTranslate.ParseError.self) {
            try GoogleTranslate.parse(Data("[]".utf8), endpoint: .dictionaryExtension)
        }
    }

    @Test func temporaryFailures() {
        #expect(GoogleTranslate.isTemporaryFailure(status: 429))
        #expect(GoogleTranslate.isTemporaryFailure(status: 503))
        #expect(!GoogleTranslate.isTemporaryFailure(status: 400))
    }

    @Test func rotationSkipsLimitedEndpointUntilCooldownEnds() {
        var r = GoogleEndpointRotation()
        let t0 = Date(timeIntervalSince1970: 1000)
        #expect(r.order(now: t0) == [.gtx, .dictionaryExtension])
        r.markLimited(.gtx, now: t0)
        #expect(r.order(now: t0.addingTimeInterval(30)) == [.dictionaryExtension, .gtx])
        #expect(r.order(now: t0.addingTimeInterval(61)) == [.gtx, .dictionaryExtension])
        // 两个都在冷却：先结束冷却的排前面。
        r.markLimited(.dictionaryExtension, now: t0.addingTimeInterval(10))
        #expect(r.order(now: t0.addingTimeInterval(20)) == [.gtx, .dictionaryExtension])
        r.markSuccess(.gtx)
        #expect(r.order(now: t0.addingTimeInterval(20)) == [.gtx, .dictionaryExtension])
    }

    @Test func backoffDoublesUpToFourSecondsAndResets() {
        var r = GoogleEndpointRotation()
        #expect([r.nextBackoff(), r.nextBackoff(), r.nextBackoff(), r.nextBackoff()] == [1, 2, 4, 4])
        r.markSuccess(.gtx)
        #expect(r.nextBackoff() == 1)
    }

    @Test func parseRejectsUnexpectedReply() {
        #expect(throws: GoogleTranslate.ParseError.self) { try GoogleTranslate.parse(Data("{}".utf8)) }
        #expect(throws: GoogleTranslate.ParseError.self) { try GoogleTranslate.parse(Data("[[]]".utf8)) }
    }
}
