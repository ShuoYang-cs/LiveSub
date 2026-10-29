import AppKit
import Foundation
import LiveSubCore
import Observation

enum AudioSourceMode: String, CaseIterable, Identifiable {
    case microphone, system, both
    var id: String { rawValue }
    var title: String {
        switch self {
        case .microphone: L("麦克风")
        case .system: L("系统音频")
        case .both: L("麦克风和系统音频")
        }
    }
    var shortTitle: String {
        switch self {
        case .microphone: L("麦克风")
        case .system: L("系统音频")
        case .both: L("两者")
        }
    }
    /// 字幕快捷按钮上的图标。
    var symbol: String {
        switch self {
        case .microphone: "mic.fill"
        case .system: "speaker.wave.2.fill"
        case .both: "waveform.badge.mic"
        }
    }
}

/// 外观：跟随系统（默认）、浅色、深色。字幕、AI 助手和各个窗口一起变。
enum AppearanceMode: String, CaseIterable, Identifiable {
    case system, light, dark
    var id: String { rawValue }
    var title: String {
        switch self {
        case .system: L("跟随系统")
        case .light: L("浅色")
        case .dark: L("深色")
        }
    }
    var nsAppearance: NSAppearance? {
        switch self {
        case .system: nil
        case .light: NSAppearance(named: .aqua)
        case .dark: NSAppearance(named: .darkAqua)
        }
    }
}

/// 用户添加的模型服务：自己的 API（OpenAI 兼容或 Anthropic），或 Ollama、LM Studio 等本地服务。
/// API 密钥保存在钥匙串里（`KeychainStore`），不在这里。
struct ModelEndpoint: Codable, Identifiable, Hashable {
    var id = UUID()
    var name: String
    var format: ChatAPIFormat
    var baseURL: String
    /// 这家能选的模型。
    var models: [String]
    /// 翻译、AI 助手分别用哪个模型（在“模型服务”里选）。模型列表里每家服务只列一项（2026-10-22 用户要求，和 Claude 一样）。
    var translationModel: String?
    var assistantModel: String?
    var isLocal: Bool { ChatEndpointPreset.isLocal(baseURL: baseURL) }

    /// 没选过、或选的模型已经从列表里删掉时，用第一个。
    func model(for purpose: ModelPurpose) -> String? {
        let chosen = purpose == .translation ? translationModel : assistantModel
        if let chosen, models.contains(chosen) { return chosen }
        return models.first
    }

    mutating func setModel(_ model: String, for purpose: ModelPurpose) {
        if purpose == .translation { translationModel = model } else { assistantModel = model }
    }
}

/// 翻译或 AI 助手使用的模型。
enum ModelChoice: Hashable, Codable {
    /// 系统自带的离线翻译（只能翻译，不能做 AI 助手、笔记和自动纠错）。
    case apple
    /// Google 翻译的免费网页接口：不需要密钥，需要联网（同样只能翻译）。
    case google
    /// 下载到本机的混元翻译模型（`LocalTranslator`）：离线、只能翻译。
    case localModel
    /// 本机 Claude Code 命令行：haiku / sonnet / opus。
    case claudeCode(String)
    /// 某个模型服务里的某个模型。
    case endpoint(UUID, String)

    /// 能不能做 AI 助手、笔记和按发音纠正（翻译专用的引擎不行）。
    var isLanguageModel: Bool { self != .apple && self != .google && self != .localModel }

    var endpointID: UUID? {
        if case .endpoint(let id, _) = self { return id }
        return nil
    }
}

/// 模型列表用在哪里：翻译还有“基础”一组（Google、Apple）和本机的混元翻译；每家服务的具体模型两边分开选。
enum ModelPurpose {
    case translation, assistant
}

/// 设置界面和菜单里的一组模型。
struct ModelGroup: Identifiable {
    let title: String
    let choices: [ModelChoice]
    var id: String { title }
}

enum DisplayMode: String, CaseIterable, Identifiable {
    case bilingual, translationOnly, sourceOnly
    var id: String { rawValue }
    var title: String {
        switch self {
        case .bilingual: L("双语")
        case .translationOnly: L("仅译文")
        case .sourceOnly: L("仅原文")
        }
    }
}

