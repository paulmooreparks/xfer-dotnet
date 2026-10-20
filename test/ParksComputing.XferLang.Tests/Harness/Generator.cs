using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace ParksComputing.XferLang.Tests.Harness;

/// <summary>
/// The generator of xfer-js <c>test/random.ts</c>. It is ported rather than taken from
/// <see cref="Random"/>, whose sequence for a seed is not promised to stay the same across .NET
/// versions, so a failing seed reproduces anywhere.
/// </summary>
internal sealed class Mulberry32(uint seed)
{
    private uint a = seed;

    public double Next()
    {
        unchecked
        {
            a += 0x6D2B79F5;
            uint t = (a ^ (a >> 15)) * (1 | a);
            t = (t + ((t ^ (t >> 7)) * (61 | t))) ^ t;
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }

    public int Below(int n) => (int)Math.Floor(Next() * n);

    public bool Chance(double p) => Next() < p;

    public T Pick<T>(IReadOnlyList<T> items) => items[Below(items.Count)];
}

/// <summary>
/// A port of xfer-js <c>test/generator.ts</c>: a random document model, its text under any
/// spacing, and the tagged value (suite/tests/README.md) the text must parse to. Everything random
/// is drawn when the model is built, so rendering it is deterministic.
/// </summary>
internal static class Generator
{
    /// <summary>A scalar's text and its tagged form, as type and value (null for <c>null</c>).</summary>
    internal sealed record Scalar(string Text, string Type, string? Value);

    internal sealed record Annotation(string Text, string Name, Scalar Value);

    internal abstract record Model;

    internal sealed record ScalarModel(Scalar Scalar) : Model;

    internal sealed record SequenceModel(IReadOnlyList<Annotated> Items) : Model;

    internal sealed record ObjectModel(IReadOnlyList<Pair> Pairs) : Model;

    internal sealed record Annotated(IReadOnlyList<Annotation> Annotations, Model Node);

    internal sealed record Pair(string Spelled, string Key, IReadOnlyList<Annotation> Annotations, Model Value);

    internal sealed record Document(bool Head, Annotated Root);

    /// <summary>The text between tokens; <c>required</c> means two tokens would otherwise run together.</summary>
    internal delegate string Separator(bool required);

    private static readonly (string Spelled, string Key)[] Keys =
    [
        ("a", "a"), ("b", "b"), ("menu-id", "menu-id"), ("x.y", "x.y"), ("\"first name\"", "first name"),
        ("\"true\"", "true"), ("日本", "日本"), ("_k", "_k"), ("\"{ braces }\"", "{ braces }"), ("#\"a \"quoted\" key\"#", "a \"quoted\" key"),
    ];

    private static readonly string[] Symbols = ["dark", "light", "v1.2.3", "read-only", "naïve", "日本語", "_x", "infinity"];

    // The last two of each are not in xfer-js: values no .NET type holds exactly, for the
    // differential tests and the object mapper.
    private static readonly string[] Fractionals = ["1.5", "0.25", "6.02e23", "1e5", "2.5E-3", "1_000.000_1", "-3.14", "0e0", "inf", "-inf", "nan", "1e400", "1e-400"];

    private static readonly (string Kind, string Lexeme)[] Temporals =
    [
        ("date", "2025-08-01"), ("time", "09:30:00.250"), ("local-datetime", "2025-08-01T09:30:00"),
        ("datetime", "2025-08-01T09:30:00Z"), ("datetime", "2025-08-01T09:30:00+08:00"), ("datetime", "2016-12-31T23:59:60Z"), ("date", "0000-01-01"),
    ];

    private static readonly string[] Spaces = [" ", "\n", "  ", "\t", "\n   ", " </ a note /> ", "</ x />", "\n  </ multi\n line /> \n"];

