using System;
using System.Collections.Generic;
using System.Text;

namespace ParksComputing.XferLang.Tests.Harness;

/// <summary>
/// The lossless property, checked from outside the implementation: every byte of the normalized
/// text is reachable from the tokens and their leading trivia, in order. The expected text is
/// computed here, independently, from the input bytes (specification 3.1 and 3.2).
/// </summary>
internal static class Lossless
{
    /// <summary>The text the tokens say the document holds.</summary>
    public static string Reconstruct(IReadOnlyList<TokenView> tokens)
    {
        var text = new StringBuilder();
        foreach (TokenView token in tokens)
        {
            foreach (string trivia in token.LeadingTrivia)
            {
                text.Append(trivia);
            }

            text.Append(token.Text);
        }

        return text.ToString();
    }

    /// <summary>
    /// The normalized text of valid UTF-8 input: a leading byte order mark removed, then CRLF and
    /// lone CR replaced by LF.
    /// </summary>
    public static string Normalize(byte[] input)
    {
        ReadOnlySpan<byte> bytes = input;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }
}
