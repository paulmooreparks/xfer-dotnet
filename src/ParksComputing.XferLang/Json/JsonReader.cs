using System;
using System.Collections.Generic;
using System.Globalization;
using ParksComputing.XferLang.Syntax;
using ParksComputing.XferLang.Text;

namespace ParksComputing.XferLang.Json;

/// <summary>
/// JSON to XferLang (specification 8.2). A JSON reader of our own, because the platform's readers
/// lose the integer and fractional distinction, the spelling of numbers, and duplicate keys. A port
/// of xfer-js <c>src/json/read.ts</c>, over a string so that positions and messages match it.
/// Iterative, so nesting depth cannot overflow the stack.
/// </summary>
internal static class JsonReader
{
    private sealed class Malformed : Exception
    {
        public Malformed(int index, string message)
            : base(message)
        {
            Index = index;
        }

        public int Index { get; }
    }

    private sealed class Frame
    {
        public Frame(bool isObject)
        {
            IsObject = isObject;
        }

        public bool IsObject { get; }

        public List<XferValue> Items { get; } = [];

        public OrderedDictionary<string, XferValue> Entries { get; } = new(StringComparer.Ordinal);

        /// <summary>Object only: the key waiting for its value.</summary>
        public string? Key { get; set; }

        /// <summary>Object only: the index of the waiting key's opening quote.</summary>
        public int KeyAt { get; set; }
    }

    private readonly record struct Raw(XferDiagnosticCode Code, int Index, string Detail);

    /// <summary>Tracks a position in the JSON text as it advances; positions are asked for in order.</summary>
    private sealed class Cursor
    {
        private readonly string text;
        private int index;
        private int offset;
        private int line = 1;
        private int column = 1;

        public Cursor(string text)
        {
            this.text = text;
        }

        public XferPosition At(int target)
        {
            int stop = Math.Min(target, text.Length);
            while (index < stop)
            {
                int cp = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
                    ? char.ConvertToUtf32(text[index], text[index + 1])
                    : text[index];
                int size = cp > 0xFFFF ? 2 : 1;
                if (index + size > stop)
                {
                    break;
                }

                index += size;
                offset += Unicode.Utf8Length(cp is >= 0xD800 and <= 0xDFFF ? 0xFFFD : cp);
                if (cp == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }

            return new XferPosition(offset, line, column);
        }
    }

