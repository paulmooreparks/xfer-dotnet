using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using ParksComputing.XferLang.Syntax;
using ParksComputing.XferLang.Writing;
using ParksComputing.XferLang.Tests.Harness;
using Xunit;

namespace ParksComputing.XferLang.Tests;

public class WriterTests
{
    private const int Limit = 64;

    private static string Canon(string text) => XferWriter.Canonical(XferDocument.Parse(text));

    /// <summary>Parses the text of a string literal as a document and returns its content.</summary>
    private static string? Read(string literal, int runLength = Limit)
    {
        XferParseResult result = XferDocument.Parse(literal, new XferParseOptions { Limits = new XferLimits { RunLength = runLength } });
        return result.Value is XferString s ? s.Value : null;
    }

    [Fact]
    public void UsesOneQuoteWhenTheContentHasNone()
    {
        foreach (string content in new[] { string.Empty, "plain", "with #hash and <tags> and \\ backslashes", "日本語", "a\nb" })
        {
            Assert.Equal((XferStringForm.Repetition, 1), StringDelimiters.Choose(content, Limit));
        }
    }

    [Fact]
    public void UsesRepetitionOneLongerThanTheLongestRunWhenNoQuoteIsAtAnEnd()
    {
        Assert.Equal((XferStringForm.Repetition, 3), StringDelimiters.Choose("a\"b", Limit)); // 2 would be the empty string
        Assert.Equal((XferStringForm.Repetition, 3), StringDelimiters.Choose("a\" \"b", Limit));
        Assert.Equal((XferStringForm.Repetition, 3), StringDelimiters.Choose("a\"\"b", Limit));
        Assert.Equal((XferStringForm.Repetition, 4), StringDelimiters.Choose("a\"\"\"b", Limit));
        Assert.Equal((XferStringForm.Repetition, 5), StringDelimiters.Choose("a\"b\"\"c\"\"\"\"d", Limit));
        Assert.Equal((XferStringForm.Repetition, 3), StringDelimiters.Choose("x \"# y", Limit)); // a quote in the middle is not an edge
    }

    [Fact]
    public void UsesHashRunWhenAQuoteIsAtEitherEndWithOneMoreHashThanAnyRunAfterAQuote()
    {
        foreach ((string content, int run) in new[] { ("\"", 1), ("\"\"", 1), ("\"a", 1), ("a\"", 1), ("\"a\"", 1), ("\"#x\"", 2), ("\"a\"##", 3), ("#\"", 1) })
        {
            Assert.Equal((XferStringForm.Hash, run), StringDelimiters.Choose(content, Limit));
        }
    }

    [Fact]
    public void TakesTheSmallestUnusedLengthWhenThePreferredOnePassesTheLimit()
    {
        // Runs of 1 and 2 quotes (a 3 is free), under a limit of 2: 3 is over, so the smallest
        // unused length other than 2 is used, which is 3 anyway; with runs 1, 3 and 4 used it is 5.
        Assert.Equal((XferStringForm.Repetition, 3), StringDelimiters.Choose("a\"b\"\"c", 2));
        Assert.Equal((XferStringForm.Repetition, 5), StringDelimiters.Choose("a\"b\"\"\"c\"\"\"\"d", 3));
        Assert.Equal((XferStringForm.Hash, 3), StringDelimiters.Choose("\"#x\"##\"", 1));
    }

    [Fact]
    public void WritesEveryAwkwardStringSoThatItReadsBackAsItself()
    {
        string[] pieces = ["\"", "#", " ", "\n", "a", "\"\"", "\"#", "#\"", "##", "\"\"\""];
        var random = new Random(20261019);
        for (int run = 0; run < 30_000; run++)
        {
            var content = new StringBuilder();
            int n = random.Next(0, 12);
            for (int i = 0; i < n; i++)
            {
                content.Append(pieces[random.Next(pieces.Length)]);
            }

            string literal = StringDelimiters.Write(content.ToString(), Limit);
            Assert.Equal(content.ToString(), Read(literal));
        }
    }