/// 所有用户设置，改动即时写入 UserDefaults。
@MainActor @Observable
final class AppSettings {
    static let shared = AppSettings()
    @ObservationIgnored private let defaults = UserDefaults.standard

    var sourceLanguage: String { didSet { save("sourceLanguage", sourceLanguage) } }
    var targetLanguage: String { didSet { save("targetLanguage", targetLanguage) } }
    var audioSource: AudioSourceMode { didSet { save("audioSource", audioSource.rawValue) } }
    /// 字幕翻译使用的模型。
    var translationModel: ModelChoice {
        didSet {
            saveJSON("translationModel", translationModel)
            adopt(translationModel, for: .translation)
            if !Self.onlyModelChanged(oldValue, translationModel), let id = endpointMissingKey(translationModel) { onModelNeedsSetup?(id) }
            if translationModel == .localModel, !LocalModelFiles.isInstalled { onModelNeedsSetup?(LocalModelFiles.serviceID) }
            // 自动纠正只对云端模型有效：换到别的模型时关掉，开关也置灰（2026-10-26 用户要求，灰着却是开的容易误解）。
            if !translationModel.isLanguageModel, autoCorrect { autoCorrect = false }
        }
    }
    /// 选了还没填 API 密钥的服务（比如默认列出的 OpenAI）、或还没下载的混元翻译：
    /// 由 AppDelegate 打开“模型服务”，定位到它。
    @ObservationIgnored var onModelNeedsSetup: ((UUID) -> Void)?
    /// 用户添加的模型服务。
    var endpoints: [ModelEndpoint] {
        didSet {
            saveJSON("endpoints", endpoints)
            // 在“模型服务”里换了某家的模型、或删掉了正在用的模型：正在用这家的翻译和 AI 助手跟着换。
            if let synced = synced(translationModel, for: .translation), synced != translationModel { translationModel = synced }
            if let synced = synced(assistantModel, for: .assistant), synced != assistantModel { assistantModel = synced }
        }
    }
    var claudePath: String { didSet { save("claudePath", claudePath) } }
    /// 选 Claude 时用哪个型号（在“模型服务”里改；模型列表里 Claude 只有一项，2026-10-22 用户要求）。
    /// 翻译要快，默认 Haiku（2026-10-22 实测每句 0.9–1.2 秒，Sonnet 1.1–3.3 秒）；AI 助手默认 Sonnet。
    var claudeTranslationModel: String {
        didSet {
            save("claudeTranslationModel", claudeTranslationModel)
            if case .claudeCode(let m) = translationModel, m != claudeTranslationModel { translationModel = .claudeCode(claudeTranslationModel) }
        }
    }
    var claudeAssistantModel: String {
        didSet {
            save("claudeAssistantModel", claudeAssistantModel)
            if case .claudeCode(let m) = assistantModel, m != claudeAssistantModel { assistantModel = .claudeCode(claudeAssistantModel) }
        }
    }
    static let claudeModels = ["haiku", "sonnet", "opus"]
    /// 字幕底部的运行状态条（有没有声音、识别是否清晰、翻译积压）。
    var showStatusBar: Bool { didSet { save("showStatusBar", showStatusBar) } }
    /// 识别困难（口音重、语速快、噪音大）时让大模型按发音和上下文纠正识别错误。默认关闭。
    var autoCorrect: Bool {
        didSet {
            save("autoCorrect", autoCorrect)
            // 纠正会改原文：只看译文时看不出来，打开时从“仅译文”切到“双语”（2026-10-26 用户要求）。
            if autoCorrect, !oldValue, displayMode == .translationOnly { displayMode = .bilingual }
            if autoCorrect, !translationModel.isLanguageModel { autoCorrect = false }
        }
    }
    /// 开着自动纠正又保留识别原文时，“仅译文”不能选（看不到原文就没有意义）。
    var translationOnlyLocked: Bool { autoCorrect && keepOriginalText }
    /// 纠正改过原文时，在记录和字幕记录窗口里同时保留识别器原来给出的文字。
    var keepOriginalText: Bool {
        didSet {
            save("keepOriginalText", keepOriginalText)
            if translationOnlyLocked, displayMode == .translationOnly { displayMode = .bilingual }
        }
    }
    /// AI 助手和笔记使用的模型（必须是大模型），以及面板是否显示、大小。
    var assistantModel: ModelChoice {
        didSet {
            saveJSON("assistantModelChoice", assistantModel)
            adopt(assistantModel, for: .assistant)
            if !Self.onlyModelChanged(oldValue, assistantModel), let id = endpointMissingKey(assistantModel) { onModelNeedsSetup?(id) }
        }
    }
    /// 用户删掉过的内置服务（不再自动加回来）。
    var dismissedBuiltIns: [String] { didSet { save("dismissedBuiltIns", dismissedBuiltIns) } }
    /// 点过“开始”：第一次启动时开始按钮下面多一行提示，之后不再显示。
    var hasStartedOnce: Bool { didSet { save("hasStartedOnce", hasStartedOnce) } }
    /// 已经替用户加进模型服务的本机服务地址（Ollama、LM Studio）：用户删掉后不再自动加回来。
    var offeredLocalServices: [String] { didSet { save("offeredLocalServices", offeredLocalServices) } }
    /// 本次启动时检测到的：本机装了 Claude Code、正在运行的本机服务地址。不保存。
    var detectedClaude = false
    var runningLocalServices: Set<String> = []
    /// AI 助手面板是否打开：每次启动都默认收起，用到时才打开（不保存）。
    var assistantVisible = false
    var assistantWidth: Double { didSet { save("assistantWidth", assistantWidth) } }
    var assistantHeight: Double { didSet { save("assistantHeight", assistantHeight) } }

