using System;
using System.Collections.Generic;
using System.Text;
using ParksComputing.XferLang.Text;

namespace ParksComputing.XferLang.Syntax;

/// <summary>What every view of a tree reads from: the tokens, the trivia and the line map.</summary>
internal sealed class SyntaxSource
{
    public SyntaxSource(TokenStream stream, LineMap lines)
    {
        Stream = stream;
        Lines = lines;
    }

    public TokenStream Stream { get; }

    public LineMap Lines { get; }

    public byte[] Bytes => Stream.Text;

    public Token TokenAt(int index) => Stream.Tokens[index];

    public string Slice(int start, int length) => Encoding.UTF8.GetString(Stream.Text, start, length);

    public string TextOf(int tokenIndex)
    {
        Token t = Stream.Tokens[tokenIndex];
        return Slice(t.Start, t.Length);
    }

    public string ContentOf(int tokenIndex)
    {
        Token t = Stream.Tokens[tokenIndex];
        return Slice(t.ContentStart, t.ContentLength);
    }

    public XferToken View(int tokenIndex) => new(this, tokenIndex);

    public XferToken? ViewOrNull(int tokenIndex) => tokenIndex < 0 ? null : new XferToken(this, tokenIndex);
}

/// <summary>
/// The lossless tree of a document (specification 9): every byte of the normalized text is
/// reachable from it, through the tokens and the trivia before each one.
/// </summary>
public sealed class XferSyntaxTree
{
    private readonly int[] trailing;
    private string? text;

    internal XferSyntaxTree(SyntaxSource source, XferAnnotationNode? head, XferNode? root, int[] trailing, int endOfFile)
    {
        Source = source;
        Head = head;
        Root = root;
        this.trailing = trailing;
        EndOfFile = new XferToken(source, endOfFile);
    }

    /// <summary>The <c>xfer</c> head, when the document has one.</summary>
    public XferAnnotationNode? Head { get; }

    /// <summary>The root element, or null for a document with none.</summary>
    public XferNode? Root { get; }

    /// <summary>Tokens after the root element, kept so the tree stays lossless. Non-empty only with an error.</summary>
    public IReadOnlyList<XferToken> Trailing => new TokenList(Source, trailing);

    /// <summary>The end-of-file token. Its leading trivia is the document's trailing trivia.</summary>
    public XferToken EndOfFile { get; }

    /// <summary>The normalized text.</summary>
    public string Text => text ??= Encoding.UTF8.GetString(Source.Bytes);

    /// <summary>Every token of the document in order, ending with <see cref="EndOfFile"/>.</summary>
    public IReadOnlyList<XferToken> Tokens => new TokenList(Source, null);

    internal SyntaxSource Source { get; }

    /// <summary>The position of a UTF-8 byte offset in the normalized text.</summary>
    public XferPosition GetPosition(int utf8Offset) => Source.Lines.GetPosition(utf8Offset);

    /// <summary>A read-only list of token views over indices into the token stream.</summary>
    private sealed class TokenList : IReadOnlyList<XferToken>
    {
        private readonly SyntaxSource source;
        private readonly int[]? indices;

        public TokenList(SyntaxSource source, int[]? indices)
        {
            this.source = source;
            this.indices = indices;
        }

        public int Count => indices?.Length ?? source.Stream.Tokens.Count;

        public XferToken this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
                return new XferToken(source, indices is null ? index : indices[index]);
            }
        }

        public IEnumerator<XferToken> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>A token of the lossless tree: its kind, where it is, its exact text, and the trivia before it.</summary>
