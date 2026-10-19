using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using ParksComputing.XferLang.Syntax;

namespace ParksComputing.XferLang.Model;

/// <summary>
/// The value model of a parsed document (a port of xfer-js <c>src/value.ts</c>). Iterative, so
/// depth cannot overflow the stack.
/// </summary>
internal static class Projection
{
    /// <summary>
    /// The value of a tree, or null when the tree holds an error node, a missing value, an
    /// unclosed container or anything after the root. The caller has already seen an error then.
    /// </summary>
    public static XferValue? ToValue(XferSyntaxTree tree)
    {
        if (tree.Root is null || tree.Trailing.Count > 0)
        {
            return null;
        }

        SyntaxSource source = tree.Source;
        var stack = new Stack<Frame>();
        XferNode? pending = tree.Root;
        XferValue? produced = null;

        while (true)
        {
            if (pending is not null)
            {
                XferNode node = pending;
                pending = null;
                switch (node)
                {
                    case XferErrorNode:
                        return null;
                    case XferScalarNode scalar:
                        produced = Annotate(Scalar(source, scalar.TokenIndex), node.AnnotationArray, null, source);
                        if (produced is null)
                        {
                            return null;
                        }

                        break;
                    case XferObjectNode obj:
                        if (obj.CloseIndex < 0)
                        {
                            return null;
                        }

                        foreach (XferPairNode pair in obj.PairArray)
                        {
                            if (pair.KeyIndex < 0 || pair.Value is null)
                            {
                                return null;
                            }
                        }

                        stack.Push(new Frame(obj, obj.PairArray.Length));
                        break;
                    case XferSequenceNode seq:
                        if (seq.CloseIndex < 0)
                        {
                            return null;
                        }

                        stack.Push(new Frame(seq, seq.ItemArray.Length));
                        break;
                }
            }

            if (produced is not null)
            {
                if (stack.Count == 0)
                {
                    return produced;
                }

                stack.Peek().Done.Add(produced);
                produced = null;
            }

            Frame top = stack.Peek();
            if (top.Next < top.Count)
            {
                int i = top.Next++;
                pending = top.Node is XferObjectNode o ? o.PairArray[i].Value : ((XferSequenceNode)top.Node).ItemArray[i];
            }
            else
            {
                stack.Pop();
                produced = Finish(source, top);
                if (produced is null)
                {
                    return null;
                }
            }
        }
    }

    private sealed class Frame
    {
        public Frame(XferNode node, int count)
        {
            Node = node;
            Count = count;
            Done = new List<XferValue>(count);
        }

        public XferNode Node { get; }

        public int Count { get; }

        public int Next { get; set; }

        public List<XferValue> Done { get; }
    }

    private static XferValue? Finish(SyntaxSource source, Frame frame)
    {
        if (frame.Node is XferSequenceNode seq)
        {
            return Annotate(new XferSequence([.. frame.Done], null), seq.AnnotationArray, null, source);
        }

        var obj = (XferObjectNode)frame.Node;
        var entries = new OrderedDictionary<string, XferValue>(obj.PairArray.Length, StringComparer.Ordinal);
        for (int i = 0; i < obj.PairArray.Length; i++)
        {
            XferPairNode pair = obj.PairArray[i];
            string key = source.TokenAt(pair.KeyIndex).Kind == XferTokenKind.String ? source.ContentOf(pair.KeyIndex) : source.TextOf(pair.KeyIndex);

            // An annotation before a pair describes the pair's value; there is no pair in the model.
            XferValue? member = Annotate(frame.Done[i], [], pair.AnnotationArray, source);
            if (member is null)
            {
                return null;
            }

            // A repeated key is a diagnostic, and the later value wins (specification 5.3).
            entries[key] = member;
        }

        return Annotate(new XferObject(entries, null), obj.AnnotationArray, null, source);
    }

    /// <summary>Puts annotations on a value: those before it (a pair's), then its own, then the list given.</summary>
    private static XferValue? Annotate(XferValue? value, XferAnnotationNode[] list, XferAnnotationNode[]? before, SyntaxSource source)
    {
        if (value is null)
        {
            return null;
        }

        int count = (before?.Length ?? 0) + value.Annotations.Count + list.Length;
        if (count == 0)
        {
            return value;
        }

        var all = new List<XferAnnotation>(count);
        if (!AddAnnotations(all, before, source))
        {
            return null;
        }

        all.AddRange(value.Annotations);
        if (!AddAnnotations(all, list, source))
        {
            return null;
        }

        return WithAnnotations(value, all);
    }

