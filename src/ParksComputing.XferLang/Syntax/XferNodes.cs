using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ParksComputing.XferLang.Syntax;

/// <summary>The kind of an <see cref="XferNode"/>.</summary>
public enum XferNodeKind
{
    /// <summary>An <see cref="XferObjectNode"/>.</summary>
    Object,

    /// <summary>An <see cref="XferSequenceNode"/>.</summary>
    Sequence,

    /// <summary>An <see cref="XferScalarNode"/>.</summary>
    Scalar,

    /// <summary>An <see cref="XferErrorNode"/>: text the parser could not read as an element.</summary>
    Error,
}

/// <summary>An element of the lossless tree, with the annotations before it.</summary>
public abstract class XferNode
{
    private static readonly XferAnnotationNode[] None = [];

    private protected XferNode(SyntaxSource source, XferAnnotationNode[]? annotations)
    {
        Source = source;
        AnnotationArray = annotations is { Length: > 0 } ? annotations : None;
    }

    /// <summary>The kind of node.</summary>
    public abstract XferNodeKind Kind { get; }

    /// <summary>The annotations before the element, in order.</summary>
    public IReadOnlyList<XferAnnotationNode> Annotations => annotationView ??= ReadOnly(AnnotationArray);

    internal XferAnnotationNode[] AnnotationArray { get; }

    internal SyntaxSource Source { get; }

    private ReadOnlyCollection<XferAnnotationNode>? annotationView;

    /// <summary>A read-only view of an internal array, so a caller cannot change the tree through a cast.</summary>
    internal static ReadOnlyCollection<T> ReadOnly<T>(T[] items) => items.Length == 0 ? ReadOnlyCollection<T>.Empty : Array.AsReadOnly(items);
}

/// <summary>An object: <c>{</c>, its pairs, and <c>}</c>.</summary>
public sealed class XferObjectNode : XferNode
{
    private readonly int open;
    private readonly int close;

    internal XferObjectNode(SyntaxSource source, XferAnnotationNode[]? annotations, int open, int close, XferPairNode[] pairs)
        : base(source, annotations)
    {
        this.open = open;
        this.close = close;
        PairArray = pairs;
    }

    /// <inheritdoc/>
    public override XferNodeKind Kind => XferNodeKind.Object;

    /// <summary>The <c>{</c>.</summary>
    public XferToken Open => Source.View(open);

    /// <summary>The <c>}</c> (or the bracket that closed it by mistake), or null when it was never closed.</summary>
    public XferToken? Close => Source.ViewOrNull(close);

    /// <summary>The pairs, in order.</summary>
    public IReadOnlyList<XferPairNode> Pairs => pairView ??= ReadOnly(PairArray);

    private ReadOnlyCollection<XferPairNode>? pairView;

    internal XferPairNode[] PairArray { get; }

    internal int OpenIndex => open;

    internal int CloseIndex => close;
}

/// <summary>A sequence: <c>[</c>, its items, and <c>]</c>.</summary>
public sealed class XferSequenceNode : XferNode
{
    private readonly int open;
    private readonly int close;

    internal XferSequenceNode(SyntaxSource source, XferAnnotationNode[]? annotations, int open, int close, XferNode[] items)
        : base(source, annotations)
    {
        this.open = open;
        this.close = close;
        ItemArray = items;
    }

    /// <inheritdoc/>
    public override XferNodeKind Kind => XferNodeKind.Sequence;

    /// <summary>The <c>[</c>.</summary>
    public XferToken Open => Source.View(open);

    /// <summary>The <c>]</c> (or the bracket that closed it by mistake), or null when it was never closed.</summary>
    public XferToken? Close => Source.ViewOrNull(close);

    /// <summary>The items, in order.</summary>
    public IReadOnlyList<XferNode> Items => itemView ??= ReadOnly(ItemArray);

    private ReadOnlyCollection<XferNode>? itemView;

    internal XferNode[] ItemArray { get; }

    internal int OpenIndex => open;

    internal int CloseIndex => close;
}

/// <summary>A scalar: one token that is a value.</summary>
public sealed class XferScalarNode : XferNode
{
    private readonly int token;

    internal XferScalarNode(SyntaxSource source, XferAnnotationNode[]? annotations, int token)
        : base(source, annotations)
    {
        this.token = token;
    }

    /// <inheritdoc/>
    public override XferNodeKind Kind => XferNodeKind.Scalar;

    /// <summary>The token.</summary>
    public XferToken Token => Source.View(token);

    internal int TokenIndex => token;
}

/// <summary>Text the parser could not read as an element; a diagnostic says why.</summary>
public sealed class XferErrorNode : XferNode
{
    private readonly int[] tokens;

    internal XferErrorNode(SyntaxSource source, XferAnnotationNode[]? annotations, int[] tokens)
        : base(source, annotations)
    {
        this.tokens = tokens;
    }

    /// <inheritdoc/>
    public override XferNodeKind Kind => XferNodeKind.Error;

