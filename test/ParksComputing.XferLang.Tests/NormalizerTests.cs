using System;
using System.Text;
using ParksComputing.XferLang.Text;
using Xunit;

namespace ParksComputing.XferLang.Tests;

public class NormalizerTests
{
    private static readonly XferLimits Limits = XferLimits.Default;

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static string TextOf(NormalizeResult result) => Encoding.UTF8.GetString(result.Text);

    /// <summary>The one diagnostic a rejected input produces, as code, offset, line and column.</summary>
    private static (XferDiagnosticCode Code, int Offset, int Line, int Column) Rejection(NormalizeResult result)
    {
        Assert.Empty(result.Text);
        XferDiagnostic fault = Assert.IsType<XferDiagnostic>(result.Fault);
        return (fault.Code, fault.Start.Offset, fault.Start.Line, fault.Start.Column);
    }

    [Fact]
    public void DecodesUtf8AndReportsTheInputSize()
    {
        NormalizeResult result = Normalizer.Normalize(Utf8("{ a 1 }"), Limits);
        Assert.Null(result.Fault);
        Assert.Equal("{ a 1 }", TextOf(result));
        Assert.Equal(7, result.InputBytes);
    }

    [Fact]
    public void AcceptsAnEmptyInputAndLeavesItToTheParserToSayADocumentNeedsAnElement()
    {
        NormalizeResult result = Normalizer.Normalize(ReadOnlySpan<byte>.Empty, Limits);
        Assert.Null(result.Fault);
        Assert.Empty(result.Text);
        Assert.Equal(0, result.InputBytes);
    }