    [Fact]
    public void WritesEveryStringOverASmallAlphabetUpToLengthTen()
    {
        char[] alphabet = ['"', '#', 'a'];
        for (int length = 0; length <= 10; length++)
        {
            int count = (int)Math.Pow(alphabet.Length, length);
            for (int n = 0; n < count; n++)
            {
                var chars = new char[length];
                int rest = n;
                for (int i = 0; i < length; i++)
                {
                    chars[i] = alphabet[rest % alphabet.Length];
                    rest /= alphabet.Length;
                }

                string content = new(chars);
                Assert.Equal(content, Read(StringDelimiters.Write(content, Limit)));
            }
        }
    }

    [Fact]
    public void ARunPastTheLimitIsStillWrittenAndReadByAParserThatAdmitsIt()
    {
        // A string with a quote run of every length 1 to 3 and a quote at an edge, under a limit of 3.
        string content = "\"a\"\"b\"\"\"c\"";
        string literal = StringDelimiters.Write(content, 3);
        Assert.Equal(content, Read(literal, 64));
        Assert.Equal(content, Read(StringDelimiters.Write(content, 1), 64));
    }

    [Fact]
    public void WritesTheSpecificationsLayout()
    {
        Assert.Equal("{\n    a 1\n    b [\n        2\n        3\n    ]\n    c {}\n    d []\n}\n", Canon("{a 1 b[2 3] c{} d[]}"));
        Assert.Equal("7\n", Canon("  7  "));
        Assert.Equal("[\n    1\n]\n", Canon("</ a /> [ </ b /> 1 ]"));
        Assert.Equal("{\n    z 1\n    a 2\n}\n", Canon("{ z 1 a 2 }"));
    }

    [Fact]
    public void WritesTheHeadFirstOnlyWhenTheSourceHadOneAndAnnotationsBeforeTheirTargets()
    {
        Assert.Equal("<! xfer 1 !>\n7\n", Canon("<! xfer 1 !> 7"));
        Assert.Equal("7\n", Canon("7"));
        Assert.Equal("<! xfer 1 !>\n<! id \"a\" !>\n{\n    <! tag t !>\n    x 1\n    <! lang en !>\n    <! tag \"u\" !>\n    y [\n        <! id \"b\" !>\n        2\n    ]\n}\n", Canon("<! xfer 1 !> <! id \"a\" !> { <! tag t !> x 1 <! lang en !> <! tag \"u\" !> y [ <! id \"b\" !> 2 ] }"));
    }

    [Fact]
    public void WritesKeysBareOrAsStringsAndNumbersAsSpelledExceptHexDigits()
    {
        Assert.Equal("{\n    a.b 1\n    \"first name\" 2\n    \"true\" 3\n    \"\" 4\n    日本 5\n    \"9a\" 6\n    \"a.\" 7\n}\n", Canon("{ a.b 1 \"first name\" 2 \"true\" 3 \"\" 4 日本 5 \"9a\" 6 \"a.\" 7 }"));
        Assert.Equal("[\n    0xff\n    -0xAB\n    0o17\n    0b1_0\n    1_000\n    1E5\n    -0\n    1.50\n    inf\n    -inf\n    nan\n]\n".Replace("-0xAB", "-0xab", StringComparison.Ordinal), Canon("[ 0xFF -0xAB 0o17 0b1_0 1_000 1E5 -0 1.50 inf -inf nan ]"));
        Assert.Equal("[\n    2025-08-01\n    09:30:00.250\n    2025-08-01T09:30:00Z\n    sym\n    true\n    null\n]\n", Canon("[ 2025-08-01 09:30:00.250 2025-08-01T09:30:00Z sym true null ]"));
    }

    [Fact]
    public void RefusesAParseResultWithAnErrorUsingItsOwnDiagnostics()
    {
        XferException e = Assert.Throws<XferException>(() => XferWriter.Canonical(XferDocument.Parse("{ a")));
        Assert.Contains(e.Diagnostics, d => d.Code == XferDiagnosticCode.XFER0405);
        Assert.Throws<ArgumentNullException>(() => XferWriter.Canonical((XferParseResult)null!));
        Assert.Throws<ArgumentNullException>(() => XferWriter.Canonical((XferValue)null!));
    }

