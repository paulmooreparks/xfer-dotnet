using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using ParksComputing.XferLang.Model;
using ParksComputing.XferLang.Syntax;
using ParksComputing.XferLang.Text;
using ParksComputing.XferLang.Writing;

namespace ParksComputing.XferLang;

/// <summary>Options for writing.</summary>
public sealed record XferWriteOptions
{
    private XferLimits limits = XferLimits.Default;

    /// <summary>
    /// The limits the writer enforces: the delimiter run length (which the choice rule keeps within
    /// where it can), the nesting depth, and the document size the output may reach. Defaults to the
    /// specification's minimums, so a result parsed with raised limits needs the same limits here.
    /// </summary>
    public XferLimits Limits
    {
        get => limits;
        init => limits = value ?? throw new ArgumentNullException(nameof(value));
    }
}

/// <summary>Writes values as XferLang.</summary>
public static class XferWriter
{
    /// <summary>
    /// The canonical form of a parsed document (specification 6): one spelling for a value, so two
    /// implementations given the same document write the same bytes. Comments are not part of it.
    /// The result keeps the <c>xfer</c> head and is trusted to be well formed.
    /// </summary>
    /// <exception cref="XferException">
    /// The parse had an error (carrying its diagnostics), the value is nested deeper than the depth
    /// limit (<c>XFER0602</c>), or the output would be longer than the document size limit (<c>XFER0601</c>).
    /// </exception>
    public static string Canonical(XferParseResult result, XferWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Ok)
        {
            throw new XferException(result.Diagnostics);
        }

