using LiveSub.Core.Audio;
using LiveSub.Core.Speech;

namespace LiveSub.Core.Tests;

public class AudioConverterTests
{
    private static byte[] FloatStereo(float[] left, float[] right)
    {
        var bytes = new byte[left.Length * 8];
        for (int i = 0; i < left.Length; i++)
        {
            BitConverter.GetBytes(left[i]).CopyTo(bytes, i * 8);
            BitConverter.GetBytes(right[i]).CopyTo(bytes, i * 8 + 4);
        }
        return bytes;
    }

    [Fact]
    public void DownmixesAndResamples48kStereoTo16kMono()
    {
        int n = 48_000;
        var left = Enumerable.Repeat(0.5f, n).ToArray();
        var right = Enumerable.Repeat(0.1f, n).ToArray();
        var c = new AudioConverter(48_000, 2, SampleFormat.Float32);
        var output = c.Convert(FloatStereo(left, right));
        Assert.InRange(output.Length, 15_999, 16_001);
        Assert.All(output.Skip(2), s => Assert.Equal(0.3f, s, 3));
        Assert.Equal(0.3f, c.LastRms, 2);
    }

    [Fact]
    public void ChunkedInputMatchesOneShot()
    {
        int n = 4410;
        var signal = Enumerable.Range(0, n).Select(i => (float)Math.Sin(2 * Math.PI * 440 * i / 44_100.0) * 0.5f).ToArray();
        var bytes = FloatStereo(signal, signal);

        var whole = new AudioConverter(44_100, 2, SampleFormat.Float32).Convert(bytes);
        var chunked = new AudioConverter(44_100, 2, SampleFormat.Float32);
        var parts = new List<float>();
        // 故意在帧中间、甚至样本中间断开。
        int pos = 0;
        foreach (var size in new[] { 3, 1001, 7, 5000 })
        {
            parts.AddRange(chunked.Convert(bytes.AsSpan(pos, size)));
            pos += size;
        }
        parts.AddRange(chunked.Convert(bytes.AsSpan(pos)));

        Assert.Equal(whole.Length, parts.Count);
        for (int i = 0; i < whole.Length; i++) Assert.Equal(whole[i], parts[i], 5);
        Assert.InRange(whole.Length, 1599, 1601);
    }

    [Fact]
    public void DecodesInt16AndInt24()
    {
        var pcm16 = new AudioConverter(16_000, 1, SampleFormat.Int16);
        var out16 = pcm16.Convert([0x00, 0x40, 0x00, 0xC0]); // 0.5, -0.5
        Assert.Equal([0.5f, -0.5f], out16);

        var pcm24 = new AudioConverter(16_000, 1, SampleFormat.Int24);
        var out24 = pcm24.Convert([0x00, 0x00, 0x40, 0x00, 0x00, 0xC0]);
        Assert.Equal([0.5f, -0.5f], out24);
    }

    [Fact]
    public void PassthroughAt16k()
    {
        var r = new StreamingResampler(16_000, 16_000);
        Assert.Equal([0.1f, 0.2f, 0.3f], r.Process([0.1f, 0.2f, 0.3f]));
    }
}

public class SpeechChunkerTests
{
    private const int Rate = 16_000;

    private static float[] Silence(double seconds, float level = 0.001f)
    {
        var rnd = new Random(1);
        return Enumerable.Range(0, (int)(seconds * Rate)).Select(_ => (float)(rnd.NextDouble() * 2 - 1) * level).ToArray();
    }

    private static float[] Tone(double seconds, float amplitude = 0.2f) =>
        Enumerable.Range(0, (int)(seconds * Rate)).Select(i => (float)Math.Sin(2 * Math.PI * 220 * i / Rate) * amplitude).ToArray();

    private static List<SpeechChunk> Run(SpeechChunker c, params float[][] parts)
    {
        var chunks = new List<SpeechChunk>();
        foreach (var p in parts)
        {
            // 每次送 10 ms，模拟采集回调。
            for (int i = 0; i < p.Length; i += 160) chunks.AddRange(c.Feed(p.AsSpan(i, Math.Min(160, p.Length - i))));
        }
        return chunks;
    }

    [Fact]
    public void OneUtteranceBetweenSilences()
    {
        var c = new SpeechChunker();
        var chunks = Run(c, Silence(1), Tone(2), Silence(1));
        var chunk = Assert.Single(chunks);
        Assert.InRange(chunk.StartSeconds, 0.75, 1.0);  // 带 0.2 秒预留
        Assert.InRange(chunk.EndSeconds, 3.0, 3.3);     // 末尾保留 0.2 秒
        Assert.Equal(chunk.Samples.Length, (int)(chunk.EndSample - chunk.StartSample));
        Assert.False(c.InSpeech);
    }

