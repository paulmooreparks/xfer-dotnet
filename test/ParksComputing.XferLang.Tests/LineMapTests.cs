using System;
using System.Linq;
using System.Text;
using ParksComputing.XferLang.Text;
using ParksComputing.XferLang.Tests.Harness;
using Xunit;

namespace ParksComputing.XferLang.Tests;

public class LineMapTests
{
    private static LineMap Map(string text)
    {
        NormalizeResult result = Normalizer.Normalize(text, XferLimits.Default);
        Assert.Null(result.Fault);
        return result.Lines;
    }

    [Fact]
    public void StartsAtOffsetZeroLineOneColumnOne()
    {
        Assert.Equal(XferPosition.Origin, Map("abc").GetPosition(0));
        Assert.Equal(XferPosition.Origin, Map(string.Empty).GetPosition(0));
    }

    [Fact]
    public void CountsColumnsInScalarValuesAndOffsetsInUtf8Bytes()
    {
        LineMap map = Map("é日😀x");
        Assert.Equal(new XferPosition(0, 1, 1), map.GetPosition(0));
        Assert.Equal(new XferPosition(2, 1, 2), map.GetPosition(2));
        Assert.Equal(new XferPosition(5, 1, 3), map.GetPosition(5));
        Assert.Equal(new XferPosition(9, 1, 4), map.GetPosition(9));
        Assert.Equal(new XferPosition(10, 1, 5), map.GetPosition(10));
    }

    [Fact]
    public void StartsANewLineAfterEachLineFeedAndResetsTheColumn()
    {
        LineMap map = Map("a\nbc\n\nd");
        Assert.Equal(new XferPosition(3, 2, 2), map.GetPosition(3));
        Assert.Equal(new XferPosition(6, 4, 1), map.GetPosition(6));
        Assert.Equal(new XferPosition(7, 4, 2), map.GetPosition(7));
        Assert.Equal(4, map.LineCount);
    }

    [Fact]
    public void CountsALineFeedAsOneByteInTheLineItEnds()
    {
        Assert.Equal(new XferPosition(3, 2, 1), Map("é\nx").GetPosition(3));
    }

    [Fact]
    public void DoesNotTreatUnicodeLineSeparatorsAsLineEnds()
    {
        Assert.Equal(new XferPosition(5, 1, 4), Map("a\u2028b").GetPosition(5));
        Assert.Equal(new XferPosition(3, 1, 3), Map("a\u0085b").GetPosition(3));
    }

    [Fact]
    public void GivesThePositionOfTheEndForAnOffsetPastTheEnd()
    {
        Assert.Equal(new XferPosition(1, 1, 2), Map("a").GetPosition(5));
        Assert.Equal(XferPosition.Origin, Map(string.Empty).GetPosition(3));
        Assert.Equal(XferPosition.Origin, Map("abc").GetPosition(-4));
    }

    [Fact]
    public void GivesThePositionOfTheCharacterNotAfterItForAnOffsetInsideAMultiByteCharacter()
    {
        LineMap map = Map("a😀x");
        Assert.Equal(new XferPosition(1, 1, 2), map.GetPosition(2));
        Assert.Equal(new XferPosition(1, 1, 2), map.GetPosition(4));
        Assert.Equal(new XferPosition(5, 1, 3), map.GetPosition(5));
    }

    [Fact]
    public void ResolveAgreesWithGetPositionForEveryOffsetOfSomeAwkwardText()
    {
        LineMap map = Map("é\n\n日本語 x\r\n😀😀\nlast");
        int length = Encoding.UTF8.GetByteCount("é\n\n日本語 x\n😀😀\nlast");
        int[] offsets = Enumerable.Range(0, length + 3).ToArray();
        XferPosition[] resolved = map.Resolve(offsets);
        for (int i = 0; i < offsets.Length; i++)
        {
            Assert.Equal(map.GetPosition(offsets[i]), resolved[i]);
        }
    }

    [Fact]
    public void ResolveAcceptsRepeatedOffsetsAndRefusesDescendingOnes()
    {
        LineMap map = Map("abc\ndef");
        Assert.Equal([new XferPosition(1, 1, 2), new XferPosition(1, 1, 2), new XferPosition(5, 2, 2)], map.Resolve([1, 1, 5]));
        Assert.Throws<ArgumentException>(() => map.Resolve([5, 1]));
        Assert.Empty(map.Resolve([]));
    }

    [Fact]
    [Trait("Category", "Timing")]
    public void ResolvingManyPositionsOnOneVeryLongLineIsLinearNotQuadratic()
    {
        // 200,000 faults on a single 2 MB line: one scan per fault would be 4e11 byte visits.
        string text = new('x', 2_000_000);
        LineMap map = Map(text);
        int[] offsets = Enumerable.Range(0, 200_000).Select(i => i * 10).ToArray();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        XferPosition[] resolved = map.Resolve(offsets);
        timer.Stop();
        Assert.Equal(new XferPosition(1_999_990, 1, 1_999_991), resolved[^1]);
        Assert.True(timer.ElapsedMilliseconds < Timing.Scale * 2_000, $"took {timer.ElapsedMilliseconds} ms");
    }
}
