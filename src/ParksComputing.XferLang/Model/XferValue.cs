using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;

namespace ParksComputing.XferLang;

/// <summary>The kind of an <see cref="XferValue"/>.</summary>
public enum XferValueKind
{
    /// <summary>An <see cref="XferObject"/>.</summary>
    Object,

    /// <summary>An <see cref="XferSequence"/>.</summary>
    Sequence,

    /// <summary>An <see cref="XferString"/>.</summary>
    String,

    /// <summary>An <see cref="XferInteger"/>.</summary>
    Integer,

    /// <summary>An <see cref="XferFractional"/>, including <c>inf</c>, <c>-inf</c> and <c>nan</c>.</summary>
    Fractional,

    /// <summary>An <see cref="XferBool"/>.</summary>
    Bool,

    /// <summary>An <see cref="XferNull"/>.</summary>
    Null,

    /// <summary>An <see cref="XferSymbol"/>.</summary>
    Symbol,

    /// <summary>An <see cref="XferTemporal"/> holding a date.</summary>
    Date,

    /// <summary>An <see cref="XferTemporal"/> holding a time.</summary>
    Time,

    /// <summary>An <see cref="XferTemporal"/> holding a date and time with no offset.</summary>
    LocalDateTime,

    /// <summary>An <see cref="XferTemporal"/> holding a date and time with <c>Z</c> or an offset.</summary>
    DateTime,
}

/// <summary>The four temporal kinds of specification 4.6.</summary>
public enum XferTemporalKind
{
    /// <summary><c>2025-08-01</c>.</summary>
    Date,

    /// <summary><c>09:30:00</c>.</summary>
    Time,

    /// <summary><c>2025-08-01T09:30:00</c>.</summary>
    LocalDateTime,

    /// <summary><c>2025-08-01T09:30:00Z</c> or with an offset.</summary>
    DateTime,
}

/// <summary>An annotation on a value: <c>&lt;! name value !&gt;</c>.</summary>
/// <param name="Name">The name: <c>id</c>, <c>tag</c> or <c>lang</c> in a document.</param>
/// <param name="Value">The value, a scalar in a document.</param>
public sealed record XferAnnotation(string Name, XferValue Value);

/// <summary>
/// A value of the value model (specification 9): what a document says, without its spelling,
/// trivia or layout. Values are immutable. Equality is structural, annotations included, and is
/// computed without recursion, so it is safe at any depth.
/// </summary>
/// <remarks>
/// A program may build values directly. A value that no document could hold (specification 9,
/// "Values built by a program") is accepted here and refused by the writer, so that a program can
/// build and inspect a value before deciding what to do with it.
/// </remarks>
public abstract class XferValue : IEquatable<XferValue>
{
    private static readonly IReadOnlyList<XferAnnotation> NoAnnotations = Array.Empty<XferAnnotation>();

    private protected XferValue(IEnumerable<XferAnnotation>? annotations)
    {
        if (annotations is null)
        {
            Annotations = NoAnnotations;
            return;
        }

        XferAnnotation[] list = annotations.ToArray();
        foreach (XferAnnotation a in list)
        {
            ArgumentNullException.ThrowIfNull(a, nameof(annotations));
        }

        Annotations = list.Length == 0 ? NoAnnotations : list;
    }

    /// <summary>The kind of value.</summary>
    public abstract XferValueKind Kind { get; }

    /// <summary>The annotations on the value, in source order. Empty, never null, when there are none.</summary>
    public IReadOnlyList<XferAnnotation> Annotations { get; }