    public static Document Generate(uint seed)
    {
        var random = new Mulberry32(seed);
        int nextId = 0;

        string Text(string[] alphabet, int max)
        {
            var sb = new StringBuilder();
            for (int i = random.Below(max); i > 0; i--)
            {
                sb.Append(random.Pick(alphabet));
            }

            return sb.ToString();
        }

        Scalar StringScalar()
        {
            switch (random.Below(5))
            {
                case 0:
                    return new("\"\"", "string", string.Empty);
                case 1:
                {
                    // Content with inner quotes: a run of three or more quotes delimits it.
                    string content = $"{Text(["a", " ", "b"], 3)} \"{Text(["x", " "], 3)}\" {Text(["c", "\n"], 3)}";
                    return new("\"\"\"" + content + "\"\"\"", "string", content);
                }

                case 2:
                {
                    // Content that begins and ends with a quote can only be written in the hash
                    // form, whose closer must be longer than every hash run after a quote.
                    string content = "\"" + Text(["a", " ", "#"], 4) + "\"";
                    int m = 1;
                    for (int i = 0; i < content.Length; i++)
                    {
                        if (content[i] == '"')
                        {
                            int run = 0;
                            while (i + 1 + run < content.Length && content[i + 1 + run] == '#')
                            {
                                run++;
                            }

                            m = Math.Max(m, run + 1);
                        }
                    }

                    string hashes = new('#', m);
                    return new($"{hashes}\"{content}\"{hashes}", "string", content);
                }

                default:
                {
                    string content = Text(["a", "b", " ", "\n", "\\", "<", "/", "{", "#", "é", "日", "!", "<!", "</"], 8);
                    return new("\"" + content + "\"", "string", content);
                }
            }
        }

        Scalar IntegerScalar()
        {
            BigInteger n = new BigInteger(random.Below(70000)) * (random.Chance(0.5) ? BigInteger.One : new BigInteger(1_000_000_000_000));
            string sign = random.Chance(0.3) ? "-" : string.Empty;
            BigInteger value = sign == "-" ? -n : n;
            string body = random.Below(5) switch
            {
                0 => n.ToString(CultureInfo.InvariantCulture),
                1 => "0x" + Radix(n, 16),
                2 => "0o" + Radix(n, 8),
                3 => "0b" + Radix(n, 2),
                _ => Grouped(n.ToString(CultureInfo.InvariantCulture)),
            };
            return new(sign + body, "integer", value.ToString(CultureInfo.InvariantCulture));
        }

        Scalar Fractional()
        {
            string lexeme = random.Pick(Fractionals);
            return new(lexeme, "fractional", lexeme.Replace("_", string.Empty, StringComparison.Ordinal));
        }

        Scalar AnyScalar()
        {
            switch (random.Below(9))
            {
                case 0:
                    return IntegerScalar();
                case 1:
                    return Fractional();
                case 2:
                case 3:
                    return StringScalar();
                case 4:
                    string name = random.Pick(Symbols);
                    return new(name, "symbol", name);
                case 5:
                    string word = random.Pick(["true", "false", "null"]);
                    return word == "null" ? new(word, "null", null) : new(word, "bool", word);
                default:
                    (string kind, string lexeme) = random.Pick(Temporals);
                    return new(lexeme, kind, lexeme);
            }
        }

        Annotation MakeAnnotation()
        {
            switch (random.Below(4))
            {
                case 0:
                    string id = $"id{nextId++}";
                    return new($"<! id \"{id}\" !>", "id", new($"\"{id}\"", "string", id));
                case 1:
                    string name = random.Pick(Symbols);
                    return new($"<! tag {name} !>", "tag", new(name, "symbol", name));
                case 2:
                    return new("<! tag \"two words\" !>", "tag", new("\"two words\"", "string", "two words"));
                default:
                    return new("<! lang javascript !>", "lang", new("javascript", "symbol", "javascript"));
            }
        }

        List<Annotation> Annotations(double p)
        {
            var list = new List<Annotation>();
            while (random.Chance(p) && list.Count < 3)
            {
                list.Add(MakeAnnotation());
            }

            return list;
        }

        Model Value(int depth, bool forceContainer = false)
        {
            if (!forceContainer && (depth <= 0 || random.Chance(0.4)))
            {
                return new ScalarModel(AnyScalar());
            }

            if (random.Chance(0.5))
            {
                var items = new List<Annotated>();
                for (int i = random.Below(5); i > 0; i--)
                {
                    List<Annotation> annotations = Annotations(0.2);
                    items.Add(new Annotated(annotations, Value(depth - 1)));
                }

                return new SequenceModel(items);
            }

            // xfer-js shuffles with a random comparator, whose result depends on the engine's sort;
            // Fisher-Yates does the same job with a result this code fixes.
            (string, string)[] keys = [.. Keys];
            for (int i = keys.Length - 1; i > 0; i--)
            {
                int j = random.Below(i + 1);
                (keys[i], keys[j]) = (keys[j], keys[i]);
            }

            var pairs = new List<Pair>();
            foreach ((string spelled, string key) in keys.Take(random.Below(5)))
            {
                List<Annotation> annotations = Annotations(0.2);
                pairs.Add(new Pair(spelled, key, annotations, Value(depth - 1)));
            }

            return new ObjectModel(pairs);
        }

        bool head = random.Chance(0.3);
        List<Annotation> rootAnnotations = Annotations(0.2);
        return new Document(head, new Annotated(rootAnnotations, Value(4, random.Chance(0.85))));
    }

