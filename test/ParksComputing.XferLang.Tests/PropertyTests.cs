using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ParksComputing.XferLang.Json;
using ParksComputing.XferLang.Syntax;
using ParksComputing.XferLang.Tests.Harness;
using ParksComputing.XferLang.Writing;
using Xunit;

namespace ParksComputing.XferLang.Tests;

/// <summary>
/// DESIGN.md section 12.4: the properties of xfer-js <c>test/parser.test.ts</c> ("random
/// documents"), <c>test/canonical.test.ts</c> ("round trips") and <c>test/strings.test.ts</c>
/// (the embedding invariant), over the ported generator and seeded random input. Every case is
/// drawn from a seed, so a failure names the input that caused it and reproduces anywhere.
/// </summary>
public sealed partial class PropertyTests
{
    private const int Seeds = 1500;
    private const int Limit = 64;

    private static string Show(string text) => JsonSerializer.Serialize(text);

    private static string Diagnostics(XferParseResult result) => string.Join("; ", result.Diagnostics.Select(d => $"{d.CodeText} {d.Start.Line}:{d.Start.Column} {d.Message}"));

    // ---- Random documents ----

    [Fact]
    public void RandomDocumentsParseToExactlyTheModelTheyWereBuiltFrom()
    {
        for (uint seed = 1; seed <= Seeds; seed++)
        {
            Generator.Document doc = Generator.Generate(seed);
            var random = new Mulberry32(seed * 7919);
            Generator.Separator sep = Generator.RandomSeparator(random);

            // Before the head there may be whitespace but no comment, so the head is the first token.
            string leading = random.Pick(["", " ", "\n  ", "\t"]);
            string text = Generator.Render(doc, sep, leading);
            XferParseResult result = XferDocument.Parse(text);
            string where = $"seed {seed}: {Show(text)}";
            Assert.True(result.Diagnostics.Count == 0, $"{where}\n{Diagnostics(result)}");
            JsonNode expected = Generator.Tagged(doc);
            JsonNode actual = TaggedWriter.ToTaggedDocument(result);
            Assert.True(Tagged.Equal(expected, actual), $"{where}\nexpected {expected.ToJsonString()}\nactual   {actual.ToJsonString()}");
            Assert.Equal(result.Tree.Text, Lossless.Reconstruct(TaggedWriter.TokensInOrder(result.Tree)));
        }
    }

    [Fact]
    public void RandomDocumentsParseTheSameWithEveryOptionalSpaceRemoved()
    {
        // The whitespace invariant: whitespace beside a bracket is optional, so removing it must not
        // change the value. The tight text is rendered from the model, so no string is touched.
        for (uint seed = 1; seed <= Seeds; seed++)
        {
            Generator.Document doc = Generator.Generate(seed);
            string tight = Generator.Render(doc, Generator.TightSeparator, string.Empty);
            XferParseResult result = XferDocument.Parse(tight);
            string where = $"seed {seed}: {Show(tight)}";
            Assert.True(result.Diagnostics.Count == 0, $"{where}\n{Diagnostics(result)}");
            Assert.True(Tagged.Equal(Generator.Tagged(doc), TaggedWriter.ToTaggedDocument(result)), where);
        }
    }