    /// <inheritdoc/>
    public bool Equals(XferValue? other) => other is not null && StructuralEquality.Equal(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is XferValue other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => StructuralEquality.Hash(this);

    /// <summary>Whether this value, ignoring annotations and children, equals another of the same kind.</summary>
    private protected abstract bool ShallowEquals(XferValue other);

    /// <summary>A hash of this value, ignoring annotations and children.</summary>
    private protected abstract int ShallowHash();

    /// <summary>Iterative structural equality and hashing, so that depth cannot overflow the stack.</summary>
    private static class StructuralEquality
    {
        public static bool Equal(XferValue a, XferValue b)
        {
            var stack = new Stack<(XferValue A, XferValue B)>();
            stack.Push((a, b));
            while (stack.Count > 0)
            {
                (XferValue x, XferValue y) = stack.Pop();
                if (ReferenceEquals(x, y))
                {
                    continue;
                }

                if (x.Kind != y.Kind || !x.ShallowEquals(y) || x.Annotations.Count != y.Annotations.Count)
                {
                    return false;
                }

                for (int i = 0; i < x.Annotations.Count; i++)
                {
                    if (x.Annotations[i].Name != y.Annotations[i].Name)
                    {
                        return false;
                    }

                    stack.Push((x.Annotations[i].Value, y.Annotations[i].Value));
                }

                switch (x)
                {
                    case XferObject ox:
                        var oy = (XferObject)y;
                        for (int i = 0; i < ox.Count; i++)
                        {
                            KeyValuePair<string, XferValue> ex = ox.GetAt(i);
                            KeyValuePair<string, XferValue> ey = oy.GetAt(i);
                            if (ex.Key != ey.Key)
                            {
                                return false; // member order is part of the value (specification 5.3)
                            }

                            stack.Push((ex.Value, ey.Value));
                        }

                        break;
                    case XferSequence sx:
                        var sy = (XferSequence)y;
                        for (int i = 0; i < sx.Count; i++)
                        {
                            stack.Push((sx[i], sy[i]));
                        }

                        break;
                }
            }

            return true;
        }

        public static int Hash(XferValue root)
        {
            var hash = default(HashCode);
            var stack = new Stack<XferValue>();
            stack.Push(root);
            int visited = 0;

            // A bounded walk: the first few thousand nodes decide the hash, which keeps hashing a
            // huge value cheap and stays consistent with equality.
            while (stack.Count > 0 && visited < 4096)
            {
                XferValue v = stack.Pop();
                visited++;
                hash.Add(v.Kind);
                hash.Add(v.ShallowHash());
                hash.Add(v.Annotations.Count);
                switch (v)
                {
                    case XferObject o:
                        for (int i = o.Count - 1; i >= 0; i--)
                        {
                            KeyValuePair<string, XferValue> e = o.GetAt(i);
                            hash.Add(e.Key, StringComparer.Ordinal);
                            stack.Push(e.Value);
                        }

                        break;
                    case XferSequence s:
                        for (int i = s.Count - 1; i >= 0; i--)
                        {
                            stack.Push(s[i]);
                        }

                        break;
                }
            }

            return hash.ToHashCode();
        }
    }
}

/// <summary>An object: members in order, keys unique (specification 5.3).</summary>
public sealed class XferObject : XferValue, IReadOnlyDictionary<string, XferValue>
{
    private readonly OrderedDictionary<string, XferValue> entries;

    /// <summary>Builds an object from members in order.</summary>
    /// <exception cref="ArgumentException">A key appears twice.</exception>
    public XferObject(IEnumerable<KeyValuePair<string, XferValue>> members, IEnumerable<XferAnnotation>? annotations = null)
        : base(annotations)
    {
        ArgumentNullException.ThrowIfNull(members);
        entries = new OrderedDictionary<string, XferValue>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, XferValue> member in members)
        {
            ArgumentNullException.ThrowIfNull(member.Key, nameof(members));
            ArgumentNullException.ThrowIfNull(member.Value, nameof(members));
            if (!entries.TryAdd(member.Key, member.Value))
            {
                throw new ArgumentException($"the key \"{member.Key}\" appears twice", nameof(members));
            }
        }
    }

    // Used by the projection, which has already made the keys unique (last value wins).
    internal XferObject(OrderedDictionary<string, XferValue> entries, IReadOnlyList<XferAnnotation>? annotations)
        : base(annotations)
    {
        this.entries = entries;
    }

    internal OrderedDictionary<string, XferValue> Entries => entries;

    /// <inheritdoc/>
    public override XferValueKind Kind => XferValueKind.Object;

