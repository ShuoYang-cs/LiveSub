import Foundation
import LiveSubCore

/// 启动时看一眼本机有什么能用的模型：Claude Code 命令行，正在运行的 Ollama、LM Studio。
/// 结果用来在模型菜单里标“已检测到”，并把正在运行的本机服务加进模型服务。
enum LocalServiceDetector {
    private static let services = [("Ollama", "http://localhost:11434/v1"), ("LM Studio", "http://localhost:1234/v1")]

    @MainActor
    static func run(settings: AppSettings) async {
        let claudePath = settings.claudePath
        // 找 claude 最后会开登录 shell，放到后台线程。
        settings.detectedClaude = await Task.detached(priority: .utility) {
            ClaudeCLI.locate(customPath: claudePath) != nil
        }.value
        for (name, baseURL) in services {
            let models = await fetchModels(baseURL: baseURL)
            guard !models.isEmpty else { continue }
            settings.runningLocalServices.insert(baseURL)
            settings.addDetectedLocalService(name: name, baseURL: baseURL, models: models)
        }
    }

    /// 服务没在运行时连接马上被拒绝，不会等。
    private static func fetchModels(baseURL: String) async -> [String] {
        guard let url = ChatAPI.endpoint(format: .openAI, baseURL: baseURL, path: "models") else { return [] }
        var request = URLRequest(url: url, timeoutInterval: 1.5)
        request.httpMethod = "GET"
        guard let (data, response) = try? await URLSession.shared.data(for: request),
              (response as? HTTPURLResponse)?.statusCode == 200 else { return [] }
        return ChatAPI.parseModelIDs(data)
    }
}