    /// <summary>The tokens, in order. Empty for a node that stands for annotations with no target.</summary>
    public IReadOnlyList<XferToken> Tokens
    {
        get
        {
            var list = new XferToken[tokens.Length];
            for (int i = 0; i < list.Length; i++)
            {
                list[i] = Source.View(tokens[i]);
            }

            return list;
        }
    }

    internal int[] TokenIndices => tokens;
}

/// <summary>A key and its value in an object, with the annotations before the key.</summary>
public sealed class XferPairNode
{
    private static readonly XferAnnotationNode[] NoAnnotations = [];
    private static readonly XferSkippedSyntax[] NoSkipped = [];
    private readonly SyntaxSource source;
    private readonly int key;

    internal XferPairNode(SyntaxSource source, XferAnnotationNode[]? annotations, int key, XferNode? value, XferSkippedSyntax[]? skipped)
    {
        this.source = source;
        AnnotationArray = annotations is { Length: > 0 } ? annotations : NoAnnotations;
        this.key = key;
        Value = value;
        SkippedArray = skipped is { Length: > 0 } ? skipped : NoSkipped;
    }

    /// <summary>
    /// The key, or null when annotations were left with no key to attach to, or a bracket stood
    /// where a key belongs. Both have a diagnostic.
    /// </summary>
    public XferToken? Key => source.ViewOrNull(key);

    /// <summary>The value, or null when it is missing (a diagnostic says so).</summary>
    public XferNode? Value { get; }

    /// <summary>The annotations before the key, in order.</summary>
    public IReadOnlyList<XferAnnotationNode> Annotations => annotationView ??= XferNode.ReadOnly(AnnotationArray);

    private ReadOnlyCollection<XferAnnotationNode>? annotationView;
    private ReadOnlyCollection<XferSkippedSyntax>? skippedView;

    /// <summary>
    /// What stood between the key and the value and belongs to neither, in source order: stray
    /// separators carried over from another format, and annotations placed after the key. Each has
    /// a diagnostic; they are kept so the tree stays lossless.
    /// </summary>
    public IReadOnlyList<XferSkippedSyntax> Skipped => skippedView ??= XferNode.ReadOnly(SkippedArray);

    internal XferAnnotationNode[] AnnotationArray { get; }

    internal XferSkippedSyntax[] SkippedArray { get; }

    internal int KeyIndex => key;
}

/// <summary>A token or an annotation skipped between a key and its value.</summary>
public readonly struct XferSkippedSyntax : IEquatable<XferSkippedSyntax>
{
    private readonly SyntaxSource? source;
    private readonly int token;

    internal XferSkippedSyntax(SyntaxSource source, int token)
    {
        this.source = source;
        this.token = token;
        Annotation = null;
    }

    internal XferSkippedSyntax(XferAnnotationNode annotation)
    {
        source = null;
        token = -1;
        Annotation = annotation;
    }

    /// <summary>The skipped token, or null when an annotation was skipped.</summary>
    public XferToken? Token => source is null ? null : source.View(token);

    /// <summary>The skipped annotation, or null when a token was skipped.</summary>
    public XferAnnotationNode? Annotation { get; }

    internal int TokenIndex => token;

    /// <inheritdoc/>
    public bool Equals(XferSkippedSyntax other) => ReferenceEquals(source, other.source) && token == other.token && ReferenceEquals(Annotation, other.Annotation);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is XferSkippedSyntax other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(token, Annotation);

    /// <summary>Whether two items are the same.</summary>
    public static bool operator ==(XferSkippedSyntax left, XferSkippedSyntax right) => left.Equals(right);

    /// <summary>Whether two items differ.</summary>
    public static bool operator !=(XferSkippedSyntax left, XferSkippedSyntax right) => !left.Equals(right);
}

/// <summary>An annotation: <c>&lt;!</c>, a name, a value, and <c>!&gt;</c>, any of which may be missing in a malformed one.</summary>
public sealed class XferAnnotationNode
{
    private readonly SyntaxSource source;
    private readonly int open;
    private readonly int name;
    private readonly int close;

    internal XferAnnotationNode(SyntaxSource source, int open, int name, XferNode? value, int close)
    {
        this.source = source;
        this.open = open;
        this.name = name;
        Value = value;
        this.close = close;
    }

    /// <summary>The <c>&lt;!</c>.</summary>
    public XferToken Open => source.View(open);

    /// <summary>The name, or null in a malformed annotation that has none.</summary>
    public XferToken? Name => source.ViewOrNull(name);

    /// <summary>The value, or null in a malformed annotation that has none.</summary>
    public XferNode? Value { get; }

    /// <summary>The <c>!&gt;</c>, or null in a malformed annotation that is never closed.</summary>
    public XferToken? Close => source.ViewOrNull(close);

    internal int OpenIndex => open;

    internal int NameIndex => name;

    internal int CloseIndex => close;
}
