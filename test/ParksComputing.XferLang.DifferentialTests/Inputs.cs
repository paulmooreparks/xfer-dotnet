using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ParksComputing.XferLang.Tests.Harness;

namespace ParksComputing.XferLang.DifferentialTests;

/// <summary>
/// The inputs both CLIs are run on: documents from the port of the xfer-js generator that the
/// main test project's property tests use (<c>Harness/Generator.cs</c>, linked here), the same
/// documents damaged, token soup as in <c>tools/fixtures/parser-fixture.mjs</c>, raw bytes, and
/// JSON texts.
/// </summary>
internal static class Inputs
{
    /// <summary>One generated document, rendered with random spacing.</summary>
    public static string Generate(uint seed) =>
        Generator.Render(Generator.Generate(seed), Generator.RandomSeparator(new Mulberry32(seed * 7919)), string.Empty);

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
}
