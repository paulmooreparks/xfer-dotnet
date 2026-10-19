using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace ParksComputing.XferLang;

public sealed partial class XferInteger
{
    /// <summary>
    /// The value as an integral type, or false when it does not fit. Never rounds or wraps.
    /// For example <c>TryGet(out int value)</c>. A parsed integer too long to fit is refused from
    /// the length of its spelling, without converting it.
    /// </summary>
    public bool TryGet<T>(out T value)
        where T : IBinaryInteger<T>, IMinMaxValue<T>
    {
        BigInteger max = BigInteger.CreateChecked(T.MaxValue);
        if (CannotFit(max.GetBitLength() + 1))
        {
            value = default!;
            return false;
        }

        BigInteger v = Value;
        if (v >= BigInteger.CreateChecked(T.MinValue) && v <= max)
        {
            value = T.CreateChecked(v);
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>The value as a <see cref="long"/>, or false when it does not fit.</summary>
    public bool TryGetInt64(out long value) => TryGet(out value);

    /// <summary>
    /// The value as a <see cref="double"/>, rounded to nearest with ties to even (as a JavaScript
    /// number is), and whether that is exactly the value. An integer too large for a double is
    /// infinity and not exact.
    /// </summary>
    public double ToDouble(out bool exact)
    {
        BigInteger v = Value;
        double result = Rounding.ToDouble(v);
        exact = double.IsFinite(result) && new BigInteger(result) == v;
        return result;
    }

    /// <summary>The value as a <see cref="decimal"/>, or false when it does not fit.</summary>
    public bool TryGetDecimal(out decimal value)
    {
        if (CannotFit(97))
        {
            value = 0;
            return false;
        }

        BigInteger v = Value;
        if (v >= new BigInteger(decimal.MinValue) && v <= new BigInteger(decimal.MaxValue))
        {
            value = (decimal)v;
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Whether the spelling alone shows that the value needs more than <paramref name="bits"/> bits,
    /// so that a parsed integer of millions of digits is refused without being converted.
    /// </summary>
    private bool CannotFit(long bits)
    {
        if (!ValueFollowsFromSpelling)
        {
            return false; // already converted, or built by hand: the value is cheap or the caller's own
        }

        ReadOnlySpan<char> body = Lexeme.AsSpan();
        if (body.StartsWith("-", StringComparison.Ordinal))
        {
            body = body[1..];
        }

        double bitsPerDigit = 3.321928;
        if (body.Length > 1 && body[0] == '0')
        {
            switch (body[1])
            {
                case 'x':
                    bitsPerDigit = 4;
                    body = body[2..];
                    break;
                case 'o':
                    bitsPerDigit = 3;
                    body = body[2..];
                    break;
                case 'b':
                    bitsPerDigit = 1;
                    body = body[2..];
                    break;
            }
        }

        int significant = 0;
        bool leading = true;
        foreach (char c in body)
        {
            if (c == '_')
            {
                continue;
            }

            if (leading && c == '0')
            {
                continue;
            }

            leading = false;
            significant++;
        }

        // Digits needed are at least (digits - 1) * bitsPerDigit bits, so this never refuses a value that fits.
        return Math.Max(0, significant - 1) * bitsPerDigit > bits + 1;
    }
}

/// <summary>Rounding that does not depend on how a runtime converts big integers.</summary>
internal static class Rounding
{
    /// <summary>The <see cref="double"/> nearest to the value, ties to even; infinity past the range.</summary>
    public static double ToDouble(BigInteger value)
    {
        if (value.IsZero)
        {
            return 0;
        }

        bool negative = value.Sign < 0;
        BigInteger magnitude = BigInteger.Abs(value);
        long bits = (long)magnitude.GetBitLength();
        double result;
        if (bits <= 53)
        {
            result = (double)(ulong)magnitude;
        }
        else if (bits > 1100)
        {
            result = double.PositiveInfinity;
        }
        else
        {
            int shift = (int)(bits - 54);
            ulong top = (ulong)(magnitude >> shift); // the 54 high bits: 53 of mantissa and a round bit
            bool sticky = !(magnitude & ((BigInteger.One << shift) - 1)).IsZero;
            ulong mantissa = top >> 1;
            if ((top & 1) == 1 && (sticky || (mantissa & 1) == 1))
            {
                mantissa++; // may reach 2^53, which a double holds exactly
            }

            result = Math.ScaleB((double)mantissa, shift + 1);
        }

        return negative ? -result : result;
    }
}

public sealed partial class XferFractional
{
    /// <summary>
    /// The spelling of a number split into its sign, its significant digits with no leading or
    /// trailing zeros, and the exponent that goes with them: <c>1.50</c> is "15" and -1,
    /// <c>6.02e23</c> is "602" and 21. Null for anything that is not a number in the grammar of
    /// specification 4.5 (<c>inf</c>, <c>nan</c>, an empty string, text that is not a number).
    /// An all-zero number has empty digits.
    /// </summary>
    private (bool Negative, string Digits, long Exponent, int FractionLength, long LiteralExponent)? Significant()
    {
        string text = Lexeme.Replace("_", string.Empty, StringComparison.Ordinal);
        int k = 0;
        bool negative = false;
        if (k < text.Length && text[k] is '+' or '-')
        {
            negative = text[k] == '-';
            k++;
        }

        int wholeStart = k;
        while (k < text.Length && text[k] is >= '0' and <= '9')
        {
            k++;
        }

        int wholeLength = k - wholeStart;
        int fractionStart = k;
        int fractionLength = 0;
        if (k < text.Length && text[k] == '.')
        {
            k++;
            fractionStart = k;
            while (k < text.Length && text[k] is >= '0' and <= '9')
            {
                k++;
            }

            fractionLength = k - fractionStart;
            if (fractionLength == 0)
            {
                return null;
            }
        }

        if (wholeLength == 0)
        {
            return null;
        }

        long exp = 0;
        if (k < text.Length && text[k] is 'e' or 'E')
        {
            k++;
            int expStart = k;
            if (k < text.Length && text[k] is '+' or '-')
            {
                k++;
            }

            int expDigits = k;
            while (k < text.Length && text[k] is >= '0' and <= '9')
            {
                k++;
            }

            if (k == expDigits)
            {
                return null;
            }

            // An exponent too long for a long is clamped: such a number is out of every range anyway.
            exp = long.TryParse(text.AsSpan(expStart, k - expStart), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed)
                ? parsed
                : (text[expStart] == '-' ? long.MinValue / 4 : long.MaxValue / 4);
        }

        if (k != text.Length)
        {
            return null;
        }

        string all = string.Concat(text.AsSpan(wholeStart, wholeLength), text.AsSpan(fractionStart, fractionLength));
        int first = 0;
        while (first < all.Length && all[first] == '0')
        {
            first++;
        }

        int last = all.Length;
        while (last > first && all[last - 1] == '0')
        {
            last--;
        }

        string digits = all[first..last];
        long exponent = exp - fractionLength + (all.Length - last);
        return (negative, digits, exponent, fractionLength, exp);
    }

    /// <summary>
    /// The spelling as <c>digits * 10 ** exponent</c> (<c>1.50</c> is 150 and -2; <c>6.02e23</c> is
    /// 602 and 21). False for <c>inf</c>, <c>-inf</c> and <c>nan</c>, for anything that is not a
    /// number, and for a number whose exponent does not fit an <see cref="int"/> (unless it is zero,
    /// which is always 0 and 0).
    /// </summary>
    public bool TryGetDecimalParts(out BigInteger digits, out int exponent)
    {
        digits = BigInteger.Zero;
        exponent = 0;
        if (Significant() is not { } s)
        {
            return false;
        }

        // The digits as written, so that 1.50 is 150 and -2.
        string text = Lexeme.Replace("_", string.Empty, StringComparison.Ordinal);
        int pointAt = text.IndexOf('.', StringComparison.Ordinal);
        int eAt = text.AsSpan().IndexOfAny('e', 'E');
        int mantissaEnd = eAt < 0 ? text.Length : eAt;
        int signLength = text.Length > 0 && text[0] is '+' or '-' ? 1 : 0;
        string mantissa = pointAt < 0
            ? text[signLength..mantissaEnd]
            : string.Concat(text.AsSpan(signLength, pointAt - signLength), text.AsSpan(pointAt + 1, mantissaEnd - pointAt - 1));
        BigInteger magnitude = BigInteger.Parse(mantissa, NumberStyles.None, CultureInfo.InvariantCulture);
        if (magnitude.IsZero)
        {
            return true; // zero is zero, whatever its exponent says
        }

        long scaled = s.LiteralExponent - s.FractionLength;
        if (scaled is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        digits = s.Negative ? -magnitude : magnitude;
        exponent = (int)scaled;
        return true;
    }

    /// <summary>
    /// The value as a <see cref="double"/>, rounded to nearest (correctly, as <c>double.Parse</c> is
    /// documented to be since .NET Core 3.0), and whether that is exactly the value. A finite value
    /// too large becomes infinity and one too small becomes zero, neither exact. <c>inf</c>,
    /// <c>-inf</c> and <c>nan</c> are exact. A spelling that is not a number (only possible for a
    /// value built by hand) gives NaN and <c>exact</c> false.
    /// </summary>
    public double ToDouble(out bool exact)
    {
        switch (Lexeme)
        {
            case "inf":
                exact = true;
                return double.PositiveInfinity;
            case "-inf":
                exact = true;
                return double.NegativeInfinity;
            case "nan":
                exact = true;
                return double.NaN;
        }

        if (Significant() is not { } s)
        {
            exact = false;
            return double.NaN;
        }

        if (s.Digits.Length == 0)
        {
            exact = true;
            return s.Negative ? -0.0 : 0.0;
        }

        // Past 767 significant digits no number is exactly a double, and more digits can only
        // matter as "something is left over", so a short form with a final 1 rounds the same way.
        const int Enough = 767;
        double value;
        if (s.Digits.Length > Enough && s.Digits.Length <= Enough + 3)
        {
            exact = false;
            return ParseScientific(s.Negative, s.Digits, s.Exponent);
        }

        if (s.Digits.Length > Enough + 3)
        {
            string shortened = string.Concat(s.Digits.AsSpan(0, Enough + 3), "1");
            long shortExponent = s.Exponent + (s.Digits.Length - (Enough + 3)) - 1; // the appended 1 is one more place
            value = ParseScientific(s.Negative, shortened, shortExponent);
            exact = false;
            return value;
        }

        value = ParseScientific(s.Negative, s.Digits, s.Exponent);
        if (value == 0 || !double.IsFinite(value))
        {
            exact = false;
            return value;
        }

        // digits * 10^e == mantissa * 2^x, both sides scaled to integers.
        BigInteger digits = BigInteger.Parse(s.Digits, NumberStyles.None, CultureInfo.InvariantCulture);
        int e = (int)s.Exponent;
        long bits = BitConverter.DoubleToInt64Bits(value);
        int biased = (int)((bits >> 52) & 0x7FF);
        long fractionBits = bits & ((1L << 52) - 1);
        BigInteger mantissa = biased == 0 ? fractionBits : fractionBits | (1L << 52);
        int x = (biased == 0 ? 1 : biased) - 1075;
        BigInteger left = digits * BigInteger.Pow(10, Math.Max(e, 0)) * BigInteger.Pow(2, Math.Max(-x, 0));
        BigInteger right = mantissa * BigInteger.Pow(2, Math.Max(x, 0)) * BigInteger.Pow(10, Math.Max(-e, 0));
        exact = left == right;
        return value;
    }

    private static double ParseScientific(bool negative, string digits, long exponent)
    {
        // double.Parse gives infinity or zero, not an exception, for a value out of range.
        string text = (negative ? "-" : string.Empty) + digits + "E" + exponent.ToString(CultureInfo.InvariantCulture);
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The value as a <see cref="decimal"/>, or false when <see cref="decimal"/> cannot hold it
    /// exactly (more than 29 digits, more than 28 after the point, or <c>inf</c>, <c>nan</c>, or text
    /// that is not a number). A spelling such as <c>1.50</c> keeps its scale where the value still
    /// fits with it, so the result prints as 1.50.
    /// </summary>
    public bool TryGetDecimal(out decimal value)
    {
        value = 0;
        if (Significant() is not { } s)
        {
            return false;
        }

        // The scale the spelling asks for: 1.50 asks for 2, 1e3 for none.
        long asked = Math.Clamp(s.FractionLength - s.LiteralExponent, 0, 28);
        if (s.Digits.Length == 0)
        {
            value = new decimal(0, 0, 0, s.Negative, (byte)asked);
            return true;
        }

        BigInteger limit = new(decimal.MaxValue);
        long needed = Math.Max(0, -s.Exponent); // the smallest scale that holds the value
        if (s.Digits.Length > 29 || needed > 28 || (s.Exponent > 0 && s.Digits.Length + s.Exponent > 29))
        {
            return false;
        }

        BigInteger significant = BigInteger.Parse(s.Digits, NumberStyles.None, CultureInfo.InvariantCulture);
        BigInteger fits = needed == 0 ? significant * BigInteger.Pow(10, (int)Math.Max(0, s.Exponent)) : significant;
        if (fits > limit)
        {
            return false;
        }

        // Prefer the scale the spelling asked for if the mantissa still fits with it.
        long scale = Math.Max(needed, asked);
        BigInteger mantissa = fits * BigInteger.Pow(10, (int)(scale - needed));
        if (mantissa > limit)
        {
            scale = needed;
            mantissa = fits;
        }

        uint lo = (uint)(mantissa & uint.MaxValue);
        uint mid = (uint)((mantissa >> 32) & uint.MaxValue);
        uint hi = (uint)((mantissa >> 64) & uint.MaxValue);
        value = new decimal((int)lo, (int)mid, (int)hi, s.Negative, (byte)scale);
        return true;
    }
}
