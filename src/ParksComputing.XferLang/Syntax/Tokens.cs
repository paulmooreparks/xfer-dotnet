using System.Collections.Generic;

namespace ParksComputing.XferLang.Syntax;

/// <summary>The kind of a token in the lossless tree.</summary>
public enum XferTokenKind : byte
{
    /// <summary><c>{</c>, which opens an object.</summary>
    LeftBrace,

    /// <summary><c>}</c>, which closes an object.</summary>
    RightBrace,

    /// <summary><c>[</c>, which opens a sequence.</summary>
    LeftBracket,

    /// <summary><c>]</c>, which closes a sequence.</summary>
    RightBracket,

    /// <summary><c>&lt;!</c>, which opens an annotation.</summary>
    AnnotationOpen,

    /// <summary><c>!&gt;</c>, which closes an annotation.</summary>
    AnnotationClose,

    /// <summary>A string, in either delimiter form.</summary>
    String,

    /// <summary>An integer in any radix.</summary>
    Integer,

    /// <summary>A fractional number. <c>inf</c>, <c>-inf</c> and <c>nan</c> are keywords instead.</summary>
    Fractional,

    /// <summary>A date, <c>2025-08-01</c>.</summary>
    Date,

    /// <summary>A time, <c>09:30:00</c>.</summary>
    Time,

    /// <summary>A date and time with no offset.</summary>
    LocalDateTime,

    /// <summary>A date and time with <c>Z</c> or an offset.</summary>
    DateTime,

    /// <summary><c>true</c>, <c>false</c>, <c>null</c>, <c>inf</c>, <c>-inf</c> or <c>nan</c>.</summary>
    Keyword,

    /// <summary>A bare word: a symbol in value position, a key in key position.</summary>
    Identifier,

    /// <summary>Text the lexer could not read as a token; a diagnostic says why.</summary>
    Error,

    /// <summary>The end of the document. Its leading trivia is the document's trailing trivia.</summary>
    EndOfFile,
}

/// <summary>Which keyword a <see cref="XferTokenKind.Keyword"/> token is.</summary>
public enum XferKeyword : byte
{
    /// <summary>The token is not a keyword.</summary>
    None,

    /// <summary><c>true</c>.</summary>
    True,

    /// <summary><c>false</c>.</summary>
    False,

    /// <summary><c>null</c>.</summary>
    Null,

    /// <summary><c>inf</c>.</summary>
    Inf,

    /// <summary><c>-inf</c>.</summary>
    MinusInf,

    /// <summary><c>nan</c>.</summary>
    Nan,
}

/// <summary>The delimiter form of a string token.</summary>
public enum XferStringForm : byte
{
    /// <summary>The token is not a string.</summary>
    None,

    /// <summary>A run of n quotes on each side.</summary>
    Repetition,

    /// <summary>m hashes and a quote on each side.</summary>
    Hash,
}

/// <summary>The kind of a piece of trivia: text that is not part of any value.</summary>
public enum XferTriviaKind : byte
{
    /// <summary>Spaces, tabs, line feeds and form feeds.</summary>
    Whitespace,

    /// <summary>A comment, <c>&lt;/ ... /&gt;</c>.</summary>
    Comment,
}

/// <summary>
/// One token, as a small struct in a flat array (DESIGN.md section 6.3). Positions are not stored:
/// <see cref="Start"/> and <see cref="Length"/> are in bytes of the normalized text, and a
/// position is resolved from the line map when someone asks for it.
/// </summary>
internal readonly struct Token
{
    // Declared ints first and the three byte-sized fields last, so the struct is 24 bytes
    // (a test checks it); declared in parameter order it would be 32.
    private readonly int start;
    private readonly int length;
    private readonly int firstTrivia;
    private readonly int triviaCount;
    private readonly int run;
    private readonly XferTokenKind kind;
    private readonly XferStringForm form;
    private readonly XferKeyword keyword;

    public Token(
        XferTokenKind kind,
        int start,
        int length,
        int firstTrivia,
        int triviaCount,
        XferStringForm form = XferStringForm.None,
        int run = 0,
        XferKeyword keyword = XferKeyword.None)
    {
        this.kind = kind;
        this.start = start;
        this.length = length;
        this.firstTrivia = firstTrivia;
        this.triviaCount = triviaCount;
        this.form = form;
        this.run = run;
        this.keyword = keyword;
    }

    public XferTokenKind Kind => kind;

    public int Start => start;

    public int Length => length;

    public int FirstTrivia => firstTrivia;

    public int TriviaCount => triviaCount;

    public XferStringForm Form => form;

    public int Run => run;

    public XferKeyword Keyword => keyword;

    /// <summary>The byte offset just after the token.</summary>
    public int End => Start + Length;

    /// <summary>For a string: the byte offset where the content begins.</summary>
    public int ContentStart => Form == XferStringForm.Hash ? Start + Run + 1 : Start + Run;

    /// <summary>For a string: the length of the content in bytes.</summary>
    public int ContentLength => Form == XferStringForm.Hash ? Length - (2 * (Run + 1)) : Length - (2 * Run);
}

/// <param name="Kind">Whitespace or a comment.</param>
/// <param name="Start">The byte offset of its first byte.</param>
/// <param name="Length">The length in bytes.</param>
/// <param name="Run">For a comment: the length of its slash run.</param>
internal readonly record struct Trivia(XferTriviaKind Kind, int Start, int Length, int Run = 0);

/// <summary>A diagnostic whose position is still a byte offset, resolved later in one pass.</summary>
internal readonly record struct PendingDiagnostic(XferDiagnosticCode Code, int Offset, string? Detail = null, int End = -1);

/// <summary>The output of the lexer: every token through the end-of-file token, and the trivia.</summary>
internal sealed class TokenStream
{
    public TokenStream(byte[] text, List<Token> tokens, List<Trivia> trivia)
    {
        Text = text;
        Tokens = tokens;
        Trivia = trivia;
    }

    /// <summary>The normalized text the offsets refer to.</summary>
    public byte[] Text { get; }

    /// <summary>Every token, in order; the last is <see cref="XferTokenKind.EndOfFile"/>.</summary>
    public List<Token> Tokens { get; }

    /// <summary>Every piece of trivia, in order; each token refers to its own range.</summary>
    public List<Trivia> Trivia { get; }
}
