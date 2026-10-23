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
    /// 打开窗口时要滚到的服务（选了还没填密钥的服务时）。
    var focus: UUID?
    /// Claude Code 一栏在 `busy`、`notices` 里用的 id。
    static let claudeID = UUID(uuidString: "C0000000-0000-4000-8000-000000000000")!
}

/// 添加、编辑模型服务：自己的 API（OpenAI 兼容或 Anthropic），或 Ollama、LM Studio 等本地服务。
struct ModelServicesView: View {
    @Bindable var settings: AppSettings
    let state: ModelServicesState
    let onChanged: () -> Void

    var body: some View {
        ScrollViewReader { proxy in
            form
                .onAppear {
                    LocalModelStore.shared.refresh()
                    scrollToFocus(proxy)
                }
                .onChange(of: state.focus) { scrollToFocus(proxy) }
        }
    }

    private func scrollToFocus(_ proxy: ScrollViewProxy) {
        guard let id = state.focus else { return }
        state.notices[id] = .init(text: id == LocalModelFiles.serviceID ? L("下载后就能使用。")
                                      : L("填写 API 密钥后就能使用。密钥在服务商的网站上申请。"), isError: false)
        DispatchQueue.main.async { withAnimation { proxy.scrollTo(id, anchor: .top) } }
        state.focus = nil
    }

