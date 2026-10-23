using LiveSub.Core.Audio;

namespace LiveSub.Core.Tests;

public class RecordingFilesTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "LiveSub");
    private static readonly string Transcript = Path.Combine(Folder, "2026-10-22 13.35 字幕记录.md");

    [Fact]
    public void RecordingIsNamedAfterTheTranscript()
    {
        Assert.Equal(Path.Combine(Folder, "2026-10-22 13.35 录音.m4a"),
            RecordingFiles.PathFor(Transcript, "字幕记录", "录音", null, _ => false));
        // “两者”时每路一个文件，后面带音源。
        Assert.Equal(Path.Combine(Folder, "2026-10-22 13.35 录音（麦克风）.m4a"),
            RecordingFiles.PathFor(Transcript, "字幕记录", "录音", "（麦克风）", _ => false));
        // 记录是另一种界面语言下建的：在后面加上，不和记录同名。
        Assert.Equal(Path.Combine(Folder, "2026-10-22 13.35 Transcript Recording.m4a"),
            RecordingFiles.PathFor(Path.Combine(Folder, "2026-10-22 13.35 Transcript.md"), "字幕记录", "Recording", null, _ => false));
    }

    [Fact]
    public void ExistingRecordingsAreNotOverwritten()
    {
        var taken = new HashSet<string>
        {
            Path.Combine(Folder, "2026-10-22 13.35 录音.m4a"),
            Path.Combine(Folder, "2026-10-22 13.35 录音 2.m4a"),
        };
        Assert.Equal(Path.Combine(Folder, "2026-10-22 13.35 录音 3.m4a"),
            RecordingFiles.PathFor(Transcript, "字幕记录", "录音", null, taken.Contains));
    }

    [Fact]
    public void FindsTheTranscriptOfARecording()
    {
        var exists = (string p) => p == Transcript;
        foreach (var name in new[] { "2026-10-22 13.35 录音.m4a", "2026-10-22 13.35 录音（系统音频）.m4a", "2026-10-22 13.35 录音 2.m4a",
                     "2026-10-22 13.35 录音（麦克风） 2.m4a" })
        {
            Assert.Equal(Transcript, RecordingFiles.TranscriptFor(Path.Combine(Folder, name), "字幕记录", "录音", exists));
        }
        Assert.Equal(Path.Combine(Folder, "2026-10-22 13.35 Transcript.md"),
            RecordingFiles.TranscriptFor(Path.Combine(Folder, "2026-10-22 13.35 Recording (Microphone).m4a"), "Transcript", "Recording", _ => true));
        Assert.Null(RecordingFiles.TranscriptFor(Path.Combine(Folder, "2026-10-23 09.00 录音.m4a"), "字幕记录", "录音", exists));
        Assert.Null(RecordingFiles.TranscriptFor(Path.Combine(Folder, "music.m4a"), "字幕记录", "录音", _ => true));
    }

    [Fact]
    public void DurationText()
    {
        Assert.Equal("0:07", RecordingFiles.Duration(TimeSpan.FromSeconds(6.6)));
        Assert.Equal("12:05", RecordingFiles.Duration(TimeSpan.FromSeconds(725)));
        Assert.Equal("1:02:03", RecordingFiles.Duration(TimeSpan.FromSeconds(3723)));
    }

    [Fact]
    public void FloatSamplesBecome16BitPcm()
    {
        var bytes = Pcm16.FromFloat([0f, 1f, -1f, 0.5f, 2f, float.NaN]);
        var values = Enumerable.Range(0, bytes.Length / 2).Select(i => BitConverter.ToInt16(bytes, i * 2)).ToArray();
        Assert.Equal(new short[] { 0, 32767, -32767, 16384, 32767, 0 }, values);
    }
}

public class OverlayLayoutTests
{
    [Fact]
    public void DefaultSizeIsASixteenthOfTheScreenAtThreeToOne()
    {
        var (w, h) = OverlayLayout.DefaultSize(1920, 1080);
        Assert.Equal(624, w);
        Assert.Equal(208, h);
        Assert.InRange(w * h / (1920.0 * 1080), 0.062, 0.064);
        // 小屏幕上也不窄于 320。
        Assert.Equal(320, OverlayLayout.DefaultSize(640, 400).Width);
    }

