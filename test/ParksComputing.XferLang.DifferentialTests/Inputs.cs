using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;

namespace ParksComputing.XferLang.DifferentialTests;

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
/// The inputs both CLIs are run on: documents from a port of the xfer-js generator
/// (<c>test/generator.ts</c>, its rendering only), the same documents damaged, token soup as in
/// <c>tools/fixtures/parser-fixture.mjs</c>, raw bytes, and JSON texts.
/// </summary>
internal static class Inputs
{
    private static readonly string[] Keys = ["a", "b", "menu-id", "x.y", "\"first name\"", "\"true\"", "日本", "_k", "\"{ braces }\"", "#\"a \"quoted\" key\"#"];

    private static readonly string[] Symbols = ["dark", "light", "v1.2.3", "read-only", "naïve", "日本語", "_x", "infinity"];

    private static readonly string[] Fractionals = ["1.5", "0.25", "6.02e23", "1e5", "2.5E-3", "1_000.000_1", "-3.14", "0e0", "inf", "-inf", "nan", "1e400", "1e-400"];

    private static readonly string[] Temporals = ["2025-08-01", "09:30:00.250", "2025-08-01T09:30:00", "2025-08-01T09:30:00Z", "2025-08-01T09:30:00+08:00", "2016-12-31T23:59:60Z", "0000-01-01"];

    private static readonly string[] Spaces = [" ", "\n", "  ", "\t", "\n   ", " </ a note /> ", "</ x />", "\n  </ multi\n line /> \n"];

    private static readonly string[] Pieces = ["{", "}", "[", "]", "<!", "!>", " ", ",", ":", "x", "1", "\"", "#", "id", "tag", "xfer", "\"k\"", "<!-", "</ c />"];

    private static readonly string[] Soup =
    [
        "{", "}", "[", "]", "<!", "!>", "<! id \"a\" !>", "<! tag t !>", "<! xfer 1 !>", "<! xfer 2 !>", "<! lang en !>", "<! let x 1 !>",
        " ", "\n", "</ c />", ",", ":", "a", "b", "\"s\"", "\"\"", "#\"q\"#", "1", "-2", "1.5", "true", "null", "inf", "nan", "2025-08-01",
        "~", "?", "<x", "\"open", "a.", "0755", "1-2", "!",
        "é", "日本", "\U0001F600", "\"" + string.Concat(Enumerable.Repeat("\U0001F600", 15)) + "\"", "\"" + new string('k', 30) + "\"", new string('x', 26), "\r\n", "\r", "\uFEFF",
        "<! xfer 01 !>", "<! xfer 1_0 !>", "<! xfer 0x1 !>", "<! xfer 0 !>", "<! xfer -1 !>", "<! xfer \"1\" !>", "99999999999999999999999", "0x_1",
    ];

    private static readonly string[] JsonSoup =
    [
        "{", "}", "[", "]", ",", ":", " ", "\n", "\"a\"", "\"\"", "\"\\u0041\"", "\"\\ud800\"", "\"\\udc00\\ud800\"", "\"\\n\"", "\"\\x\"", "1", "-0", "0.5", "1e400", "-1e-400",
        "12345678901234567890", "01", "1.", ".5", "true", "false", "null", "nul", "\"é\"", "\"\u0001\"", "\"open", "NaN", "\uFEFF", "/", "\"k\":",
    ];

