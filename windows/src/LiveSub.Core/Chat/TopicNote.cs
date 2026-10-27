namespace LiveSub.Core.Chat;

/// <summary>
/// 自动判断出的这节课的主题和术语，随翻译会话发给模型（只在纠正时用，与 macOS 的 TopicNote 相同）。
/// 会话里已经带过的主题不重复发：主题变了，才在下一句前面补上。
/// </summary>
public sealed class TopicNote
{
    private readonly object gate = new();
    private string? topic;
    private string? sentTopic;

    public string? Current
    {
        get { lock (gate) return topic; }
    }

    public void Set(string? value)
    {
        lock (gate) topic = value;
    }

    /// <summary>已经在进行的会话：主题变了就返回新的（并记为已发），否则 null。</summary>
    public string? ChangedForOngoing()
    {
        lock (gate)
        {
            if (topic == sentTopic) return null;
            sentTopic = topic;
            return topic;
        }
    }

    /// <summary>进行中的会话的这一条消息：主题变了才带 [Topic]。</summary>
    public string OngoingMessage(string text) => Prompts.TranslationMessage(text, [], ChangedForOngoing());
}
