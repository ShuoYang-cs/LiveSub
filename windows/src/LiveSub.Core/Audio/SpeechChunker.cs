namespace LiveSub.Core.Audio;

/// <summary>一段要送去识别的语音，样本位置是从开始采集算起的样本序号。</summary>
public sealed record SpeechChunk(float[] Samples, long StartSample, long EndSample, int SampleRate = AudioConverter.TargetRate)
{
    public double StartSeconds => (double)StartSample / SampleRate;
    public double EndSeconds => (double)EndSample / SampleRate;
    public double DurationSeconds => (double)(EndSample - StartSample) / SampleRate;
}

/// <summary>
/// 按音量做简单的语音检测，把连续的音频切成一段段送给 Whisper（Whisper 本身不是流式的）。
///
/// - 每 30 ms 一帧，音量高于“底噪 × 倍数”算有声。底噪在没说话时较快地跟随，说话时极慢地上浮，
///   这样持续的背景音乐、风扇声最终会被当成底噪，而正常讲话不会。
/// - 连续 3 帧有声开始一段，并带上前面 0.2 秒；停顿约 0.6 秒结束一段，末尾保留 0.2 秒。
/// - 一段最长约 8 秒：到了就在最后 1.5 秒里最安静的地方切开，剩下的接着算下一段。
/// - 有声部分太短（咔哒声）的段直接丢掉。
/// </summary>
public sealed class SpeechChunker
{
    public sealed record Options
    {
        public int SampleRate { get; init; } = AudioConverter.TargetRate;
        public double FrameSeconds { get; init; } = 0.03;
        public double PauseSeconds { get; init; } = 0.6;
        public double MaxChunkSeconds { get; init; } = 8;
        public double MinSpeechSeconds { get; init; } = 0.25;
        public double PreRollSeconds { get; init; } = 0.2;
        public double TrailSeconds { get; init; } = 0.2;
        public double SplitSearchSeconds { get; init; } = 1.5;
        /// <summary>绝对下限，约 -46 dBFS。</summary>
        public float MinThreshold { get; init; } = 0.005f;
        /// <summary>绝对上限，避免底噪估计过高时什么都听不到。</summary>
        public float MaxThreshold { get; init; } = 0.08f;
        public float NoiseFactor { get; init; } = 2.0f;
        public int StartFrames { get; init; } = 3;
    }

    private readonly Options o;
    private readonly int frameSamples;
    private readonly float[] frame;
    private int frameFill;
    private long frameStart; // 当前帧第一个样本的序号

    // 没在说话时：最近几帧（作为开头的预留）
    private readonly LinkedList<(float[] Samples, long Start, float Rms, bool Speech)> preRoll = new();
    private readonly int preRollFrames;
    private int consecutiveSpeech;

    // 正在说话时：当前这一段
    private readonly List<float> chunk = [];
    private readonly List<(float Rms, bool Speech)> chunkFrames = [];
    private long chunkStart;
    private long lastSpeechEnd;
    private int silenceFrames;

    private float noiseFloor = -1;

    public bool InSpeech { get; private set; }

    /// <summary>当前帧的音量（RMS）。</summary>
    public float CurrentRms { get; private set; }

    /// <summary>已经处理过的样本数（音频时间轴）。</summary>
    public long ProcessedSamples => frameStart + frameFill;

    /// <param name="startSample">第一个样本的序号。采集中途重启（换了音频设备）时接着原来的时间轴编号。</param>
    public SpeechChunker(Options? options = null, long startSample = 0)
    {
        o = options ?? new Options();
        frameStart = startSample;
        frameSamples = Math.Max(1, (int)Math.Round(o.FrameSeconds * o.SampleRate));
        frame = new float[frameSamples];
        preRollFrames = (int)Math.Ceiling(o.PreRollSeconds / o.FrameSeconds) + o.StartFrames;
    }

    /// <summary>送入 16 kHz 单声道样本，返回已经结束的语音段。</summary>
    public List<SpeechChunk> Feed(ReadOnlySpan<float> samples)
    {
        var output = new List<SpeechChunk>();
        while (samples.Length > 0)
        {
            int n = Math.Min(frameSamples - frameFill, samples.Length);
            samples[..n].CopyTo(frame.AsSpan(frameFill));
            frameFill += n;
            samples = samples[n..];
            if (frameFill == frameSamples)
            {
                ProcessFrame(output);
                frameStart += frameSamples;
                frameFill = 0;
            }
        }
        return output;
    }

    /// <summary>停止采集时调用：把正在进行的一段送出。</summary>
    public SpeechChunk? Flush()
    {
        SpeechChunk? result = null;
        if (InSpeech)
        {
            if (frameFill > 0) chunk.AddRange(frame.AsSpan(0, frameFill).ToArray());
            result = Close(chunkStart + chunk.Count);
        }
        frameStart += frameFill;
        frameFill = 0;
        preRoll.Clear();
        consecutiveSpeech = 0;
        return result;
    }