public readonly struct XferToken : IEquatable<XferToken>
{
    private readonly SyntaxSource source;
    private readonly int index;

    internal XferToken(SyntaxSource source, int index)
    {
        this.source = source;
        this.index = index;
    }

    /// <summary>The kind.</summary>
    public XferTokenKind Kind => Raw.Kind;

    /// <summary>The UTF-8 byte offset of the token's first byte in the normalized text.</summary>
    public int Offset => Raw.Start;

    /// <summary>The length in UTF-8 bytes, delimiters included.</summary>
    public int Length => Raw.Length;

    /// <summary>Where the token begins.</summary>
    public XferPosition Start => Source.Lines.GetPosition(Raw.Start);

    /// <summary>Where the token ends, exclusive.</summary>
    public XferPosition End => Source.Lines.GetPosition(Raw.End);

    /// <summary>The exact source text, delimiters included.</summary>
    public string Text => Source.TextOf(index);

    /// <summary>For a string: the content between the delimiters, verbatim. Otherwise null.</summary>
    public string? Content => Raw.Kind == XferTokenKind.String ? Source.ContentOf(index) : null;

    /// <summary>For a string: its delimiter form. Otherwise <see cref="XferStringForm.None"/>.</summary>
    public XferStringForm Form => Raw.Form;

    /// <summary>For a string: the run length of its delimiter (1 for <c>""</c>). Otherwise 0.</summary>
    public int Run => Raw.Run;

    /// <summary>For a keyword: which. Otherwise <see cref="XferKeyword.None"/>.</summary>
    public XferKeyword Keyword => Raw.Keyword;

    /// <summary>The whitespace and comments before the token, in order.</summary>
    public IReadOnlyList<XferTrivia> LeadingTrivia
    {
        get
        {
            Token raw = Raw;
            if (raw.TriviaCount == 0)
            {
                return [];
            }

            var list = new XferTrivia[raw.TriviaCount];
            for (int i = 0; i < list.Length; i++)
            {
                list[i] = new XferTrivia(source, raw.FirstTrivia + i);
            }

            return list;
        }
    }

    internal int Index => index;

    private SyntaxSource Source => source ?? throw new InvalidOperationException("this XferToken is a default value and refers to no token");

    private Token Raw => Source.TokenAt(index);

    /// <inheritdoc/>
    public bool Equals(XferToken other) => ReferenceEquals(source, other.source) && index == other.index;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is XferToken other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => index;

    /// <inheritdoc/>
    public override string ToString() => source is null ? "(default)" : $"{Kind} {Text}";

    /// <summary>Whether two views are of the same token of the same tree.</summary>
    public static bool operator ==(XferToken left, XferToken right) => left.Equals(right);

    /// <summary>Whether two views are of different tokens.</summary>
    public static bool operator !=(XferToken left, XferToken right) => !left.Equals(right);
}

/// <summary>Whitespace or a comment before a token.</summary>
public readonly struct XferTrivia : IEquatable<XferTrivia>
{
    private readonly SyntaxSource source;
    private readonly int index;

    internal XferTrivia(SyntaxSource source, int index)
    {
        this.source = source;
        this.index = index;
    }

    /// <summary>Whitespace or a comment.</summary>
    public XferTriviaKind Kind => Raw.Kind;

    /// <summary>The UTF-8 byte offset of its first byte in the normalized text.</summary>
    public int Offset => Raw.Start;

    /// <summary>The length in UTF-8 bytes.</summary>
    public int Length => Raw.Length;

    /// <summary>Where it begins.</summary>
    public XferPosition Start => Source.Lines.GetPosition(Raw.Start);

    /// <summary>Where it ends, exclusive.</summary>
    public XferPosition End => Source.Lines.GetPosition(Raw.Start + Raw.Length);

    /// <summary>The exact text.</summary>
    public string Text => Source.Slice(Raw.Start, Raw.Length);

    /// <summary>For a comment: the length of its slash run. Otherwise 0.</summary>
    public int Run => Raw.Run;

    private SyntaxSource Source => source ?? throw new InvalidOperationException("this XferTrivia is a default value and refers to no trivia");

    private Trivia Raw => Source.Stream.Trivia[index];

    /// <inheritdoc/>
    public override string ToString() => source is null ? "(default)" : $"{Kind} {Text}";

    /// <inheritdoc/>
    public bool Equals(XferTrivia other) => ReferenceEquals(source, other.source) && index == other.index;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is XferTrivia other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => index;

    /// <summary>Whether two views are of the same trivia of the same tree.</summary>
    public static bool operator ==(XferTrivia left, XferTrivia right) => left.Equals(right);

    /// <summary>Whether two views are of different trivia.</summary>
    public static bool operator !=(XferTrivia left, XferTrivia right) => !left.Equals(right);
}
