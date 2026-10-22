import Foundation
import LiveSubCore
import Observation

/// AI 助手对话（通过本机 Claude Code）。
///
/// - 每次开始字幕时新开一个对话；
/// - 提问时把上次提问以来新增的字幕（和正在说的半句）放在问题前面一起发，Claude 手上一直有完整的字幕记录；
/// - 会话中途重启（进程退出）时，自动把本次对话以来的全部字幕重新带上。
@MainActor @Observable
final class AssistantSession {
    struct Message: Identifiable, Equatable {
        enum Role { case user, assistant }
        let id = UUID()
        let role: Role
        /// 显示的文字：回答里要保存成文件的部分不显示，只显示文件名。
        var text: String
        var failed = false
        /// 模型的原始回答（流式输出只会变长）。
        var raw = ""
        /// 回答里正在写的文件还没写完。
        var writingFile = false
        var files: [SavedFile] = []
    }

    /// 替用户保存的文件：成功时有位置，失败时有原因。
    struct SavedFile: Equatable {
        var name: String
        var url: URL?
        var error: String?
    }

    private(set) var messages: [Message] = []
    private(set) var isAnswering = false
    /// 输入框里的文字。
    var draft = ""

    @ObservationIgnored private let settings: AppSettings
    @ObservationIgnored private var session: ChatSession?
    /// 建会话时的模型、语言和服务配置；变了就换新会话（见 `sessionSignature`）。
    @ObservationIgnored private var sessionSignature = ""
    @ObservationIgnored private var conversationStart = Date()
    /// 每次新对话加一：上一个对话晚到的回复不能改动新对话的状态。
    @ObservationIgnored private var generation = 0
    /// 已经发给 Claude 的字幕。
    @ObservationIgnored private var sentIDs = Set<UUID>()
    /// 由控制器提供：当前的字幕和正在说的半句。
    @ObservationIgnored var transcript: () -> (lines: [SubtitleLine], speaking: String) = { ([], "") }

    init(settings: AppSettings) {
        self.settings = settings
    }

    /// 新对话：清空问答，之后只带上从现在开始的字幕。
    func newConversation() {
        session?.shutdown()
        session = nil
        messages = []
        sentIDs = []
        isAnswering = false
        generation += 1
        conversationStart = Date()
        appLog.notice("assistant: new conversation")
    }

    /// 提前启动 claude 进程，第一次提问不用等。
    func warmUp() {
        _ = try? makeSessionIfNeeded()
    }

    func send() {
        let question = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        // 正在回答时按回车：问题留在输入框里，不清掉。
        guard !question.isEmpty, !isAnswering else { return }
        draft = ""
        ask(question)
    }

    func ask(_ question: String) {
        guard !isAnswering else { return }
        let session: ChatSession
        do {
            session = try makeSessionIfNeeded()
        } catch {
            messages.append(Message(role: .user, text: question))
            messages.append(Message(role: .assistant, text: error.localizedDescription, failed: true))
            return
        }
        let (lines, speaking) = transcript()
        // 只带已经开始翻译的句子：还在排队的句子可能会和后面的句子合并，放进“正在说”里。
        let conversation = lines.filter { $0.time >= conversationStart }
        let settled = conversation.filter { $0.state != .waiting && !$0.source.isEmpty }
        let pending = conversation.filter { $0.state == .waiting }.map(\.source)
        let speakingNow = (pending + [speaking]).filter { !$0.isEmpty }.joined(separator: " ")
        let all = settled.map { AssistantPrompt.Line(time: $0.time, text: $0.source) }
        let new = settled.filter { !sentIDs.contains($0.id) }.map { AssistantPrompt.Line(time: $0.time, text: $0.source) }
        let fresh = AssistantPrompt.message(newLines: all, speaking: speakingNow, question: question)
        let ongoing = AssistantPrompt.message(newLines: new, speaking: speakingNow, question: question)

        messages.append(Message(role: .user, text: question))
        let answer = Message(role: .assistant, text: "")
        messages.append(answer)
        isAnswering = true
        let answerID = answer.id
        let settledIDs = settled.map(\.id)
        let gen = generation
        Task {
            do {
                let reply = try await session.request(newSessionMessage: fresh, ongoingMessage: ongoing) { [weak self] partial in
                    Task { @MainActor in self?.update(answerID, partial) }
                }
                guard gen == generation else { return }
                update(answerID, reply)
                saveFiles(answerID)
                sentIDs.formUnion(settledIDs)
            } catch {
                guard gen == generation else { return }
                if let i = messages.firstIndex(where: { $0.id == answerID }) {
                    messages[i].text = error.localizedDescription
                    messages[i].failed = true
                    messages[i].writingFile = false
                }
                appLog.error("assistant failed: \(error.localizedDescription, privacy: .public)")
            }
            isAnswering = false
        }
    }