    [Fact]
    public void ShortPauseDoesNotSplitButLongPauseDoes()
    {
        var c = new SpeechChunker();
        Assert.Single(Run(c, Silence(0.5), Tone(1), Silence(0.3), Tone(1), Silence(1)));

        var c2 = new SpeechChunker();
        Assert.Equal(2, Run(c2, Silence(0.5), Tone(1), Silence(0.8), Tone(1), Silence(1)).Count);
    }

    [Fact]
    public void ClicksAreIgnored()
    {
        var c = new SpeechChunker();
        Assert.Empty(Run(c, Silence(0.5), Tone(0.12), Silence(1)));
    }

    [Fact]
    public void LongSpeechIsCutAtMaximumLength()
    {
        var c = new SpeechChunker();
        var chunks = Run(c, Silence(0.5), Tone(20), Silence(1));
        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, ch => Assert.True(ch.DurationSeconds <= 8.01, $"chunk {ch.DurationSeconds:F2}s"));
        // 切开的各段首尾相接，没有丢音频。
        for (int i = 1; i < chunks.Count - 1; i++) Assert.Equal(chunks[i - 1].EndSample, chunks[i].StartSample);
        Assert.InRange(chunks[^1].EndSeconds, 20.4, 20.8);
    }

    [Fact]
    public void FlushSendsSpeechInProgressAndSnapshotSeesIt()
    {
        var c = new SpeechChunker();
        Run(c, Silence(0.5), Tone(1.5));
        Assert.True(c.InSpeech);
        var snap = c.Snapshot();
        Assert.NotNull(snap);
        var flushed = c.Flush();
        Assert.NotNull(flushed);
        Assert.InRange(flushed!.DurationSeconds, 1.5, 1.8);
        Assert.Null(c.Snapshot());
    }

    [Fact]
    public void RestartedChunkerContinuesTheTimeline()
    {
        // 换了音频设备后重新采集：样本序号接着原来的时间轴（这里从第 100 秒开始）。
        var c = new SpeechChunker(startSample: 100 * Rate);
        var chunk = Assert.Single(Run(c, Silence(1), Tone(2), Silence(1)));
        Assert.InRange(chunk.StartSeconds, 100.75, 101.0);
        Assert.Equal(104 * Rate, c.ProcessedSamples);
    }

    [Fact]
    public void DigitalSilenceProducesNothing()
    {
        var c = new SpeechChunker();
        Assert.Empty(Run(c, new float[Rate * 3]));
        Assert.Equal(Rate * 3, c.ProcessedSamples);
    }
}

public class MicrophoneAccessTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Later = TimeSpan.FromMinutes(5);

    [Fact]
    public void AnyDenySwitchMeansDenied()
    {
        Assert.True(MicrophoneAccess.IsDenied(["Allow", "Allow", "Deny"]));
        Assert.True(MicrophoneAccess.IsDenied([null, "deny ", null]));
        Assert.False(MicrophoneAccess.IsDenied(["Allow", "Allow", "Allow"]));
        // 读不到开关（键或值不存在）时不能断定被关闭。
        Assert.False(MicrophoneAccess.IsDenied([null, null, null]));
    }

    [Fact]
    public void DenySwitchOrAccessDeniedIsCertain()
    {
        Assert.Equal(MicrophoneBlock.Certain, MicrophoneAccess.Classify(true, false, true, Later, true));
        Assert.Equal(MicrophoneBlock.Certain, MicrophoneAccess.Classify(false, true, false, TimeSpan.Zero, true));
    }

    [Fact]
    public void EarlyDisconnectWithMicrophonePresentIsLikely()
    {
        Assert.Equal(MicrophoneBlock.Likely, MicrophoneAccess.Classify(false, false, true, Soon, true));
    }

    [Fact]
    public void UnpluggedOrLateDisconnectIsNotPermission()
    {
        // 麦克风被拔掉：没有默认录音设备了。
        Assert.Equal(MicrophoneBlock.None, MicrophoneAccess.Classify(false, false, true, Soon, false));
        // 用了很久才断开：多半是设备或采样率变了，重新打开就好。
        Assert.Equal(MicrophoneBlock.None, MicrophoneAccess.Classify(false, false, true, Later, true));
        // 其他错误。
        Assert.Equal(MicrophoneBlock.None, MicrophoneAccess.Classify(false, false, false, Soon, true));
    }
}

