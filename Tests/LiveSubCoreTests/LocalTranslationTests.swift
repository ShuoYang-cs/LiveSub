import Foundation
import Testing
@testable import LiveSubCore

@Suite struct LocalTranslationTests {
    let english = LanguageOption.source("en-US")
    let japanese = LanguageOption.source("ja-JP")

    @Test func chinesePromptWithoutContext() {
        let prompt = LocalTranslationModel.prompt("Hello.", context: [], source: english, target: .target("zh-Hans"))
        #expect(prompt == "将以下文本翻译为中文，注意只需要输出翻译后的结果，不要额外解释：\n\nHello.")
    }

    @Test func chinesePromptUsesLastTwoLinesAsBackground() {
        let prompt = LocalTranslationModel.prompt("C.", context: ["A.", "B1.", "B2."], source: english, target: .target("zh-Hant"))
        #expect(prompt == "【背景信息】\nB1.\nB2.\n\n请结合背景信息将以下文本翻译为繁体中文。\n\n【待翻译文本】\nC.")
    }

    @Test func englishPromptWhenNoChineseInvolved() {
        let plain = LocalTranslationModel.prompt("こんにちは。", context: [], source: japanese, target: .target("en"))
        #expect(plain.hasPrefix("Translate the following text into English. Note that"))
        #expect(plain.hasSuffix("\n\nこんにちは。"))
        let withContext = LocalTranslationModel.prompt("x", context: ["y"], source: japanese, target: .target("ko"))
        #expect(withContext == "[Background Information]\ny\n\nPlease translate the following text into Korean, taking the provided background information into consideration.\n\n[Source Text]\nx")
    }

    @Test func everyTargetLanguageHasAName() {
        for target in LanguageOption.targets {
            #expect(LocalTranslationModel.languageName(target.translationCode, chinese: true) != target.translationCode)
            #expect(LocalTranslationModel.languageName(target.translationCode, chinese: false) != target.translationCode)
        }
    }

    @Test func requestBodyHasOneUserMessageAndSampling() throws {
        let body = try #require(JSONSerialization.jsonObject(with: LocalTranslationModel.requestBody(prompt: "p")) as? [String: Any])
        let messages = try #require(body["messages"] as? [[String: String]])
        #expect(messages == [["role": "user", "content": "p"]])
        #expect(body["stream"] as? Bool == true)
        #expect(body["top_k"] as? Int == 20)
    }

    @Test func runtimeMatchesArchitecture() {
        #expect(LocalTranslationModel.runtime(arm64: true).url.lastPathComponent.hasSuffix("macos-arm64.tar.gz"))
        #expect(LocalTranslationModel.runtime(arm64: false).url.lastPathComponent.hasSuffix("macos-x64.tar.gz"))
        #expect(!LocalTranslationModel.runtime(arm64: false).usesGPU)
    }
}
