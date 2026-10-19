// The object mapper is reflection over the caller's types by design (DESIGN.md section 10.8). Every way
// into this file is marked [RequiresUnreferencedCode] and [RequiresDynamicCode], on XferSerializer's
// methods and on the internal types, so a caller who trims or compiles ahead of time is warned there;
// the analysis warnings inside the implementation are the same warning repeated, and are silenced.
#pragma warning disable IL2026, IL2060, IL2067, IL2070, IL2072, IL2075, IL2077, IL2080, IL3050

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace ParksComputing.XferLang.Serialization;

/// <summary>A mapping fault: a code, a detail, and the value or key it is about.</summary>
internal sealed class MappingFault : Exception
{
    public MappingFault(XferDiagnosticCode code, string detail, XferValue? at)
        : base(detail)
    {
        Code = code;
        At = at;
    }

    public MappingFault()
    {
        Code = XferDiagnosticCode.XFER0801;
    }

    public MappingFault(string message)
        : base(message)
    {
        Code = XferDiagnosticCode.XFER0801;
    }

    public MappingFault(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = XferDiagnosticCode.XFER0801;
    }

    public XferDiagnosticCode Code { get; }

    /// <summary>The value the fault is about, for its position.</summary>
    public XferValue? At { get; init; }

    /// <summary>For a fault about a key: the object, and the key's index in it.</summary>
    public XferObject? KeyOwner { get; init; }

    public int KeyIndex { get; init; } = -1;

    /// <summary>The pointer to the value, filled in by the engine where the fault surfaces.</summary>
    public string? Pointer { get; set; }
}

/// <summary>The conversions between scalar values and the .NET types of DESIGN.md sections 10.3 and 10.4.</summary>
[RequiresUnreferencedCode(XferSerializer.TrimmingMessage)]
[RequiresDynamicCode(XferSerializer.AotMessage)]
internal static class Scalars
{
    private static string KindName(XferValue v) => v.Kind switch
    {
        XferValueKind.LocalDateTime => "local date-time",
        XferValueKind.DateTime => "date-time",
        _ => v.Kind.ToString().ToLowerInvariant(),
    };

    public static MappingFault Mismatch(XferValue value, Type type, string? wants = null) =>
        new(XferDiagnosticCode.XFER0801, $"{TypeName(type)} takes {wants ?? Wants(type)}, found {KindName(value)}", value);

    private static MappingFault Range(XferValue value, Type type, string why) => new(XferDiagnosticCode.XFER0303, $"{why} for {TypeName(type)}", value);