    [Fact]
    public void RefusesAValueNoDocumentCouldHoldInsteadOfWritingOneThatReadsBackDifferently()
    {
        // Specification 9, "Values built by a program".
        XferValue[] bad =
        [
            new XferString("a\rb"),
            new XferString("a\uD800b"),
            new XferString("\uDC00"),
            new XferObject([new KeyValuePair<string, XferValue>("k\r", new XferNull())]),
            new XferObject([new KeyValuePair<string, XferValue>("\uD800", new XferNull())]),
            new XferSymbol("has space"),
            new XferSymbol("true"),
            new XferSymbol("a."),
            new XferSymbol(string.Empty),
            new XferSymbol("1abc"),
            new XferInteger(5, "7"),
            new XferInteger(7, "07"),
            new XferInteger(7, " 7"),
            new XferInteger(7, "7 "),
            new XferInteger(7, string.Empty),
            new XferInteger(7, "7.0"),
            new XferFractional("1"),
            new XferFractional("true"),
            new XferFractional("1.5 2"),
            new XferFractional(string.Empty),
            new XferTemporal(XferTemporalKind.Date, "2025-13-01"),
            new XferTemporal(XferTemporalKind.Time, "2025-08-01"),
            new XferTemporal(XferTemporalKind.DateTime, "2025-08-01T09:30:00"),
            new XferNull([new XferAnnotation("xfer", new XferInteger(1, "1"))]),
            new XferNull([new XferAnnotation("other", new XferString("x"))]),
            new XferNull([new XferAnnotation("id", new XferSymbol("x"))]),
            new XferNull([new XferAnnotation("tag", new XferInteger(1, "1"))]),
            new XferNull([new XferAnnotation("tag", new XferSequence([]))]),
            new XferNull([new XferAnnotation("tag", new XferString("x", [new XferAnnotation("tag", new XferString("y"))]))]),
            new XferSequence([new XferNull([new XferAnnotation("id", new XferString("a"))]), new XferNull([new XferAnnotation("id", new XferString("a"))])]),
        ];
        foreach (XferValue value in bad)
        {
            Assert.Throws<ArgumentException>(() => XferWriter.Canonical(value));
        }
    }

    [Fact]
    public void WritesAValueBuiltByHandThatADocumentCouldHold()
    {
        var value = new XferObject(
            [
                new KeyValuePair<string, XferValue>("name", new XferString("Ada", [new XferAnnotation("lang", new XferSymbol("en"))])),
                new KeyValuePair<string, XferValue>("n", new XferInteger(new BigInteger(255), "0xFF")),
                new KeyValuePair<string, XferValue>("say \"hi\"", new XferSequence([new XferBool(true), new XferNull(), new XferFractional("-inf")])),
            ]);
        string text = XferWriter.Canonical(value);
        Assert.Equal("{\n    <! lang en !>\n    name \"Ada\"\n    n 0xff\n    #\"say \"hi\"\"# [\n        true\n        null\n        -inf\n    ]\n}\n", text); // the key ends with a quote, so it takes the hash form
        var reread = (XferObject)XferDocument.ParseValue(text);
        Assert.Equal(["name", "n", "say \"hi\""], reread.Keys);
        Assert.Equal(new BigInteger(255), ((XferInteger)reread["n"]).Value);
        Assert.Equal(XferWriter.Canonical(reread), text);
        var writer = new System.IO.StringWriter();
        XferWriter.Canonical(value, writer);
        Assert.Equal(text, writer.ToString());
    }

    [Fact]
    public void RefusesAValueNestedPastTheDepthLimitBecauseTheOutputGrowsWithItsSquare()
    {
        XferValue value = new XferNull();
        for (int i = 0; i < 300; i++)
        {
            value = new XferSequence([value]);
        }

        XferException e = Assert.Throws<XferException>(() => XferWriter.Canonical(value));
        Assert.Equal(XferDiagnosticCode.XFER0602, Assert.Single(e.Diagnostics).Code);

        // A deep value within a raised limit is written, and a result parsed under that limit too.
        var options = new XferWriteOptions { Limits = new XferLimits { Depth = 1000 } };
        string text = XferWriter.Canonical(value, options);
        Assert.StartsWith("[\n    [\n", text, StringComparison.Ordinal);
        XferParseResult parsed = XferDocument.Parse(new string('[', 500) + new string(']', 500), new XferParseOptions { Limits = options.Limits });
        Assert.Equal(500, XferWriter.Canonical(parsed, options).Count(c => c == '['));
    }