    /// <summary>One generated document, as the xfer-js generator renders it with random spacing.</summary>
    public static string Generate(uint seed)
    {
        var random = new Mulberry32(seed);
        int nextId = 0;
        var spacing = new Mulberry32(seed * 7919);
        string Sep(bool required) => !required && spacing.Next() < 0.4 ? string.Empty : spacing.Pick(Spaces);

        string Text(string[] alphabet, int max)
        {
            var sb = new StringBuilder();
            for (int i = random.Below(max); i > 0; i--)
            {
                sb.Append(random.Pick(alphabet));
            }

            return sb.ToString();
        }

        string StringScalar()
        {
            switch (random.Below(5))
            {
                case 0:
                    return "\"\"";
                case 1:
                    return "\"\"\"" + $"{Text(["a", " ", "b"], 3)} \"{Text(["x", " "], 3)}\" {Text(["c", "\n"], 3)}" + "\"\"\"";
                case 2:
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
                    return $"{hashes}\"{content}\"{hashes}";
                default:
                    return "\"" + Text(["a", "b", " ", "\n", "\\", "<", "/", "{", "#", "é", "日", "!", "<!", "</"], 8) + "\"";
            }
        }

        string IntegerScalar()
        {
            BigInteger n = new BigInteger(random.Below(70000)) * (random.Chance(0.5) ? BigInteger.One : new BigInteger(1_000_000_000_000));
            string sign = random.Chance(0.3) ? "-" : string.Empty;
            string body = random.Below(5) switch
            {
                0 => n.ToString(CultureInfo.InvariantCulture),
                1 => "0x" + Radix(n, 16),
                2 => "0o" + Radix(n, 8),
                3 => "0b" + Radix(n, 2),
                _ => Grouped(n.ToString(CultureInfo.InvariantCulture)),
            };
            return sign + body;
        }

        string Scalar() => random.Below(9) switch
        {
            0 => IntegerScalar(),
            1 => random.Pick(Fractionals),
            2 or 3 => StringScalar(),
            4 => random.Pick(Symbols),
            5 => random.Pick(["true", "false", "null"]),
            _ => random.Pick(Temporals),
        };

        string Annotation() => random.Below(4) switch
        {
            0 => $"<! id \"id{nextId++}\" !>",
            1 => $"<! tag {random.Pick(Symbols)} !>",
            2 => "<! tag \"two words\" !>",
            _ => "<! lang javascript !>",
        };

        string Annotations(double p)
        {
            var sb = new StringBuilder();
            for (int count = 0; random.Chance(p) && count < 3; count++)
            {
                sb.Append(Annotation()).Append(Sep(true));
            }

            return sb.ToString();
        }

        string Value(int depth, bool forceContainer = false)
        {
            if (!forceContainer && (depth <= 0 || random.Chance(0.4)))
            {
                return Scalar();
            }

            if (random.Chance(0.5))
            {
                var items = new List<string>();
                for (int i = random.Below(5); i > 0; i--)
                {
                    items.Add(Annotations(0.2) + Value(depth - 1));
                }

                return "[" + Sep(false) + string.Join(Sep(true), items) + Sep(false) + "]";
            }

            string[] keys = [.. Keys];
            for (int i = keys.Length - 1; i > 0; i--)
            {
                int j = random.Below(i + 1);
                (keys[i], keys[j]) = (keys[j], keys[i]);
            }

            var pairs = keys.Take(random.Below(5)).Select(k => Annotations(0.2) + k + Sep(true) + Value(depth - 1)).ToList();
            return "{" + Sep(false) + string.Join(Sep(true), pairs) + Sep(false) + "}";
        }

        bool head = random.Chance(0.3);
        string root = Annotations(0.2) + Value(4, random.Chance(0.85));
        string leading = spacing.Chance(0.5) ? string.Empty : " \n";
        return (head ? leading + "<! xfer 1 !>" + Sep(true) : Sep(false)) + root + Sep(false);
    }

    /// <summary>A text damaged one of three ways: a few characters deleted, a piece inserted, or cut short.</summary>
    public static string Damage(string text, Mulberry32 random)
    {
        int at = random.Below(text.Length + 1);
        double r = random.Next();
        if (r < 0.34)
        {
            int end = Math.Min(text.Length, at + 1 + random.Below(4));
            return text[..at] + text[end..];
        }

        return r < 0.67 ? text[..at] + random.Pick(Pieces) + text[at..] : text[..at];
    }

    public static string TokenSoup(Mulberry32 random, IReadOnlyList<string> pieces)
    {
        var sb = new StringBuilder();
        for (int i = random.Below(16); i > 0; i--)
        {
            sb.Append(random.Pick(pieces));
            if (random.Chance(0.5))
            {
                sb.Append(' ');
            }
        }

        return sb.ToString();
    }

    public static IReadOnlyList<string> XferSoup => Soup;

    public static IReadOnlyList<string> JsonPieces => JsonSoup;

    /// <summary>Raw bytes: invalid UTF-8, byte order marks, NUL and lone line ends among valid text.</summary>
    public static byte[] Bytes(Mulberry32 random)
    {
        byte[][] pieces =
        [
            [0xEF, 0xBB, 0xBF], [0xFF], [0xC0, 0x80], [0xED, 0xA0, 0x80], [0xF4, 0x90, 0x80, 0x80], [0xE6, 0x97], [0x00], [0x0D], [0x0A], [0x0D, 0x0A],
            "{ a 1 }"u8.ToArray(), "\"x\""u8.ToArray(), "[1 2]"u8.ToArray(), "{\"a\": 1}"u8.ToArray(), " "u8.ToArray(), "日"u8.ToArray(),
        ];
        var bytes = new List<byte>();
        for (int i = random.Below(10); i > 0; i--)
        {
            bytes.AddRange(random.Pick(pieces));
        }

        return [.. bytes];
    }

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
