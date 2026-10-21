using LiveSub.Core.Audio;

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
