import LiveSubCore
import SwiftUI

/// “模型服务”窗口的编辑状态（命令行工具链不能用 @State）。
@MainActor @Observable
final class ModelServicesState {
    struct Notice: Equatable {
        var text: String
        var isError: Bool
    }

    /// 已读取或修改过的 API 密钥（只在内存里缓存，保存到钥匙串）。
    var keys: [UUID: String] = [:]
    /// 模型输入框的原始文字（逗号分隔），保留用户正在输入的逗号。
    var modelText: [UUID: String] = [:]
    var busy: Set<UUID> = []
    var notices: [UUID: Notice] = [:]
}

/// 添加、编辑模型服务：自己的 API（OpenAI 兼容或 Anthropic），或 Ollama、LM Studio 等本地服务。
struct ModelServicesView: View {
    @Bindable var settings: AppSettings
    let state: ModelServicesState
    let onChanged: () -> Void

    var body: some View {
        Form {
            Section {
                Text("添加自己的 API 或本机运行的模型后，可以在翻译模型和 AI 助手中选择。API 密钥保存在系统钥匙串中。")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                Menu("添加模型服务") {
                    ForEach(ChatEndpointPreset.all) { preset in
                        Button(L(preset.name)) { add(preset) }
                    }
                    Divider()
                    Button("自定义") { addCustom() }
                }
                .fixedSize()
            }
            ForEach($settings.endpoints) { $endpoint in
                Section {
                    TextField("名称", text: $endpoint.name)
                    Picker("接口格式", selection: $endpoint.format) {
                        Text("OpenAI 兼容").tag(ChatAPIFormat.openAI)
                        Text("Anthropic").tag(ChatAPIFormat.anthropic)
                    }
                    TextField("接口地址", text: $endpoint.baseURL)
                    SecureField("API 密钥", text: keyBinding(endpoint.id),
                                prompt: Text(endpoint.isLocal ? L("本地服务无需填写") : L("必填")))
                    LabeledContent("模型") {
                        HStack {
                            TextField("模型", text: modelBinding($endpoint), prompt: Text("多个模型用逗号分隔"))
                                .labelsHidden()
                            Button("获取列表") { Task { await fetchModels(endpoint) } }
                                .disabled(state.busy.contains(endpoint.id))
                        }
                    }
                    HStack {
                        Button("测试") { Task { await test(endpoint) } }
                            .disabled(state.busy.contains(endpoint.id) || endpoint.models.isEmpty)
                        if state.busy.contains(endpoint.id) { ProgressView().controlSize(.small) }
                        if let notice = state.notices[endpoint.id] {
                            Text(notice.text)
                                .font(.caption)
                                .foregroundStyle(notice.isError ? Color.orange : Color.secondary)
                                .lineLimit(2)
                        }
                        Spacer()
                        Button("删除", role: .destructive) { remove(endpoint) }
                    }
                } header: {
                    Text(endpoint.isLocal ? L("%@ · 本地", endpoint.name) : endpoint.name)
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 560)
        .frame(minHeight: 420)
        .onChange(of: settings.endpoints) { onChanged() }
    }

    // MARK: - 绑定

    private func keyBinding(_ id: UUID) -> Binding<String> {
        Binding(
            get: {
                if let key = state.keys[id] { return key }
                return KeychainStore.key(for: id) ?? ""
            },
            set: { key in
                state.keys[id] = key
                KeychainStore.setKey(key, for: id)
                onChanged()
            })
    }

    private func modelBinding(_ endpoint: Binding<ModelEndpoint>) -> Binding<String> {
        let id = endpoint.wrappedValue.id
        return Binding(
            get: { state.modelText[id] ?? endpoint.wrappedValue.models.joined(separator: ", ") },
            set: { text in
                state.modelText[id] = text
                endpoint.wrappedValue.models = Self.parseModels(text)
            })
    }

    static func parseModels(_ text: String) -> [String] {
        var seen = Set<String>()
        return text.split(whereSeparator: { $0 == "," || $0 == "，" || $0.isNewline })
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty && seen.insert($0).inserted }
    }

    // MARK: - 操作

    private func add(_ preset: ChatEndpointPreset) {
        settings.endpoints.append(ModelEndpoint(name: L(preset.name), format: preset.format,
                                                baseURL: preset.baseURL, models: preset.exampleModels))
    }

    private func addCustom() {
        settings.endpoints.append(ModelEndpoint(name: L("自定义服务"), format: .openAI, baseURL: "https://", models: []))
    }

    private func remove(_ endpoint: ModelEndpoint) {
        KeychainStore.setKey("", for: endpoint.id)
        settings.endpoints.removeAll { $0.id == endpoint.id }
        // 正在用这个服务的地方换回默认模型。
        if case .endpoint(endpoint.id, _) = settings.translationModel { settings.translationModel = .claudeCode("haiku") }
        if case .endpoint(endpoint.id, _) = settings.assistantModel { settings.assistantModel = .claudeCode("sonnet") }
        onChanged()
    }

    private func apiKey(_ endpoint: ModelEndpoint) -> String? {
        state.keys[endpoint.id] ?? KeychainStore.key(for: endpoint.id)
    }

    private func fetchModels(_ endpoint: ModelEndpoint) async {
        state.busy.insert(endpoint.id)
        defer { state.busy.remove(endpoint.id) }
        do {
            let models = try await APIChatSession.fetchModels(format: endpoint.format, baseURL: endpoint.baseURL,
                                                              apiKey: apiKey(endpoint))
            guard !models.isEmpty else {
                state.notices[endpoint.id] = .init(text: L("服务没有返回模型列表，请手动填写模型名。"), isError: true)
                return
            }
            // 已经填写的模型排在前面，其余按名称排序，方便挑选后删减。
            let kept = endpoint.models.filter(models.contains)
            let merged = kept + models.filter { !kept.contains($0) }
            if let i = settings.endpoints.firstIndex(where: { $0.id == endpoint.id }) {
                settings.endpoints[i].models = merged
                state.modelText[endpoint.id] = merged.joined(separator: ", ")
            }
            state.notices[endpoint.id] = .init(text: L("获取到 %d 个模型。请删去不需要的模型。", models.count), isError: false)
        } catch {
            state.notices[endpoint.id] = .init(text: error.localizedDescription, isError: true)
        }
    }

    /// 用第一个模型翻译一句话，确认地址、密钥和模型都可用，并显示用时。
    private func test(_ endpoint: ModelEndpoint) async {
        guard let model = endpoint.models.first else { return }
        state.busy.insert(endpoint.id)
        defer { state.busy.remove(endpoint.id) }
        let session = APIChatSession(config: .init(
            format: endpoint.format, baseURL: endpoint.baseURL, apiKey: apiKey(endpoint), model: model,
            systemPrompt: Prompts.translatorSystemPrompt(source: settings.source, target: settings.target),
            displayName: endpoint.name, maxTurns: 1, timeout: 30))
        let start = Date()
        do {
            let reply = try await session.translate("Welcome, everyone. Let's get started.", context: [], onPartial: { _ in })
            let seconds = String(format: "%.1f", Date().timeIntervalSince(start))
            let text = TranslationOutput.split(reply, targetIsCJK: settings.targetIsCJK).translation
            state.notices[endpoint.id] = .init(text: L("%@ 可用，用时 %@ 秒：%@", model, seconds, text), isError: false)
        } catch {
            state.notices[endpoint.id] = .init(text: error.localizedDescription, isError: true)
        }
    }
}