    [Fact]
    public void TheGeneratorCoversWhatItClaimsTo()
    {
        // A guard on the generator itself: if it stops producing a feature, this fails, instead of
        // the properties above quietly checking less than they say.
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        void Count(string feature, bool present) => seen[feature] = seen.GetValueOrDefault(feature) + (present ? 1 : 0);
        for (uint seed = 1; seed <= Seeds; seed++)
        {
            Generator.Document doc = Generator.Generate(seed);
            string text = Generator.Render(doc, Generator.RandomSeparator(new Mulberry32(seed * 7919)), string.Empty);
            Count("annotated", AnnotatedPattern().IsMatch(text));
            Count("root annotated", doc.Root.Annotations.Count > 0);
            Count("many annotations", ManyAnnotationsPattern().IsMatch(text));
            Count("id annotation", text.Contains("<! id ", StringComparison.Ordinal));
            Count("lang annotation", text.Contains("<! lang ", StringComparison.Ordinal));
            Count("string key", StringKeyPattern().IsMatch(text));
            Count("hash string", text.Contains("#\"", StringComparison.Ordinal));
            Count("triple-quote string", text.Contains("\"\"\"", StringComparison.Ordinal));
            Count("radix integer", RadixPattern().IsMatch(text));
            Count("underscore number", UnderscorePattern().IsMatch(text));
            Count("inf or nan", SpecialPattern().IsMatch(text));
            Count("nested container", NestedPattern().IsMatch(text));
            Count("comment", text.Contains("</", StringComparison.Ordinal));
            Count("scalar root", doc.Root.Node is Generator.ScalarModel);
            Count("head", doc.Head);
        }

        // The shares xfer-js requires. A scalar root is the one feature with a ceiling, since most
        // documents should have containers to check.
        foreach ((string feature, int count) in seen)
        {
            double share = (double)count / Seeds;
            if (feature == "scalar root")
            {
                Assert.True(share < 0.2, $"{feature}: {share:P1} of documents, not below 20%");
                continue;
            }

            double floor = feature switch
            {
                "annotated" => 0.4,
                "nested container" or "comment" => 0.5,
                "head" => 0.2,
                "inf or nan" => 0.05,
                _ => 0.1,
            };
            Assert.True(share > floor, $"{feature}: {share:P1} of documents, not above {floor:P0}");
        }

        Assert.Equal(15, seen.Count);
    }

    [GeneratedRegex(@"<! (id|tag|lang)")]
    private static partial Regex AnnotatedPattern();

    [GeneratedRegex(@"<![\s\S]*?!>\s*<!")]
    private static partial Regex ManyAnnotationsPattern();

    [GeneratedRegex(@"(^|[\s{])""[a-z{ ]+""\s")]
    private static partial Regex StringKeyPattern();

    [GeneratedRegex(@"0[xob][0-9a-f]")]
    private static partial Regex RadixPattern();

    [GeneratedRegex(@"\d_\d")]
    private static partial Regex UnderscorePattern();

    [GeneratedRegex(@"\b(inf|nan)\b")]
    private static partial Regex SpecialPattern();

    [GeneratedRegex(@"[\[{][\s\S]*[\[{]")]
    private static partial Regex NestedPattern();

    // ---- Round trips through the canonical writer ----

    [Fact]
    public void EveryGeneratedDocumentIsWrittenSoThatItReadsBackToTheSameValueAndWritingItAgainChangesNothing()
    {
        for (uint seed = 1; seed <= Seeds; seed++)
        {
            Generator.Document doc = Generator.Generate(seed);
            var random = new Mulberry32(seed * 7919);
            string text = Generator.Render(doc, Generator.RandomSeparator(random), random.Pick(["", " ", "\n"]));
            string where = $"seed {seed}: {Show(text)}";
            XferParseResult first = XferDocument.Parse(text);
            Assert.True(first.Ok, where);
            string once = XferWriter.Canonical(first);
            XferParseResult second = XferDocument.Parse(once);
            Assert.True(second.Diagnostics.Count == 0, $"{where}\n{Diagnostics(second)}");
            Assert.True(Tagged.Equal(Generator.Tagged(doc), TaggedWriter.ToTaggedDocument(second)), where);
            Assert.Equal(once, XferWriter.Canonical(second));
        }
    }

    [Fact]
    public void TheCanonicalFormIsTheSameWhateverTheSpacingOfTheSource()
    {
        for (uint seed = 1; seed <= Seeds; seed++)
        {
            Generator.Document doc = Generator.Generate(seed);
            string loose = XferWriter.Canonical(XferDocument.Parse(Generator.Render(doc, Generator.RandomSeparator(new Mulberry32(seed * 104729)), string.Empty)));
            string tight = XferWriter.Canonical(XferDocument.Parse(Generator.Render(doc, Generator.TightSeparator, string.Empty)));
            Assert.True(tight == loose, $"seed {seed}");
        }
    }

