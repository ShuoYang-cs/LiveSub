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
        var text: String
        var failed = false
    }

    private(set) var messages: [Message] = []
    private(set) var isAnswering = false
    /// 输入框里的文字。
    var draft = ""

    @ObservationIgnored private let settings: AppSettings
    @ObservationIgnored private var session: ChatSession?
    @ObservationIgnored private var sessionModel: ModelChoice?
    @ObservationIgnored private var conversationStart = Date()
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
        conversationStart = Date()
        appLog.notice("assistant: new conversation")
    }

    /// 提前启动 claude 进程，第一次提问不用等。
    func warmUp() {
        _ = try? makeSessionIfNeeded()
    }

    func send() {
        let question = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !question.isEmpty else { return }
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
        Task {
            do {
                let reply = try await session.request(newSessionMessage: fresh, ongoingMessage: ongoing) { [weak self] partial in
                    Task { @MainActor in self?.update(answerID, partial) }
                }
                update(answerID, reply)
                sentIDs.formUnion(settledIDs)
            } catch {
                if let i = messages.firstIndex(where: { $0.id == answerID }) {
                    messages[i].text = error.localizedDescription
                    messages[i].failed = true
                }
                appLog.error("assistant failed: \(error.localizedDescription, privacy: .public)")
            }
            isAnswering = false
        }
    }

    private func update(_ id: UUID, _ text: String) {
        guard let i = messages.firstIndex(where: { $0.id == id }), text.count >= messages[i].text.count else { return }
        messages[i].text = text
    }

    private func makeSessionIfNeeded() throws -> ChatSession {
        // 换了模型：开新会话（同一个对话，下一问会把全部字幕重新带上）。
        if session != nil, sessionModel != settings.assistantModel {
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
        sessionModel = settings.assistantModel
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
