using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ParksComputing.XferLang.Syntax;
using ParksComputing.XferLang.Tests.Harness;
using ParksComputing.XferLang.Text;
using Xunit;

namespace ParksComputing.XferLang.Tests;

public class LexerTests
{
    private static readonly XferLimits Defaults = XferLimits.Default;

    // The diagnostics the lexer and the normalizer produce.
    private static readonly HashSet<string> Lexical =
    [
        "XFER0101", "XFER0102", "XFER0103", "XFER0104", "XFER0105", "XFER0201", "XFER0202",
        "XFER0301", "XFER0302", "XFER0304", "XFER0305", "XFER0601", "XFER0603", "XFER0604", "XFER0701",
    ];

    private sealed record Lexed(byte[] Text, TokenStream Stream, List<XferDiagnostic> Diagnostics);

    private static Lexed Lex(string text, XferLimits? limits = null) => Lex(Encoding.UTF8.GetBytes(text), limits);

    private static Lexed Lex(byte[] normalized, XferLimits? limits = null)
    {
        var pending = new List<PendingDiagnostic>();
        TokenStream stream = Lexer.Tokenize(normalized, limits ?? Defaults, pending);
        // The text is already normalized, so its line map is made directly; normalizing it again
        // would drop a leading U+FEFF, which here is content, and shift every position.
        return new Lexed(normalized, stream, DiagnosticResolver.Resolve(pending, LineMap.Create(normalized)));
    }

    private static string Reconstruct(Lexed lexed)
    {
        var text = new StringBuilder();
        foreach (Token token in lexed.Stream.Tokens)
        {
            for (int i = token.FirstTrivia; i < token.FirstTrivia + token.TriviaCount; i++)
            {
                Trivia trivia = lexed.Stream.Trivia[i];
                text.Append(Encoding.UTF8.GetString(lexed.Text, trivia.Start, trivia.Length));
            }

            text.Append(Encoding.UTF8.GetString(lexed.Text, token.Start, token.Length));
        }

        return text.ToString();
    }

