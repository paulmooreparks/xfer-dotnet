using System;
using System.Collections.Generic;
using System.Text;
using ParksComputing.XferLang.Json;

namespace ParksComputing.XferLang;

/// <summary>What a conversion produced and what it noticed along the way.</summary>
/// <typeparam name="T">The kind of result.</typeparam>
public readonly struct XferJsonResult<T> : IEquatable<XferJsonResult<T>>
{
    private readonly IReadOnlyList<XferDiagnostic>? diagnostics;

    /// <summary>Creates a result.</summary>
    /// <param name="value">The converted result. For <see cref="XferJson.FromJson(string, XferFromJsonOptions?)"/>, null when there is an error.</param>
    /// <param name="diagnostics">Every diagnostic, in order of position.</param>
    public XferJsonResult(T value, IReadOnlyList<XferDiagnostic> diagnostics)
    {
        Value = value;
        this.diagnostics = diagnostics;
    }

    /// <summary>The converted result. For <see cref="XferJson.FromJson(string, XferFromJsonOptions?)"/>, null when there is an error.</summary>
    public T Value { get; }

    /// <summary>Every diagnostic, in order of position. Check the severities: a conversion can succeed with warnings and notes. Empty, never null, even for a default result.</summary>
    public IReadOnlyList<XferDiagnostic> Diagnostics => diagnostics ?? Array.Empty<XferDiagnostic>();

    /// <summary>Splits the result into its value and its diagnostics.</summary>
    public void Deconstruct(out T value, out IReadOnlyList<XferDiagnostic> diagnostics)
    {
        value = Value;
        diagnostics = Diagnostics;
    }

    /// <inheritdoc/>
    public bool Equals(XferJsonResult<T> other) => EqualityComparer<T>.Default.Equals(Value, other.Value) && ReferenceEquals(Diagnostics, other.Diagnostics);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is XferJsonResult<T> other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Value, Diagnostics);

    /// <summary>Whether two results have the same value and the same list of diagnostics.</summary>
    public static bool operator ==(XferJsonResult<T> left, XferJsonResult<T> right) => left.Equals(right);

    /// <summary>Whether two results differ.</summary>
    public static bool operator !=(XferJsonResult<T> left, XferJsonResult<T> right) => !left.Equals(right);
}

/// <summary>Options for <see cref="XferJson.ToJson"/>.</summary>
public sealed record XferToJsonOptions
{
    /// <summary>
    /// Write an integer outside plus or minus 2^53 as a JSON string of its digits, so that a reader
    /// that makes every number a double does not lose it. <c>XFER0901</c> is still reported, because
    /// the type changed.
    /// </summary>
    public bool StringifyBigIntegers { get; init; }

    /// <summary>
    /// The number of spaces to indent by, clamped to 0 to 10 as <c>JSON.stringify</c> does. Zero
    /// (the default) writes the compact form of specification 8.1, which is what the conformance
    /// suite compares.
    /// </summary>
    public int Indent { get; init; }
}

/// <summary>Options for <see cref="XferJson.FromJson(string, XferFromJsonOptions?)"/>.</summary>
public sealed record XferFromJsonOptions
{
    private XferLimits limits = XferLimits.Default;

    /// <summary>
    /// The limits to enforce. Only <see cref="XferLimits.Depth"/> applies: JSON nested deeper
    /// cannot be written as a document within the limit, so it is refused with <c>XFER0602</c>.
    /// </summary>
    public XferLimits Limits
    {
        get => limits;
        init => limits = value ?? throw new ArgumentNullException(nameof(value));
    }
}

/// <summary>The mapping between XferLang and JSON (specification 8).</summary>
public static class XferJson
{
    private static readonly XferToJsonOptions DefaultToJson = new();
    private static readonly XferFromJsonOptions DefaultFromJson = new();

    /// <summary>
    /// Writes a value as JSON text. Comments and annotations are dropped, a symbol and a temporal
    /// become strings (<c>XFER0903</c> and <c>XFER0904</c>, reported once each with a count),
    /// <c>inf</c>, <c>-inf</c> and <c>nan</c> become <c>null</c> (<c>XFER0902</c>, an error), and an
    /// integer is written in full (<c>XFER0901</c> beyond 2^53). Diagnostics have no source position,
    /// so each carries the origin and says where it applies as a JSON Pointer in its message.
    /// </summary>
    /// <exception cref="ArgumentException">A value of a kind this specification does not define.</exception>
    public static XferJsonResult<string> ToJson(XferValue value, XferToJsonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonWriter.Write(value, options ?? DefaultToJson);
    }

    /// <summary>
    /// Reads JSON text into a value. Malformed JSON is <c>XFER0906</c>, a string that holds U+000D or
    /// a lone surrogate (which XferLang cannot represent) is <c>XFER0907</c>, and JSON nested past the
    /// depth limit is <c>XFER0602</c>; each gives no value. A duplicate key keeps the last value
    /// (<c>XFER0905</c>, a warning). Numbers keep their spelling: one with no fraction or exponent is
    /// an integer, any other a fractional.
    /// </summary>
    public static XferJsonResult<XferValue?> FromJson(string json, XferFromJsonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonReader.Read(json, (options ?? DefaultFromJson).Limits);
    }

    /// <summary>
    /// Reads JSON text given as UTF-8 bytes. A leading byte order mark is dropped (RFC 8259 permits a
    /// parser to ignore one). Bytes that are not valid UTF-8 are <c>XFER0906</c> at the origin, with no value.
    /// </summary>
    public static XferJsonResult<XferValue?> FromJson(ReadOnlySpan<byte> utf8Json, XferFromJsonOptions? options = null)
    {
        ReadOnlySpan<byte> body = utf8Json.Length >= 3 && utf8Json[0] == 0xEF && utf8Json[1] == 0xBB && utf8Json[2] == 0xBF ? utf8Json[3..] : utf8Json;
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return new XferJsonResult<XferValue?>(
                null,
                [XferDiagnostics.Create(XferDiagnosticCode.XFER0906, XferPosition.Origin, null, "input is not valid UTF-8")]);
        }

        return JsonReader.Read(text, (options ?? DefaultFromJson).Limits);
    }
}