    var displayMode: DisplayMode { didSet { save("displayMode", displayMode.rawValue) } }
    var translationFontSize: Double { didSet { save("translationFontSize", translationFontSize) } }
    var sourceFontSize: Double { didSet { save("sourceFontSize", sourceFontSize) } }
    var backgroundOpacity: Double { didSet { save("backgroundOpacity", backgroundOpacity) } }
    var visibleLines: Int { didSet { save("visibleLines", visibleLines) } }
    var overlayWidth: Double { didSet { save("overlayWidth", overlayWidth) } }
    /// 0 = 自动高度（显示最近 N 句）；大于 0 = 固定高度，字幕可滚动查看历史。
    var overlayHeight: Double { didSet { save("overlayHeight", overlayHeight) } }
    var clickThrough: Bool { didSet { save("clickThrough", clickThrough) } }
    var appearance: AppearanceMode { didSet { save("appearance", appearance.rawValue) } }
    /// 录音：字幕运行期间把听到的声音存进记录文件夹。默认关闭。
    var recordAudio: Bool { didSet { save("recordAudio", recordAudio) } }

    var transcriptFolder: String { didSet { save("transcriptFolder", transcriptFolder) } }

    private init() {
        let d = UserDefaults.standard
        sourceLanguage = d.string(forKey: "sourceLanguage") ?? "en-US"
        targetLanguage = d.string(forKey: "targetLanguage") ?? "zh-Hans"
        audioSource = AudioSourceMode(rawValue: d.string(forKey: "audioSource") ?? "") ?? .both
        // 旧版本只有“引擎 + Claude 模型”两个设置，迁移过来保持原来的选择。
        // 新用户默认用 Google 翻译：不需要配置，所有系统版本都能用（2026-10-22 用户决定，和 ICT 一样）。
        let legacyModel = d.string(forKey: "claudeModel") ?? "haiku"
        let legacyEngine = d.string(forKey: "engine")
        let translation = Self.loadJSON(ModelChoice.self, "translationModel")
            ?? (legacyEngine == "apple" ? .apple : legacyEngine != nil ? .claudeCode(legacyModel) : .google)
        translationModel = translation
        let assistant = Self.loadJSON(ModelChoice.self, "assistantModelChoice")
            ?? .claudeCode(d.string(forKey: "assistantModel") ?? "sonnet")
        assistantModel = assistant
        // 老用户（0.1.0 起每次启动都会存下 claudeTranslationModel）不再看到首次提示；必须在下面的 d.set 之前读。
        hasStartedOnce = d.object(forKey: "hasStartedOnce") as? Bool ?? (d.object(forKey: "claudeTranslationModel") != nil)
        offeredLocalServices = d.stringArray(forKey: "offeredLocalServices") ?? []
        let dismissed = d.stringArray(forKey: "dismissedBuiltIns") ?? []
        dismissedBuiltIns = dismissed
        // 以前在列表里直接选过某家的某个模型：记成这家的“翻译用”/“AI 助手用”，接着用原来的模型。
        let saved = Self.loadJSON([ModelEndpoint].self, "endpoints") ?? []
        var services = Self.withBuiltInServices(saved, dismissed: dismissed)
        Self.adopt(translation, for: .translation, in: &services)
        Self.adopt(assistant, for: .assistant, in: &services)
        endpoints = services
        // 迁移时记下的选择马上存起来：之后换到别家服务，回来时还是这个模型。
        if services.contains(where: { $0.translationModel != nil || $0.assistantModel != nil }),
           !saved.contains(where: { $0.translationModel != nil || $0.assistantModel != nil }) {
            if let data = try? JSONEncoder().encode(services) { d.set(data, forKey: "endpoints") }
        }
        claudePath = d.string(forKey: "claudePath") ?? ""
        // 以前直接在列表里选过 Haiku / Opus 的，接着用原来的型号。
        if case .claudeCode(let m) = translation, d.string(forKey: "claudeTranslationModel") == nil {
            claudeTranslationModel = m
        } else {
            claudeTranslationModel = d.string(forKey: "claudeTranslationModel") ?? "haiku"
        }
        if case .claudeCode(let m) = assistant, d.string(forKey: "claudeAssistantModel") == nil {
            claudeAssistantModel = m
        } else {
            claudeAssistantModel = d.string(forKey: "claudeAssistantModel") ?? "sonnet"
        }
        autoCorrect = d.object(forKey: "autoCorrect") as? Bool ?? false
        keepOriginalText = d.object(forKey: "keepOriginalText") as? Bool ?? false
        if !translation.isLanguageModel { autoCorrect = false }
        showStatusBar = d.object(forKey: "showStatusBar") as? Bool ?? true
        assistantWidth = d.object(forKey: "assistantWidth") as? Double ?? 380
        assistantHeight = d.object(forKey: "assistantHeight") as? Double ?? 420
        displayMode = DisplayMode(rawValue: d.string(forKey: "displayMode") ?? "") ?? .bilingual
        appearance = AppearanceMode(rawValue: d.string(forKey: "appearance") ?? "") ?? .system
        translationFontSize = d.object(forKey: "translationFontSize") as? Double ?? 18
        sourceFontSize = d.object(forKey: "sourceFontSize") as? Double ?? 11
        backgroundOpacity = d.object(forKey: "backgroundOpacity") as? Double ?? 0.62
        visibleLines = d.object(forKey: "visibleLines") as? Int ?? 2
        let overlaySize = Self.defaultOverlaySize
        overlayWidth = d.object(forKey: "overlayWidth") as? Double ?? overlaySize.width
        overlayHeight = d.object(forKey: "overlayHeight") as? Double ?? overlaySize.height
        clickThrough = d.object(forKey: "clickThrough") as? Bool ?? false
        recordAudio = d.object(forKey: "recordAudio") as? Bool ?? false
        transcriptFolder = d.string(forKey: "transcriptFolder")
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Documents/LiveSub").path
        // 从旧设置迁移来的 Claude 型号马上存下来：之后换到别家服务、重启后再换回 Claude，还是这个型号。
        d.set(claudeTranslationModel, forKey: "claudeTranslationModel")
        d.set(claudeAssistantModel, forKey: "claudeAssistantModel")
    }