    private var form: some View {
        Form {
            Section {
                Text("OpenAI、通义千问、DeepSeek、Kimi、Grok 已经列在翻译模型和 AI 助手里，填上自己的 API 密钥就能用；翻译和 AI 助手各用这家的哪个模型，在下面分别选。也可以添加别的 API 或本机运行的模型。API 密钥保存在系统钥匙串中。")
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
            claudeSection
            localSection
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
                    LabeledContent("可用模型") {
                        HStack {
                            TextField("可用模型", text: modelBinding($endpoint), prompt: Text("多个模型用逗号分隔"))
                                .labelsHidden()
                            Button("获取列表") { Task { await fetchModels(endpoint) } }
                                .disabled(state.busy.contains(endpoint.id))
                        }
                    }
                    if !endpoint.models.isEmpty {
                        modelPicker("翻译用", $endpoint, .translation)
                        modelPicker("AI 助手用", $endpoint, .assistant)
                    }
                    HStack {
                        testButton(endpoint.id, disabled: endpoint.models.isEmpty) { await test(endpoint) }
                        Spacer()
                        Button("删除", role: .destructive) { remove(endpoint) }
                    }
                } header: {
                    Text(endpoint.isLocal ? L("%@ · 本地", endpoint.name) : endpoint.name)
                }
                .id(endpoint.id)
            }
        }
        .formStyle(.grouped)
        .frame(width: 560)
        .frame(minHeight: 420)
        .onChange(of: settings.endpoints) { onChanged() }
        .onChange(of: settings.claudeTranslationModel) { onChanged() }
        .onChange(of: settings.claudeAssistantModel) { onChanged() }
        .onChange(of: settings.claudePath) { onChanged() }
    }

    /// 本机 Claude Code：模型列表里只有一项“Claude”，型号在这里分别为翻译和 AI 助手选。
    private var claudeSection: some View {
        Section {
            Text("使用本机已登录的 Claude Code，不需要 API 密钥。翻译要快，建议 Haiku；AI 助手和笔记更看重准确，可以选 Sonnet 或 Opus。")
                .font(.callout)
                .foregroundStyle(.secondary)
            Picker("翻译用", selection: $settings.claudeTranslationModel) { claudeModelOptions }
            Picker("AI 助手用", selection: $settings.claudeAssistantModel) { claudeModelOptions }
            TextField("Claude 路径", text: $settings.claudePath, prompt: Text("自动查找"))
            testButton(ModelServicesState.claudeID) {
                let model = settings.claudeTranslationModel
                await test(ModelServicesState.claudeID, name: "Claude " + AppSettings.claudeModelName(model)) {
                    try session(.claudeCode(model), timeout: 60)
                }
            }
        } header: {
            Text("Claude Code")
        }
    }

    /// 下载到本机的混元翻译模型：只能翻译，在翻译模型的“本地模型”一组里。
    private var localSection: some View {
        let store = LocalModelStore.shared
        return Section {
            Text("腾讯混元的翻译模型 Hy-MT2（1.8B）。下载后在这台 Mac 上离线翻译，不需要联网和 API 密钥，每句约 0.3 秒。只能翻译，不能做 AI 助手。")
                .font(.callout)
                .foregroundStyle(.secondary)
            LabeledContent("模型文件") {
                HStack {
                    switch store.phase {
                    case .downloading:
                        ProgressView(value: Double(store.received), total: Double(store.total))
                            .frame(width: 120)
                        Text(L("%@ / %@", Self.size(store.received), Self.size(store.total)))
                            .foregroundStyle(.secondary)
                            .monospacedDigit()
                        Button("取消") { store.cancel() }
                    case .verifying:
                        ProgressView().controlSize(.small)
                        Text("正在校验…").foregroundStyle(.secondary)
                    case .failed(let message):
                        Text(message).foregroundStyle(.orange).lineLimit(2)
                        Button("重试") { store.download() }
                    case .idle:
                        if store.installed {
                            Text("已下载").foregroundStyle(.secondary)
                            Button("删除", role: .destructive) { removeLocalModel() }
                        } else {
                            Text(L("约 %@，运行时占用约 1.4 GB 内存", Self.size(store.total))).foregroundStyle(.secondary)
                            Button("下载") { store.download() }
                        }
                    }
                }
            }
            testButton(LocalModelFiles.serviceID, disabled: !store.installed) {
                LocalModelServer.shared.clearFailure()
                await test(LocalModelFiles.serviceID, name: L("混元翻译")) {
                    LocalTranslator(source: settings.source, target: settings.target)
                }
            }
        } header: {
            Text("混元翻译 · 本机")
        }
        .id(LocalModelFiles.serviceID)
        // 下载好了：去掉“下载后就能使用”，正在运行的字幕换成混元翻译（下载前先用的是 Google 翻译）。
        .onChange(of: store.installed) {
            state.notices[LocalModelFiles.serviceID] = nil
            onChanged()
        }
    }

    private static func size(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }

    private func removeLocalModel() {
        LocalModelStore.shared.delete()
        state.notices[LocalModelFiles.serviceID] = nil
        if settings.translationModel == .localModel { settings.translationModel = .google }
        onChanged()
    }

    /// 每家服务的“测试”按钮和结果，Claude Code 一栏也一样。
    private func testButton(_ id: UUID, disabled: Bool = false, action: @escaping @MainActor () async -> Void) -> some View {
        HStack {
            Button("测试") { Task { await action() } }
                .disabled(state.busy.contains(id) || disabled)
                .help("用“翻译用”的模型翻译一句话，确认这个服务能用")
            if state.busy.contains(id) { ProgressView().controlSize(.small) }
            if let notice = state.notices[id] {
                Text(notice.text)
                    .font(.caption)
                    .foregroundStyle(notice.isError ? Color.orange : Color.secondary)
                    .lineLimit(2)
            }
        }
    }

    /// 从这家的可用模型里为翻译或 AI 助手选一个（模型列表里这家只列一项）。
    private func modelPicker(_ title: LocalizedStringKey, _ endpoint: Binding<ModelEndpoint>, _ purpose: ModelPurpose) -> some View {
        Picker(title, selection: Binding(
            get: { endpoint.wrappedValue.model(for: purpose) ?? "" },
            set: { endpoint.wrappedValue.setModel($0, for: purpose) })) {
            ForEach(endpoint.wrappedValue.models, id: \.self) { Text($0).tag($0) }
        }
    }

    @ViewBuilder
    private var claudeModelOptions: some View {
        Text("Haiku（最快）").tag("haiku")
        Text("Sonnet（均衡）").tag("sonnet")
        Text("Opus（最准确、最慢）").tag("opus")
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
        // 删掉的内置服务不再自动加回来。
        if let builtIn = ChatEndpointPreset.builtIn.first(where: { $0.builtInID == endpoint.id.uuidString }), let raw = builtIn.builtInID {
            settings.dismissedBuiltIns.append(raw)
        }
        settings.endpoints.removeAll { $0.id == endpoint.id }
        // 正在用这个服务的地方换回默认模型。
        if case .endpoint(endpoint.id, _) = settings.translationModel { settings.translationModel = .google }
        if case .endpoint(endpoint.id, _) = settings.assistantModel { settings.assistantModel = .claudeCode(settings.claudeAssistantModel) }
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

    private func test(_ endpoint: ModelEndpoint) async {
        guard let model = endpoint.model(for: .translation) else { return }
        await test(endpoint.id, name: model) { try session(.endpoint(endpoint.id, model), timeout: 30) }
    }

    /// 和翻译时一样由 `ChatSessionFactory` 创建会话。
    private func session(_ choice: ModelChoice, timeout: TimeInterval) throws -> Translator {
        try ChatSessionFactory.make(
            choice, settings: settings,
            systemPrompt: Prompts.translatorSystemPrompt(source: settings.source, target: settings.target),
            maxTurns: 1, timeout: timeout)
    }

    /// 用“翻译用”的模型翻译一句话，确认服务（地址和密钥、Claude Code 的登录、本机模型）和模型都可用，并显示用时。
    /// Claude Code 的用时包括启动命令行；混元翻译不算启动服务、加载模型的时间（只在第一句时有）。
    private func test(_ id: UUID, name: String, make: () throws -> Translator) async {
        state.busy.insert(id)
        defer { state.busy.remove(id) }
        state.notices[id] = nil
        do {
            let session = try make()
            defer { session.shutdown() }
            if let local = session as? LocalTranslator { try await local.prepare() }
            let start = Date()
            let reply = try await session.translate("Welcome, everyone. Let's get started.", context: [], onPartial: { _ in })
            let seconds = String(format: "%.1f", Date().timeIntervalSince(start))
            let text = TranslationOutput.split(reply, targetIsCJK: settings.targetIsCJK).translation
            state.notices[id] = .init(text: L("%@ 可用，用时 %@ 秒：%@", name, seconds, text), isError: false)
        } catch {
            state.notices[id] = .init(text: error.localizedDescription, isError: true)
        }
    }
}