    /// <inheritdoc/>
    public int Count => entries.Count;

    /// <inheritdoc/>
    public IEnumerable<string> Keys => entries.Keys;

    /// <inheritdoc/>
    public IEnumerable<XferValue> Values => entries.Values;

    /// <inheritdoc/>
    public XferValue this[string key] => entries[key];

    /// <summary>The member at a position, in source order.</summary>
    public KeyValuePair<string, XferValue> GetAt(int index) => entries.GetAt(index);

    /// <inheritdoc/>
    public bool ContainsKey(string key) => entries.ContainsKey(key);

    /// <inheritdoc/>
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out XferValue value) => entries.TryGetValue(key, out value);

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, XferValue>> GetEnumerator() => entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private protected override bool ShallowEquals(XferValue other) => ((XferObject)other).Count == Count;

    private protected override int ShallowHash() => Count;
}

/// <summary>A sequence of values (specification 5.4).</summary>
public sealed class XferSequence : XferValue, IReadOnlyList<XferValue>
{
    private readonly XferValue[] items;

    /// <summary>Builds a sequence from items in order.</summary>
    public XferSequence(IEnumerable<XferValue> items, IEnumerable<XferAnnotation>? annotations = null)
        : base(annotations)
    {
        ArgumentNullException.ThrowIfNull(items);
        this.items = items.ToArray();
        foreach (XferValue item in this.items)
        {
            ArgumentNullException.ThrowIfNull(item, nameof(items));
        }
    }

    internal XferSequence(XferValue[] items, IReadOnlyList<XferAnnotation>? annotations)
        : base(annotations)
    {
        this.items = items;
    }

    internal XferValue[] ItemArray => items;

    /// <inheritdoc/>
    public override XferValueKind Kind => XferValueKind.Sequence;

    /// <inheritdoc/>
    public int Count => items.Length;

    /// <inheritdoc/>
    public XferValue this[int index] => items[index];

    /// <inheritdoc/>
    public IEnumerator<XferValue> GetEnumerator() => ((IEnumerable<XferValue>)items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private protected override bool ShallowEquals(XferValue other) => ((XferSequence)other).Count == Count;

    private protected override int ShallowHash() => Count;
}

/// <summary>A string: its content, verbatim.</summary>
public sealed class XferString : XferValue
{
    /// <summary>Builds a string value.</summary>
    public XferString(string value, IEnumerable<XferAnnotation>? annotations = null)
        : base(annotations)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <inheritdoc/>
    public override XferValueKind Kind => XferValueKind.String;

    /// <summary>The content.</summary>
    public string Value { get; }

    private protected override bool ShallowEquals(XferValue other) => string.Equals(Value, ((XferString)other).Value, StringComparison.Ordinal);

    private protected override int ShallowHash() => StringComparer.Ordinal.GetHashCode(Value);
}

/// <summary>An integer: its exact value at any size, and its spelling.</summary>
public sealed class XferInteger : XferValue
{
    /// <summary>Builds an integer value with its spelling, which the canonical writer keeps.</summary>
    public XferInteger(BigInteger value, string lexeme, IEnumerable<XferAnnotation>? annotations = null)
        : base(annotations)
    {
        ArgumentNullException.ThrowIfNull(lexeme);
        Value = value;
        Lexeme = lexeme;
    }

    /// <inheritdoc/>
    public override XferValueKind Kind => XferValueKind.Integer;

    /// <summary>The value.</summary>
    public BigInteger Value { get; }

    /// <summary>The spelling, radix and digit separators included.</summary>
    public string Lexeme { get; }

    private protected override bool ShallowEquals(XferValue other) =>
        Value == ((XferInteger)other).Value && string.Equals(Lexeme, ((XferInteger)other).Lexeme, StringComparison.Ordinal);