    var source: LanguageOption { LanguageOption.source(sourceLanguage) }
    var target: LanguageOption { LanguageOption.target(targetLanguage) }
    /// 字幕语言是中文/日文/韩文（用来清理 Claude 偶尔混进译文的英文自言自语）。
    var targetIsCJK: Bool { ["zh", "ja", "ko"].contains { target.translationCode.hasPrefix($0) } }
    /// 字幕窗口的默认大小：面积是屏幕的 1/16，宽高比 3:1（和 ICT 一样）。
    static var defaultOverlaySize: (width: Double, height: Double) {
        let screen = (NSScreen.main ?? NSScreen.screens.first)?.frame.size ?? NSSize(width: 1470, height: 956)
        let width = max(320, (3 * screen.width * screen.height / 16).squareRoot().rounded())
        return (width, (width / 3).rounded())
    }

    func resetOverlaySize() {
        let size = Self.defaultOverlaySize
        overlayWidth = size.width
        overlayHeight = size.height
    }

    /// 用来识别“旁边有人说中文”的哨兵识别语言：并行跑一个中文识别器，过滤掉旁边的中文说话声。
    /// 自动开启，讲话语言是中文时不需要，为 nil。
    var guardLocale: String? { !sourceLanguage.hasPrefix("zh") ? "zh-CN" : nil }

