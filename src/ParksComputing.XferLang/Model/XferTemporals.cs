using System;

namespace ParksComputing.XferLang;

public sealed partial class XferTemporal
{
    /// <summary>The value as a date, or false for any other kind, the year 0000 (which <see cref="DateOnly"/> cannot hold), or a spelling that is not a date.</summary>
    public bool TryGetDateOnly(out DateOnly value)
    {
        value = default;
        if (TemporalKind != XferTemporalKind.Date || !ParseDate(Lexeme, 0, out int y, out int m, out int d) || Lexeme.Length != 10 || y < 1)
        {
            return false;
        }

        value = new DateOnly(y, m, d);
        return true;
    }

    /// <summary>
    /// The value as a time, or false for any other kind, second 60 (a leap second, which no .NET
    /// time type holds), a non-zero fractional digit past the seventh (a .NET tick is 100
    /// nanoseconds), or a spelling that is not a time. Never rounds.
    /// </summary>
    public bool TryGetTimeOnly(out TimeOnly value)
    {
        value = default;
        if (TemporalKind != XferTemporalKind.Time || !ParseTime(Lexeme, 0, out long ticks, out int end) || end != Lexeme.Length)
        {
            return false;
        }

        value = new TimeOnly(ticks);
        return true;
    }

    /// <summary>
    /// The value as a <see cref="DateTime"/>: a date at midnight and a local date-time as
    /// <see cref="DateTimeKind.Unspecified"/>, and a date-time with <c>Z</c> or an offset converted to
    /// UTC (<see cref="DateTimeKind.Utc"/>). False for a time, the year 0000, second 60, a non-zero
    /// fractional digit past the seventh, an offset beyond plus or minus 14:00, or a UTC time before
    /// 0001-01-01 or after 9999-12-31 (none of which a .NET type can hold). Never rounds or clamps.
    /// </summary>
    public bool TryGetDateTime(out DateTime value)
    {
        value = default;
        if (!TryParseDateTime(out long localTicks, out TimeSpan? offset))
        {
            return false;
        }

        if (TemporalKind == XferTemporalKind.DateTime)
        {
            long utc = localTicks - offset!.Value.Ticks;
            if (utc < 0 || utc > DateTime.MaxValue.Ticks)
            {
                return false;
            }

            value = new DateTime(utc, DateTimeKind.Utc);
            return true;
        }

        value = new DateTime(localTicks, DateTimeKind.Unspecified);
        return true;
    }

    /// <summary>
    /// The value as a <see cref="DateTimeOffset"/>, keeping its offset, for a date-time with
    /// <c>Z</c> or an offset only. False under every condition <see cref="TryGetDateTime"/> lists.
    /// </summary>
    public bool TryGetDateTimeOffset(out DateTimeOffset value)
    {
        value = default;
        if (TemporalKind != XferTemporalKind.DateTime || !TryParseDateTime(out long localTicks, out TimeSpan? offset))
        {
            return false;
        }

        long utc = localTicks - offset!.Value.Ticks;
        if (utc < 0 || utc > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        value = new DateTimeOffset(new DateTime(localTicks, DateTimeKind.Unspecified), offset.Value);
        return true;
    }

    private bool TryParseDateTime(out long localTicks, out TimeSpan? offset)
    {
        localTicks = 0;
        offset = null;
        string text = Lexeme;
        if (TemporalKind is not (XferTemporalKind.Date or XferTemporalKind.LocalDateTime or XferTemporalKind.DateTime)
            || !ParseDate(text, 0, out int y, out int m, out int d) || y < 1)
        {
            return false;
        }

        long ticks = new DateTime(y, m, d).Ticks;
        int end = 10;
        if (TemporalKind != XferTemporalKind.Date)
        {
            if (text.Length < 11 || text[10] != 'T' || !ParseTime(text, 11, out long timeTicks, out end))
            {
                return false;
            }

            ticks += timeTicks;
        }

        if (TemporalKind == XferTemporalKind.DateTime)
        {
            if (end >= text.Length)
            {
                return false;
            }

            if (text[end] == 'Z' && end + 1 == text.Length)
            {
                offset = TimeSpan.Zero;
            }
            else if (text[end] is '+' or '-' && end + 6 == text.Length && text[end + 3] == ':'
                && TwoDigits(text, end + 1, out int oh) && TwoDigits(text, end + 4, out int om))
            {
                // DateTimeOffset holds offsets of at most 14 hours; the specification allows up to 23:59.
                long minutes = (oh * 60L) + om;
                if (om > 59 || minutes > 14 * 60)
                {
                    return false;
                }

                offset = TimeSpan.FromMinutes(text[end] == '-' ? -minutes : minutes);
            }
            else
            {
                return false;
            }
        }
        else if (end != text.Length)
        {
            return false;
        }

        localTicks = ticks;
        return true;
    }

    private static bool TwoDigits(string text, int at, out int value)
    {
        value = 0;
        if (at + 2 > text.Length || text[at] is < '0' or > '9' || text[at + 1] is < '0' or > '9')
        {
            return false;
        }

        value = ((text[at] - '0') * 10) + (text[at + 1] - '0');
        return true;
    }

    /// <summary><c>YYYY-MM-DD</c> at an index; the year may be 0000 (the caller refuses it).</summary>
    private static bool ParseDate(string text, int at, out int year, out int month, out int day)
    {
        year = month = day = 0;
        if (at + 10 > text.Length || text[at + 4] != '-' || text[at + 7] != '-')
        {
            return false;
        }

        for (int k = 0; k < 4; k++)
        {
            if (text[at + k] is < '0' or > '9')
            {
                return false;
            }

            year = (year * 10) + (text[at + k] - '0');
        }

        if (!TwoDigits(text, at + 5, out month) || !TwoDigits(text, at + 8, out day))
        {
            return false;
        }

        // The year 0000 passes here; the callers refuse it, so a leap-year test on it is harmless.
        return month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(Math.Max(year, 1), month);
    }

    /// <summary>
    /// <c>HH:MM:SS</c> with an optional fraction at an index, as ticks since midnight. False for second
    /// 60 and for a non-zero fractional digit past the seventh (a digit of zero there is exact).
    /// </summary>
    private static bool ParseTime(string text, int at, out long ticks, out int end)
    {
        ticks = 0;
        end = at;
        if (at + 8 > text.Length || text[at + 2] != ':' || text[at + 5] != ':'
            || !TwoDigits(text, at, out int h) || !TwoDigits(text, at + 3, out int mi) || !TwoDigits(text, at + 6, out int s)
            || h > 23 || mi > 59 || s > 59)
        {
            return false;
        }

        long result = (((h * 60L) + mi) * 60 + s) * TimeSpan.TicksPerSecond;
        int k = at + 8;
        if (k < text.Length && text[k] == '.')
        {
            k++;
            int digitsStart = k;
            long fraction = 0;
            int used = 0;
            while (k < text.Length && text[k] is >= '0' and <= '9')
            {
                if (used < 7)
                {
                    fraction = (fraction * 10) + (text[k] - '0');
                    used++;
                }
                else if (text[k] != '0')
                {
                    return false;
                }

                k++;
            }

            if (k == digitsStart)
            {
                return false;
            }

            for (; used < 7; used++)
            {
                fraction *= 10;
            }

            result += fraction;
        }

        ticks = result;
        end = k;
        return true;
    }
}
