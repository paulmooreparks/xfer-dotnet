namespace ParksComputing.XferLang.Text;

/// <summary>
/// The character classes the specification defines. Everything works on code points (ints), so
/// the lexer can classify what it decodes without allocating.
/// </summary>
internal static class Unicode
{
    // Specification 4.3: scalar values at or above U+0080 that identifiers may not contain. Listed
    // as the specification lists them, unmerged, so that each can be checked against its text.
    private static readonly (int Low, int High)[] Excluded =
    [
        // Unicode space characters
        (0x00A0, 0x00A0),
        (0x1680, 0x1680),
        (0x2000, 0x200A),
        (0x2028, 0x2028),
        (0x2029, 0x2029),
        (0x202F, 0x202F),
        (0x205F, 0x205F),
        (0x3000, 0x3000),
        (0xFEFF, 0xFEFF),

        // Format and bidirectional controls
        (0x200B, 0x200F),
        (0x202A, 0x202E),
        (0x2060, 0x2064),
        (0x2066, 0x2069),
    ];

    /// <summary>Specification 3.3: space, tab, line feed and form feed. CR is gone after normalization.</summary>
    public static bool IsWhitespace(int codePoint) => codePoint is 0x20 or 0x09 or 0x0A or 0x0C;

    /// <summary>Whether a value is a Unicode scalar value: in range and not a surrogate.</summary>
    public static bool IsScalarValue(int codePoint) => codePoint is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF);

    /// <summary>A scalar value at or above U+0080 that is not in the excluded list.</summary>
    public static bool IsAdmittedNonAscii(int codePoint)
    {
        if (codePoint < 0x80 || !IsScalarValue(codePoint))
        {
            return false;
        }

        foreach ((int low, int high) in Excluded)
        {
            if (codePoint >= low && codePoint <= high)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>An ASCII letter, <c>_</c>, or an admitted non-ASCII scalar value.</summary>
    public static bool IsIdentifierStart(int codePoint) => IsAsciiLetter(codePoint) || codePoint == 0x5F || IsAdmittedNonAscii(codePoint);

    /// <summary>An identifier-start character, an ASCII digit, <c>-</c> or <c>.</c>.</summary>
    public static bool IsIdentifierContinue(int codePoint) =>
        IsIdentifierStart(codePoint) || IsAsciiDigit(codePoint) || codePoint is 0x2D or 0x2E;

    /// <summary>The number of bytes UTF-8 uses for a scalar value.</summary>
    public static int Utf8Length(int codePoint) => codePoint switch
    {
        < 0x80 => 1,
        < 0x800 => 2,
        < 0x10000 => 3,
        _ => 4,
    };

    /// <summary>
    /// The UTF-8 size of a string, counted by hand so that nothing depends on how a platform
    /// encoder treats a lone surrogate. A lone surrogate counts as three bytes, as U+FFFD would.
    /// </summary>
    public static long Utf8ByteLength(string text)
    {
        long bytes = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char unit = text[i];
            if (unit < 0x80)
            {
                bytes += 1;
            }
            else if (unit < 0x800)
            {
                bytes += 2;
            }
            else if (char.IsHighSurrogate(unit) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                bytes += 4;
                i++;
            }
            else
            {
                bytes += 3;
            }
        }

        return bytes;
    }

    private static bool IsAsciiLetter(int codePoint) => codePoint is (>= 0x41 and <= 0x5A) or (>= 0x61 and <= 0x7A);

    private static bool IsAsciiDigit(int codePoint) => codePoint is >= 0x30 and <= 0x39;
}
