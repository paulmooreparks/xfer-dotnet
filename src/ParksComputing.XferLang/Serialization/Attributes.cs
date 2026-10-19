using System;

namespace ParksComputing.XferLang.Serialization;

/// <summary>The key a member is written with and read from, in place of its name and the naming policy.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class XferPropertyNameAttribute : Attribute
{
    /// <summary>Gives a member its key.</summary>
    public XferPropertyNameAttribute(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    /// <summary>The key.</summary>
    public string Name { get; }

    /// <summary>
    /// Further keys accepted on reading and never written, such as an older name for the member.
    /// An object holding the key and an alias, or two aliases, for one member is refused with
    /// <c>XFER0804</c> at the second.
    /// </summary>
    public string[] Aliases { get; init; } = [];
}

/// <summary>Leaves a member out, in both directions.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class XferIgnoreAttribute : Attribute
{
}

/// <summary>Makes a member required on reading: an object without it is refused with <c>XFER0803</c>. A C# <c>required</c> member is required without this.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class XferRequiredAttribute : Attribute
{
}

/// <summary>Chooses the constructor the mapper builds a type with, when it has more than one.</summary>
[AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false)]
public sealed class XferConstructorAttribute : Attribute
{
}

/// <summary>Writes a string member as a symbol when its text is a valid symbol, and as a string otherwise.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class XferSymbolAttribute : Attribute
{
}

/// <summary>
/// The order a member is written in: members are written by ascending order, and members of
/// equal order by key, ordinally. Without this attribute every member is at 0, so a type's
/// members are written in key order on every runtime.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class XferPropertyOrderAttribute : Attribute
{
    /// <summary>Gives a member its place.</summary>
    public XferPropertyOrderAttribute(int order)
    {
        Order = order;
    }

    /// <summary>The place; lower is earlier, and members without one are at 0.</summary>
    public int Order { get; }
}

/// <summary>Uses a converter for a member, or for every use of a type.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, AllowMultiple = false)]
public sealed class XferConverterAttribute : Attribute
{
    /// <summary>Names the converter type, which needs a public parameterless constructor.</summary>
    public XferConverterAttribute(Type converterType)
    {
        ArgumentNullException.ThrowIfNull(converterType);
        ConverterType = converterType;
    }

    /// <summary>The converter type.</summary>
    public Type ConverterType { get; }
}

/// <summary>What a converter has to hand while reading.</summary>
public sealed class XferReadContext
{
    internal XferReadContext(XferSerializerOptions options, string pointer)
    {
        Options = options;
        Pointer = pointer;
    }

    /// <summary>The options of the call.</summary>
    public XferSerializerOptions Options { get; }

    /// <summary>Where the value is, as a JSON Pointer (RFC 6901) into the document; empty for the root.</summary>
    public string Pointer { get; }
}

/// <summary>What a converter has to hand while writing.</summary>
public sealed class XferWriteContext
{
    internal XferWriteContext(XferSerializerOptions options)
    {
        Options = options;
    }

    /// <summary>The options of the call.</summary>
    public XferSerializerOptions Options { get; }
}

/// <summary>
/// Converts one type to and from XferLang values. A converter works on values, not text, so it
/// cannot produce an invalid document; a value it returns that no document could hold is refused
/// by the writer. Derive from <see cref="XferConverter{T}"/>.
/// </summary>
public abstract class XferConverter
{
    private protected XferConverter()
    {
    }

    /// <summary>Whether this converter handles a type.</summary>
    public abstract bool CanConvert(Type type);

    internal abstract object? ReadObject(XferValue value, Type type, XferReadContext context);

    internal abstract XferValue WriteObject(object? value, XferWriteContext context);
}

/// <summary>Converts one type to and from XferLang values.</summary>
/// <typeparam name="T">The type.</typeparam>
[System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0022", Justification = "XferConverter cannot be derived from outside this assembly: its abstract members are internal. XferConverter<T> is the type to derive from.")]
public abstract class XferConverter<T> : XferConverter
{
    /// <inheritdoc/>
    public override bool CanConvert(Type type) => type == typeof(T);

    /// <summary>
    /// The value of a type for an XferLang value. Throw <see cref="XferException"/> to refuse it:
    /// the mapper reports it at the value's position.
    /// </summary>
    public abstract T Read(XferValue value, XferReadContext context);

    /// <summary>The XferLang value for a value of the type.</summary>
    public abstract XferValue Write(T value, XferWriteContext context);

    internal override object? ReadObject(XferValue value, Type type, XferReadContext context) => Read(value, context);

    internal override XferValue WriteObject(object? value, XferWriteContext context) => Write((T)value!, context);
}
