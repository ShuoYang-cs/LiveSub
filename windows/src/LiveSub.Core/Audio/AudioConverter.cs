using System.Buffers.Binary;

namespace LiveSub.Core.Audio;

/// <summary>采集设备给出的样本格式。</summary>
public enum SampleFormat { Float32, Int16, Int24, Int32 }

/// <summary>
/// 把采集设备的 PCM 数据（任意采样率、声道数）转换成识别用的 16 kHz 单声道 float。
/// 有状态：数据块可以在任意字节处断开，跨块的半帧会被保留到下一块。
/// </summary>
public sealed class AudioConverter
{
    public const int TargetRate = 16_000;

    private readonly int channels;
    private readonly SampleFormat format;
    private readonly int bytesPerSample;
    private readonly int blockAlign;
    private readonly StreamingResampler resampler;
    private readonly byte[] carry;
    private int carryCount;

    /// <summary>最近一块数据的音量（RMS，0…1）。</summary>
    public float LastRms { get; private set; }

    public AudioConverter(int sampleRate, int channels, SampleFormat format, int targetRate = TargetRate)
    {
        if (sampleRate <= 0 || channels <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        this.channels = channels;
        this.format = format;
        bytesPerSample = format switch
        {
            SampleFormat.Int16 => 2,
            SampleFormat.Int24 => 3,
            _ => 4,
        };
        blockAlign = bytesPerSample * channels;
        resampler = new StreamingResampler(sampleRate, targetRate);
        carry = new byte[blockAlign];
    }

    public float[] Convert(ReadOnlySpan<byte> data)
    {
        // 先补齐上一块留下的半帧。
        int total = carryCount + data.Length;
        int frames = total / blockAlign;
        if (frames == 0)
        {
            data.CopyTo(carry.AsSpan(carryCount));
            carryCount += data.Length;
            return [];
        }
        var mono = new float[frames];
        int frame = 0;
        if (carryCount > 0)
        {
            int need = blockAlign - carryCount;
            data[..need].CopyTo(carry.AsSpan(carryCount));
            mono[frame++] = Downmix(carry);
            data = data[need..];
            carryCount = 0;
        }
        while (frame < frames)
        {
            mono[frame++] = Downmix(data[..blockAlign]);
            data = data[blockAlign..];
        }
        if (data.Length > 0)
        {
            data.CopyTo(carry);
            carryCount = data.Length;
        }
        var output = resampler.Process(mono);
        LastRms = Rms(output);
        return output;
    }

    private float Downmix(ReadOnlySpan<byte> frame)
    {
        float sum = 0;
        for (int c = 0; c < channels; c++)
        {
            sum += Sample(frame.Slice(c * bytesPerSample, bytesPerSample));
        }
        return sum / channels;
    }

    private float Sample(ReadOnlySpan<byte> b) => format switch
    {
        SampleFormat.Float32 => BinaryPrimitives.ReadSingleLittleEndian(b),
        SampleFormat.Int16 => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f,
        SampleFormat.Int24 => ((b[0] | (b[1] << 8) | (b[2] << 16)) << 8 >> 8) / 8388608f,
        _ => BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648f,
    };

    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return 0;
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        return (float)Math.Sqrt(sum / samples.Length);
    }
}

/// <summary>
/// 流式重采样：降采样前先做长度为 round(比例) 的滑动平均（简单的抗混叠），再线性插值。
/// 对语音识别足够；数据分块送入与一次送入的结果一致。
/// </summary>
public sealed class StreamingResampler
{
    private readonly double step;
    private readonly float[] window;
    private int windowFill;
    private int windowPos;
    private double windowSum;
    private long index;      // 下一个滤波后样本的序号
    private float last;      // 上一个滤波后样本
    private double nextTime; // 下一个输出样本在输入时间轴上的位置

    public StreamingResampler(int inputRate, int outputRate)
    {
        step = (double)inputRate / outputRate;
        int taps = step > 1 ? Math.Max(1, (int)Math.Round(step)) : 1;
        window = new float[taps];
    }

    public float[] Process(ReadOnlySpan<float> input)
    {
        var output = new List<float>((int)(input.Length / step) + 2);
        foreach (var x in input)
        {
            // 滑动平均
            if (windowFill == window.Length) windowSum -= window[windowPos];
            else windowFill++;
            window[windowPos] = x;
            windowSum += x;
            windowPos = (windowPos + 1) % window.Length;
            float filtered = (float)(windowSum / windowFill);

            // 线性插值：在 (index-1, index] 之间的输出点
            while (nextTime <= index)
            {
                double frac = nextTime - (index - 1);
                output.Add((float)(last + (filtered - last) * frac));
                nextTime += step;
            }
            last = filtered;
            index++;
        }
        return output.ToArray();
    }
}