    [Fact]
    public void AnEmptyCollectionCountsAsALevel()
    {
        XferValue Nest(int depth)
        {
            XferValue v = new XferSequence([]);
            for (int i = 1; i < depth; i++)
            {
                v = new XferSequence([v]);
            }

            return v;
        }

        Assert.Throws<XferException>(() => XferWriter.Canonical(Nest(257)));
        Assert.True(XferDocument.Parse(XferWriter.Canonical(Nest(256))).Ok);
        Assert.Throws<XferException>(() => XferWriter.Canonical(Nest(4), new XferWriteOptions { Limits = new XferLimits { Depth = 3 } }));
        Assert.Throws<XferException>(() => XferWriter.Canonical(new XferObject([new KeyValuePair<string, XferValue>("a", new XferObject([]))]), new XferWriteOptions { Limits = new XferLimits { Depth = 1 } }));
    }

    [Fact]
    public void TheHeadAndTheFinalLineFeedCountTowardsTheDocumentSize()
    {
        var limits = new XferWriteOptions { Limits = new XferLimits { DocumentBytes = 6 } };
        Assert.Equal("\"aaa\"\n", XferWriter.Canonical(new XferString("aaa"), limits));
        Assert.Throws<XferException>(() => XferWriter.Canonical(new XferString("aaaa"), limits));
        XferParseResult headed = XferDocument.Parse("<! xfer 1 !> 1");
        Assert.Throws<XferException>(() => XferWriter.Canonical(headed, limits));
        Assert.Equal("<! xfer 1 !>\n1\n", XferWriter.Canonical(headed, new XferWriteOptions { Limits = new XferLimits { DocumentBytes = 15 } }));
        Assert.Throws<XferException>(() => XferWriter.Canonical(headed, new XferWriteOptions { Limits = new XferLimits { DocumentBytes = 14 } }));
    }

    [Fact]
    public void ASymbolOrKeyWithInvalidUtf16IsRefusedNotWrittenBare()
    {
        foreach (string bad in new[] { "\uD835", "\uDC00", "a\uD835", "\uD835a", "a\uD835b" })
        {
            Assert.Throws<ArgumentException>(() => XferWriter.Canonical(new XferSymbol(bad)));
            // As a key it is a string, which refuses a lone surrogate too.
            Assert.Throws<ArgumentException>(() => XferWriter.Canonical(new XferObject([new KeyValuePair<string, XferValue>(bad, new XferNull())])));
        }

        Assert.Equal("𝒜\n", XferWriter.Canonical(new XferSymbol("𝒜")));
    }

    [Fact]
    public void WritingToATextWriterStreamsAndGivesTheSameTextAsTheStringMethod()
    {
        XferValue value = new XferNull();
        for (int i = 0; i < 2500; i++)
        {
            value = new XferSequence([value, new XferString("x")]);
        }

        var options = new XferWriteOptions { Limits = new XferLimits { Depth = 5000 } };
        string whole = XferWriter.Canonical(value, options);
        var counting = new ChunkCountingWriter();
        XferWriter.Canonical(value, counting, options);
        Assert.Equal(whole, counting.ToString());
        Assert.True(counting.Writes > 100, $"{counting.Writes} writes: the output was not streamed in chunks");
    }

    private sealed class ChunkCountingWriter : System.IO.TextWriter
    {
        private readonly StringBuilder text = new();

        public int Writes { get; private set; }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => text.Append(value);

        public override void Write(string? value)
        {
            Writes++;
            text.Append(value);
        }

        public override void Write(StringBuilder? value)
        {
            Writes++;
            text.Append(value);
        }

        public override string ToString() => text.ToString();
    }