    public static XferJsonResult<XferValue?> Read(string text, XferLimits limits)
    {
        int depthLimit = limits.Depth;
        var raw = new List<Raw>();
        bool failed = false;
        bool depthReported = false;
        int i = 0;

        List<XferDiagnostic> Finish()
        {
            var cursor = new Cursor(text);
            var order = new int[raw.Count];
            for (int k = 0; k < order.Length; k++)
            {
                order[k] = k;
            }

            Array.Sort(order, (a, b) => raw[a].Index != raw[b].Index ? raw[a].Index.CompareTo(raw[b].Index) : a.CompareTo(b));
            var resolved = new List<XferDiagnostic>(order.Length);
            foreach (int k in order)
            {
                XferPosition position = cursor.At(raw[k].Index);
                resolved.Add(XferDiagnostics.Create(raw[k].Code, position, null, raw[k].Detail));
            }

            return resolved;
        }

        void Report(XferDiagnosticCode code, int index, string detail)
        {
            raw.Add(new Raw(code, index, detail));
            if (code != XferDiagnosticCode.XFER0905)
            {
                failed = true;
            }
        }

        var stack = new Stack<Frame>();

        // Called for every container, empty or not, with the index of its opening bracket.
        void Enter(int at)
        {
            if (stack.Count + 1 > depthLimit && !depthReported)
            {
                depthReported = true;
                Report(XferDiagnosticCode.XFER0602, at, $"nesting exceeds {depthLimit} levels");
            }
        }

        void SkipSpace()
        {
            while (i < text.Length && text[i] is ' ' or '\t' or '\n' or '\r')
            {
                i++;
            }
        }

        // Reads a string whose opening quote is at i; leaves i after the closing quote.
        string ReadString()
        {
            int start = i;
            i++;
            var output = new System.Text.StringBuilder();
            int from = i;
            while (true)
            {
                if (i >= text.Length)
                {
                    throw new Malformed(start, "unterminated string");
                }

                char c = text[i];
                if (c == '"')
                {
                    output.Append(text, from, i - from);
                    i++;
                    break;
                }

                if (c < 0x20)
                {
                    throw new Malformed(i, "control character in string");
                }

                if (c != '\\')
                {
                    i++;
                    continue;
                }

                output.Append(text, from, i - from);
                char e = i + 1 < text.Length ? text[i + 1] : '\0';
                bool hasEscape = i + 1 < text.Length;
                i += 2;
                switch (hasEscape ? e : '￿')
                {
                    case '"':
                        output.Append('"');
                        break;
                    case '\\':
                        output.Append('\\');
                        break;
                    case '/':
                        output.Append('/');
                        break;
                    case 'b':
                        output.Append('\b');
                        break;
                    case 'f':
                        output.Append('\f');
                        break;
                    case 'n':
                        output.Append('\n');
                        break;
                    case 'r':
                        output.Append('\r');
                        break;
                    case 't':
                        output.Append('\t');
                        break;
                    case 'u':
                        if (i + 4 > text.Length || !IsHex4(text, i))
                        {
                            throw new Malformed(i - 2, "bad \\u escape");
                        }

                        output.Append((char)int.Parse(text.AsSpan(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default:
                        throw new Malformed(i - 2, "bad escape");
                }

                from = i;
            }

            string result = output.ToString();
            if (result.Contains('\r', StringComparison.Ordinal) || HasLoneSurrogate(result))
            {
                Report(XferDiagnosticCode.XFER0907, start, "a string holds U+000D or a lone surrogate");
            }

            return result;
        }

        XferValue? finalValue = null;

        void Push(XferValue value)
        {
            if (stack.Count == 0)
            {
                finalValue = value;
                return;
            }

            Frame top = stack.Peek();
            if (!top.IsObject)
            {
                top.Items.Add(value);
                return;
            }

            string key = top.Key!;
            int existing = top.Entries.IndexOf(key);
            if (existing >= 0)
            {
                Report(XferDiagnosticCode.XFER0905, top.KeyAt, $"duplicate key {Messages.JsQuote(key)}");
                top.Entries.SetAt(existing, value); // last value wins, in the first key's position
            }
            else
            {
                top.Entries.Add(key, value);
            }

            top.Key = null;
        }

        try
        {
            // A value is expected at the start of the document, after "[", after "," in a sequence
            // and after the ":" in an object. expectValue says which.
            bool expectValue = true;
            while (true)
            {
                SkipSpace();
                if (expectValue)
                {
                    int start = i;
                    char c = i < text.Length ? text[i] : '\0';
                    bool atEnd = i >= text.Length;
                    if (!atEnd && c == '{')
                    {
                        i++;
                        Enter(start);
                        stack.Push(new Frame(isObject: true));
                        SkipSpace();
                        if (i < text.Length && text[i] == '}')
                        {
                            i++;
                            stack.Pop();
                            Push(new XferObject(new OrderedDictionary<string, XferValue>(StringComparer.Ordinal), null));
                            expectValue = false;
                        }
                        else
                        {
                            // Read the first key.
                            if (i >= text.Length || text[i] != '"')
                            {
                                throw new Malformed(i, "expected a string key");
                            }

                            Frame frame = stack.Peek();
                            frame.KeyAt = i;
                            frame.Key = ReadString();
                            SkipSpace();
                            if (i >= text.Length || text[i] != ':')
                            {
                                throw new Malformed(i, "expected `:`");
                            }

                            i++;
                        }

                        continue;
                    }

                    if (!atEnd && c == '[')
                    {
                        Enter(start);
                        i++;
                        SkipSpace();
                        if (i < text.Length && text[i] == ']')
                        {
                            i++;
                            Push(new XferSequence(Array.Empty<XferValue>(), null));
                            expectValue = false;
                        }
                        else
                        {
                            stack.Push(new Frame(isObject: false));
                        }

                        continue;
                    }

                    if (!atEnd && c == '"')
                    {
                        Push(new XferString(ReadString()));
                    }
                    else if (string.CompareOrdinal(text, i, "true", 0, 4) == 0)
                    {
                        i += 4;
                        Push(new XferBool(true));
                    }
                    else if (string.CompareOrdinal(text, i, "false", 0, 5) == 0)
                    {
                        i += 5;
                        Push(new XferBool(false));
                    }
                    else if (string.CompareOrdinal(text, i, "null", 0, 4) == 0)
                    {
                        i += 4;
                        Push(new XferNull());
                    }
                    else if (!atEnd && (c == '-' || c is >= '0' and <= '9'))
                    {
                        int end = ScanNumber(text, i, out bool isFractional);
                        if (end < 0)
                        {
                            throw new Malformed(i, "malformed number");
                        }

                        string lexeme = text.Substring(i, end - i);
                        i = end;
                        Push(isFractional ? new XferFractional(lexeme) : new XferInteger(lexeme, null));
                    }
                    else
                    {
                        throw new Malformed(i, i >= text.Length ? "unexpected end of text" : "unexpected character");
                    }

                    // A literal that runs into more name characters (truex, 1.5.3) is caught by the
                    // delimiter check below, since only a separator may follow a value.
                    expectValue = false;
                    continue;
                }

                // After a value: a separator, a closer, or the end of the document.
                if (stack.Count == 0)
                {
                    if (i < text.Length)
                    {
                        throw new Malformed(i, "text after the value");
                    }

                    break;
                }

                Frame top = stack.Peek();
                char next = i < text.Length ? text[i] : '\0';
                bool end2 = i >= text.Length;
                if (!end2 && next == ',')
                {
                    i++;
                    SkipSpace();
                    if (top.IsObject)
                    {
                        if (i >= text.Length || text[i] != '"')
                        {
                            throw new Malformed(i, "expected a string key");
                        }

                        top.KeyAt = i;
                        top.Key = ReadString();
                        SkipSpace();
                        if (i >= text.Length || text[i] != ':')
                        {
                            throw new Malformed(i, "expected `:`");
                        }

                        i++;
                    }

                    expectValue = true;
                    continue;
                }

                if (!end2 && ((top.IsObject && next == '}') || (!top.IsObject && next == ']')))
                {
                    i++;
                    stack.Pop();
                    Push(top.IsObject ? new XferObject(top.Entries, null) : new XferSequence([.. top.Items], null));
                    continue;
                }

                throw new Malformed(i, end2 ? "unexpected end of text" : "unexpected character");
            }
        }
        catch (Malformed e)
        {
            raw.Add(new Raw(XferDiagnosticCode.XFER0906, e.Index, e.Message));
            return new XferJsonResult<XferValue?>(null, Finish());
        }

        return new XferJsonResult<XferValue?>(failed ? null : finalValue, Finish());
    }

    private static bool IsHex4(string text, int at)
    {
        for (int k = 0; k < 4; k++)
        {
            char c = text[at + k];
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasLoneSurrogate(string text)
    {
        for (int k = 0; k < text.Length; k++)
        {
            if (char.IsHighSurrogate(text[k]))
            {
                if (k + 1 < text.Length && char.IsLowSurrogate(text[k + 1]))
                {
                    k++;
                    continue;
                }

                return true;
            }

            if (char.IsLowSurrogate(text[k]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The end of the JSON number that starts at <paramref name="start"/>, or -1 if there is none:
    /// <c>-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?</c>, the longest match.
    /// </summary>
    private static int ScanNumber(string text, int start, out bool isFractional)
    {
        isFractional = false;
        int k = start;
        if (k < text.Length && text[k] == '-')
        {
            k++;
        }

        if (k >= text.Length || text[k] is < '0' or > '9')
        {
            return -1;
        }

        if (text[k] == '0')
        {
            k++;
        }
        else
        {
            while (k < text.Length && text[k] is >= '0' and <= '9')
            {
                k++;
            }
        }

        if (k + 1 < text.Length && text[k] == '.' && text[k + 1] is >= '0' and <= '9')
        {
            isFractional = true;
            k++;
            while (k < text.Length && text[k] is >= '0' and <= '9')
            {
                k++;
            }
        }

        if (k < text.Length && text[k] is 'e' or 'E')
        {
            int e = k + 1;
            if (e < text.Length && text[e] is '+' or '-')
            {
                e++;
            }

            if (e < text.Length && text[e] is >= '0' and <= '9')
            {
                isFractional = true;
                while (e < text.Length && text[e] is >= '0' and <= '9')
                {
                    e++;
                }

                k = e;
            }
        }

        return k;
    }
}