    public static TheoryData<string> ValidIds
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (ValidCase c in SuiteCases.Valid)
            {
                data.Add(c.Id);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ValidIds))]
    public void EveryValidSuiteDocumentWithItsWhitespaceCollapsedHasTheSameCanonicalForm(string id)
    {
        // The whitespace invariant (specification 2): replacing any run of whitespace between tokens
        // with a single space changes nothing. Comments stay, and strings are never touched.
        ValidCase c = SuiteCases.Valid.Single(v => v.Id == id);
        XferSyntaxTree tree = XferDocument.Parse(c.Input).Tree;
        var collapsed = new StringBuilder();
        foreach (XferToken token in tree.Tokens)
        {
            foreach (XferTrivia trivia in token.LeadingTrivia)
            {
                collapsed.Append(trivia.Kind == XferTriviaKind.Comment ? trivia.Text : " ");
            }

            collapsed.Append(token.Text);
        }

        XferParseResult result = XferDocument.Parse(collapsed.ToString());
        Assert.True(result.Diagnostics.Count == 0, $"{Show(collapsed.ToString())}\n{Diagnostics(result)}");
        Assert.Equal(c.Canonical, XferWriter.Canonical(result));
    }

    // ---- JSON (xfer-js test/json.test.ts) ----

    // Written as numbers, not escapes: the units include lone surrogates, which a source file
    // cannot hold literally, and the editing tools turn escapes into characters.
    private static readonly char[] TextUnits =
    [
        'a', 'Z', ' ', '"', '\\', '/', '\n', '\r', '\t', '#', '<', '{',
        (char)0x0000, (char)0x001F, (char)0x007F, (char)0x00E9, (char)0x65E5, (char)0x2028, (char)0xD83D, (char)0xDE00, (char)0xD800, (char)0xDFFF,
    ];

    /// <summary>A string of any UTF-16 units, lone surrogates and carriage returns included.</summary>
    private static string AnyText(Mulberry32 random)
    {
        var sb = new StringBuilder();
        for (int i = random.Below(12); i > 0; i--)
        {
            sb.Append(random.Pick(TextUnits));
        }

        return sb.ToString();
    }

    /// <summary>A random JSON value, as fast-check's jsonValue builds one, to a bounded depth.</summary>
    private static JsonNode? AnyJson(Mulberry32 random, int depth)
    {
        switch (random.Below(depth > 0 ? 8 : 6))
        {
            case 0:
                return null;
            case 1:
                return JsonValue.Create(random.Chance(0.5));
            case 2:
                return JsonValue.Create((long)(random.Next() * 2e15) - 1_000_000_000_000_000);
            case 3:
                return JsonValue.Create(Math.Round((random.Next() - 0.5) * 1e6, random.Below(6)));
            case 4:
            case 5:
                return JsonValue.Create(AnyText(random));
            case 6:
            {
                var array = new JsonArray();
                for (int i = random.Below(4); i > 0; i--)
                {
                    array.Add(AnyJson(random, depth - 1));
                }

                return array;
            }

            default:
            {
                var obj = new JsonObject();
                for (int i = random.Below(4); i > 0; i--)
                {
                    obj[AnyText(random)] = AnyJson(random, depth - 1);
                }

                return obj;
            }
        }
    }

    /// <summary>A JSON string literal as JSON.stringify writes it (ECMA-262 QuoteJSONString).</summary>
    private static string Stringify(string s)
    {
        const char backslash = (char)0x5C;
        var sb = new StringBuilder("\"");
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool pair = char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);
            if (pair)
            {
                sb.Append(c).Append(s[++i]);
            }
            else if (c is '"' or '\\')
            {
                sb.Append(backslash).Append(c);
            }
            else if (c < 0x20 || char.IsSurrogate(c))
            {
                sb.Append(backslash).Append('u').Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.Append('"').ToString();
    }

    private static bool XferCannotHold(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\r' || (char.IsHighSurrogate(s[i]) && !(i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))) || (char.IsLowSurrogate(s[i]) && !(i > 0 && char.IsHighSurrogate(s[i - 1]))))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void FromJsonNeverThrowsAndGivesAValueExactlyWhenThereIsNoError()
    {
        var random = new Mulberry32(5000);
        string[] pieces = ["{", "}", "[", "]", ",", ":", " ", "\"", "\\", "a", "1", "-", ".", "e", "0", "true", "null", "\"k\"", "\\u", "d800"];
        for (int run = 0; run < 5000; run++)
        {
            string text = random.Chance(0.5) ? AnyText(random) : Inputs(random, pieces);
            XferJsonResult<XferValue?> result = XferJson.FromJson(text);
            bool failed = result.Diagnostics.Any(d => d.Severity == XferSeverity.Error);
            Assert.True(result.Value is null == failed, Show(text));
        }

        static string Inputs(Mulberry32 random, string[] pieces)
        {
            var sb = new StringBuilder();
            for (int i = random.Below(16); i > 0; i--)
            {
                sb.Append(random.Pick(pieces));
            }

            return sb.ToString();
        }
    }

    [Fact]
    public void JsonValuesRoundTripThroughXferLang()
    {
        var random = new Mulberry32(174);
        var deep = new JsonDocumentOptions { MaxDepth = 64 };
        for (int run = 0; run < 5000; run++)
        {
            string text = AnyJson(random, 3)?.ToJsonString() ?? "null";
            XferJsonResult<XferValue?> read = XferJson.FromJson(text);
            if (read.Value is null)
            {
                // Only the characters XferLang cannot hold may stop a conversion.
                Assert.True(read.Diagnostics.All(d => d.Code == XferDiagnosticCode.XFER0907), $"{Show(text)}\n{string.Join("; ", read.Diagnostics)}");
                continue;
            }

            string? back = XferJson.ToJson(XferDocument.ParseValue(XferWriter.Canonical(read.Value))).Value;
            Assert.NotNull(back);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text, documentOptions: deep), JsonNode.Parse(back, documentOptions: deep)), $"{Show(text)}\n{Show(back)}");
        }
    }

    [Fact]
    public void AnyTextInJsonStringsAndKeysRoundTripsOrIsRefusedForWhatXferLangCannotHold()
    {
        var random = new Mulberry32(243);
        for (int run = 0; run < 5000; run++)
        {
            string s = AnyText(random);

            // Written as JSON.stringify writes it, with a lone surrogate escaped: System.Text.Json
            // would replace it with U+FFFD, and the case under test would be lost.
            string q = Stringify(s);
            string text = $"[{q},{{{q}:{q}}}]";
            XferJsonResult<XferValue?> read = XferJson.FromJson(text);
            if (XferCannotHold(s))
            {
                Assert.True(read.Value is null, Show(s));
                Assert.True(read.Diagnostics.All(d => d.Code == XferDiagnosticCode.XFER0907), Show(s));
                continue;
            }

            Assert.True(read.Diagnostics.Count == 0, $"{Show(s)}: {string.Join("; ", read.Diagnostics)}");
            var array = (XferSequence)XferDocument.ParseValue(XferWriter.Canonical(read.Value!));
            Assert.Equal(s, ((XferString)array[0]).Value);
            var obj = (XferObject)array[1];
            Assert.Equal([s], obj.Keys);
            Assert.Equal(s, ((XferString)obj[s]).Value);
        }
    }

    // ---- The embedding invariant (specification 2) ----

    private static string WriteString(string content, int runLimit) =>
        XferWriter.Canonical(new XferString(content), new XferWriteOptions { Limits = new XferLimits { RunLength = runLimit } });

    private static (IReadOnlyList<string> Codes, string? Content) Read(string text, int runLimit = Limit)
    {
        XferParseResult result = XferDocument.Parse(text, new XferParseOptions { Limits = new XferLimits { RunLength = runLimit } });
        return ([.. result.Diagnostics.Select(d => d.CodeText)], (result.Value as XferString)?.Value);
    }

    private static string Draw(Mulberry32 random, string[] alphabet, int maxLength)
    {
        var sb = new StringBuilder();
        for (int i = random.Below(maxLength + 1); i > 0; i--)
        {
            sb.Append(random.Pick(alphabet));
        }

        return sb.ToString();
    }

    public static TheoryData<string, string[]> Alphabets => new()
    {
        { "quotes and hashes", ["\"", "#", "a", " ", "\n"] },
        { "quotes only", ["\"", "x"] },
        { "hashes after quotes", ["\"", "#", "#"] },
        { "code and markup", ["\"", "#", "{", "}", "<", "!", "/", ">", "\\", "'", "(", ")", "a", " ", "\n", "\t"] },
        { "unicode and controls", ["é", "日", "😀", "\u0000", "\u0001", "\u2028", "\u00a0", "\"", "#"] },
    };

    [Theory]
    [MemberData(nameof(Alphabets))]
    public void AnyStringReadsBackAsItself(string name, string[] alphabet)
    {
        var random = new Mulberry32((uint)name.Length * 7919u);
        for (int run = 0; run < 4000; run++)
        {
            string content = Draw(random, alphabet, 64);
            (IReadOnlyList<string> codes, string? read) = Read(WriteString(content, Limit));
            Assert.True(codes.Count == 0, $"{Show(content)}: {string.Join(' ', codes)}");
            Assert.Equal(content, read);
        }
    }

    [Fact]
    public void AnyStringReadsBackUnderASmallerRunLimitWheneverItFitsWithinIt()
    {
        var random = new Mulberry32(424243);
        for (int run = 0; run < 4000; run++)
        {
            string content = Draw(random, ["\"", "#", "a"], 40);
            int limit = 3 + random.Below(6);
            if (StringDelimiters.Choose(content, limit).Run > limit)
            {
                continue; // nothing fits within the limit; the test below covers it
            }

            (IReadOnlyList<string> codes, string? read) = Read(WriteString(content, limit), limit);
            Assert.True(codes.Count == 0, $"{Show(content)} under {limit}: {string.Join(' ', codes)}");
            Assert.Equal(content, read);
        }
    }

    /// <summary>
    /// Strings assembled from runs of quotes and hashes of any length, so the hard cases are
    /// reached on purpose: quotes at either edge, a hash run after a quote, runs at and past the
    /// default limit of 64. A flat alphabet almost never builds a run of 64.
    /// </summary>
    private static string Structured(Mulberry32 random)
    {
        var sb = new StringBuilder();
        for (int parts = random.Below(8); parts > 0; parts--)
        {
            int n = 1 + random.Below(70);
            sb.Append(random.Below(4) switch
            {
                0 => new string('"', n),
                1 => "\"" + new string('#', n),
                2 => new string('#', n),
                _ => random.Pick(["a", " ", "\n", "x"]),
            });
        }

        return sb.ToString();
    }

    [Fact]
    public void StringsBuiltFromLongRunsReadBackAtTheEdgesAndTheLimit()
    {
        var random = new Mulberry32(20261020);
        for (int run = 0; run < 8000; run++)
        {
            string content = Structured(random);

            // A delimiter can pass the limit only for content no real document has; it is read
            // with a parser that admits longer runs, so that every case checks the content.
            (IReadOnlyList<string> codes, string? read) = Read(WriteString(content, Limit), 200);
            Assert.True(codes.Count == 0, $"{Show(content)}: {string.Join(' ', codes)}");
            Assert.Equal(content, read);

            // Within the default limit it reads back whenever its delimiter fits, and is refused
            // with XFER0604 alone when none does.
            (IReadOnlyList<string> limited, string? within) = Read(WriteString(content, Limit), Limit);
            if (StringDelimiters.Choose(content, Limit).Run <= Limit)
            {
                Assert.True(limited.Count == 0, Show(content));
                Assert.Equal(content, within);
            }
            else
            {
                Assert.Equal(["XFER0604"], limited);
            }
        }
    }

    [Fact]
    public void KeysReadBackAsThemselvesThroughTheCanonicalWriter()
    {
        var random = new Mulberry32(7);
        for (int run = 0; run < 4000; run++)
        {
            string key = Structured(random);
            var value = new XferObject([new(key, new XferInteger(1, "1"))]);
            string text = XferWriter.Canonical(value, new XferWriteOptions { Limits = new XferLimits { RunLength = 200 } });
            XferParseResult result = XferDocument.Parse(text, new XferParseOptions { Limits = new XferLimits { RunLength = 200 } });
            Assert.True(result.Diagnostics.Count == 0, $"{Show(key)}\n{Diagnostics(result)}");
            Assert.Equal([key], ((XferObject)result.Value!).Keys);
        }
    }
}
