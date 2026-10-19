using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using ParksComputing.XferLang.Syntax;

namespace ParksComputing.XferLang.Json;

/// <summary>
/// XferLang to JSON (specification 8.1), the compact form unless an indent is asked for. A port of
/// xfer-js <c>src/json/write.ts</c>. Iterative, so depth cannot overflow the stack.
/// </summary>
internal static class JsonWriter
{
    private static readonly BigInteger Safe = BigInteger.Pow(2, 53);

    private sealed class Frame
    {
        public Frame(IReadOnlyList<XferValue> kids, IReadOnlyList<string>? keys, bool isObject)
        {
            Kids = kids;
            Keys = keys;
            IsObject = isObject;
        }

        public IReadOnlyList<XferValue> Kids { get; }

        public IReadOnlyList<string>? Keys { get; }

        public bool IsObject { get; }

        public int Next { get; set; }
    }

    public static XferJsonResult<string?> Write(XferValue value, XferToJsonOptions options)
    {
        int indent = Math.Clamp(options.Indent, 0, 10);
        var diagnostics = new List<XferDiagnostic>();
        var output = new StringBuilder();
        var stack = new Stack<Frame>();
        int symbols = 0;
        int temporals = 0;
        string firstSymbol = string.Empty;
        string firstTemporal = string.Empty;

        // The JSON Pointer to the value being written: the current member of every open container.
        // It is built only when a diagnostic needs it, because carrying a pointer per level would
        // cost memory quadratic in the depth.
        string Pointer()
        {
            var parts = new List<string>(stack.Count);
            foreach (Frame f in stack)
            {
                int index = f.Next - 1;
                parts.Add(f.IsObject ? PointerToken(f.Keys![index]) : index.ToString(CultureInfo.InvariantCulture));
            }

            parts.Reverse(); // a Stack enumerates from the top
            return parts.Count == 0 ? string.Empty : "/" + string.Join('/', parts);
        }

        void Newline(int depth)
        {
            if (indent > 0)
            {
                output.Append('\n').Append(' ', indent * depth);
            }
        }

        // Writes a scalar, or opens a container.
        void Emit(XferValue v)
        {
            switch (v)
            {
                case XferObject or XferSequence:
                    bool isObject = v is XferObject;
                    IReadOnlyList<XferValue> kids;
                    IReadOnlyList<string>? keys = null;
                    if (v is XferObject o)
                    {
                        var values = new List<XferValue>(o.Count);
                        var names = new List<string>(o.Count);
                        foreach (KeyValuePair<string, XferValue> member in o)
                        {
                            names.Add(member.Key);
                            values.Add(member.Value);
                        }

                        kids = values;
                        keys = names;
                    }
                    else
                    {
                        kids = (XferSequence)v;
                    }

                    if (kids.Count == 0)
                    {
                        output.Append(isObject ? "{}" : "[]");
                        return;
                    }

                    output.Append(isObject ? '{' : '[');
                    stack.Push(new Frame(kids, keys, isObject));
                    return;
                case XferString s:
                    Quote(output, s.Value);
                    return;
                case XferInteger i:
                    WriteInteger(i);
                    return;
                case XferFractional f:
                    if (f.Lexeme is "inf" or "-inf" or "nan")
                    {
                        diagnostics.Add(XferDiagnostics.Create(XferDiagnosticCode.XFER0902, XferPosition.Origin, null, $"{f.Lexeme} at {Where(Pointer())}"));
                        output.Append("null");
                    }
                    else
                    {
                        output.Append(f.Lexeme.Replace("_", string.Empty, StringComparison.Ordinal));
                    }

                    return;
                case XferBool b:
                    output.Append(b.Value ? "true" : "false");
                    return;
                case XferNull:
                    output.Append("null");
                    return;
                case XferSymbol sym:
                    if (symbols++ == 0)
                    {
                        firstSymbol = Pointer();
                    }

                    Quote(output, sym.Name);
                    return;
                case XferTemporal t:
                    if (temporals++ == 0)
                    {
                        firstTemporal = Pointer();
                    }

                    Quote(output, t.Lexeme);
                    return;
                default:
                    throw new ArgumentException($"{v.GetType().Name} is not a kind of value");
            }
        }

        void WriteInteger(XferInteger i)
        {
            string digits;
            bool big;
            if (i.ValueFollowsFromSpelling && DecimalDigits(i.Lexeme) is { } fast)
            {
                // A parsed decimal integer needs no conversion: its digits are its spelling, which
                // matters for an integer of millions of digits.
                digits = fast;
                string magnitude = fast.StartsWith('-') ? fast[1..] : fast;
                big = magnitude.Length > 16 || (magnitude.Length == 16 && string.CompareOrdinal(magnitude, "9007199254740992") > 0);
            }
            else
            {
                BigInteger v = i.Value;
                digits = v.ToString(CultureInfo.InvariantCulture);
                big = v > Safe || v < -Safe;
            }

            if (big)
            {
                diagnostics.Add(XferDiagnostics.Create(XferDiagnosticCode.XFER0901, XferPosition.Origin, null, Where(Pointer())));
            }

            output.Append(big && options.StringifyBigIntegers ? "\"" + digits + "\"" : digits);
        }

        // Text past the document size limit is refused, as xfer-js refuses it: the length only
        // grows, so stopping as soon as it is over gives the same answer as writing it all.
        int maxChars = options.Limits.DocumentBytes;
        Emit(value);
        while (stack.Count > 0 && output.Length <= maxChars)
        {
            Frame frame = stack.Peek();
            int depth = stack.Count;
            if (frame.Next == frame.Kids.Count)
            {
                stack.Pop();
                Newline(depth - 1);
                output.Append(frame.IsObject ? '}' : ']');
                continue;
            }

            int index = frame.Next++;
            if (index > 0)
            {
                output.Append(',');
            }

            Newline(depth);
            if (frame.IsObject)
            {
                Quote(output, frame.Keys![index]);
                output.Append(indent == 0 ? ":" : ": ");
            }

            Emit(frame.Kids[index]);
        }

        if (output.Length > maxChars)
        {
            return new XferJsonResult<string?>(null, [XferDiagnostics.Create(XferDiagnosticCode.XFER0601, XferPosition.Origin, null, $"the JSON text is longer than {maxChars} characters")]);
        }

        if (symbols > 0)
        {
            diagnostics.Add(XferDiagnostics.Create(XferDiagnosticCode.XFER0903, XferPosition.Origin, null, $"{symbols} symbol{(symbols == 1 ? string.Empty : "s")}, first at {Where(firstSymbol)}"));
        }

        if (temporals > 0)
        {
            diagnostics.Add(XferDiagnostics.Create(XferDiagnosticCode.XFER0904, XferPosition.Origin, null, $"{temporals} temporal{(temporals == 1 ? string.Empty : "s")}, first at {Where(firstTemporal)}"));
        }

        return new XferJsonResult<string?>(output.ToString(), diagnostics);
    }