    private protected override int ShallowHash() => Value.GetHashCode();
}

/// <summary>A fractional number, kept as its spelling only: the package never decides float or decimal.</summary>
public sealed class XferFractional : XferValue
{
    /// <summary>Builds a fractional value from its spelling, which may be <c>inf</c>, <c>-inf</c> or <c>nan</c>.</summary>
    public XferFractional(string lexeme, IEnumerable<XferAnnotation>? annotations = null)
        : base(annotations)
    {
        ArgumentNullException.ThrowIfNull(lexeme);
        Lexeme = lexeme;
    }

    /// <inheritdoc/>
    public override XferValueKind Kind => XferValueKind.Fractional;

    /// <summary>The spelling.</summary>
    public string Lexeme { get; }

    private protected override bool ShallowEquals(XferValue other) => string.Equals(Lexeme, ((XferFractional)other).Lexeme, StringComparison.Ordinal);

    private protected override int ShallowHash() => StringComparer.Ordinal.GetHashCode(Lexeme);
}

/// <summary><c>true</c> or <c>false</c>.</summary>
public sealed class XferBool : XferValue
{
    /// <summary>Builds a boolean value.</summary>
    public XferBool(bool value, IEnumerable<XferAnnotation>? annotations = null)
        : base(annotations)
    {
        Value = value;
    }

    /// <inheritdoc/>
    public override XferValueKind Kind => XferValueKind.Bool;

    /// <summary>The value.</summary>
    public bool Value { get; }

    private protected override bool ShallowEquals(XferValue other) => Value == ((XferBool)other).Value;

    private protected override int ShallowHash() => Value ? 1 : 0;
}

/// <summary><c>null</c>.</summary>
public sealed class XferNull : XferValue
{
    /// <summary>Builds a null value.</summary>
    public XferNull(IEnumerable<XferAnnotation>? annotations = null)
        : base(annotations)
    {
    }

    /// <inheritdoc/>
    public override XferValueKind Kind => XferValueKind.Null;

    private protected override bool ShallowEquals(XferValue other) => true;

    private protected override int ShallowHash() => 0;
}

/// <summary>A symbol: a bare word in value position (specification 4.3).</summary>
public sealed class XferSymbol : XferValue
{
    /// <summary>Builds a symbol value.</summary>
    public XferSymbol(string name, IEnumerable<XferAnnotation>? annotations = null)
        : base(annotations)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    /// <inheritdoc/>
    public override XferValueKind Kind => XferValueKind.Symbol;

    /// <summary>The word.</summary>
    public string Name { get; }

    private protected override bool ShallowEquals(XferValue other) => string.Equals(Name, ((XferSymbol)other).Name, StringComparison.Ordinal);

    private protected override int ShallowHash() => StringComparer.Ordinal.GetHashCode(Name);
}

/// <summary>A date, a time, or a date and time, kept as its spelling (specification 4.6).</summary>
public sealed class XferTemporal : XferValue
{
    /// <summary>Builds a temporal value from its kind and spelling.</summary>
    public XferTemporal(XferTemporalKind temporalKind, string lexeme, IEnumerable<XferAnnotation>? annotations = null)
        : base(annotations)
    {
        ArgumentNullException.ThrowIfNull(lexeme);
        if (!Enum.IsDefined(temporalKind))
        {
            throw new ArgumentOutOfRangeException(nameof(temporalKind));
        }

        TemporalKind = temporalKind;
        Lexeme = lexeme;
    }

    /// <inheritdoc/>
    public override XferValueKind Kind => TemporalKind switch
    {
        XferTemporalKind.Date => XferValueKind.Date,
        XferTemporalKind.Time => XferValueKind.Time,
        XferTemporalKind.LocalDateTime => XferValueKind.LocalDateTime,
        _ => XferValueKind.DateTime,
    };

    /// <summary>Which of the four temporal kinds.</summary>
    public XferTemporalKind TemporalKind { get; }

    /// <summary>The spelling.</summary>
    public string Lexeme { get; }

    private protected override bool ShallowEquals(XferValue other) => string.Equals(Lexeme, ((XferTemporal)other).Lexeme, StringComparison.Ordinal);

    private protected override int ShallowHash() => StringComparer.Ordinal.GetHashCode(Lexeme);
}