    [Fact]
    public void RemovesOneLeadingByteOrderMarkAndNoMore()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        Assert.Equal("{}", TextOf(Normalizer.Normalize([.. bom, (byte)'{', (byte)'}'], Limits)));
        Assert.Equal("\uFEFF{}", TextOf(Normalizer.Normalize([.. bom, .. bom, (byte)'{', (byte)'}'], Limits)));
        Assert.Equal("{\uFEFF}", TextOf(Normalizer.Normalize([(byte)'{', .. bom, (byte)'}'], Limits)));
    }

    [Fact]
    public void TurnsCrLfAndLoneCrIntoLf()
    {
        Assert.Equal("a\nb\nc\nd\n\ne\n", TextOf(Normalizer.Normalize(Utf8("a\r\nb\rc\nd\r\r\ne\r"), Limits)));
    }

    [Fact]
    public void LeavesEveryOtherCharacterAloneIncludingNulControlsAndUnicodeLineSeparators()
    {
        const string text = "a\u0000b\u0001c\u2028d\u0085e\u000Bf";
        Assert.Equal(text, TextOf(Normalizer.Normalize(Utf8(text), Limits)));
    }

    [Fact]
    public void RejectsInvalidUtf8AtTheFirstInvalidByte()
    {
        (string What, byte[] Bytes, int Offset)[] bad =
        [
            ("a byte that can never occur", [0xFF], 0),
            ("a lone continuation byte", [0x80], 0),
            ("a truncated sequence at the end", [0x7B, 0xE2, 0x82], 1),
            ("an overlong encoding", [0xC0, 0x80], 0),
            ("an encoded surrogate", [0xED, 0xA0, 0x80], 0),
            ("a value past U+10FFFF", [0xF4, 0x90, 0x80, 0x80], 0),
            ("a fault after valid text", [0x61, 0x62, 0xFF], 2),
            ("a fault after multi-byte text", [0xC3, 0xA9, 0xE6, 0x97, 0xA5, 0xFF], 5),
        ];
        foreach ((string what, byte[] bytes, int offset) in bad)
        {
            (XferDiagnosticCode code, int actualOffset, _, _) = Rejection(Normalizer.Normalize(bytes, Limits));
            Assert.True(code == XferDiagnosticCode.XFER0701 && actualOffset == offset, $"{what}: {code} at {actualOffset}");
        }
    }

    [Fact]
    public void ReportsInvalidUtf8WithItsLineAndColumnAsTheNormalizedTextBeforeItCountsThem()
    {
        // Column counts scalar values, so the two-byte é and the three-byte 日 each count once.
        (_, int offset, int line, int column) = Rejection(Normalizer.Normalize([0xC3, 0xA9, 0xE6, 0x97, 0xA5, 0xFF], Limits));
        Assert.Equal((5, 1, 3), (offset, line, column));

        // A line feed, CR LF and a lone CR each end a line, so the fault is on line 4. The offset
        // is the raw offset, counted from after the byte order mark.
        byte[] input = [0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\n', (byte)'b', (byte)'\r', (byte)'\n', (byte)'c', (byte)'\r', 0xFF];
        (_, offset, line, column) = Rejection(Normalizer.Normalize(input, Limits));
        Assert.Equal((7, 4, 1), (offset, line, column));
    }

    [Fact]
    public void ChecksTheSizeLimitBeforeDecoding()
    {
        var small = new XferLimits { DocumentBytes = 4 };
        Assert.Equal("abcd", TextOf(Normalizer.Normalize(Utf8("abcd"), small)));
        Assert.Equal((XferDiagnosticCode.XFER0601, 0, 1, 1), Rejection(Normalizer.Normalize(Utf8("abcde"), small)));
        Assert.Equal((XferDiagnosticCode.XFER0601, 0, 1, 1), Rejection(Normalizer.Normalize([0xFF, 0xFF, 0xFF, 0xFF, 0xFF], small)));
    }

    [Fact]
    public void MeasuresTheSizeAsSuppliedBeforeTheByteOrderMarkAndLineEndingsChangeIt()
    {
        Assert.Equal(4, Normalizer.Normalize(Utf8("a\r\nb"), Limits).InputBytes);
        Assert.Equal(5, Normalizer.Normalize([0xEF, 0xBB, 0xBF, (byte)'a', (byte)'b'], Limits).InputBytes);
        var small = new XferLimits { DocumentBytes = 4 };
        Assert.Equal(XferDiagnosticCode.XFER0601, Normalizer.Normalize([0xEF, 0xBB, 0xBF, (byte)'a', (byte)'b'], small).Fault?.Code);
    }

    [Fact]
    public void StringInputIsNormalizedTheSameWayAsByteInput()
    {
        NormalizeResult marked = Normalizer.Normalize("\uFEFF{}", Limits);
        Assert.Equal("{}", TextOf(marked));
        Assert.Equal(5, marked.InputBytes);
        Assert.Equal("a\nb\nc\n", TextOf(Normalizer.Normalize("a\r\nb\rc\n", Limits)));
        Assert.Equal("\uFEFF{}", TextOf(Normalizer.Normalize("\uFEFF\uFEFF{}", Limits)));
    }

    [Fact]
    public void StringAndByteInputAgreeOnWellFormedText()
    {
        foreach (string s in new[] { string.Empty, "{ a 1 }", "日本語", "😀   \u0000", "x\r\ny\rz", "\uFEFFé" })
        {
            Assert.Equal(TextOf(Normalizer.Normalize(Utf8(s), Limits)), TextOf(Normalizer.Normalize(s, Limits)));
        }
    }

    [Fact]
    public void StringSizeCountsUtf8BytesNotUtf16Units()
    {
        Assert.Equal(2, Normalizer.Normalize("é", Limits).InputBytes);
        Assert.Equal(4, Normalizer.Normalize("😀", Limits).InputBytes);
        var small = new XferLimits { DocumentBytes = 2 };
        Assert.Equal("é", TextOf(Normalizer.Normalize("é", small)));
        Assert.Equal((XferDiagnosticCode.XFER0601, 0, 1, 1), Rejection(Normalizer.Normalize("éa", small)));
    }

    [Fact]
    public void AcceptsAWellFormedSurrogatePair()
    {
        Assert.Null(Normalizer.Normalize("a😀b", Limits).Fault);
    }

    [Fact]
    public void RejectsALoneSurrogateAtItsPositionInTheNormalizedText()
    {
        Assert.Equal((XferDiagnosticCode.XFER0701, 1, 1, 2), Rejection(Normalizer.Normalize("a\uD800b", Limits)));
        Assert.Equal((XferDiagnosticCode.XFER0701, 0, 1, 1), Rejection(Normalizer.Normalize("\uDC00", Limits)));
        Assert.Equal((XferDiagnosticCode.XFER0701, 2, 1, 3), Rejection(Normalizer.Normalize("ab\uD800", Limits)));
        Assert.Equal((XferDiagnosticCode.XFER0701, 0, 1, 1), Rejection(Normalizer.Normalize("\uD800😀", Limits)));
        Assert.Equal((XferDiagnosticCode.XFER0701, 4, 1, 2), Rejection(Normalizer.Normalize("😀\uDE00", Limits)));
    }

    [Fact]
    public void CountsTheSurrogatePositionInScalarValuesAndUtf8Bytes()
    {
        Assert.Equal((XferDiagnosticCode.XFER0701, 4, 1, 2), Rejection(Normalizer.Normalize("😀\uD800", Limits)));
        Assert.Equal((XferDiagnosticCode.XFER0701, 5, 1, 3), Rejection(Normalizer.Normalize("é日\uD800", Limits)));
    }

    [Fact]
    public void ReportsTheSurrogatePositionAfterTheByteOrderMarkAndCrLfAreGone()
    {
        Assert.Equal((XferDiagnosticCode.XFER0701, 2, 2, 1), Rejection(Normalizer.Normalize("x\n\uD800", Limits)));
        Assert.Equal((XferDiagnosticCode.XFER0701, 2, 2, 1), Rejection(Normalizer.Normalize("x\r\n\uD800", Limits)));
        Assert.Equal((XferDiagnosticCode.XFER0701, 4, 3, 2), Rejection(Normalizer.Normalize("x\r\ry\uD800", Limits)));
        Assert.Equal((XferDiagnosticCode.XFER0701, 0, 1, 1), Rejection(Normalizer.Normalize("\uFEFF\uD800", Limits)));
    }

    [Fact]
    public void TheLineMapOfNormalizedTextMatchesTheText()
    {
        NormalizeResult result = Normalizer.Normalize(Utf8("a\r\nbé\rc"), Limits);
        Assert.Equal(3, result.Lines.LineCount);
        Assert.Equal(new XferPosition(0, 1, 1), result.Lines.GetPosition(0));
        Assert.Equal(new XferPosition(2, 2, 1), result.Lines.GetPosition(2));
        Assert.Equal(new XferPosition(5, 2, 3), result.Lines.GetPosition(5));
        Assert.Equal(new XferPosition(6, 3, 1), result.Lines.GetPosition(6));
    }

    [Fact]
    public void EveryByteStringOfLengthUpToThreeIsClassifiedLikeTheSystemDecoder()
    {
        // The first-invalid-byte search must agree with a strict decoder about whether bytes are
        // valid, for every short input, so no valid input is rejected and no invalid one accepted.
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        byte[] interesting = [0x00, 0x41, 0x7F, 0x80, 0xBF, 0xC0, 0xC2, 0xDF, 0xE0, 0xE1, 0xED, 0xEF, 0xF0, 0xF4, 0xF5, 0xFF, 0xA0, 0x9F, 0x90, 0x8F];
        for (int length = 1; length <= 3; length++)
        {
            int count = (int)Math.Pow(interesting.Length, length);
            for (int n = 0; n < count; n++)
            {
                var bytes = new byte[length];
                int rest = n;
                for (int i = 0; i < length; i++)
                {
                    bytes[i] = interesting[rest % interesting.Length];
                    rest /= interesting.Length;
                }

                bool valid;
                try
                {
                    strict.GetString(bytes);
                    valid = true;
                }
                catch (DecoderFallbackException)
                {
                    valid = false;
                }

                NormalizeResult result = Normalizer.Normalize(bytes, Limits);
                Assert.True((result.Fault is null) == valid, BitConverter.ToString(bytes));
            }
        }
    }
}