    [Fact]
    public void DefaultPositionIsTopRightAndClampedOnScreen()
    {
        var work = new ScreenRect(0, 0, 1920, 1040);
        Assert.Equal((1280.0, 16.0), OverlayLayout.TopRight(work, 624));
        // 往上长、往右长出界时推回屏幕内。
        Assert.Equal((1296.0, 0.0), OverlayLayout.Clamp(new ScreenRect(1500, -40, 624, 300), work));
        Assert.Equal((100.0, 200.0), OverlayLayout.Clamp(new ScreenRect(100, 200, 624, 300), work));
        // 按物理像素放（主屏 150%）：离边缘的距离也放大。
        Assert.Equal((2880 - 936 - 24.0, 24.0), OverlayLayout.TopRight(new ScreenRect(0, 0, 2880, 1560), 936, OverlayLayout.Margin * 1.5));
    }

    [Fact]
    public void SettingsWindowsFitTheWorkArea()
    {
        // 1366×768 放大到 150%：工作区约 911×482，760 高的设置窗口打开时缩到 442。
        Assert.Equal(442, WindowFit.Size(760, 482));
        Assert.Equal(760, WindowFit.Size(760, 1040));
        // 显示后标题栏在屏幕上面外：缩小并推回工作区。
        var work = new ScreenRect(0, 0, 911, 482);
        Assert.Equal(new ScreenRect(0, 0, 560, 482), WindowFit.Fit(new ScreenRect(-20, -139, 560, 760), work));
        // 在第二块屏幕上、本来就在工作区里：不动。
        var second = new ScreenRect(1920, 0, 1280, 1000);
        var frame = new ScreenRect(2000, 100, 560, 700);
        Assert.Equal(frame, WindowFit.Fit(frame, second));
    }
}

public class RecordingClockTests
{
    private const int Rate = 48_000;
    private static readonly DateTime T0 = new(2026, 10, 23, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>从 <paramref name="from"/> 开始连续送 <paramref name="seconds"/> 秒、每块 10 毫秒的声音，返回补的静音总数。</summary>
    private static long Feed(RecordingClock clock, DateTime from, double seconds, bool newCapture)
    {
        long gaps = 0;
        int blocks = (int)Math.Round(seconds * 100);
        for (int i = 1; i <= blocks; i++) gaps += clock.Add(Rate / 100, from.AddMilliseconds(i * 10), newCapture && i == 1);
        return gaps;
    }

    [Fact]
    public void ContinuousCaptureNeedsNoPadding()
    {
        var clock = new RecordingClock(Rate);
        Assert.Equal(0, Feed(clock, T0, 5, newCapture: true));
        Assert.Equal(5 * Rate, clock.Written);
        // 中断不到 0.1 秒的新采集：不补。
        Assert.Equal(0, Feed(clock, T0.AddSeconds(5.05), 1, newCapture: true));
    }

    [Fact]
    public void GapBetweenCapturesIsFilledWithSilence()
    {
        var clock = new RecordingClock(Rate);
        Feed(clock, T0, 10, newCapture: true);
        // 断了 4 秒才重新打开：补 4 秒，之后录音和真实时间对齐。
        long gap = Feed(clock, T0.AddSeconds(14), 2, newCapture: true);
        Assert.InRange(gap, (long)(3.99 * Rate), (long)(4.01 * Rate));
        Assert.InRange(clock.Written, (long)(15.99 * Rate), (long)(16.01 * Rate));
    }

    [Fact]
    public void LongGapsAreCappedAndDoNotCarryOver()
    {
        var clock = new RecordingClock(Rate);
        Feed(clock, T0, 1, newCapture: true);
        // 睡眠了两小时：只补 30 秒。
        Assert.Equal(30 * Rate, Feed(clock, T0.AddHours(2), 1, newCapture: true));
        // 之后又断了 3 秒：只补这 3 秒，不再补睡眠的时间。
        long gap = Feed(clock, T0.AddHours(2).AddSeconds(4), 1, newCapture: true);
        Assert.InRange(gap, (long)(2.99 * Rate), (long)(3.01 * Rate));
        // 不是新采集的块（同一个采集里）不补。
        Assert.Equal(0, clock.Add(Rate / 100, T0.AddHours(3), newCapture: false));
    }
}
