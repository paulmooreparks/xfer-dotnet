using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using ParksComputing.XferLang.Syntax;

namespace ParksComputing.XferLang.Tests.Harness;

/// <summary>
/// Converts values to the suite's tagged JSON form (suite/tests/README.md), as xfer-js
/// test/tagged.ts does, and walks a tree for the lossless check, as xfer-js test/lossless.ts does.
/// Both are iterative, so a document at the depth limit cannot overflow the stack.
/// </summary>
internal static class TaggedWriter
{
    public static JsonNode ToTaggedDocument(XferParseResult result)
    {
        XferValue value = result.Value ?? throw new InvalidOperationException("the parse produced no value");
        var document = new JsonObject();
        if (result.Tree.Head is not null)
        {
            document["xfer"] = result.Version;
        }

        document["root"] = ToTagged(value);
        return document;
    }

    public static JsonNode ToTagged(XferValue root)
    {
        // Build children first, then parents, with an explicit stack of (value, visited).
        var results = new Dictionary<XferValue, JsonNode>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(XferValue Value, bool Expanded)>();
        stack.Push((root, false));
        while (stack.Count > 0)
        {
            (XferValue v, bool expanded) = stack.Pop();
            if (!expanded && v is XferObject or XferSequence)
            {
                stack.Push((v, true));
                IEnumerable<XferValue> kids = v is XferObject o ? o.Values : (XferSequence)v;
                foreach (XferValue kid in kids.Reverse())
                {
                    stack.Push((kid, false));
                }

                continue;
            }

            JsonObject node = v switch
            {
                XferObject o => Wrap("object", Members(o, results)),
                XferSequence s => Wrap("sequence", new JsonArray(s.Select(item => Detach(results, item)).ToArray())),
                _ => Leaf(v),
            };
            results[v] = Annotate(v, node);
        }

        return results[root];
    }

    private static JsonNode Detach(Dictionary<XferValue, JsonNode> results, XferValue v)
    {
        // A value instance may be shared (the projection never shares, but a hand-built value could),
        // so each use gets its own copy of the JSON node.
        JsonNode node = results[v];
        return node.Parent is null ? node : node.DeepClone();
    }

    private static JsonObject Members(XferObject o, Dictionary<XferValue, JsonNode> results)
    {
        var members = new JsonObject();
        foreach (KeyValuePair<string, XferValue> member in o)
        {
            members[member.Key] = Detach(results, member.Value);
        }

        return members;
    }

    private static JsonObject Wrap(string type, JsonNode value) => new() { ["type"] = type, ["value"] = value };

    private static JsonObject Leaf(XferValue v) => v switch
    {
        XferString s => Wrap("string", s.Value),
        XferInteger i => Wrap("integer", i.Value.ToString(CultureInfo.InvariantCulture)),
        XferFractional f => Wrap("fractional", f.Lexeme.Replace("_", string.Empty, StringComparison.Ordinal)),
        XferBool b => Wrap("bool", b.Value ? "true" : "false"),
        XferNull => new JsonObject { ["type"] = "null" },
        XferSymbol s => Wrap("symbol", s.Name),
        XferTemporal t => Wrap(
            t.TemporalKind switch
            {
                XferTemporalKind.Date => "date",
                XferTemporalKind.Time => "time",
                XferTemporalKind.LocalDateTime => "local-datetime",
                _ => "datetime",
            },
            t.Lexeme),
        _ => throw new ArgumentOutOfRangeException(nameof(v), v.Kind, "not a scalar"),
    };

    private static JsonObject Annotate(XferValue v, JsonObject node)
    {
        if (v.Annotations.Count == 0)
        {
            return node;
        }

        // Annotation values carry no annotations, so this is one level deep.
        var annotations = new JsonArray(v.Annotations.Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["value"] = Leaf(a.Value) }).ToArray());
        var annotated = new JsonObject { ["annotations"] = annotations };
        foreach (KeyValuePair<string, JsonNode?> member in node.ToList())
        {
            node.Remove(member.Key);
            annotated[member.Key] = member.Value;
        }

        return annotated;
    }

    /// <summary>Every token of a tree in source order, ending with the end-of-file token, found by walking the nodes.</summary>
    public static List<TokenView> TokensInOrder(XferSyntaxTree tree)
    {
        var output = new List<TokenView>();
        var stack = new Stack<object>();
        stack.Push(tree.EndOfFile);
        for (int i = tree.Trailing.Count - 1; i >= 0; i--)
        {
            stack.Push(tree.Trailing[i]);
        }

        if (tree.Root is not null)
        {
            stack.Push(tree.Root);
        }

        if (tree.Head is not null)
        {
            stack.Push(tree.Head);
        }

        void PushAll(List<object> parts)
        {
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                stack.Push(parts[i]);
            }
        }

        while (stack.Count > 0)
        {
            object item = stack.Pop();
            var parts = new List<object>();
            switch (item)
            {
                case XferToken token:
                    output.Add(new TokenView(token.LeadingTrivia.Select(t => t.Text).ToArray(), token.Text));
                    continue;
                case XferAnnotationNode a:
                    parts.Add(a.Open);
                    if (a.Name is { } name)
                    {
                        parts.Add(name);
                    }

                    if (a.Value is not null)
                    {
                        parts.Add(a.Value);
                    }

                    if (a.Close is { } close)
                    {
                        parts.Add(close);
                    }

                    break;
                case XferPairNode p:
                    parts.AddRange(p.Annotations);
                    if (p.Key is { } key)
                    {
                        parts.Add(key);
                    }

                    foreach (XferSkippedSyntax skipped in p.Skipped)
                    {
                        parts.Add(skipped.Token is { } t ? t : skipped.Annotation!);
                    }

                    if (p.Value is not null)
                    {
                        parts.Add(p.Value);
                    }

                    break;
                case XferNode n:
                    parts.AddRange(n.Annotations);
                    switch (n)
                    {
                        case XferScalarNode s:
                            parts.Add(s.Token);
                            break;
                        case XferErrorNode e:
                            parts.AddRange(e.Tokens.Cast<object>());
                            break;
                        case XferObjectNode o:
                            parts.Add(o.Open);
                            parts.AddRange(o.Pairs);
                            if (o.Close is { } oc)
                            {
                                parts.Add(oc);
                            }

                            break;
                        case XferSequenceNode q:
                            parts.Add(q.Open);
                            parts.AddRange(q.Items);
                            if (q.Close is { } qc)
                            {
                                parts.Add(qc);
                            }

                            break;
                    }

                    break;
            }

            PushAll(parts);
        }

        return output;
    }
}
