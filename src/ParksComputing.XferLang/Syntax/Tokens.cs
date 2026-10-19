using System.Collections.Generic;

namespace ParksComputing.XferLang.Syntax;

/// <summary>The kinds of token (xfer-js <c>TokenKind</c>).</summary>
internal enum TokenKind : byte
{
    LeftBrace,
    RightBrace,
    LeftBracket,
    RightBracket,
    AnnotationOpen,
    AnnotationClose,
    String,
    Integer,
    Fractional,
    Date,
    Time,
    LocalDateTime,
    DateTime,
    Keyword,
    Identifier,
    Error,
    EndOfFile,
}

/// <summary>Which keyword a <see cref="TokenKind.Keyword"/> token is.</summary>
internal enum Keyword : byte
{
    None,
    True,
    False,
    Null,
    Inf,
    MinusInf,
    Nan,
}

/// <summary>The delimiter form of a string token.</summary>
internal enum StringForm : byte
{
    None,

    /// <summary>A run of n quotes on each side.</summary>
    Repetition,

    /// <summary>m hashes and a quote on each side.</summary>
    Hash,
}

internal enum TriviaKind : byte
{
    Whitespace,
    Comment,
}

/// <summary>
/// One token, as a small struct in a flat array (DESIGN.md section 6.3). Positions are not stored:
/// <see cref="Start"/> and <see cref="Length"/> are in bytes of the normalized text, and a
/// position is resolved from the line map when someone asks for it.
/// </summary>
/// <param name="Kind">The kind.</param>
/// <param name="Start">The byte offset of the token's first byte.</param>
/// <param name="Length">The length in bytes, delimiters included.</param>
/// <param name="FirstTrivia">The index in the trivia array of the first trivia before the token.</param>
/// <param name="TriviaCount">How many trivia come before the token.</param>
/// <param name="Form">For a string: its delimiter form.</param>
/// <param name="Run">For a string: the run length of its delimiter (1 for <c>""</c>).</param>
/// <param name="Keyword">For a keyword: which.</param>
internal readonly record struct Token(
    TokenKind Kind,
    int Start,
    int Length,
    int FirstTrivia,
    int TriviaCount,
    StringForm Form = StringForm.None,
    int Run = 0,
    Keyword Keyword = Keyword.None)
{
    /// <summary>The byte offset just after the token.</summary>
    public int End => Start + Length;

    /// <summary>For a string: the byte offset where the content begins.</summary>
    public int ContentStart => Form == StringForm.Hash ? Start + Run + 1 : Start + Run;

    /// <summary>For a string: the length of the content in bytes.</summary>
    public int ContentLength => Form == StringForm.Hash ? Length - (2 * (Run + 1)) : Length - (2 * Run);
}

/// <param name="Kind">Whitespace or a comment.</param>
/// <param name="Start">The byte offset of its first byte.</param>
/// <param name="Length">The length in bytes.</param>
/// <param name="Run">For a comment: the length of its slash run.</param>
internal readonly record struct Trivia(TriviaKind Kind, int Start, int Length, int Run = 0);

/// <summary>A diagnostic whose position is still a byte offset, resolved later in one pass.</summary>
internal readonly record struct PendingDiagnostic(XferDiagnosticCode Code, int Offset, string? Detail = null);

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

    /// <summary>Every token, in order; the last is <see cref="TokenKind.EndOfFile"/>.</summary>
    public List<Token> Tokens { get; }

    /// <summary>Every piece of trivia, in order; each token refers to its own range.</summary>
    public List<Trivia> Trivia { get; }
}
