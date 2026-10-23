import Foundation

/// 本机翻译模型：腾讯混元 Hy-MT2 1.8B（翻译专用，Apache-2.0，33 种语言），用 llama.cpp 的 llama-server 运行。
/// 2026-10-22 实测（M 系列芯片）：每句 0.2–0.3 秒翻完，占用约 1.4 GB 内存；同一时期的小模型里它体积最小、
/// 翻译最准（TranslateGemma 4B 要 3.5 GB 以上，还更慢）。
public enum LocalTranslationModel {
    public static let name = "Hy-MT2 1.8B"
    public static let fileName = "Hy-MT2-1.8B-Q4_K_M.gguf"
    public static let bytes: Int64 = 1_133_080_448
    public static let sha256 = "dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699"
    /// Hugging Face 打不开时（例如在中国大陆）换镜像；下载后都按 `sha256` 校验。
    public static let downloadURLs = [
        "https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/main/Hy-MT2-1.8B-Q4_K_M.gguf",
        "https://hf-mirror.com/tencent/Hy-MT2-1.8B-GGUF/resolve/main/Hy-MT2-1.8B-Q4_K_M.gguf",
    ].compactMap(URL.init(string:))

    /// llama.cpp 的运行程序：固定用测过的版本，不跟着每天的新版本走。
    public struct Runtime: Sendable {
        public let url: URL
        public let bytes: Int64
        public let sha256: String
        /// 解压后的文件夹名（里面有 llama-server 和它的动态库）。
        public let folder: String
        /// Intel 版没有 Metal，只用 CPU。
        public let usesGPU: Bool
    }

    public static func runtime(arm64: Bool) -> Runtime {
        let version = "b11829"
        let arch = arm64 ? "arm64" : "x64"
        return Runtime(
            url: URL(string: "https://github.com/ggml-org/llama.cpp/releases/download/\(version)/llama-\(version)-bin-macos-\(arch).tar.gz")!,
            bytes: arm64 ? 16_156_560 : 11_890_726,
            sha256: arm64 ? "e006ea32abe757c10a93718d246796b9bb7eafd309398927ff7b69139e4ba27e"
                          : "e4d34d663b44bcae30690f2cc62e1a7837f3d3dafe82b77ca3307d7b6ebdaea4",
            folder: "llama-\(version)", usesGPU: arm64)
    }

    /// 官方推荐的生成参数（1.8B）；模型没有系统提示词。
    public static let sampling: [String: Any] = [
        "temperature": 0.7, "top_p": 0.6, "top_k": 20, "repeat_penalty": 1.05, "max_tokens": 512,
    ]

    /// 官方提示词：涉及中文时用中文提示词，否则用英文提示词，语言名写全称。
    /// 有上文时用“背景信息”模板，带上前两句原文（不需要译文），让代词、术语前后一致。
    public static func prompt(_ text: String, context: [String], source: LanguageOption, target: LanguageOption) -> String {
        let chinese = source.translationCode.hasPrefix("zh") || target.translationCode.hasPrefix("zh")
        let language = languageName(target.translationCode, chinese: chinese)
        let background = context.suffix(2).joined(separator: "\n")
        switch (chinese, background.isEmpty) {
        case (true, true):
            return "将以下文本翻译为\(language)，注意只需要输出翻译后的结果，不要额外解释：\n\n\(text)"
        case (true, false):
            return "【背景信息】\n\(background)\n\n请结合背景信息将以下文本翻译为\(language)。\n\n【待翻译文本】\n\(text)"
        case (false, true):
            return "Translate the following text into \(language). Note that you should only output the translated result without any additional explanation:\n\n\(text)"
        case (false, false):
            return "[Background Information]\n\(background)\n\nPlease translate the following text into \(language), taking the provided background information into consideration.\n\n[Source Text]\n\(text)"
        }
    }

    /// 模型说明里的语言名（简体中文就叫“中文”）。
    static func languageName(_ code: String, chinese: Bool) -> String {
        let names: [String: (String, String)] = [
            "zh-Hans": ("中文", "Chinese"), "zh-Hant": ("繁体中文", "Traditional Chinese"),
            "en": ("英语", "English"), "ja": ("日语", "Japanese"), "ko": ("韩语", "Korean"),
            "fr": ("法语", "French"), "de": ("德语", "German"), "es": ("西班牙语", "Spanish"),
        ]
        guard let name = names[code] else { return code }
        return chinese ? name.0 : name.1
    }

    /// 请求体：OpenAI 兼容格式，一条用户消息，流式输出。
    public static func requestBody(prompt: String) -> Data {
        var body = sampling
        body["messages"] = [["role": "user", "content": prompt]]
        body["stream"] = true
        return (try? JSONSerialization.data(withJSONObject: body)) ?? Data()
    }
}