    public static string TypeName(Type type) => type.IsGenericType ? type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)] + "<" + string.Join(", ", Array.ConvertAll(type.GetGenericArguments(), TypeName)) + ">" : type.Name;

    private static string Wants(Type type) => type switch
    {
        _ when type == typeof(string) => "a string or a symbol",
        _ when type == typeof(char) => "a string or symbol of one character",
        _ when type == typeof(bool) => "true or false",
        _ when type == typeof(double) || type == typeof(float) || type == typeof(Half) => "a number",
        _ when type == typeof(decimal) => "a number",
        _ when type == typeof(DateOnly) => "a date",
        _ when type == typeof(TimeOnly) => "a time",
        _ when type == typeof(DateTime) => "a date, a local date-time or a date-time",
        _ when type == typeof(DateTimeOffset) => "a date-time with Z or an offset",
        _ when type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(Uri) || type == typeof(Version) => "a string",
        _ when type.IsEnum => "a symbol or string naming a member, or an integer",
        _ => "an integer",
    };

    /// <summary>Reads a scalar target. <paramref name="options"/> supplies the naming policy for enums.</summary>
    public static object? Read(XferValue value, Type type, XferSerializerOptions options)
    {
        if (type == typeof(string))
        {
            return value switch
            {
                XferString s => s.Value,
                XferSymbol s => s.Name,
                _ => throw Mismatch(value, type),
            };
        }

        if (type == typeof(char))
        {
            string? text = value switch { XferString s => s.Value, XferSymbol s => s.Name, _ => null };
            return text is { Length: 1 } ? text[0] : throw Mismatch(value, type);
        }

        if (type == typeof(bool))
        {
            return value is XferBool b ? b.Value : throw Mismatch(value, type);
        }

        if (type.IsEnum)
        {
            return ReadEnum(value, type, options);
        }

        if (type == typeof(double) || type == typeof(float) || type == typeof(Half))
        {
            return ReadFloat(value, type);
        }

        if (type == typeof(decimal))
        {
            return value switch
            {
                XferInteger i => i.TryGetDecimal(out decimal d) ? d : throw Range(value, type, "out of range"),
                XferFractional f => f.TryGetDecimal(out decimal d) ? d : throw Range(value, type, "not exactly representable"),
                _ => throw Mismatch(value, type),
            };
        }

        if (type == typeof(BigInteger))
        {
            return value is XferInteger i ? i.Value : throw Mismatch(value, type);
        }

        if (type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return ReadTemporal(value, type);
        }

        if (type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(Uri) || type == typeof(Version))
        {
            string text = value is XferString s ? s.Value : throw Mismatch(value, type);
            object? parsed = type == typeof(TimeSpan) ? (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan ts) ? ts : null)
                : type == typeof(Guid) ? (Guid.TryParse(text, out Guid g) ? g : null)
                : type == typeof(Uri) ? (Uri.TryCreate(text, UriKind.RelativeOrAbsolute, out Uri? u) ? u : null)
                : (Version.TryParse(text, out Version? ver) ? ver : null);
            return parsed ?? throw new MappingFault(XferDiagnosticCode.XFER0801, $"\"{Shorten(text)}\" is not a {TypeName(type)}", value);
        }

        // The integral types.
        return value is XferInteger integer ? ReadIntegral(integer, type) : throw Mismatch(value, type);
    }

    private static string Shorten(string text) => Syntax.Messages.Shorten(text);

    private static object ReadIntegral(XferInteger value, Type type)
    {
        object? result = type switch
        {
            _ when type == typeof(int) => value.TryGet(out int v) ? v : null,
            _ when type == typeof(long) => value.TryGet(out long v) ? v : null,
            _ when type == typeof(short) => value.TryGet(out short v) ? v : null,
            _ when type == typeof(sbyte) => value.TryGet(out sbyte v) ? v : null,
            _ when type == typeof(byte) => value.TryGet(out byte v) ? v : null,
            _ when type == typeof(ushort) => value.TryGet(out ushort v) ? v : null,
            _ when type == typeof(uint) => value.TryGet(out uint v) ? v : null,
            _ when type == typeof(ulong) => value.TryGet(out ulong v) ? v : null,
            _ when type == typeof(nint) => value.TryGet(out nint v) ? v : null,
            _ when type == typeof(nuint) => value.TryGet(out nuint v) ? v : null,
            _ when type == typeof(Int128) => value.TryGet(out Int128 v) ? v : null,
            _ when type == typeof(UInt128) => value.TryGet(out UInt128 v) ? v : null,
            _ => throw new InvalidOperationException($"no integral conversion for {type}"),
        };
        return result ?? throw Range(value, type, $"{Shorten(value.Lexeme)} is out of range");
    }

    private static object ReadFloat(XferValue value, Type type)
    {
        string lexeme;
        switch (value)
        {
            case XferFractional f when f.Lexeme is "inf" or "-inf" or "nan":
                return f.Lexeme switch
                {
                    "inf" => Box(type, double.PositiveInfinity),
                    "-inf" => Box(type, double.NegativeInfinity),
                    _ => Box(type, double.NaN),
                };
            case XferFractional f:
                lexeme = f.Lexeme.Replace("_", string.Empty, StringComparison.Ordinal);
                break;
            case XferInteger i:
                lexeme = i.Value.ToString(CultureInfo.InvariantCulture);
                break;
            default:
                throw Mismatch(value, type);
        }

        // Each type's own Parse is correctly rounded (documented since .NET Core 3.0), so there is
        // no double rounding through double for float or Half.
        object result = type == typeof(double) ? double.Parse(lexeme, NumberStyles.Float, CultureInfo.InvariantCulture)
            : type == typeof(float) ? float.Parse(lexeme, NumberStyles.Float, CultureInfo.InvariantCulture)
            : Half.Parse(lexeme, NumberStyles.Float, CultureInfo.InvariantCulture);
        double asDouble = result switch { double d => d, float s => s, Half h => (double)h, _ => 0 };
        if (double.IsInfinity(asDouble))
        {
            throw Range(value, type, $"{Shorten(lexeme)} is too large");
        }

        if (asDouble == 0 && HasNonZeroDigit(lexeme))
        {
            throw Range(value, type, $"{Shorten(lexeme)} is too small");
        }

        return result;
    }