        XferValue value = result.Value ?? throw new XferException(result.Diagnostics, "the parse produced no value");
        bool head = result.Tree.Head is not null;
        return Write(value, head, trusted: true, (options ?? Defaults).Limits);
    }

    /// <summary>
    /// The canonical form of a value, which has no head. A value that no document could hold
    /// (specification 9, "Values built by a program") is refused and never repaired, so that the
    /// output always reads back as exactly the value given.
    /// </summary>
    /// <exception cref="ArgumentException">The value could not appear in any document.</exception>
    /// <exception cref="XferException">The value is nested deeper than the depth limit, or the output would be longer than the document size limit.</exception>
    public static string Canonical(XferValue value, XferWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Write(value, head: false, trusted: false, (options ?? Defaults).Limits);
    }

    /// <summary>Writes the canonical form of a value to a text writer.</summary>
    /// <inheritdoc cref="Canonical(XferValue, XferWriteOptions?)"/>
    public static void Canonical(XferValue value, TextWriter writer, XferWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(Canonical(value, options));
    }

    private static readonly XferWriteOptions Defaults = new();

    private static string Write(XferValue root, bool head, bool trusted, XferLimits limits)
    {
        var context = new Context(limits, trusted);
        string body = context.WriteValue(root);
        return (head ? "<! xfer 1 !>\n" : string.Empty) + body + "\n";
    }

    private const string Indent = "    ";

    /// <summary>The annotation names a document can carry on a value. <c>xfer</c> is the head, not an annotation.</summary>
    private static bool IsAnnotationName(string name) => name is "id" or "tag" or "lang";

    private sealed class Frame
    {
        public Frame(IReadOnlyList<XferValue> values, IReadOnlyList<string>? keys, int indent, char closer)
        {
            Values = values;
            Keys = keys;
            Indent = indent;
            Closer = closer;
        }

        public IReadOnlyList<XferValue> Values { get; }

        public IReadOnlyList<string>? Keys { get; }

        public int Indent { get; }

        public char Closer { get; }

        public int Next { get; set; }
    }

    private sealed class Context
    {
        private readonly XferLimits limits;
        private readonly bool trusted;
        private readonly StringBuilder output = new();
        private readonly Stack<Frame> stack = new();
        private readonly HashSet<string> ids = new(StringComparer.Ordinal);
        private long size;

        public Context(XferLimits limits, bool trusted)
        {
            this.limits = limits;
            this.trusted = trusted;
        }

        /// <summary>
        /// Writes the value in the canonical layout: every member on its own line, four spaces in
        /// per level. Iterative, so depth cannot overflow the stack. The result has no final newline.
        /// </summary>
        public string WriteValue(XferValue root)
        {
            Annotations(root, 0);
            Emit(root, 0);
            while (stack.Count > 0)
            {
                Frame frame = stack.Peek();
                if (frame.Next < frame.Values.Count)
                {
                    int i = frame.Next++;
                    XferValue item = frame.Values[i];
                    int level = frame.Indent + 1;
                    Annotations(item, level);
                    Put(Repeat(level));
                    if (frame.Keys is not null)
                    {
                        string key = frame.Keys[i];
                        if (!trusted)
                        {
                            AssertWritable(key);
                        }

                        Put(IsBareName(key) ? key : StringDelimiters.Write(key, limits.RunLength), " ");
                    }

                    Emit(item, level);
                }
                else
                {
                    stack.Pop();
                    Put(Repeat(frame.Indent), frame.Closer.ToString());
                    FinishItem();
                }
            }

            return output.ToString();
        }

        private static string Repeat(int level) => level == 0 ? string.Empty : string.Concat(System.Linq.Enumerable.Repeat(Indent, level));

        private void Put(params string[] parts)
        {
            foreach (string part in parts)
            {
                size += Unicode.Utf8ByteLength(part);
                output.Append(part);
            }

            if (size > limits.DocumentBytes)
            {
                throw new XferException(
                    [XferDiagnostics.Create(XferDiagnosticCode.XFER0601, XferPosition.Origin, null, $"the canonical form is longer than {limits.DocumentBytes} bytes")]);
            }
        }

        private void FinishItem()
        {
            if (stack.Count > 0)
            {
                Put("\n");
            }
        }

        // An annotation goes before the element or pair it attaches to, on its own line. In the
        // value model a pair's annotations are on its value, so they come out before the key.
        private void Annotations(XferValue target, int level)
        {
            foreach (XferAnnotation a in target.Annotations)
            {
                if (!trusted)
                {
                    CheckAnnotation(a);
                }

                Put(Repeat(level), "<! ", a.Name, " ", ScalarText(a.Value), " !>\n");
            }
        }

        private void Emit(XferValue value, int level)
        {
            if (value is not (XferObject or XferSequence))
            {
                Put(ScalarText(value));
                FinishItem();
                return;
            }

            bool isObject = value is XferObject;
            IReadOnlyList<XferValue> members;
            IReadOnlyList<string>? keys = null;
            if (value is XferObject o)
            {
                var values = new List<XferValue>(o.Count);
                var names = new List<string>(o.Count);
                foreach (KeyValuePair<string, XferValue> member in o)
                {
                    names.Add(member.Key);
                    values.Add(member.Value);
                }

                members = values;
                keys = names;
            }
            else
            {
                members = (XferSequence)value;
            }

            if (members.Count == 0)
            {
                Put(isObject ? "{}" : "[]");
                FinishItem();
                return;
            }

            // The output grows with the square of the depth, so the depth limit also bounds the output.
            if (stack.Count >= limits.Depth)
            {
                throw new XferException(
                    [XferDiagnostics.Create(XferDiagnosticCode.XFER0602, XferPosition.Origin, null, $"the value is nested more than {limits.Depth} levels deep")]);
            }

            Put(isObject ? "{" : "[", "\n");
            stack.Push(new Frame(members, keys, level, isObject ? '}' : ']'));
        }

        /// <summary>The text of a scalar, checking a hand-built one: the output must always read back as the value.</summary>
        private string ScalarText(XferValue value)
        {
            switch (value)
            {
                case XferString s:
                    if (!trusted)
                    {
                        AssertWritable(s.Value);
                    }

                    return StringDelimiters.Write(s.Value, limits.RunLength);
                case XferInteger i:
                    if (!trusted && !(IsLexeme(XferTokenKind.Integer, i.Lexeme) && Projection.ParseInteger(i.Lexeme) == i.Value))
                    {
                        throw new ArgumentException($"{Quote(i.Lexeme)} is not the spelling of the integer {i.Value}");
                    }

                    return IntegerText(i.Lexeme);
                case XferFractional f:
                    if (!trusted && !IsLexeme(XferTokenKind.Fractional, f.Lexeme))
                    {
                        throw new ArgumentException($"{Quote(f.Lexeme)} is not a fractional number");
                    }

                    return f.Lexeme;
                case XferBool b:
                    return b.Value ? "true" : "false";
                case XferNull:
                    return "null";
                case XferSymbol s:
                    if (!IsBareName(s.Name))
                    {
                        throw new ArgumentException($"{Quote(s.Name)} is not a valid symbol name");
                    }

                    return s.Name;
                case XferTemporal t:
                    if (!trusted && !IsLexeme(TokenKindOf(t.TemporalKind), t.Lexeme))
                    {
                        throw new ArgumentException($"{Quote(t.Lexeme)} is not a {t.TemporalKind}");
                    }

                    return t.Lexeme;
                default:
                    throw new ArgumentException($"{value.GetType().Name} is not a kind of value a document can hold");
            }
        }

        /// <summary>Checks a hand-built annotation: only what a document can carry, once per id, with a scalar value.</summary>
        private void CheckAnnotation(XferAnnotation annotation)
        {
            (string name, XferValue value) = (annotation.Name, annotation.Value);
            if (!IsAnnotationName(name))
            {
                throw new ArgumentException($"{Quote(name)} is not an annotation a value can carry (id, tag and lang are; xfer is the head)");
            }

            if (value is null || value.Annotations.Count > 0)
            {
                throw new ArgumentException($"the value of the {name} annotation cannot carry an annotation");
            }

            bool fits = name == "id" ? value is XferString : value is XferString or XferSymbol;
            if (!fits)
            {
                throw new ArgumentException($"the {name} annotation takes {(name == "id" ? "a string" : "a string or a symbol")}");
            }

            if (name == "id" && !ids.Add(((XferString)value).Value))
            {
                throw new ArgumentException($"the id {Quote(((XferString)value).Value)} is used twice");
            }
        }
    }

    private static XferTokenKind TokenKindOf(XferTemporalKind kind) => kind switch
    {
        XferTemporalKind.Date => XferTokenKind.Date,
        XferTemporalKind.Time => XferTokenKind.Time,
        XferTemporalKind.LocalDateTime => XferTokenKind.LocalDateTime,
        _ => XferTokenKind.DateTime,
    };

    private static string Quote(string text) => Messages.JsQuote(text);

    /// <summary>Whether text can be written bare, as a key or as a symbol (specification 4.3).</summary>
    private static bool IsBareName(string text)
    {
        bool first = true;
        int last = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int cp = rune.Value;
            if (first ? !Unicode.IsIdentifierStart(cp) : !Unicode.IsIdentifierContinue(cp))
            {
                return false;
            }

            first = false;
            last = cp;
        }

        return !first && last != '.' && Lexer.KeywordOf(Encoding.UTF8.GetBytes(text)) == XferKeyword.None;
    }

    /// <summary>
    /// Refuses text that no document can hold. A document is normalized before it is read, so
    /// U+000D becomes LF and cannot be part of a value (specification 3.2), and a lone surrogate is
    /// not a scalar value. Writing either would give a document that reads back as a different value.
    /// </summary>
    private static void AssertWritable(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r' || (char.IsSurrogate(c) && !(char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) && ++i > 0)))
            {
                throw new ArgumentException(
                    $"a string cannot contain U+{(int)c:X4}: a document cannot hold it, so it would be read back as a different value");
            }
        }
    }

    /// <summary>A hexadecimal integer is written with lower-case digits; everything else is as it was written.</summary>
    private static string IntegerText(string lexeme)
    {
        int start = lexeme.StartsWith('-') ? 1 : 0;
        return lexeme.AsSpan(start).StartsWith("0x", StringComparison.Ordinal)
            ? string.Concat(lexeme.AsSpan(0, start + 2), lexeme.AsSpan(start + 2).ToString().ToLowerInvariant())
            : lexeme;
    }

    /// <summary>Whether the lexeme is exactly one token of the kind, as the lexer would read it.</summary>
    private static bool IsLexeme(XferTokenKind kind, string lexeme)
    {
        if (lexeme.Length == 0)
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = new UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(lexeme);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        var pending = new List<PendingDiagnostic>();
        TokenStream stream = Lexer.Tokenize(bytes, XferLimits.Default, pending);
        if (pending.Count > 0 || stream.Tokens.Count != 2)
        {
            return false;
        }

        Token token = stream.Tokens[0];
        if (token.TriviaCount != 0 || token.Start != 0 || token.Length != bytes.Length || stream.Tokens[1].TriviaCount != 0)
        {
            return false;
        }

        return kind == XferTokenKind.Fractional
            ? token.Kind == XferTokenKind.Fractional
                || (token.Kind == XferTokenKind.Keyword && token.Keyword is XferKeyword.Inf or XferKeyword.MinusInf or XferKeyword.Nan)
            : token.Kind == kind;
    }
}