    private func update(_ id: UUID, _ raw: String) {
        guard let i = messages.firstIndex(where: { $0.id == id }), raw.count >= messages[i].raw.count else { return }
        let parsed = AssistantFiles.parse(raw)
        messages[i].raw = raw
        messages[i].text = parsed.text
        messages[i].writingFile = parsed.writing
    }

    /// 回答里要保存的文件写进记录文件夹下的“AI 助手”文件夹（模型自己不能写文件）。不覆盖已有文件。
    private func saveFiles(_ id: UUID) {
        guard let i = messages.firstIndex(where: { $0.id == id }) else { return }
        let files = AssistantFiles.parse(messages[i].raw).files
        messages[i].writingFile = false
        guard !files.isEmpty else { return }
        let stamp = TranscriptFormat.fileStampFormatter.string(from: Date())
        var saved: [SavedFile] = []
        do {
            let folder = try Self.filesFolder(settings.transcriptFolder)
            for file in files {
                let name = AssistantFiles.uniqueName(AssistantFiles.fileName(for: file.name, stamp: stamp, fallback: L("AI 助手"))) {
                    FileManager.default.fileExists(atPath: folder.appendingPathComponent($0).path)
                }
                let url = folder.appendingPathComponent(name)
                do {
                    try (file.content + "\n").write(to: url, atomically: true, encoding: .utf8)
                    saved.append(SavedFile(name: name, url: url))
                } catch {
                    saved.append(SavedFile(name: name, error: error.localizedDescription))
                }
            }
        } catch {
            saved = files.map { SavedFile(name: $0.name, error: error.localizedDescription) }
        }
        messages[i].files = saved
        appLog.notice("assistant: saved \(saved.filter { $0.url != nil }.count)/\(files.count) files")
    }

    /// 记录文件夹下的“AI 助手”文件夹；记录文件夹写不进去时放到“应用程序支持”里（和字幕记录一样）。
    private static func filesFolder(_ transcriptFolder: String) throws -> URL {
        let name = L("AI 助手")
        let preferred = URL(fileURLWithPath: (transcriptFolder as NSString).expandingTildeInPath, isDirectory: true)
            .appendingPathComponent(name, isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: preferred, withIntermediateDirectories: true)
            return preferred
        } catch {
            let fallback = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
                .appendingPathComponent("LiveSub/\(name)", isDirectory: true)
            try FileManager.default.createDirectory(at: fallback, withIntermediateDirectories: true)
            return fallback
        }
    }

    /// 决定会话能不能继续用的设置：模型、讲话和字幕语言；用的是模型服务时还有它的地址、格式和密钥。
    /// 在“模型服务”里改了密钥或地址、换了字幕语言，旧会话还用着旧的连接和提示词，要换新的。
    private var currentSignature: String {
        var parts = ["\(settings.assistantModel)", settings.sourceLanguage, settings.targetLanguage]
        if case .endpoint(let id, _) = settings.assistantModel, let endpoint = settings.endpoint(id) {
            parts += [endpoint.baseURL, endpoint.format.rawValue, String(KeychainStore.key(for: id).hashValue)]
        }
        return parts.joined(separator: "\u{1}")
    }

    private func makeSessionIfNeeded() throws -> ChatSession {
        // 模型或相关设置变了：开新会话（同一个对话，下一问会把全部字幕重新带上）。
        let signature = currentSignature
        if session != nil, sessionSignature != signature {
            session?.shutdown()
            session = nil
        }
        if let session { return session }
        let session = try ChatSessionFactory.make(
            settings.assistantModel, settings: settings,
            systemPrompt: AssistantPrompt.system(source: settings.source, target: settings.target),
            maxTurns: 10_000, timeout: 180)
        session.warmUp()
        self.session = session
        sessionSignature = signature
        return session
    }

    func shutdown() {
        session?.shutdown()
    }

    /// 仅用于 `--assistant-test` 自检：把对话开始时间提前，包含记录里的旧字幕。
    func debugBackdate(to date: Date) {
        conversationStart = date
    }

    /// 仅用于 `--render-assistant` 自检：填入示例问答。
    func debugLoad(_ pairs: [(question: String, answer: String)]) {
        messages = pairs.flatMap { [Message(role: .user, text: $0.question), Message(role: .assistant, text: $0.answer)] }
    }
}
