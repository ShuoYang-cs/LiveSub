using System.Diagnostics.CodeAnalysis;

namespace LiveSub.Core.Speech;

/// <summary>
/// 等待识别的实时预览：每个音源只留最新的一次请求（旧的已经过时）。
/// 先请求的先识别；同一音源还在排队时再次请求，只换成新的内容，位置不变；识别过之后再请求才排到后面。
/// “两者”模式下两个音源因此轮流，不会一个一直占着识别器（再次请求就排到后面的话，每次都先请求的音源会把另一个一直挤在后面）。
/// 不是线程安全的，由调用方加锁。
/// </summary>
public sealed class PreviewQueue<T>
{
    private readonly List<(int Source, T Item)> requests = [];

    public int Count => requests.Count;

    public void Request(int source, T item)
    {
        int index = requests.FindIndex(r => r.Source == source);
        if (index >= 0) requests[index] = (source, item);
        else requests.Add((source, item));
    }

    /// <summary>这个音源的一段说完了（确认结果马上要识别）：它的预览不用再做。</summary>
    public void Remove(int source) => requests.RemoveAll(r => r.Source == source);

    /// <summary>取出最早的一个请求。</summary>
    public bool TryTake(out int source, [MaybeNullWhen(false)] out T item)
    {
        if (requests.Count == 0)
        {
            source = 0;
            item = default;
            return false;
        }
        (source, item) = requests[0];
        requests.RemoveAt(0);
        return true;
    }

    public void Clear() => requests.Clear();
}