#pragma warning disable CA1859 // it returns double, float or Half, so object is the type
    private static object Box(Type type, double value) => type == typeof(double) ? (object)value : type == typeof(float) ? (float)value : (object)(Half)value;
#pragma warning restore CA1859

    private static bool HasNonZeroDigit(string lexeme)
    {
        foreach (char c in lexeme)
        {
            if (c is 'e' or 'E')
            {
                return false;
            }

            if (c is >= '1' and <= '9')
            {
                return true;
            }
        }

        return false;
    }

    private static object ReadTemporal(XferValue value, Type type)
    {
        if (value is not XferTemporal t)
        {
            throw Mismatch(value, type);
        }

        if (type == typeof(DateOnly))
        {
            return t.TemporalKind != XferTemporalKind.Date ? throw Mismatch(value, type)
                : t.TryGetDateOnly(out DateOnly d) ? d : throw Range(value, type, $"{t.Lexeme} cannot be held");
        }

        if (type == typeof(TimeOnly))
        {
            return t.TemporalKind != XferTemporalKind.Time ? throw Mismatch(value, type)
                : t.TryGetTimeOnly(out TimeOnly time) ? time : throw Range(value, type, $"{t.Lexeme} cannot be held");
        }

        if (type == typeof(DateTime))
        {
            return t.TemporalKind == XferTemporalKind.Time ? throw Mismatch(value, type)
                : t.TryGetDateTime(out DateTime dt) ? dt : throw Range(value, type, $"{t.Lexeme} cannot be held");
        }

        return t.TemporalKind != XferTemporalKind.DateTime ? throw Mismatch(value, type)
            : t.TryGetDateTimeOffset(out DateTimeOffset dto) ? dto : throw Range(value, type, $"{t.Lexeme} cannot be held");
    }

    private static object ReadEnum(XferValue value, Type type, XferSerializerOptions options)
    {
        Type underlying = Enum.GetUnderlyingType(type);
        switch (value)
        {
            case XferSymbol or XferString:
                string text = value is XferSymbol sym ? sym.Name : ((XferString)value).Value;
                return EnumByName(text, type, options) ?? throw new MappingFault(XferDiagnosticCode.XFER0801, $"\"{Shorten(text)}\" names no member of {TypeName(type)}", value);
            case XferInteger i:
                object number = ReadIntegral(i, underlying);
                return Enum.ToObject(type, number);
            case XferSequence seq when type.IsDefined(typeof(FlagsAttribute), false):
                ulong bits = 0;
                foreach (XferValue item in seq)
                {
                    string? name = item switch { XferSymbol s => s.Name, XferString s => s.Value, _ => null };
                    object member = (name is null ? null : EnumByName(name, type, options)) ?? throw new MappingFault(XferDiagnosticCode.XFER0801, $"a {TypeName(type)} flag is a symbol or string naming a member", item);
                    bits |= ToBits(member);
                }

                return Enum.ToObject(type, bits);
            default:
                throw Mismatch(value, type);
        }
    }

    /// <summary>The bits of an enum value, whatever its underlying type and sign.</summary>
    private static ulong ToBits(object value) => Type.GetTypeCode(Enum.GetUnderlyingType(value.GetType())) switch
    {
        TypeCode.SByte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 => unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        _ => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
    };

    private static object? EnumByName(string text, Type type, XferSerializerOptions options)
    {
        foreach (string name in Enum.GetNames(type))
        {
            string key = options.PropertyNamingPolicy?.ConvertName(name) ?? name;
            if (string.Equals(key, text, options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return Enum.Parse(type, name);
            }
        }

        return null;
    }

    // Writing.

    /// <summary>The XferLang value of a scalar.</summary>
    public static XferValue Write(object value, XferSerializerOptions options, bool asSymbol)
    {
        switch (value)
        {
            case string s:
                return asSymbol && XferWriter.IsBareName(s) ? new XferSymbol(s) : new XferString(s);
            case char c:
                return new XferString(c.ToString());
            case bool b:
                return new XferBool(b);
            case double d:
                return Float(d, d.ToString("R", CultureInfo.InvariantCulture));
            case float f:
                return Float(f, f.ToString("R", CultureInfo.InvariantCulture));
            case Half h:
                return Float((double)h, h.ToString("R", CultureInfo.InvariantCulture));
            case decimal m:
                string text = m.ToString(CultureInfo.InvariantCulture);
                return new XferFractional(text.Contains('.', StringComparison.Ordinal) ? text : text + ".0");
            case Enum e:
                return WriteEnum(e, options);
            case DateOnly d:
                return new XferTemporal(XferTemporalKind.Date, d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            case TimeOnly t:
                return new XferTemporal(XferTemporalKind.Time, Time(t.Ticks));
            case DateTime dt:
                DateTime asWritten = dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt;
                string local = asWritten.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T" + Time(asWritten.TimeOfDay.Ticks);
                return dt.Kind == DateTimeKind.Unspecified
                    ? new XferTemporal(XferTemporalKind.LocalDateTime, local)
                    : new XferTemporal(XferTemporalKind.DateTime, local + "Z");
            case DateTimeOffset dto:
                string clock = dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T" + Time(dto.TimeOfDay.Ticks);
                TimeSpan offset = dto.Offset;
                string zone = offset == TimeSpan.Zero ? "Z" : (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
                return new XferTemporal(XferTemporalKind.DateTime, clock + zone);
            case TimeSpan ts:
                return new XferString(ts.ToString("c", CultureInfo.InvariantCulture));
            case Guid g:
                return new XferString(g.ToString("D"));
            case Uri u:
                return new XferString(u.OriginalString);
            case Version v:
                return new XferString(v.ToString());
            case BigInteger big:
                return Integer(big);
            case sbyte or byte or short or ushort or int or uint or long or ulong or nint or nuint or Int128 or UInt128:
                return Integer(BigInteger.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture));
            default:
                throw new InvalidOperationException($"no scalar conversion for {value.GetType()}");
        }
    }

    private static XferInteger Integer(BigInteger v) => new(v, v.ToString(CultureInfo.InvariantCulture));

    private static XferFractional Float(double value, string roundTrip)
    {
        if (double.IsNaN(value))
        {
            return new XferFractional("nan");
        }

        if (double.IsInfinity(value))
        {
            return new XferFractional(value > 0 ? "inf" : "-inf");
        }

        // The shortest round-trip spelling, made to read back as fractional (1 becomes 1.0).
        bool fractional = roundTrip.AsSpan().IndexOfAny('.', 'E', 'e') >= 0;
        return new XferFractional(fractional ? roundTrip : roundTrip + ".0");
    }

    /// <summary><c>HH:MM:SS</c>, with fractional seconds only when they are not zero, trailing zeros removed.</summary>
    private static string Time(long ticks)
    {
        var t = new TimeSpan(ticks);
        string text = t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        long fraction = ticks % TimeSpan.TicksPerSecond;
        return fraction == 0 ? text : text + "." + fraction.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
    }

    private static XferValue WriteEnum(Enum value, XferSerializerOptions options)
    {
        Type type = value.GetType();
        string Key(string name) => options.PropertyNamingPolicy?.ConvertName(name) ?? name;
        if (Enum.IsDefined(type, value))
        {
            string key = Key(Enum.GetName(type, value)!);
            if (XferWriter.IsBareName(key))
            {
                return new XferSymbol(key);
            }

            return new XferString(key);
        }

        ulong bits = ToBits(value);
        if (type.IsDefined(typeof(FlagsAttribute), false) && bits != 0)
        {
            var parts = new List<XferValue>();
            ulong covered = 0;
            foreach (object member in Enum.GetValues(type))
            {
                ulong m = ToBits(member);
                if (m != 0 && (m & (m - 1)) == 0 && (bits & m) == m)
                {
                    covered |= m;
                    string key = Key(Enum.GetName(type, member)!);
                    parts.Add(XferWriter.IsBareName(key) ? new XferSymbol(key) : new XferString(key));
                }
            }

            if (covered == bits)
            {
                return new XferSequence(parts);
            }
        }

        // An undefined value is written as its number.
        object number = Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture);
        return Integer(BigInteger.Parse(Convert.ToString(number, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture));
    }

}