    public static Separator RandomSeparator(Mulberry32 random) => required => !required && random.Next() < 0.4 ? string.Empty : random.Pick(Spaces);

    /// <summary>Every optional space removed and every required one a single space.</summary>
    public static Separator TightSeparator { get; } = required => required ? " " : string.Empty;

    /// <summary>The whole document: an optional head (after whitespace only), then the root.</summary>
    public static string Render(Document doc, Separator sep, string leading)
    {
        // The pieces are rendered in this order so that the separator draws its randomness in the
        // same order as xfer-js does.
        string head = doc.Head ? leading + "<! xfer 1 !>" + sep(true) : sep(false);
        string root = RenderAnnotated(doc.Root, sep);
        return head + root + sep(false);
    }

    private static string RenderAnnotated(Annotated item, Separator sep) =>
        string.Concat(item.Annotations.Select(a => a.Text + sep(true))) + Render(item.Node, sep);

    private static string Render(Model model, Separator sep)
    {
        // A depth of at most 5, so recursion here is bounded by the generator, not by input.
        switch (model)
        {
            case ScalarModel s:
                return s.Scalar.Text;
            case SequenceModel q:
            {
                string open = sep(false);
                string body = string.Join(sep(true), q.Items.Select(i => RenderAnnotated(i, sep)));
                return "[" + open + body + sep(false) + "]";
            }

            default:
            {
                var o = (ObjectModel)model;
                string open = sep(false);
                string body = string.Join(sep(true), o.Pairs.Select(p => string.Concat(p.Annotations.Select(a => a.Text + sep(true))) + p.Spelled + sep(true) + Render(p.Value, sep)));
                return "{" + open + body + sep(false) + "}";
            }
        }
    }

    /// <summary>The tagged form the document must parse to (suite/tests/README.md).</summary>
    public static JsonNode Tagged(Document doc)
    {
        var document = new JsonObject();
        if (doc.Head)
        {
            document["xfer"] = 1;
        }

        document["root"] = TaggedAnnotated(doc.Root.Annotations, doc.Root.Node);
        return document;
    }

    private static JsonObject TaggedAnnotated(IReadOnlyList<Annotation> annotations, Model node)
    {
        JsonObject tagged = Tagged(node);
        if (annotations.Count > 0)
        {
            tagged["annotations"] = new JsonArray([.. annotations.Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["value"] = Leaf(a.Value) })]);
        }

        return tagged;
    }

    private static JsonObject Tagged(Model model) => model switch
    {
        ScalarModel s => Leaf(s.Scalar),
        SequenceModel q => new JsonObject { ["type"] = "sequence", ["value"] = new JsonArray([.. q.Items.Select(i => (JsonNode)TaggedAnnotated(i.Annotations, i.Node))]) },
        _ => new JsonObject
        {
            ["type"] = "object",
            ["value"] = new JsonObject(((ObjectModel)model).Pairs.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, TaggedAnnotated(p.Annotations, p.Value)))),
        },
    };

    private static JsonObject Leaf(Scalar s) =>
        s.Value is null ? new JsonObject { ["type"] = s.Type } : new JsonObject { ["type"] = s.Type, ["value"] = s.Value };

    private static string Radix(BigInteger n, int radix)
    {
        if (n.IsZero)
        {
            return "0";
        }

        var digits = new StringBuilder();
        for (BigInteger v = n; v > 0; v /= radix)
        {
            digits.Insert(0, "0123456789abcdef"[(int)(v % radix)]);
        }

        return digits.ToString();
    }

    private static string Grouped(string digits)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0)
            {
                sb.Append('_');
            }

            sb.Append(digits[i]);
        }

        return sb.ToString();
    }
}
