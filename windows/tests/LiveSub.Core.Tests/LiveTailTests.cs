using LiveSub.Core;

namespace LiveSub.Core.Tests;

// “正在说”一行的从头省略（Windows 特有，macOS 用 SwiftUI 的 truncationMode(.head)）。
// 测试里每个字宽 10，“…”也算一个字。

public class LiveTailTests
{
    private static double Measure(string s) => s.Length * 10;

    [Fact]
    public void TextThatFitsIsUnchanged()
    {
        Assert.Equal("hello there", LiveTail.Fit("hello there", 110, Measure));
    }

    [Fact]
    public void NotLaidOutYetReturnsText()
    {
        Assert.Equal("one two three", LiveTail.Fit("one two three", 0, Measure));
        Assert.Equal("", LiveTail.Fit("", 100, Measure));
    }

    [Fact]
    public void SpacedTextIsCutAtWordStarts()
    {
        Assert.Equal("…three four", LiveTail.Fit("one two three four", 110, Measure));
        // “…three four”放不下时不切开 three，直接到下一个词。
        Assert.Equal("…four", LiveTail.Fit("one two three four", 100, Measure));
    }

    [Fact]
    public void CJKTextIsCutBetweenAnyCharacters()
    {
        Assert.Equal("…论机器学习", LiveTail.Fit("今天我们讨论机器学习", 60, Measure));
    }

    [Fact]
    public void MixedTextDoesNotSplitLatinWords()
    {
        // 按字截断会得到“…orch训练模型”；PyTorch 整个放不下时从“训”开始。
        Assert.Equal("…训练模型", LiveTail.Fit("我们用PyTorch训练模型", 100, Measure));
        Assert.Equal("…PyTorch训练模型", LiveTail.Fit("我们用PyTorch训练模型", 120, Measure));
    }

    [Fact]
    public void LongWordFallsBackToCharacters()
    {
        Assert.Equal("…istic", LiveTail.Fit("supercalifragilistic", 60, Measure));
        Assert.Equal("…istic", LiveTail.Fit("so supercalifragilistic", 60, Measure));
    }

    [Fact]
    public void TailDoesNotStartWithPunctuationOrSpace()
    {
        Assert.Equal("…第二句话", LiveTail.Fit("第一句。第二句话", 60, Measure));
        Assert.Equal("…next", LiveTail.Fit("first, next   ", 60, Measure));
    }

    [Fact]
    public void TrailingSpacesAloneDoNotCauseTruncation()
    {
        // 带上末尾空格放不下，去掉后正好放下：不截断。
        Assert.Equal("one two", LiveTail.Fit("one two   ", 70, Measure));
        Assert.Equal("…two", LiveTail.Fit("one two   ", 60, Measure));
    }

    [Fact]
    public void NothingFitsShowsLastCharacter()
    {
        Assert.Equal("…d", LiveTail.Fit("word", 10, Measure));
    }
}