    [Fact]
    [Trait("Category", "Timing")]
    public void AParsedIntegerRebuiltIntoANewValueIsNotConvertedTwice()
    {
        string digits = "1" + new string('7', 2_000_000);
        var parsed = (XferInteger)XferDocument.ParseValue(digits);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        string text = XferWriter.Canonical(new XferSequence([parsed, parsed]));
        timer.Stop();
        Assert.True(timer.ElapsedMilliseconds < Timing.Scale * 1_000, $"{timer.ElapsedMilliseconds} ms");
        Assert.Contains(digits, text, StringComparison.Ordinal);

        // A value that has been read is checked against its spelling, which still agrees.
        Assert.True(parsed.Value > 0);
        Assert.Contains(digits, XferWriter.Canonical(new XferSequence([parsed])), StringComparison.Ordinal);
    }

    [Fact]
    public void ALexemeIsJudgedUnderTheWritersOwnLimits()
    {
        var tight = new XferWriteOptions { Limits = new XferLimits { TokenBytes = 3 } };
        Assert.Equal("[\n    1.5\n]\n", XferWriter.Canonical(new XferSequence([new XferFractional("1.5")]), tight));
        Assert.Throws<ArgumentException>(() => XferWriter.Canonical(new XferSequence([new XferFractional("1.25")]), tight));
        Assert.Throws<ArgumentException>(() => XferWriter.Canonical(new XferFractional(new string('1', 40) + ".5"), tight));
    }

    [Fact]
    public void ErrorMessagesDoNotQuoteAHugeValueInFull()
    {
        ArgumentException e = Assert.Throws<ArgumentException>(() => XferWriter.Canonical(new XferSymbol(new string('?', 10_000))));
        Assert.True(e.Message.Length < 200, e.Message.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void RefusesOutputLongerThanTheDocumentSizeLimit()
    {
        XferValue value = new XferSequence(Enumerable.Range(0, 1000).Select(i => (XferValue)new XferString("abcdefghij")));
        XferException e = Assert.Throws<XferException>(() => XferWriter.Canonical(value, new XferWriteOptions { Limits = new XferLimits { DocumentBytes = 5000 } }));
        Assert.Equal(XferDiagnosticCode.XFER0601, Assert.Single(e.Diagnostics).Code);
        Assert.Equal(1000 + 2, XferWriter.Canonical(value).Count(c => c == (char)10));
    }

    [Fact]
    public void TheDeepestDocumentTheDefaultLimitAllowsIsWrittenWithoutRecursion()
    {
        XferParseResult parsed = XferDocument.Parse(new string('[', 256) + new string(']', 256));
        Assert.True(parsed.Ok);
        string text = XferWriter.Canonical(parsed);
        Assert.Equal(parsed.Value, XferDocument.ParseValue(text));

        // With the depth limit raised high, 100,000 levels are written (the output is quadratic, so
        // this is kept to a size a test can hold) and read back.
        var options = new XferWriteOptions { Limits = new XferLimits { Depth = 5000 } };
        XferValue deep = new XferNull();
        for (int i = 0; i < 4000; i++)
        {
            deep = new XferSequence([deep]);
        }

        string big = XferWriter.Canonical(deep, options);
        Assert.Equal(deep, XferDocument.ParseValue(big, new XferParseOptions { Limits = options.Limits }));
    }

    [Fact]
    public void WritingIsStableAndIndependentOfTheSpacingOfTheSource()
    {
        string[] sources = ["{a 1 b[1 2]}", "{ a 1 b [ 1 2 ] }", "{\n a\t1\n b\n[1\n2]\n}", "{ a 1 </ x /> b [ 1 </ y /> 2 ] }"];
        string first = Canon(sources[0]);
        foreach (string source in sources)
        {
            Assert.Equal(first, Canon(source));
        }

        Assert.Equal(first, Canon(first));
    }

    [Fact]
    public void ADocumentEmbeddedInAStringAtEveryDepthReadsBackUnchanged()
    {
        // The embedding invariant: any text embeds unchanged at any depth, with no escapes.
        string text = "{ a \"x\" b [ 1 \"\"\"q \"r\" s\"\"\" ] }";
        string current = text;
        for (int level = 0; level < 6; level++)
        {
            string wrapped = StringDelimiters.Write(current, Limit);
            XferValue read = XferDocument.ParseValue(wrapped);
            Assert.Equal(current, ((XferString)read).Value);
            current = wrapped;
        }
    }
}