public class SourceMixTests
{
    [Fact]
    public void DisplayLevelIsOnADecibelScale()
    {
        Assert.Equal(0f, SourceMix.DisplayLevel(0));
        Assert.Equal(0f, SourceMix.DisplayLevel(0.0005f)); // -66 dB
        Assert.Equal(0.5f, SourceMix.DisplayLevel(0.01f)); // -40 dB
        Assert.Equal(1f, SourceMix.DisplayLevel(0.5f));
        Assert.True(SourceMix.DisplayLevel(0.003f) > 0); // -50 dB：原来的线性映射里是 0
    }

    [Fact]
    public void LevelIsTheLoudestAndSilenceTheShortest()
    {
        var mix = SourceMix.Combine([new SourceInput(0.02f, 0.1), new SourceInput(0.3f, 12)]);
        Assert.Equal(0.3f, mix.Level);
        Assert.Equal(0.1, mix.SilentFor);
    }

    [Fact]
    public void LostSourcesAreIgnored()
    {
        // 麦克风被挡住、正在等它恢复：系统音频照常收到声音时不算“无音频输入”。
        var mix = SourceMix.Combine([null, new SourceInput(0.1f, 0.2)]);
        Assert.Equal(0.1f, mix.Level);
        Assert.Equal(0.2, mix.SilentFor);
        var none = SourceMix.Combine([null, null]);
        Assert.Equal(0f, none.Level);
        Assert.True(double.IsPositiveInfinity(none.SilentFor));
        Assert.True(double.IsPositiveInfinity(SourceMix.Combine([]).SilentFor));
    }

    [Fact]
    public void LiveRowsKeepSourceOrderAndDropNoise()
    {
        var rows = SourceMix.LiveRows([new LiveRow("麦克风", "."), new LiveRow("系统音频", "so the next step"), new LiveRow(null, "")]);
        Assert.Equal([new LiveRow("系统音频", "so the next step")], rows);
        var both = SourceMix.LiveRows([new LiveRow("麦克风", "我想问"), new LiveRow("系统音频", "eigen 2")]);
        Assert.Equal(["麦克风", "系统音频"], both.Select(r => r.Label));
    }

    [Fact]
    public void SpeakingJoinsSourcesWithSpaces()
    {
        Assert.Equal("first part second part", SourceMix.Speaking(["first part ", "", " second part"]));
        Assert.Equal("", SourceMix.Speaking(["", "  "]));
    }
}

public class PreviewQueueTests
{
    [Fact]
    public void KeepsOnlyTheNewestRequestPerSource()
    {
        var queue = new PreviewQueue<string>();
        queue.Request(0, "mic 1");
        queue.Request(0, "mic 2");
        Assert.Equal(1, queue.Count);
        Assert.True(queue.TryTake(out var source, out var item));
        Assert.Equal((0, "mic 2"), (source, item));
        Assert.False(queue.TryTake(out _, out _));
    }

    [Fact]
    public void SourcesTakeTurns()
    {
        // 麦克风先请求，系统音频接着请求；麦克风还在排队时再次请求，只换成新的内容，仍排在前面。
        var queue = new PreviewQueue<string>();
        queue.Request(0, "mic 1");
        queue.Request(1, "system 1");
        queue.Request(0, "mic 2");
        Assert.True(queue.TryTake(out var first, out var firstItem));
        Assert.Equal((0, "mic 2"), (first, firstItem));
        // 麦克风识别过之后再请求：排到系统音频后面。
        queue.Request(0, "mic 3");
        Assert.True(queue.TryTake(out var second, out var secondItem));
        Assert.True(queue.TryTake(out var third, out var thirdItem));
        Assert.Equal((1, "system 1"), (second, secondItem));
        Assert.Equal((0, "mic 3"), (third, thirdItem));
    }

    [Fact]
    public void SourceRequestingFirstEveryTickDoesNotStarveTheOther()
    {
        // 识别一次预览比请求间隔慢：每轮两个音源都再请求一次（麦克风总是先请求），识别器只来得及取一个。
        var queue = new PreviewQueue<string>();
        var served = new int[2];
        for (int tick = 0; tick < 20; tick++)
        {
            queue.Request(0, $"mic {tick}");
            queue.Request(1, $"system {tick}");
            Assert.True(queue.TryTake(out var source, out var item));
            Assert.EndsWith($" {tick}", item);
            served[source]++;
        }
        Assert.Equal([10, 10], served);
    }

    [Fact]
    public void RemovingASourceKeepsTheOther()
    {
        var queue = new PreviewQueue<string>();
        queue.Request(0, "mic");
        queue.Request(1, "system");
        queue.Remove(0);
        Assert.True(queue.TryTake(out var source, out _));
        Assert.Equal(1, source);
        queue.Request(0, "mic");
        queue.Clear();
        Assert.Equal(0, queue.Count);
    }
}