    private func save(_ key: String, _ value: Any) {
        defaults.set(value, forKey: key)
    }

    /// 默认列出的云端服务（OpenAI、通义千问、DeepSeek、Kimi、Grok）：没有的补上，放在用户自己添加的前面。
    /// 用户删掉过的、或者自己已经加过同一个地址的，不再补。
    private static func withBuiltInServices(_ saved: [ModelEndpoint], dismissed: [String]) -> [ModelEndpoint] {
        let hosts = Set(saved.compactMap { URL(string: $0.baseURL)?.host?.lowercased() })
        let missing = ChatEndpointPreset.builtIn.compactMap { preset -> ModelEndpoint? in
            guard let raw = preset.builtInID, let id = UUID(uuidString: raw), !dismissed.contains(raw),
                  !saved.contains(where: { $0.id == id }),
                  !hosts.contains(URL(string: preset.baseURL)?.host?.lowercased() ?? "") else { return nil }
            return ModelEndpoint(id: id, name: L(preset.name), format: preset.format, baseURL: preset.baseURL, models: preset.exampleModels)
        }
        return missing + saved
    }

    /// 这个模型所在的服务需要 API 密钥但还没填：返回服务的 id。
    func endpointMissingKey(_ choice: ModelChoice) -> UUID? {
        guard case .endpoint(let id, _) = choice, let endpoint = endpoint(id), !endpoint.isLocal,
              (KeychainStore.key(for: id) ?? "").isEmpty else { return nil }
        return id
    }

    private func saveJSON(_ key: String, _ value: some Encodable) {
        if let data = try? JSONEncoder().encode(value) { defaults.set(data, forKey: key) }
    }

    private static func loadJSON<T: Decodable>(_ type: T.Type, _ key: String) -> T? {
        guard let data = UserDefaults.standard.data(forKey: key) else { return nil }
        return try? JSONDecoder().decode(type, from: data)
    }

    // MARK: - 模型

    func endpoint(_ id: UUID) -> ModelEndpoint? { endpoints.first { $0.id == id } }

    /// 选中的模型记到那家服务上（命令行、旧设置里直接给了具体模型时）。
    private func adopt(_ choice: ModelChoice, for purpose: ModelPurpose) {
        var services = endpoints
        Self.adopt(choice, for: purpose, in: &services)
        if services != endpoints { endpoints = services }
    }

    /// 正在用、但不在这家可用模型里的（以前“获取列表”时被挤掉、或手动删过，照样能用）：加进列表，接着用它，
    /// 不能悄悄换成列表第一个。
    private static func adopt(_ choice: ModelChoice, for purpose: ModelPurpose, in services: inout [ModelEndpoint]) {
        guard case .endpoint(let id, let model) = choice, !model.isEmpty, let i = services.firstIndex(where: { $0.id == id }),
              services[i].model(for: purpose) != model else { return }
        if !services[i].models.contains(model) { services[i].models.append(model) }
        services[i].setModel(model, for: purpose)
    }

    /// 这家服务现在为翻译 / AI 助手选的模型。
    private func synced(_ choice: ModelChoice, for purpose: ModelPurpose) -> ModelChoice? {
        guard case .endpoint(let id, _) = choice, let model = endpoint(id)?.model(for: purpose) else { return nil }
        return .endpoint(id, model)
    }