    private static string KindName(TokenKind kind) => kind switch
    {
        TokenKind.LeftBrace => "lbrace",
        TokenKind.RightBrace => "rbrace",
        TokenKind.LeftBracket => "lbrack",
        TokenKind.RightBracket => "rbrack",
        TokenKind.AnnotationOpen => "annot-open",
        TokenKind.AnnotationClose => "annot-close",
        TokenKind.String => "string",
        TokenKind.Integer => "integer",
        TokenKind.Fractional => "fractional",
        TokenKind.Date => "date",
        TokenKind.Time => "time",
        TokenKind.LocalDateTime => "local-datetime",
        TokenKind.DateTime => "datetime",
        TokenKind.Keyword => "keyword",
        TokenKind.Identifier => "identifier",
        TokenKind.Error => "error",
        TokenKind.EndOfFile => "eof",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string KeywordName(Keyword keyword) => keyword switch
    {
        Keyword.True => "true",
        Keyword.False => "false",
        Keyword.Null => "null",
        Keyword.Inf => "inf",
        Keyword.MinusInf => "-inf",
        Keyword.Nan => "nan",
        _ => "none",
    };

    private static string[] Shape(string text) =>
        Lex(text).Stream.Tokens.Where(t => t.Kind != TokenKind.EndOfFile)
            .Select(t => $"{KindName(t.Kind)} {Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text), t.Start, t.Length)}")
            .ToArray();

    private static string[] Codes(string text, XferLimits? limits = null) =>
        Lex(text, limits).Diagnostics.Select(d => $"{d.CodeText} {d.Start.Line}:{d.Start.Column}").ToArray();

    [Fact]
    public void AgreesWithTheReferenceLexerTokenForTokenAndDiagnosticForDiagnostic()
    {
        // fixtures/lexer.jsonl is written by tools/fixtures/lexer-fixture.mjs from the xfer-js
        // lexer: every suite document plus 6,000 random texts.
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "lexer.jsonl");
        var failures = new List<string>();
        int cases = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            cases++;
            using JsonDocument doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 256 });
            JsonElement root = doc.RootElement;
            string label = root.GetProperty("label").GetString()!;
            string text = root.GetProperty("text").GetString()!;
            string? problem = Compare(text, root);
            if (problem is not null)
            {
                failures.Add($"{label} {JsonSerializer.Serialize(text)}: {problem}");
                if (failures.Count == 10)
                {
                    break;
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.True(cases > 6000, $"only {cases} fixture cases");
    }

    private static string? Compare(string text, JsonElement expected)
    {
        XferLimits limits = Defaults;
        if (expected.GetProperty("limits") is { ValueKind: JsonValueKind.Object } tight)
        {
            limits = new XferLimits { RunLength = tight.GetProperty("runLength").GetInt32(), TokenBytes = tight.GetProperty("tokenBytes").GetInt32() };
        }

        Lexed lexed = Lex(text, limits);
        JsonElement[] tokens = expected.GetProperty("tokens").EnumerateArray().ToArray();
        if (tokens.Length != lexed.Stream.Tokens.Count)
        {
            return $"{lexed.Stream.Tokens.Count} tokens, expected {tokens.Length}";
        }

        for (int i = 0; i < tokens.Length; i++)
        {
            JsonElement want = tokens[i];
            Token got = lexed.Stream.Tokens[i];
            string wantKind = want[0].GetString()!;
            if (KindName(got.Kind) != wantKind || got.Start != want[1].GetInt32() || got.End != want[2].GetInt32())
            {
                return $"token {i}: {KindName(got.Kind)} {got.Start}..{got.End}, expected {wantKind} {want[1]}..{want[2]}";
            }

            JsonElement[] wantTrivia = want[3].EnumerateArray().ToArray();
            if (wantTrivia.Length != got.TriviaCount)
            {
                return $"token {i}: {got.TriviaCount} trivia, expected {wantTrivia.Length}";
            }

            for (int t = 0; t < wantTrivia.Length; t++)
            {
                Trivia trivia = lexed.Stream.Trivia[got.FirstTrivia + t];
                string kind = trivia.Kind == TriviaKind.Comment ? "comment" : "whitespace";
                if (kind != wantTrivia[t][0].GetString() || trivia.Start != wantTrivia[t][1].GetInt32()
                    || trivia.Start + trivia.Length != wantTrivia[t][2].GetInt32() || trivia.Run != wantTrivia[t][3].GetInt32())
                {
                    return $"token {i} trivia {t}: {kind} {trivia.Start}..{trivia.Start + trivia.Length} run {trivia.Run}, expected {wantTrivia[t]}";
                }
            }

            if (got.Kind == TokenKind.String)
            {
                string content = Encoding.UTF8.GetString(lexed.Text, got.ContentStart, got.ContentLength);
                string form = got.Form == StringForm.Hash ? "hash" : "rep";
                if (content != want[4].GetString() || form != want[5].GetString() || got.Run != want[6].GetInt32())
                {
                    return $"token {i}: string {JsonSerializer.Serialize(content)} {form} {got.Run}, expected {want[4]} {want[5]} {want[6]}";
                }
            }
            else if (got.Kind == TokenKind.Keyword && KeywordName(got.Keyword) != want[4].GetString())
            {
                return $"token {i}: keyword {KeywordName(got.Keyword)}, expected {want[4]}";
            }

            // Fields that belong to another kind of token must be empty.
            if (got.Kind != TokenKind.Keyword && got.Keyword != Keyword.None)
            {
                return $"token {i}: a {KindName(got.Kind)} with keyword {got.Keyword}";
            }

            if (got.Kind != TokenKind.String && (got.Form != StringForm.None || got.Run != 0))
            {
                return $"token {i}: a {KindName(got.Kind)} with string form {got.Form} run {got.Run}";
            }
        }

        JsonElement[] diagnostics = expected.GetProperty("diagnostics").EnumerateArray().ToArray();
        if (diagnostics.Length != lexed.Diagnostics.Count)
        {
            return $"diagnostics {string.Join(", ", lexed.Diagnostics.Select(d => d.CodeText + "@" + d.Start.Offset))}, expected {expected.GetProperty("diagnostics")}";
        }

        for (int i = 0; i < diagnostics.Length; i++)
        {
            JsonElement want = diagnostics[i];
            XferDiagnostic got = lexed.Diagnostics[i];
            if (got.CodeText != want[0].GetString() || got.Start.Offset != want[1].GetInt32()
                || got.Start.Line != want[2].GetInt32() || got.Start.Column != want[3].GetInt32()
                || got.Message != want[4].GetString())
            {
                return $"diagnostic {i}: {got.CodeText} {got.Start} \"{got.Message}\", expected {want}";
            }
        }

        return null;
    }

    [Fact]
    public void LexesEveryValidSuiteDocumentWithoutADiagnosticAndReproducesIt()
    {
        foreach (ValidCase c in SuiteCases.Valid)
        {
            NormalizeResult normalized = Normalizer.Normalize(c.Input, Defaults);
            Assert.Null(normalized.Fault);
            Lexed lexed = Lex(normalized.Text);
            Assert.True(lexed.Diagnostics.Count == 0, $"{c.Id}: {string.Join(", ", lexed.Diagnostics.Select(d => d.Message))}");
            Assert.Equal(Encoding.UTF8.GetString(normalized.Text), Reconstruct(lexed));
            Assert.Equal(TokenKind.EndOfFile, lexed.Stream.Tokens[^1].Kind);
        }
    }

    [Fact]
    public void ReportsWhatEveryLexicalInvalidCaseRequires()
    {
        InvalidCase[] lexical = SuiteCases.Invalid.Where(c => c.Diagnostics.All(d => Lexical.Contains(d.Code))).ToArray();
        Assert.True(lexical.Length > 30, $"only {lexical.Length} lexical cases");
        foreach (InvalidCase c in lexical)
        {
            NormalizeResult normalized = Normalizer.Normalize(c.Input, Defaults);
            List<XferDiagnostic> found = normalized.Fault is { } fault ? [fault] : Lex(normalized.Text).Diagnostics;
            foreach (ExpectedDiagnostic want in c.Diagnostics)
            {
                bool hit = found.Any(d => d.CodeText == want.Code && (want.Line is null || (d.Start.Line == want.Line && d.Start.Column == want.Column)));
                Assert.True(hit, $"{c.Id}: expected {want.Code} at {want.Line}:{want.Column}; got {string.Join(", ", found.Select(d => $"{d.CodeText} {d.Start.Line}:{d.Start.Column}"))}");
            }
        }
    }

    [Fact]
    public void LexesTheSelfDelimitingTokensWithOrWithoutSpace()
    {
        Assert.Equal(
            ["lbrace {", "identifier a", "integer 1", "identifier b", "lbrack [", "integer 1", "integer 2", "rbrack ]", "rbrace }"],
            Shape("{a 1 b[1 2]}"));
        Assert.Equal(["annot-open <!", "identifier xfer", "integer 1", "annot-close !>"], Shape("<!xfer 1!>"));
        Assert.Contains("XFER0102 1:1", Codes("<x"));
    }

    [Fact]
    public void AnEmptyTextIsOneEndOfFileToken()
    {
        Lexed lexed = Lex(string.Empty);
        Token only = Assert.Single(lexed.Stream.Tokens);
        Assert.Equal(TokenKind.EndOfFile, only.Kind);
        Assert.Empty(lexed.Diagnostics);
    }

    [Fact]
    public void CommentsAttachToTheNextTokenWithTheirRunLength()
    {
        Lexed lexed = Lex("</ a /> <// b </ c /> //> 1");
        Token first = lexed.Stream.Tokens[0];
        Assert.Equal(TokenKind.Integer, first.Kind);
        Assert.Equal(
            [(TriviaKind.Comment, 1), (TriviaKind.Whitespace, 0), (TriviaKind.Comment, 2), (TriviaKind.Whitespace, 0)],
            lexed.Stream.Trivia.Skip(first.FirstTrivia).Take(first.TriviaCount).Select(t => (t.Kind, t.Run)).ToArray());
        Assert.Empty(Codes("</ a//> b /> 1"));
        Assert.Equal(["XFER0104 1:1"], Codes("<// a /> 1"));
        Assert.Equal(["integer 1", "integer 2"], Shape("1</ c />2"));
    }

    [Fact]
    public void StringsTakeTheirContentVerbatim()
    {
        static (string Content, StringForm Form, int Run) StringOf(string text)
        {
            Lexed lexed = Lex(text);
            Token t = lexed.Stream.Tokens[0];
            Assert.Equal(TokenKind.String, t.Kind);
            return (Encoding.UTF8.GetString(lexed.Text, t.ContentStart, t.ContentLength), t.Form, t.Run);
        }

        Assert.Equal(("plain", StringForm.Repetition, 1), StringOf("\"plain\""));
        Assert.Equal(("say \"hi\" now", StringForm.Repetition, 3), StringOf("\"\"\"say \"hi\" now\"\"\""));
        Assert.Equal(("C:\\Users\\n", StringForm.Repetition, 1), StringOf("\"C:\\Users\\n\""));
        Assert.Equal(("a\"\"b", StringForm.Repetition, 1), StringOf("\"a\"\"b\""));
        Assert.Equal((string.Empty, StringForm.Repetition, 1), StringOf("\"\""));
        Assert.Equal(("say \"hi\"", StringForm.Hash, 1), StringOf("#\"say \"hi\"\"#"));
        Assert.Equal(("\"\"", StringForm.Hash, 1), StringOf("#\"\"\"\"#"));
        Assert.Equal(["string \"\"", "error x\"\""], Shape("\"\"x\"\""));
        Assert.Equal(["XFER0101 1:3"], Codes("\"\"x\"\""));
    }

    [Fact]
    public void LimitsAreReportedAndTheStringIsStillRead()
    {
        var tight = new XferLimits { RunLength = 3 };
        Assert.Equal(["XFER0604 1:1"], Codes("\"\"\"\"x\"\"\"\"", tight));
        Assert.Equal(["XFER0604 1:1"], Codes("####\"x\"####", tight));
        Assert.Equal(["XFER0604 1:1"], Codes("<//// c ////> 1", tight));
        Assert.Empty(Codes("\"ab\"", new XferLimits { TokenBytes = 4 }));
        Assert.Equal(["XFER0603 1:1"], Codes("\"abc\"", new XferLimits { TokenBytes = 4 }));
    }

    [Fact]
    public void NumbersAndTemporalsEndWhereTheirGrammarEnds()
    {
        Assert.Equal(["XFER0101 1:2"], Codes("1-2"));
        Assert.Equal(["XFER0101 1:5"], Codes("-infinity"));
        Assert.Equal(["XFER0301 1:1"], Codes("-0755"));
        Assert.Equal(["XFER0302 1:1"], Codes("0XFF"));
        Assert.Equal(["XFER0103 1:1"], Codes("+1"));
        Assert.Equal(["XFER0305 1:1"], Codes("2023-02-29"));
        Assert.Equal(["XFER0304 1:1"], Codes("2025-08-01T09:30:00+0800"));
        Assert.Empty(Codes("2024-02-29 23:59:60 0000-02-29 2025-08-01T00:00:00-23:59"));
        Assert.Equal(["XFER0101 1:11"], Codes("2025-08-01t09:30:00"));
    }

    [Fact]
    public void IdentifiersAndKeywords()
    {
        Assert.Equal(["keyword true", "keyword false", "keyword null", "identifier True", "identifier NULL"], Shape("true false null True NULL"));
        Assert.Equal(["identifier naïve", "identifier 日本語", "identifier größe"], Shape("naïve 日本語 größe"));
        Assert.Equal(["XFER0105 1:1"], Codes("true."));
        Assert.Equal(["identifier true."], Shape("true."));
        Assert.Equal(["XFER0101 1:2"], Codes("a\u00A0b"));
    }

    [Fact]
    public void FaultsAreReportedOnceWithAHint()
    {
        Lexed lexed = Lex("[ 1-2 ]");
        XferDiagnostic d = Assert.Single(lexed.Diagnostics);
        Assert.Equal("scalar token not followed by a separator: found \"-\"", d.Message);
        Assert.Equal(["lbrack [", "integer 1", "error -2", "rbrack ]"], Shape("[ 1-2 ]"));
        Assert.Equal(["XFER0103 1:5"], Codes("{ a ~true b 1 }"));
        Assert.Contains("commas", Lex("[ 1, 2 ]").Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Contains("colon", Lex("{ \"a\": 1 }").Diagnostics[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PositionsCountLinesColumnsInScalarValuesAndOffsetsInBytes()
    {
        Lexed lexed = Lex("é\n  日 😀x 1");
        NormalizeResult map = Normalizer.Normalize(lexed.Text, Defaults);
        (int Start, XferPosition Position, int End)[] at = lexed.Stream.Tokens.Take(4).Select(t => (t.Start, map.Lines.GetPosition(t.Start), t.End)).ToArray();
        Assert.Equal((0, new XferPosition(0, 1, 1), 2), at[0]);
        Assert.Equal((5, new XferPosition(5, 2, 3), 8), at[1]);
        Assert.Equal((9, new XferPosition(9, 2, 5), 14), at[2]);
        Assert.Equal((15, new XferPosition(15, 2, 8), 16), at[3]);
    }

    [Fact]
    public void AnyValidTextLexesWithoutThrowingAndIsReproducedExactly()
    {
        var random = new Random(20261019);
        string[] alphabet = ["{", "}", "[", "]", "<", ">", "!", "/", "\"", "#", "-", ".", "_", ":", " ", "\n", "0", "1", "9", "a", "e", "T", "Z", "é", "😀", "\u00A0", "\u2028", "</", "<!", "!>", "\"\"", "inf", "2025-08-01", "09:30:00", "\u0000", "\u007F"];
        for (int run = 0; run < 20_000; run++)
        {
            var text = new StringBuilder();
            int pieces = random.Next(0, 40);
            for (int i = 0; i < pieces; i++)
            {
                text.Append(alphabet[random.Next(alphabet.Length)]);
            }

            Lexed lexed = Lex(text.ToString());
            Assert.Equal(text.ToString(), Reconstruct(lexed));
        }
    }

    [Fact]
    public void TheFixtureExercisesTheLengthLimits()
    {
        string all = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "lexer.jsonl"));
        Assert.Contains("\"XFER0603\"", all, StringComparison.Ordinal);
        Assert.Contains("\"XFER0604\"", all, StringComparison.Ordinal);
        Assert.Contains("\"limits\":{", all, StringComparison.Ordinal);
    }

    [Fact]
    public void ATokenIsTwentyFourBytes()
    {
        Assert.Equal(24, System.Runtime.CompilerServices.Unsafe.SizeOf<Token>());
        Assert.Equal(16, System.Runtime.CompilerServices.Unsafe.SizeOf<Trivia>());
    }

    [Fact]
    public void LexingATypicalDocumentAllocatesLittleMoreThanItsTokens()
    {
        byte[] text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{ name \"Alice\" age 30 tags [ a b c ] when 2025-08-01T09:30:00Z }\n", 60_000)));
        Lexer.Tokenize(text, Defaults, []); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        Lexer.Tokenize(text, Defaults, []);
        long perByte = (GC.GetAllocatedBytesForCurrentThread() - before) / text.Length;
        Assert.True(perByte <= 12, $"{perByte} bytes per input byte");
    }

    [Fact]
    public void LongAndHostileInputsLexInLinearTime()
    {
        // Each shape is 2 MB; a quadratic path would take minutes.
        string[] inputs =
        [
            new string('[', 2_000_000),
            "</" + new string('/', 63) + " " + string.Concat(Enumerable.Repeat("/// ", 500_000)),
            "\"" + new string('"', 3) + string.Concat(Enumerable.Repeat("\"\" ", 600_000)),
            string.Concat(Enumerable.Repeat("1-", 1_000_000)),
            string.Concat(Enumerable.Repeat("~", 2_000_000)),
        ];
        foreach (string input in inputs)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Lexed lexed = Lex(input);
            timer.Stop();
            Assert.Equal(input, Reconstruct(lexed));
            Assert.True(timer.ElapsedMilliseconds < 5_000, $"{input[..10]}...: {timer.ElapsedMilliseconds} ms");
        }
    }
}