    /// <summary>正在说的这一段到目前为止的音频（给“实时预览”用）；没在说话或太短时为 null。</summary>
    public SpeechChunk? Snapshot(double minSeconds = 1.0)
    {
        if (!InSpeech || chunk.Count < minSeconds * o.SampleRate) return null;
        return new SpeechChunk(chunk.ToArray(), chunkStart, chunkStart + chunk.Count, o.SampleRate);
    }

    private void ProcessFrame(List<SpeechChunk> output)
    {
        float rms = AudioConverter.Rms(frame);
        CurrentRms = rms;
        if (noiseFloor < 0) noiseFloor = rms;
        float threshold = Math.Clamp(noiseFloor * o.NoiseFactor, o.MinThreshold, o.MaxThreshold);
        bool speech = rms > threshold;
        // 底噪：安静时较快跟随（约 0.6 秒），有声时极慢上浮（约 60 秒），变小时立即跟上。
        if (rms < noiseFloor) noiseFloor = rms;
        else noiseFloor += (rms - noiseFloor) * (speech ? 0.0005f : 0.05f);

        var samples = frame.ToArray();
        if (!InSpeech)
        {
            preRoll.AddLast((samples, frameStart, rms, speech));
            while (preRoll.Count > preRollFrames) preRoll.RemoveFirst();
            consecutiveSpeech = speech ? consecutiveSpeech + 1 : 0;
            if (consecutiveSpeech >= o.StartFrames)
            {
                // 开始一段：带上前面的预留帧。
                InSpeech = true;
                chunk.Clear();
                chunkFrames.Clear();
                chunkStart = preRoll.First!.Value.Start;
                foreach (var f in preRoll)
                {
                    chunk.AddRange(f.Samples);
                    chunkFrames.Add((f.Rms, f.Speech));
                }
                preRoll.Clear();
                lastSpeechEnd = frameStart + frameSamples;
                silenceFrames = 0;
                consecutiveSpeech = 0;
            }
            return;
        }

        chunk.AddRange(samples);
        chunkFrames.Add((rms, speech));
        if (speech)
        {
            silenceFrames = 0;
            lastSpeechEnd = frameStart + frameSamples;
        }
        else
        {
            silenceFrames++;
        }

        if (silenceFrames * o.FrameSeconds >= o.PauseSeconds - 1e-9)
        {
            var end = Math.Min(chunkStart + chunk.Count, lastSpeechEnd + (long)(o.TrailSeconds * o.SampleRate));
            if (Close(end) is { } c) output.Add(c);
        }
        else if (chunk.Count >= o.MaxChunkSeconds * o.SampleRate)
        {
            SplitLongChunk(output);
        }
    }

    /// <summary>到达最长时长：在最后一段里最安静的帧处切开，前半段送出，后半段继续。</summary>
    private void SplitLongChunk(List<SpeechChunk> output)
    {
        int frames = chunkFrames.Count;
        int searchFrames = Math.Min(frames - 1, (int)(o.SplitSearchSeconds / o.FrameSeconds));
        int best = frames - 1;
        float bestRms = float.MaxValue;
        for (int i = frames - 1; i >= frames - searchFrames; i--)
        {
            if (chunkFrames[i].Rms < bestRms)
            {
                bestRms = chunkFrames[i].Rms;
                best = i;
            }
        }
        int splitSample = Math.Clamp(best * frameSamples, frameSamples, chunk.Count);
        var head = chunk.GetRange(0, splitSample).ToArray();
        int headSpeech = chunkFrames.Take(best).Count(f => f.Speech);
        if (headSpeech * o.FrameSeconds >= o.MinSpeechSeconds)
        {
            output.Add(new SpeechChunk(head, chunkStart, chunkStart + splitSample, o.SampleRate));
        }
        chunk.RemoveRange(0, splitSample);
        chunkFrames.RemoveRange(0, Math.Min(best, chunkFrames.Count));
        chunkStart += splitSample;
    }

    private SpeechChunk? Close(long end)
    {
        InSpeech = false;
        int length = (int)Math.Clamp(end - chunkStart, 0, chunk.Count);
        int speechFrames = chunkFrames.Count(f => f.Speech);
        SpeechChunk? result = speechFrames * o.FrameSeconds >= o.MinSpeechSeconds - 1e-9 && length > 0
            ? new SpeechChunk(chunk.GetRange(0, length).ToArray(), chunkStart, chunkStart + length, o.SampleRate)
            : null;
        chunk.Clear();
        chunkFrames.Clear();
        silenceFrames = 0;
        return result;
    }
}
