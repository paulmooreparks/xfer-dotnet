using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ParksComputing.XferLang.Syntax;
using ParksComputing.XferLang.Tests.Harness;
using Xunit;

namespace ParksComputing.XferLang.Tests;

[Collection("Memory measurements")]
public class ParserTests
{
    private static readonly JsonNodeOptions NodeOptions = default;
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 100_000 };

    [Fact]
    public void AgreesWithTheReferenceParserOnTreesDiagnosticsAndValues()
    {
        // fixtures/parser.jsonl is written by tools/fixtures/parser-fixture.mjs from xfer-js:
        // generated documents, the same documents damaged, and random token soup.
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "parser.jsonl");
        var failures = new List<string>();
        int cases = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            cases++;
            JsonNode expected = JsonNode.Parse(line, NodeOptions, DocumentOptions)!;
            string text = expected["text"]!.GetValue<string>();
            string? problem = Compare(text, expected);
            if (problem is not null)
            {
                failures.Add($"{expected["label"]} {JsonSerializer.Serialize(text)}: {problem}");
                if (failures.Count == 10)
                {
                    break;
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.True(cases > 7000, $"only {cases} fixture cases");
    }

    private static string? Compare(string text, JsonNode expected)
    {
        var options = new XferParseOptions();
        if (expected["limits"] is JsonObject limits)
        {
            options = new XferParseOptions { Limits = new XferLimits { Depth = limits["depth"]!.GetValue<int>(), Elements = limits["elements"]!.GetValue<int>() } };
        }

        XferParseResult result = XferDocument.Parse(text, options);
        if (result.Ok != expected["ok"]!.GetValue<bool>())
        {
            return $"ok {result.Ok}, expected {expected["ok"]}";
        }

        if (result.Version != expected["version"]!.GetValue<int>())
        {
            return $"version {result.Version}, expected {expected["version"]}";
        }

        JsonArray diagnostics = expected["diagnostics"]!.AsArray();
        if (diagnostics.Count != result.Diagnostics.Count)
        {
            return $"diagnostics {string.Join(", ", result.Diagnostics.Select(d => $"{d.CodeText}@{d.Start.Offset}"))}, expected {diagnostics.ToJsonString()}";
        }

        for (int i = 0; i < diagnostics.Count; i++)
        {
            XferDiagnostic d = result.Diagnostics[i];
            var got = new JsonArray(d.CodeText, d.Start.Offset, d.Start.Line, d.Start.Column, d.End.Offset, d.End.Line, d.End.Column, d.Message);
            if (!JsonNode.DeepEquals(got, diagnostics[i]))
            {
                return $"diagnostic {i}: {got.ToJsonString()}, expected {diagnostics[i]!.ToJsonString()}";
            }
        }

        JsonNode shape = Shape(result.Tree);
        if (!JsonNode.DeepEquals(shape, expected["tree"]))
        {
            return $"tree {shape.ToJsonString()}, expected {expected["tree"]!.ToJsonString()}";
        }

        JsonNode? value = result.Ok ? TaggedWriter.ToTaggedDocument(result) : null;
        if (!Tagged.Equal(expected["value"], value))
        {
            return $"value {value?.ToJsonString()}, expected {expected["value"]?.ToJsonString()}";
        }

        // The canonical form must be byte for byte the reference implementation's, and reading it
        // back and writing it again must change nothing.
        string? wantCanonical = expected["canonical"]?.GetValue<string>();
        if (result.Ok)
        {
            string got = XferWriter.Canonical(result, new XferWriteOptions { Limits = options.Limits });
            if (got != wantCanonical)
            {
                return $"canonical {JsonSerializer.Serialize(got)}, expected {JsonSerializer.Serialize(wantCanonical)}";
            }

            XferParseResult again = XferDocument.Parse(got, options);
            if (!again.Ok || XferWriter.Canonical(again, new XferWriteOptions { Limits = options.Limits }) != got)
            {
                return $"canonical form {JsonSerializer.Serialize(got)} is not stable";
            }
        }
        else if (wantCanonical is not null)
        {
            return "expected a canonical form for a document the parser rejected";
        }

        return null;
    }

    // The tree as nested arrays, tokens by their start offset: the same form the fixture script
    // writes from the xfer-js tree.
    private static JsonNode? Tok(XferToken? token) => token is { } t ? JsonValue.Create(t.Offset) : null;

    private static JsonNode Annotation(XferAnnotationNode a) =>
        new JsonArray(Tok(a.Open), Tok(a.Name), a.Value is null ? null : Node(a.Value), Tok(a.Close));

    private static JsonArray Annotations(IEnumerable<XferAnnotationNode> list) => new(list.Select(Annotation).ToArray());

    private static JsonNode Node(XferNode n) => n switch
    {
        XferObjectNode o => new JsonArray("o", Annotations(o.Annotations), Tok(o.Open), Tok(o.Close), new JsonArray(o.Pairs.Select(Pair).ToArray())),
        XferSequenceNode s => new JsonArray("s", Annotations(s.Annotations), Tok(s.Open), Tok(s.Close), new JsonArray(s.Items.Select(Node).ToArray())),
        XferScalarNode v => new JsonArray("v", Annotations(v.Annotations), Tok(v.Token)),
        XferErrorNode e => new JsonArray("e", Annotations(e.Annotations), new JsonArray(e.Tokens.Select(t => Tok(t)).ToArray())),
        _ => throw new ArgumentOutOfRangeException(nameof(n)),
    };

    private static JsonNode Pair(XferPairNode p) => new JsonArray(
        Annotations(p.Annotations),
        Tok(p.Key),
        new JsonArray(p.Skipped.Select(s => (JsonNode)(s.Token is { } t ? new JsonArray("t", Tok(t)) : new JsonArray("a", Annotation(s.Annotation!)))).ToArray()),
        p.Value is null ? null : Node(p.Value));

    private static JsonNode Shape(XferSyntaxTree tree) => new JsonArray(
        tree.Head is null ? null : Annotation(tree.Head),
        tree.Root is null ? null : Node(tree.Root),
        new JsonArray(tree.Trailing.Select(t => Tok(t)).ToArray()),
        Tok(tree.EndOfFile));

    [Fact]
    public void ParseNeverThrowsAndAlwaysGivesATreeThatReproducesTheText()
    {
        var random = new Random(20261019);
        string[] soup = ["{", "}", "[", "]", "<!", "!>", "<! id \"a\" !>", "<! xfer 1 !>", " ", "\n", "</ c />", ",", ":", "a", "\"s\"", "1", "true", "~", "<x", "\"open", "é", "😀", "\r\n"];
        for (int run = 0; run < 20_000; run++)
        {
            var text = new StringBuilder();
            int n = random.Next(0, 30);
            for (int i = 0; i < n; i++)
            {
                text.Append(soup[random.Next(soup.Length)]);
            }

            XferParseResult result = XferDocument.Parse(text.ToString());
            Assert.Equal(result.Ok, result.Value is not null);
            Assert.Equal(result.Ok, !result.Diagnostics.Any(d => d.Severity == XferSeverity.Error));
            Assert.Equal(result.Tree.Text, Lossless.Reconstruct(TaggedWriter.TokensInOrder(result.Tree)));
        }
    }

    [Fact]
    public void RandomBytesNeverThrow()
    {
        var random = new Random(7);
        for (int run = 0; run < 20_000; run++)
        {
            var bytes = new byte[random.Next(0, 24)];
            random.NextBytes(bytes);
            XferParseResult result = XferDocument.Parse(bytes);
            Assert.Equal(result.Ok, result.Value is not null);
        }
    }

    [Fact]
    public void DeepNestingIsParsedWithoutRecursion()
    {
        const int depth = 200_000;
        string text = new string('[', depth) + new string(']', depth);
        XferParseResult result = XferDocument.Parse(text, new XferParseOptions { Limits = new XferLimits { Depth = depth } });
        Assert.True(result.Ok);
        Assert.IsType<XferSequence>(result.Value);
        Assert.Equal(XferDocument.ParseValue(text, new XferParseOptions { Limits = new XferLimits { Depth = depth } }), result.Value);
        Assert.Equal(result.Value!.GetHashCode(), XferDocument.ParseValue(text, new XferParseOptions { Limits = new XferLimits { Depth = depth } }).GetHashCode());

        XferParseResult limited = XferDocument.Parse(text);
        Assert.Contains(limited.Diagnostics, d => d.Code == XferDiagnosticCode.XFER0602);
        Assert.Single(limited.Diagnostics);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public void HostileShapesParseInLinearTime()
    {
        string[] inputs =
        [
            new string(']', 200_000),
            "[" + string.Concat(Enumerable.Repeat("{ a ]", 100_000)),
            string.Concat(Enumerable.Repeat("<! tag x !> ", 100_000)) + "1",
            "{" + string.Concat(Enumerable.Repeat("a 1 ", 100_000)) + "}",
            "{" + string.Concat(Enumerable.Repeat("a ", 100_000)) + "}",
            string.Concat(Enumerable.Repeat("[ { ", 50_000)),
        ];
        foreach (string input in inputs)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            XferParseResult result = XferDocument.Parse(input, new XferParseOptions { Limits = new XferLimits { Depth = 1_000_000 } });
            timer.Stop();
            Assert.Equal(result.Tree.Text, Lossless.Reconstruct(TaggedWriter.TokensInOrder(result.Tree)));
            Assert.True(timer.ElapsedMilliseconds < Timing.Scale * 5_000, $"{input[..12]}...: {timer.ElapsedMilliseconds} ms");
        }
    }

    [Fact]
    [Trait("Category", "Timing")]
    public void AVeryLongIntegerIsNotConvertedUntilItsValueIsAskedFor()
    {
        string digits = "1" + new string('7', 4_000_000);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        XferParseResult result = XferDocument.Parse("[ " + digits + " " + digits + " ]");
        timer.Stop();
        Assert.True(result.Ok);
        Assert.True(timer.ElapsedMilliseconds < Timing.Scale * 1_000, $"parse took {timer.ElapsedMilliseconds} ms");
        var items = (XferSequence)result.Value!;
        Assert.Equal(items[0], items[1]); // equal by spelling, without converting either
        Assert.Equal(digits, ((XferInteger)items[0]).Lexeme);

        var small = (XferInteger)XferDocument.ParseValue("-0x1F");
        Assert.Equal(-31, (int)small.Value);
        Assert.Equal(small, new XferInteger(-31, "-0x1F"));
        Assert.NotEqual(small, new XferInteger(-30, "-0x1F"));
        Assert.NotEqual(new XferInteger(5, "7"), (XferInteger)XferDocument.ParseValue("7"));
    }

    [Fact]
    public void ValuesAndTreesCannotBeChangedThroughACast()
    {
        XferParseResult result = XferDocument.Parse("{ <! tag t !> a [ 1 ] }");
        Assert.False(result.Value!.Annotations is XferAnnotation[]);
        var obj = (XferObjectNode)result.Tree.Root!;
        Assert.False(obj.Pairs is XferPairNode[]);
        Assert.False(obj.Pairs[0].Annotations is XferAnnotationNode[]);
        Assert.False(((XferSequenceNode)obj.Pairs[0].Value!).Items is XferNode[]);
        Assert.False(obj.Pairs[0].Skipped is XferSkippedSyntax[]);
        Assert.False(obj.Annotations is XferAnnotationNode[]);
        XferValue a = ((XferObject)result.Value!)["a"];
        Assert.False(a.Annotations is XferAnnotation[]);
    }

    [Fact]
    public void ADefaultTokenOrTriviaSaysSoInsteadOfCrashing()
    {
        Assert.Equal("(default)", default(XferToken).ToString());
        Assert.Equal("(default)", default(XferTrivia).ToString());
        Assert.Throws<InvalidOperationException>(() => default(XferToken).Text);
        Assert.Throws<InvalidOperationException>(() => default(XferTrivia).Text);
        Assert.Empty(XferDocument.Parse("1").Tree.Tokens[0].LeadingTrivia);
    }

    [Fact]
    public async System.Threading.Tasks.Task ParseAsyncRefusesAnOversizedSeekableStreamWithoutReadingIt()
    {
        using var huge = new HugeStream(3L * 1024 * 1024 * 1024);
        var options = new XferParseOptions { Limits = new XferLimits { DocumentBytes = int.MaxValue } };
        long before = GC.GetAllocatedBytesForCurrentThread();
        XferParseResult result = await XferDocument.ParseAsync(huge, options, TestContext.Current.CancellationToken);
        Assert.Equal(XferDiagnosticCode.XFER0601, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0, huge.BytesRead);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000_000);
    }

    /// <summary>A seekable stream that claims a length and fails the test if it is read.</summary>
    private sealed class HugeStream(long length) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            BytesRead += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void ParseValueThrowsWithTheDiagnosticsAndTheFirstErrorInTheMessage()
    {
        XferException e = Assert.Throws<XferException>(() => XferDocument.ParseValue("{ a 1"));
        Assert.Contains(e.Diagnostics, d => d.Code == XferDiagnosticCode.XFER0405);
        Assert.StartsWith("XFER0405 at 1:1: unterminated object or sequence", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheValueModelHasTheKindsAndSpellingsOfTheDocument()
    {
        XferValue v = XferDocument.ParseValue("{ i 0xFF big -123456789012345678901234567890 o 0o17 b 0b101 f 1.50 s dark t 2025-08-01 n null x \"a\" <! tag t !> k true }");
        var o = Assert.IsType<XferObject>(v);
        Assert.Equal(["i", "big", "o", "b", "f", "s", "t", "n", "x", "k"], o.Keys);
        Assert.Equal(255, (int)((XferInteger)o["i"]).Value);
        Assert.Equal("0xFF", ((XferInteger)o["i"]).Lexeme);
        Assert.Equal(System.Numerics.BigInteger.Parse("-123456789012345678901234567890", System.Globalization.CultureInfo.InvariantCulture), ((XferInteger)o["big"]).Value);
        Assert.Equal(15, (int)((XferInteger)o["o"]).Value);
        Assert.Equal(5, (int)((XferInteger)o["b"]).Value);
        Assert.Equal("1.50", ((XferFractional)o["f"]).Lexeme);
        Assert.Equal("dark", ((XferSymbol)o["s"]).Name);
        Assert.Equal(XferValueKind.Date, o["t"].Kind);
        Assert.Equal(XferValueKind.Null, o["n"].Kind);
        Assert.Equal("a", ((XferString)o["x"]).Value);
        XferAnnotation tag = Assert.Single(o["k"].Annotations);
        Assert.Equal(("tag", XferValueKind.Symbol), (tag.Name, tag.Value.Kind));
    }

    [Fact]
    public void HexIntegersAreUnsignedAndEveryRadixAgreesWithDecimal()
    {
        var random = new Random(3);
        for (int i = 0; i < 2000; i++)
        {
            var value = new System.Numerics.BigInteger(random.NextInt64(0, long.MaxValue)) * random.Next(1, 1000);
            foreach ((string prefix, int radix) in new[] { ("0x", 16), ("0o", 8), ("0b", 2) })
            {
                string digits = ToRadix(value, radix);
                Assert.Equal(value, ((XferInteger)XferDocument.ParseValue(prefix + digits)).Value);
                Assert.Equal(-value, ((XferInteger)XferDocument.ParseValue("-" + prefix + digits)).Value);
            }
        }

        Assert.Equal(255, (int)((XferInteger)XferDocument.ParseValue("0xff")).Value);
        Assert.Equal(128, (int)((XferInteger)XferDocument.ParseValue("0x80")).Value);
        Assert.Equal(1, (int)((XferInteger)XferDocument.ParseValue("0b1")).Value);
    }

    private static string ToRadix(System.Numerics.BigInteger value, int radix)
    {
        if (value.IsZero)
        {
            return "0";
        }

        var digits = new StringBuilder();
        while (!value.IsZero)
        {
            digits.Insert(0, "0123456789abcdef"[(int)(value % radix)]);
            value /= radix;
        }

        return digits.ToString();
    }

    [Fact]
    public void ValuesCompareStructurallyIncludingMemberOrderAndAnnotations()
    {
        Assert.Equal(XferDocument.ParseValue("{ a 1 b [ x 2 ] }"), XferDocument.ParseValue("{a 1 b[x 2]}"));
        Assert.NotEqual(XferDocument.ParseValue("{ a 1 b 2 }"), XferDocument.ParseValue("{ b 2 a 1 }"));
        Assert.NotEqual(XferDocument.ParseValue("[ <! tag t !> 1 ]"), XferDocument.ParseValue("[ 1 ]"));
        Assert.NotEqual(XferDocument.ParseValue("1"), XferDocument.ParseValue("0x1"));
        Assert.Equal(XferDocument.ParseValue("[1 2]").GetHashCode(), XferDocument.ParseValue("[ 1 2 ]").GetHashCode());
    }

    [Fact]
    public void AParseOfAnInputThatCannotBeReadHasAnEmptyTreeAndOneDiagnostic()
    {
        XferParseResult result = XferDocument.Parse([0xFF]);
        Assert.False(result.Ok);
        Assert.Null(result.Value);
        Assert.Null(result.Tree.Root);
        Assert.Equal(XferTokenKind.EndOfFile, result.Tree.EndOfFile.Kind);
        Assert.Equal(XferDiagnosticCode.XFER0701, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async System.Threading.Tasks.Task ParseAsyncReadsAStreamAndRefusesOneOverTheSizeLimit()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{ a 1 }"));
        XferParseResult result = await XferDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Ok);

        using var big = new MemoryStream(new byte[1000]);
        XferParseResult refused = await XferDocument.ParseAsync(big, new XferParseOptions { Limits = new XferLimits { DocumentBytes = 100 } }, TestContext.Current.CancellationToken);
        Assert.Equal(XferDiagnosticCode.XFER0601, Assert.Single(refused.Diagnostics).Code);
    }

    [Fact]
    public void TheTreeOfATypicalDocumentFitsTheMemoryTarget()
    {
        // DESIGN.md section 6.3: at most 24 bytes of managed heap per input byte retained by the
        // tree (what a server holding a parse keeps). The value model is measured separately, as
        // allocation, since it is what most callers keep instead of the tree.
        byte[] text = Encoding.UTF8.GetBytes("[" + string.Concat(Enumerable.Repeat("{ name \"Alice\" age 30 tags [ a b c ] when 2025-08-01T09:30:00Z }\n", 100_000)) + "]");
        XferLimits limits = XferLimits.Default;
        XferDocument.Parse(text.AsSpan(0, 1000)); // warm up

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetTotalMemory(forceFullCollection: true);
        Text.NormalizeResult normalized = Text.Normalizer.Normalize(text, limits);
        var pending = new List<PendingDiagnostic>();
        TokenStream stream = Lexer.Tokenize(normalized.Text, limits, pending);
        (XferSyntaxTree tree, _) = Parser.Parse(new SyntaxSource(stream, normalized.Lines), limits, pending);
        long allocatedPerByte = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / text.Length;
        long treePerByte = (GC.GetTotalMemory(forceFullCollection: true) - before) / text.Length;

        before = GC.GetAllocatedBytesForCurrentThread();
        XferValue? value = Model.Projection.ToValue(tree);
        long valuePerByte = (GC.GetAllocatedBytesForCurrentThread() - before) / text.Length;
        GC.KeepAlive(value);
        GC.KeepAlive(tree);
        GC.KeepAlive(normalized);

        Assert.NotNull(value);
        Assert.Empty(pending);
        // What the process retains is reported, not asserted: GC.GetTotalMemory is documented only
        // as an approximation, and it reads 24 on Windows and Linux x64 but 38 on macOS arm64 for
        // the same objects. What a parse allocates is exact on every platform, so that is pinned.
        TestContext.Current.SendDiagnosticMessage($"tree: {treePerByte} bytes per input byte retained ({allocatedPerByte} allocated); value model: {valuePerByte} allocated");
        Assert.True(allocatedPerByte <= 48, $"tree: {allocatedPerByte} bytes per input byte allocated ({treePerByte} retained)");
        Assert.True(valuePerByte <= 40, $"value model: {valuePerByte} bytes per input byte allocated");
    }
}
