import Foundation
import LiveSubCore

/// 自动判断出的这节课的主题和术语，随翻译会话发给模型（只在纠正时用）。
/// 会话里已经带过的主题不重复发：主题变了，才在下一句前面补上。
final class TopicNote: @unchecked Sendable {
    private let lock = NSLock()
    private var topic: String?
    private var sentTopic: String?

    var current: String? { lock.withLock { topic } }

    func set(_ new: String?) { lock.withLock { topic = new } }

    /// 已经在进行的会话：主题变了就返回新的，否则 nil。
    func changedForOngoing() -> String? {
        lock.withLock {
            guard topic != sentTopic else { return nil }
            sentTopic = topic
            return topic
        }
    }

    /// 带着新主题的那一句翻译失败了：API 会话不记失败的轮次，模型没看到主题，下一句再带上。
    func resend() { lock.withLock { sentTopic = nil } }
}

extension TopicNote {
    /// 进行中的会话的这一条消息：主题变了才带 `[Topic]`。
    func ongoingMessage(_ text: String) -> String {
        Prompts.translationMessage(text: text, context: [], topic: changedForOngoing())
    }
}