    /// 同一家服务只是换了模型：不用再提示填密钥。
    private static func onlyModelChanged(_ old: ModelChoice, _ new: ModelChoice) -> Bool {
        old != new && old.endpointID != nil && old.endpointID == new.endpointID
    }

    /// Apple 本地翻译需要 macOS 26。
    static var appleTranslationAvailable: Bool {
        if #available(macOS 26, *) { return true }
        return false
    }

    /// 完整名称“服务 · 模型”：日志、笔记进度、API 会话里用。
    func displayName(_ choice: ModelChoice) -> String {
        switch choice {
        case .apple:
            return L("Apple 本地翻译")
        case .google:
            return L("Google 翻译")
        case .localModel:
            return L("混元翻译") + " · " + LocalTranslationModel.name
        case .claudeCode(let model):
            return "Claude · " + Self.claudeModelName(model)
        case .endpoint(let id, let model):
            guard let endpoint = endpoint(id) else { return model }
            return "\(endpoint.name) · \(model)"
        }
    }

    /// 模型列表和菜单上显示的名字：只写服务名（“Claude”“通义千问”），具体模型在“模型服务”里看和改。
    func serviceName(_ choice: ModelChoice) -> String {
        switch choice {
        case .apple, .google: return displayName(choice)
        case .localModel: return L("混元翻译")
        case .claudeCode: return "Claude"
        case .endpoint(let id, let model): return endpoint(id)?.name ?? model
        }
    }

    /// 菜单里的名字：本机检测到可用、又不是当前选中的，后面标“已检测到”。
    func menuTitle(_ choice: ModelChoice, selection: ModelChoice) -> String {
        let name = serviceName(choice)
        guard choice != selection else { return name }
        let detected: Bool
        switch choice {
        case .claudeCode: detected = detectedClaude
        case .endpoint(let id, _): detected = endpoint(id).map { runningLocalServices.contains($0.baseURL) } ?? false
        default: detected = false
        }
        return detected ? L("%@ · 已检测到", name) : name
    }

    /// 检测到正在运行、还没加过的本机服务（Ollama、LM Studio）：加进模型服务，放在“本地模型”里。
    /// 用户删掉后不再加回来；用户自己已经加过同一地址的也不加。
    func addDetectedLocalService(name: String, baseURL: String, models: [String]) {
        guard !models.isEmpty, !offeredLocalServices.contains(baseURL),
              !endpoints.contains(where: { $0.baseURL == baseURL }) else { return }
        offeredLocalServices.append(baseURL)
        endpoints.append(ModelEndpoint(name: name, format: .openAI, baseURL: baseURL, models: models))
    }

    static func claudeModelName(_ model: String) -> String { model.prefix(1).uppercased() + model.dropFirst() }

    /// 可选的模型，分为基础（只能翻译）、云端（Claude Code 与自己的 API）、本地三组。
    /// 每家服务只列一项，用哪个具体模型在“模型服务”里分别为翻译和 AI 助手选
    /// （三个 Claude、一串 GPT 型号并列显得突兀，2026-10-22 用户提出）。
    func modelGroups(for purpose: ModelPurpose) -> [ModelGroup] {
        let claude = ModelChoice.claudeCode(purpose == .translation ? claudeTranslationModel : claudeAssistantModel)
        func choice(_ e: ModelEndpoint) -> ModelChoice? { e.model(for: purpose).map { .endpoint(e.id, $0) } }
        let cloud = [claude] + endpoints.filter { !$0.isLocal }.compactMap(choice)
        let local = (purpose == .translation ? [.localModel] : []) + endpoints.filter(\.isLocal).compactMap(choice)
        var groups: [ModelGroup] = []
        if purpose == .translation {
            groups.append(ModelGroup(title: L("基础"), choices: [.google] + (Self.appleTranslationAvailable ? [.apple] : [])))
        }
        groups.append(ModelGroup(title: L("云端模型"), choices: cloud))
        if !local.isEmpty { groups.append(ModelGroup(title: L("本地模型"), choices: local)) }
        return groups
    }
}
