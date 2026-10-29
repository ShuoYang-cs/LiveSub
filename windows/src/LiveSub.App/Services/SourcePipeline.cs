using LiveSub.App.Audio;
using LiveSub.Core;
using LiveSub.Core.Audio;

namespace LiveSub.App.Services;

/// <summary>音源的种类：真实的麦克风、系统声音（环回采集），或测试用的音频文件。</summary>
internal enum SourceKind { Microphone, SystemAudio, TestFile }

/// <summary>
/// 一个音源的链路（与 macOS 版的 SourcePipeline 相同）：采集 → 分段 → （共用的 Whisper 识别器）→ 分句。
/// “两者”模式下有两条，各自有分段器、分句器、时间轴和“正在说”，采集中断时各自恢复；
/// 标签（“麦克风”“系统音频”）只在“两者”模式下有。
/// 除了标明的地方，只在界面线程上访问。
/// </summary>
internal sealed class SourcePipeline(int index, SourceKind kind, string? label)
{
    /// <summary>送给识别器的音源序号（在 <see cref="SubtitleController"/> 的音源列表里的位置）。</summary>
    public int Index { get; } = index;
    public SourceKind Kind { get; } = kind;
    public string? Label { get; } = label;

    /// <summary>是真实的麦克风：隐私设置可能挡住它。</summary>
    public bool IsMicrophone => Kind == SourceKind.Microphone;

    /// <summary>写日志用的名字。</summary>
    public string Name => Kind switch
    {
        SourceKind.Microphone => "microphone",
        SourceKind.SystemAudio => "loopback",
        _ => "test file",
    };

    public IAudioSource? Source;
    public DefaultDeviceWatcher? DeviceWatcher;
    /// <summary>录音：打开时把这一路的声音存进文件（与 macOS 相同；“两者”时每路一个文件）。采集重新打开时接着用它。</summary>
    public readonly RecorderSlot Recording = new();

    /// <summary>采集线程和界面线程都会用分段器，由它保护。</summary>
    public readonly object ChunkerGate = new();
    public SpeechChunker Chunker = new();
    public Segmenter Segmenter = new();
    public SegmentTimeline Timeline = new();
    /// <summary>实时预览的文字。</summary>
    public string VolatileText = "";
    /// <summary>最近结束的一段语音的结束样本：早于它开始的预览已经过时（采集线程写）。</summary>
    public long LastClosedChunkEnd = -1;
    public DateTime LastPreviewAt = DateTime.MinValue;

    // 采集中断后的恢复（字幕不停）
    /// <summary>采集断了，正在等它恢复。</summary>
    public bool AudioLost;
    public DateTime NextAudioRetry;
    /// <summary>这次中断以来失败了几次（一直恢复不了时不要每次都写日志）。</summary>
    public int AudioFailures;
    /// <summary>当前这次采集是什么时候打开的。</summary>
    public DateTime SourceStartedAt;
    /// <summary>麦克风被 Windows 隐私设置挡住（或多半如此）。</summary>
    public MicrophoneBlock Block;
    /// <summary>正在停掉或重新打开这个音源的采集（采集中断、默认设备变了）；这期间不再处理它的其他采集事件。</summary>
    public bool Switching;

    /// <summary>正在说、还没送去翻译的文字（分句器里剩下的 + 实时预览）。</summary>
    public string LiveText => Segmenter.Join(Segmenter.Pending, VolatileText);
}