    /// <summary>
    /// The decimal digits of an integer lexeme written in decimal (with a leading minus when
    /// negative, and negative zero as 0), or null for another radix.
    /// </summary>
    internal static string? DecimalDigits(string lexeme)
    {
        bool negative = lexeme.StartsWith('-');
        ReadOnlySpan<char> body = negative ? lexeme.AsSpan(1) : lexeme.AsSpan();
        if (body.Length == 0 || body.Length > 1 && body[0] == '0' && body[1] is 'x' or 'o' or 'b')
        {
            return null;
        }

        string digits = body.Contains('_') ? body.ToString().Replace("_", string.Empty, StringComparison.Ordinal) : body.ToString();
        foreach (char c in digits)
        {
            if (c is < '0' or > '9')
            {
                return null;
            }
        }

        if (digits.Length > 1 && digits[0] == '0')
        {
            return null; // a leading zero is not a decimal spelling a parse can produce
        }

        return negative && digits != "0" ? "-" + digits : digits;
    }

    private static string Where(string pointer) => pointer.Length == 0 ? "the root" : pointer;

    /// <summary>A JSON Pointer (RFC 6901) token: <c>~</c> and <c>/</c> are escaped.</summary>
    private static string PointerToken(string key) => key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>
    /// A JSON string literal. Every character other than <c>"</c>, <c>\</c> and the controls is
    /// written literally (specification 8.1).
    /// </summary>
    private static void Quote(StringBuilder output, string text)
    {
        output.Append('"');
        int from = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= 0x20 && c != '"' && c != '\\')
            {
                continue;
            }

            output.Append(text, from, i - from);
            from = i + 1;
            switch (c)
            {
                case '"':
                    output.Append("\\\"");
                    break;
                case '\\':
                    output.Append("\\\\");
                    break;
                case '\b':
                    output.Append("\\b");
                    break;
                case '\t':
                    output.Append("\\t");
                    break;
                case '\n':
                    output.Append("\\n");
                    break;
                case '\f':
                    output.Append("\\f");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                default:
                    output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
            }
        }

        output.Append(text, from, text.Length - from).Append('"');
    }
}
