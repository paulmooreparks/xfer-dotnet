using System;
using System.Globalization;
using System.Numerics;

namespace ParksComputing.XferLang;

public sealed partial class XferInteger
{
    /// <summary>
    /// The value as an integral type, or false when it does not fit. Never rounds or wraps.
    /// For example <c>TryGet(out int value)</c>.
    /// </summary>
    public bool TryGet<T>(out T value)
        where T : IBinaryInteger<T>, IMinMaxValue<T>
    {
        BigInteger v = Value;
        if (v >= BigInteger.CreateChecked(T.MinValue) && v <= BigInteger.CreateChecked(T.MaxValue))
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
    /// The value as a <see cref="double"/> (the nearest one), and whether that is exactly the value.
    /// An integer too large for a double is infinity and not exact.
    /// </summary>
    public double ToDouble(out bool exact)
    {
        BigInteger v = Value;
        double result = (double)v;
        exact = double.IsFinite(result) && new BigInteger(result) == v;
        return result;
    }

    /// <summary>The value as a <see cref="decimal"/>, or false when it does not fit.</summary>
    public bool TryGetDecimal(out decimal value)
    {
        BigInteger v = Value;
        if (v >= new BigInteger(decimal.MinValue) && v <= new BigInteger(decimal.MaxValue))
        {
            value = (decimal)v;
            return true;
        }

        value = 0;
        return false;
    }
}

public sealed partial class XferFractional
{
    /// <summary>
    /// The spelling as <c>digits * 10 ** exponent</c> (<c>1.50</c> is 150 and -2; <c>6.02e23</c> is
    /// 602 and 21), or false for <c>inf</c>, <c>-inf</c> and <c>nan</c>. Zero is always 0 and 0.
    /// </summary>
    public bool TryGetDecimalParts(out BigInteger digits, out int exponent)
    {
        digits = BigInteger.Zero;
        exponent = 0;
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

        string whole = text[wholeStart..k];
        string fraction = string.Empty;
        if (k < text.Length && text[k] == '.')
        {
            k++;
            int fractionStart = k;
            while (k < text.Length && text[k] is >= '0' and <= '9')
            {
                k++;
            }

            fraction = text[fractionStart..k];
        }

        if (whole.Length + fraction.Length == 0)
        {
            return false;
        }

        long exp = 0;
        if (k < text.Length && text[k] is 'e' or 'E')
        {
            k++;
            if (!long.TryParse(text.AsSpan(k), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exp))
            {
                // An exponent too large to hold is not a number this can represent, unless the digits are all zero.
                return IsAllZero(whole, fraction);
            }

            k = text.Length;
        }

        if (k != text.Length)
        {
            return false;
        }

        BigInteger magnitude = BigInteger.Parse(whole + fraction, NumberStyles.None, CultureInfo.InvariantCulture);
        if (magnitude.IsZero)
        {
            return true; // zero is zero, whatever its exponent says
        }

        long scaled = exp - fraction.Length;
        if (scaled is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        digits = negative ? -magnitude : magnitude;
        exponent = (int)scaled;
        return true;
    }

    private static bool IsAllZero(string whole, string fraction) => whole.AsSpan().IndexOfAnyExcept('0') < 0 && fraction.AsSpan().IndexOfAnyExcept('0') < 0;

    /// <summary>
    /// The value as a <see cref="double"/> (the nearest one) and whether that is exactly the value.
    /// A finite value too large becomes infinity and one too small becomes zero, neither exact.
    /// <c>inf</c>, <c>-inf</c> and <c>nan</c> are exact.
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

        // double.Parse is correctly rounded (documented since .NET Core 3.0) and gives infinity,
        // not an exception, for a value out of range.
        double value = double.Parse(Lexeme.Replace("_", string.Empty, StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!TryGetDecimalParts(out BigInteger digits, out int e))
        {
            exact = false;
            return value;
        }

        if (digits.IsZero)
        {
            exact = value == 0;
            return value;
        }

        if (value == 0 || !double.IsFinite(value))
        {
            exact = false;
            return value;
        }

        // digits * 10^e == mantissa * 2^x, both sides scaled to integers.
        long bits = BitConverter.DoubleToInt64Bits(value);
        int biased = (int)((bits >> 52) & 0x7FF);
        long fractionBits = bits & ((1L << 52) - 1);
        BigInteger mantissa = biased == 0 ? fractionBits : fractionBits | (1L << 52);
        if (bits < 0)
        {
            mantissa = -mantissa;
        }

        int x = (biased == 0 ? 1 : biased) - 1075;
        BigInteger left = digits * BigInteger.Pow(10, Math.Max(e, 0)) * BigInteger.Pow(2, Math.Max(-x, 0));
        BigInteger right = mantissa * BigInteger.Pow(2, Math.Max(x, 0)) * BigInteger.Pow(10, Math.Max(-e, 0));
        exact = left == right;
        return value;
    }

    /// <summary>
    /// The value as a <see cref="decimal"/>, or false when <see cref="decimal"/> cannot hold it
    /// exactly (more than 28 digits after the point, more than about 29 digits, or <c>inf</c> and
    /// <c>nan</c>). A spelling such as <c>1.50</c> keeps its scale, so the result prints as 1.50.
    /// </summary>
    public bool TryGetDecimal(out decimal value)
    {
        value = 0;
        if (!TryGetDecimalParts(out BigInteger digits, out int e))
        {
            return false;
        }

        if (digits.IsZero)
        {
            return true;
        }

        BigInteger limit = new(decimal.MaxValue);
        if (e >= 0)
        {
            if (e > 29)
            {
                return false;
            }

            BigInteger whole = digits * BigInteger.Pow(10, e);
            if (BigInteger.Abs(whole) > limit)
            {
                return false;
            }

            value = (decimal)whole;
            return true;
        }

        // A negative exponent is the scale. Beyond 28 it is only representable if trailing zeros of
        // the digits can be dropped to bring it back to 28.
        while (e < -28 && digits % 10 == 0)
        {
            digits /= 10;
            e++;
        }

        if (e < -28 || BigInteger.Abs(digits) > limit)
        {
            return false;
        }

        bool negative = digits.Sign < 0;
        BigInteger magnitude = BigInteger.Abs(digits);
        uint lo = (uint)(magnitude & uint.MaxValue);
        uint mid = (uint)((magnitude >> 32) & uint.MaxValue);
        uint hi = (uint)((magnitude >> 64) & uint.MaxValue);
        value = new decimal((int)lo, (int)mid, (int)hi, negative, (byte)-e);
        return true;
    }
}