    private static bool AddAnnotations(List<XferAnnotation> into, XferAnnotationNode[]? nodes, SyntaxSource source)
    {
        if (nodes is null)
        {
            return true;
        }

        foreach (XferAnnotationNode a in nodes)
        {
            // Only a scalar can be an annotation's value in a document that parsed without errors.
            if (a.NameIndex < 0 || a.Value is not XferScalarNode scalar)
            {
                return false;
            }

            XferValue? value = Scalar(source, scalar.TokenIndex);
            if (value is null)
            {
                return false;
            }

            into.Add(new XferAnnotation(source.TextOf(a.NameIndex), value));
        }

        return true;
    }

    private static XferValue WithAnnotations(XferValue value, List<XferAnnotation> annotations) => value switch
    {
        XferObject o => new XferObject(o.Entries, annotations),
        XferSequence s => new XferSequence(s.ItemArray, annotations),
        XferString s => new XferString(s.Value, annotations),
        XferInteger i => new XferInteger(i.Lexeme, annotations),
        XferFractional f => new XferFractional(f.Lexeme, annotations),
        XferBool b => new XferBool(b.Value, annotations),
        XferNull => new XferNull(annotations),
        XferSymbol s => new XferSymbol(s.Name, annotations),
        XferTemporal t => new XferTemporal(t.TemporalKind, t.Lexeme, annotations),
        _ => throw new InvalidOperationException("unknown value kind"),
    };

    /// <summary>The value a scalar token denotes, without annotations; null for a token that is not a value.</summary>
    private static XferValue? Scalar(SyntaxSource source, int index)
    {
        Token token = source.TokenAt(index);
        switch (token.Kind)
        {
            case XferTokenKind.String:
                return new XferString(source.ContentOf(index));
            case XferTokenKind.Integer:
                string lexeme = source.TextOf(index);
                return new XferInteger(lexeme, null);
            case XferTokenKind.Fractional:
                return new XferFractional(source.TextOf(index));
            case XferTokenKind.Identifier:
                return new XferSymbol(source.TextOf(index));
            case XferTokenKind.Date:
                return new XferTemporal(XferTemporalKind.Date, source.TextOf(index));
            case XferTokenKind.Time:
                return new XferTemporal(XferTemporalKind.Time, source.TextOf(index));
            case XferTokenKind.LocalDateTime:
                return new XferTemporal(XferTemporalKind.LocalDateTime, source.TextOf(index));
            case XferTokenKind.DateTime:
                return new XferTemporal(XferTemporalKind.DateTime, source.TextOf(index));
            case XferTokenKind.Keyword:
                return token.Keyword switch
                {
                    XferKeyword.True => new XferBool(true),
                    XferKeyword.False => new XferBool(false),
                    XferKeyword.Null => new XferNull(),
                    XferKeyword.Inf => new XferFractional("inf"),
                    XferKeyword.MinusInf => new XferFractional("-inf"),
                    XferKeyword.Nan => new XferFractional("nan"),
                    _ => null,
                };
            default:
                return null;
        }
    }

    /// <summary>
    /// The value of an integer lexeme: optional <c>-</c>, then decimal digits or a lowercase
    /// <c>0x</c>, <c>0o</c> or <c>0b</c> prefix and its digits, with <c>_</c> separators. The
    /// lexer has checked the form.
    /// </summary>
    internal static BigInteger ParseInteger(string lexeme)
    {
        bool negative = lexeme.StartsWith('-');
        string body = (negative ? lexeme[1..] : lexeme).Replace("_", string.Empty, StringComparison.Ordinal);
        BigInteger magnitude;
        if (body.StartsWith("0x", StringComparison.Ordinal))
        {
            // A leading 0 keeps the parse unsigned: BigInteger reads hexadecimal as two's complement.
            magnitude = BigInteger.Parse("0" + body[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }
        else if (body.StartsWith("0b", StringComparison.Ordinal))
        {
            magnitude = BigInteger.Parse("0" + body[2..], NumberStyles.AllowBinarySpecifier, CultureInfo.InvariantCulture);
        }
        else if (body.StartsWith("0o", StringComparison.Ordinal))
        {
            // There is no octal parse; each octal digit is exactly three binary digits.
            var bits = new StringBuilder((body.Length * 3) + 1).Append('0');
            foreach (char digit in body.AsSpan(2))
            {
                int d = digit - '0';
                bits.Append((d & 4) != 0 ? '1' : '0').Append((d & 2) != 0 ? '1' : '0').Append((d & 1) != 0 ? '1' : '0');
            }

            magnitude = BigInteger.Parse(bits.ToString(), NumberStyles.AllowBinarySpecifier, CultureInfo.InvariantCulture);
        }
        else
        {
            magnitude = BigInteger.Parse(body, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        return negative ? -magnitude : magnitude;
    }
}
