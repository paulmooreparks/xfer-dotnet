using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ParksComputing.XferLang.Text;

namespace ParksComputing.XferLang.Syntax;

/// <summary>
/// Turns normalized text into tokens, each with the trivia (whitespace and comments) before it.
/// Never throws: every fault is a diagnostic, and the text of a malformed token becomes an error
/// token, so the tokens and their trivia always reproduce the text exactly.
/// </summary>
/// <remarks>
/// A port of xfer-js <c>src/lexer.ts</c> (its DESIGN.md section 5 is the reference) over UTF-8
/// bytes. Every ASCII decision is a byte comparison; a non-ASCII byte is decoded to a code point
/// only where the identifier class has to be tested. A token ends where its grammar ends, and the
/// separation rule (specification 4) is checked there: <c>1-2</c> is <c>1</c> followed by an
/// error at <c>-</c>. Diagnostics are recorded with byte offsets and resolved to positions later,
/// in one pass.
/// </remarks>
internal sealed class Lexer
{
    private const int LF = 0x0A;
    private const int Bang = 0x21;
    private const int Quote = 0x22;
    private const int Hash = 0x23;
    private const int Plus = 0x2B;
    private const int Minus = 0x2D;
    private const int Dot = 0x2E;
    private const int Slash = 0x2F;
    private const int Zero = 0x30;
    private const int Colon = 0x3A;
    private const int LessThan = 0x3C;
    private const int GreaterThan = 0x3E;
    private const int UpperE = 0x45;
    private const int UpperT = 0x54;
    private const int UpperZ = 0x5A;
    private const int LeftBracket = 0x5B;
    private const int RightBracket = 0x5D;
    private const int Underscore = 0x5F;
    private const int LowerE = 0x65;
    private const int LeftBrace = 0x7B;
    private const int RightBrace = 0x7D;

    // Characters people bring from other formats, with what to write instead.
    private static readonly Dictionary<int, string> Hints = new()
    {
        [','] = "elements are separated by whitespace, not commas",
        [':'] = "a key and its value are separated by whitespace, not a colon",
        ['\''] = "strings are quoted with \"",
        ['('] = "there are no tuples; a sequence is written in [ ]",
        [')'] = "there are no tuples; a sequence is written in [ ]",
        ['+'] = "a number takes no + sign",
        ['.'] = "a fractional number needs a digit before the point",
        ['~'] = "booleans are written true and false",
        ['?'] = "null is written null",
    };

    private readonly byte[] text;
    private readonly XferLimits limits;
    private readonly List<PendingDiagnostic> diagnostics;
    private readonly List<Token> tokens;
    private readonly List<Trivia> trivia;
    private int index;

    private Lexer(byte[] text, XferLimits limits, List<PendingDiagnostic> diagnostics)
    {
        this.text = text;
        this.limits = limits;
        this.diagnostics = diagnostics;

        // Sized for a typical document (about one token and one piece of trivia per four bytes),
        // so the lists rarely grow; growth by doubling leaves the old arrays as garbage, which
        // was most of the lexer's allocation before this.
        int estimate = Math.Min((text.Length / 4) + 16, 1 << 24);
        tokens = new List<Token>(estimate);
        trivia = new List<Trivia>(estimate);
    }

    /// <summary>Lexes a whole normalized text, through the end-of-file token.</summary>
    public static TokenStream Tokenize(byte[] text, XferLimits limits, List<PendingDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var lexer = new Lexer(text, limits, diagnostics);
        lexer.Run();

        // The stream is kept for as long as the tree is, so give back what the estimate overshot.
        lexer.tokens.TrimExcess();
        lexer.trivia.TrimExcess();
        return new TokenStream(text, lexer.tokens, lexer.trivia);
    }

    private void Run()
    {
        while (true)
        {
            int firstTrivia = trivia.Count;
            ReadTrivia();
            int count = trivia.Count - firstTrivia;
            if (index >= text.Length)
            {
                tokens.Add(new Token(XferTokenKind.EndOfFile, index, 0, firstTrivia, count));
                return;
            }

            ReadToken(firstTrivia, count);
        }
    }

    // Reading the text.

    /// <summary>The byte at an index, or -1 past the end. Enough for every ASCII decision.</summary>
    private int Code(int at) => at >= 0 && at < text.Length ? text[at] : -1;

    private int Code() => Code(index);

    /// <summary>The code point at the current index, or -1 at the end.</summary>
    private int Point()
    {
        if (index >= text.Length)
        {
            return -1;
        }

        byte first = text[index];
        if (first < 0x80)
        {
            return first;
        }

        // The text is valid UTF-8 (normalization checked it), so this always decodes.
        Rune.DecodeFromUtf8(text.AsSpan(index), out Rune rune, out _);
        return rune.Value;
    }

    private void Advance()
    {
        if (index >= text.Length)
        {
            return;
        }

        int first = text[index];
        index += first < 0x80 ? 1 : first < 0xE0 ? 2 : first < 0xF0 ? 3 : 4;
    }

    /// <summary>Consumes a run of one ASCII character and returns its length.</summary>
    private int RunOf(int c)
    {
        int n = 0;
        while (Code() == c)
        {
            index++;
            n++;
        }

        return n;
    }

    /// <summary>Specification 4: what may follow a scalar token. A comment begins with <c>&lt;</c>, so it counts.</summary>
    private bool AtTerminator()
    {
        int c = Code();
        return c < 0
            || Unicode.IsWhitespace(c)
            || c is LeftBrace or RightBrace or LeftBracket or RightBracket or LessThan
            || (c == Bang && Code(index + 1) == GreaterThan);
    }

    private void Report(XferDiagnosticCode code, int at, string? detail = null) => diagnostics.Add(new PendingDiagnostic(code, at, detail));

    private void Add(XferTokenKind kind, int start, int firstTrivia, int triviaCount, XferStringForm form = XferStringForm.None, int run = 0, XferKeyword keyword = XferKeyword.None)
    {
        tokens.Add(new Token(kind, start, index - start, firstTrivia, triviaCount, form, run, keyword));
    }

    // Trivia.

    private void ReadTrivia()
    {
        while (true)
        {
            int c = Code();
            if (c >= 0 && Unicode.IsWhitespace(c))
            {
                int start = index;
                while (Unicode.IsWhitespace(Code()))
                {
                    index++;
                }

                trivia.Add(new Trivia(XferTriviaKind.Whitespace, start, index - start));
            }
            else if (c == LessThan && Code(index + 1) == Slash)
            {
                ReadComment();
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>Specification 4.1: closes at the first run of exactly n slashes that is followed by <c>&gt;</c>.</summary>
    private void ReadComment()
    {
        int start = index;
        index++;
        int n = RunOf(Slash);
        if (n > limits.RunLength)
        {
            Report(XferDiagnosticCode.XFER0604, start, $"a run of {n} slashes");
        }

        while (true)
        {
            int c = Code();
            if (c < 0)
            {
                Report(XferDiagnosticCode.XFER0104, start);
                break;
            }

            if (c == Slash)
            {
                if (RunOf(Slash) == n && Code() == GreaterThan)
                {
                    index++;
                    break;
                }
            }
            else
            {
                Advance();
            }
        }

        trivia.Add(new Trivia(XferTriviaKind.Comment, start, index - start, n));
    }

    // Tokens.

    private void ReadToken(int firstTrivia, int triviaCount)
    {
        int start = index;
        int c = Code();
        switch (c)
        {
            case LeftBrace:
                index++;
                Add(XferTokenKind.LeftBrace, start, firstTrivia, triviaCount);
                return;
            case RightBrace:
                index++;
                Add(XferTokenKind.RightBrace, start, firstTrivia, triviaCount);
                return;
            case LeftBracket:
                index++;
                Add(XferTokenKind.LeftBracket, start, firstTrivia, triviaCount);
                return;
            case RightBracket:
                index++;
                Add(XferTokenKind.RightBracket, start, firstTrivia, triviaCount);
                return;
            case LessThan:
                // "</" was taken as a comment, so this "<" begins an annotation or nothing.
                index++;
                if (Code() == Bang)
                {
                    index++;
                    Add(XferTokenKind.AnnotationOpen, start, firstTrivia, triviaCount);
                    return;
                }

                Report(XferDiagnosticCode.XFER0102, start);
                Add(XferTokenKind.Error, start, firstTrivia, triviaCount);
                return;
            case Bang:
                if (Code(index + 1) == GreaterThan)
                {
                    index += 2;
                    Add(XferTokenKind.AnnotationClose, start, firstTrivia, triviaCount);
                    return;
                }

                Unexpected(start, firstTrivia, triviaCount);
                return;
            case Quote:
                RepetitionString(start, firstTrivia, triviaCount);
                return;
            case Hash:
                HashString(start, firstTrivia, triviaCount);
                return;
            default:
                break;
        }

        if (IsDecimal(c) || c == Minus)
        {
            Number(start, firstTrivia, triviaCount);
            return;
        }

        if (Unicode.IsIdentifierStart(Point()))
        {
            Word(start, firstTrivia, triviaCount);
            return;
        }

        Unexpected(start, firstTrivia, triviaCount);
    }

    /// <summary>Finishes a scalar token: the token-length limit, then the separation rule.</summary>
    private void Scalar(XferTokenKind kind, int start, int firstTrivia, int triviaCount, XferStringForm form = XferStringForm.None, int run = 0, XferKeyword keyword = XferKeyword.None)
    {
        Add(kind, start, firstTrivia, triviaCount, form, run, keyword);
        if (index - start > limits.TokenBytes)
        {
            Report(XferDiagnosticCode.XFER0603, start);
        }

        if (!AtTerminator())
        {
            int errorStart = index;
            Report(XferDiagnosticCode.XFER0101, errorStart, Found(Point()));
            while (!AtTerminator())
            {
                Advance();
            }

            // The text that ran on is one error token with no trivia of its own.
            Add(XferTokenKind.Error, errorStart, trivia.Count, 0);
        }
    }

    /// <summary>Reports a malformed token and makes everything from its start to the next terminator one error token.</summary>
    private void Malformed(XferDiagnosticCode code, int start, int firstTrivia, int triviaCount, string? detail = null)
    {
        Report(code, start, detail);
        while (!AtTerminator())
        {
            Advance();
        }

        Add(XferTokenKind.Error, start, firstTrivia, triviaCount);
    }

    private void Unexpected(int start, int firstTrivia, int triviaCount)
    {
        // The character is not a terminator (dispatch handled those), so this always advances.
        Malformed(XferDiagnosticCode.XFER0103, start, firstTrivia, triviaCount, Found(Point()));
    }

    // Strings (specification 4.4).

    private void RepetitionString(int start, int firstTrivia, int triviaCount)
    {
        int n = RunOf(Quote);
        if (n > limits.RunLength)
        {
            Report(XferDiagnosticCode.XFER0604, start, $"a run of {n} quotes");
        }

        // A run of exactly two quotes is the empty string, whatever follows; it never opens a
        // string, so ""x"" is the empty string and then a separation error at x.
        if (n == 2)
        {
            Scalar(XferTokenKind.String, start, firstTrivia, triviaCount, XferStringForm.Repetition, 1);
            return;
        }

        while (true)
        {
            int c = Code();
            if (c < 0)
            {
                Report(XferDiagnosticCode.XFER0201, start, $"opened with a run of {n} {(n == 1 ? "quote" : "quotes")}");
                Add(XferTokenKind.Error, start, firstTrivia, triviaCount);
                return;
            }

            if (c == Quote)
            {
                if (RunOf(Quote) == n)
                {
                    Scalar(XferTokenKind.String, start, firstTrivia, triviaCount, XferStringForm.Repetition, n);
                    return;
                }
            }
            else
            {
                Advance();
            }
        }
    }

    private void HashString(int start, int firstTrivia, int triviaCount)
    {
        int m = RunOf(Hash);
        if (Code() != Quote)
        {
            // Only the hashes are in error; lexing continues after them.
            Report(XferDiagnosticCode.XFER0202, start);
            Add(XferTokenKind.Error, start, firstTrivia, triviaCount);
            return;
        }

        if (m > limits.RunLength)
        {
            Report(XferDiagnosticCode.XFER0604, start, $"a run of {m} hashes");
        }

        index++;
        while (true)
        {
            int c = Code();
            if (c < 0)
            {
                Report(XferDiagnosticCode.XFER0201, start, $"opened with {new string('#', Math.Min(m, 8))}\"");
                Add(XferTokenKind.Error, start, firstTrivia, triviaCount);
                return;
            }

            if (c == Quote)
            {
                index++;
                if (RunOf(Hash) == m)
                {
                    Scalar(XferTokenKind.String, start, firstTrivia, triviaCount, XferStringForm.Hash, m);
                    return;
                }
            }
            else
            {
                Advance();
            }
        }
    }

    // Numbers and temporals (specification 4.5 and 4.6).

    private static bool IsDecimal(int c) => c is >= 0x30 and <= 0x39;

    private static bool IsHex(int c) => IsDecimal(c) || c is (>= 0x41 and <= 0x46) or (>= 0x61 and <= 0x66);

    private static bool IsOctal(int c) => c is >= 0x30 and <= 0x37;

    private static bool IsBinary(int c) => c is 0x30 or 0x31;

    /// <summary>A run of digits in which a single <c>_</c> may stand between two digits.</summary>
    private (int Count, bool Underscore, bool Bad) Digits(Func<int, bool> isDigit)
    {
        int count = 0;
        bool underscore = false;
        while (true)
        {
            int c = Code();
            if (isDigit(c))
            {
                index++;
                count++;
            }
            else if (c == Underscore)
            {
                bool between = count > 0 && isDigit(Code(index - 1)) && isDigit(Code(index + 1));
                if (!between)
                {
                    return (count, underscore, true);
                }

                index++;
                underscore = true;
            }
            else
            {
                return (count, underscore, false);
            }
        }
    }

    private bool StartsWithInf() => Code(index) == 'i' && Code(index + 1) == 'n' && Code(index + 2) == 'f';

    private void Number(int start, int firstTrivia, int triviaCount)
    {
        bool signed = false;
        if (Code() == Minus)
        {
            index++;
            signed = true;

            // -inf is a complete token wherever it appears; -infinity fails the separation rule.
            if (StartsWithInf())
            {
                index += 3;
                Scalar(XferTokenKind.Keyword, start, firstTrivia, triviaCount, keyword: XferKeyword.MinusInf);
                return;
            }

            if (!IsDecimal(Code()))
            {
                Malformed(XferDiagnosticCode.XFER0302, start, firstTrivia, triviaCount, "a minus sign must be followed by digits or inf");
                return;
            }
        }

        if (Code() == Zero)
        {
            int letter = Code(index + 1);
            Func<int, bool>? radixDigit = letter switch
            {
                'x' => IsHex,
                'o' => IsOctal,
                'b' => IsBinary,
                _ => null,
            };
            if (radixDigit is not null)
            {
                index += 2;
                (int count, _, bool bad) = Digits(radixDigit);
                if (count == 0 || bad)
                {
                    Malformed(XferDiagnosticCode.XFER0302, start, firstTrivia, triviaCount, "a radix prefix must be followed by digits of that radix");
                    return;
                }

                Scalar(XferTokenKind.Integer, start, firstTrivia, triviaCount);
                return;
            }

            if (letter is 'X' or 'O' or 'B')
            {
                Malformed(XferDiagnosticCode.XFER0302, start, firstTrivia, triviaCount, "radix prefixes are lowercase: 0x, 0o, 0b");
                return;
            }
        }

        (int wholeCount, bool wholeUnderscore, bool wholeBad) = Digits(IsDecimal);
        if (wholeBad)
        {
            Malformed(XferDiagnosticCode.XFER0302, start, firstTrivia, triviaCount, "an underscore must stand between two digits");
            return;
        }

        // A temporal takes no sign and no digit separators. It is checked before the leading-zero
        // rule, because years and hours may begin with 0.
        if (!signed && !wholeUnderscore)
        {
            if (wholeCount == 4 && Code() == Minus)
            {
                Temporal(start, firstTrivia, triviaCount, dated: true);
                return;
            }

            if (wholeCount == 2 && Code() == Colon)
            {
                Temporal(start, firstTrivia, triviaCount, dated: false);
                return;
            }
        }

        int firstDigit = Code(signed ? start + 1 : start);
        if (wholeCount > 1 && firstDigit == Zero)
        {
            Malformed(XferDiagnosticCode.XFER0301, start, firstTrivia, triviaCount);
            return;
        }

        XferTokenKind kind = XferTokenKind.Integer;
        if (Code() == Dot)
        {
            index++;
            (int count, _, bool bad) = Digits(IsDecimal);
            if (count == 0 || bad)
            {
                Malformed(XferDiagnosticCode.XFER0302, start, firstTrivia, triviaCount, "a point must be followed by digits");
                return;
            }

            kind = XferTokenKind.Fractional;
        }

        int e = Code();
        if (e is LowerE or UpperE)
        {
            index++;
            int sign = Code();
            if (sign is Plus or Minus)
            {
                index++;
            }

            (int count, _, bool bad) = Digits(IsDecimal);
            if (count == 0 || bad)
            {
                Malformed(XferDiagnosticCode.XFER0302, start, firstTrivia, triviaCount, "an exponent must have digits");
                return;
            }

            kind = XferTokenKind.Fractional;
        }

        Scalar(kind, start, firstTrivia, triviaCount);
    }

    /// <summary>Two decimal digits as a number, consumed; -1 (consuming nothing) if they are not there.</summary>
    private int TwoDigits()
    {
        int a = Code();
        int b = Code(index + 1);
        if (!IsDecimal(a) || !IsDecimal(b))
        {
            return -1;
        }

        index += 2;
        return ((a - Zero) * 10) + (b - Zero);
    }

    /// <summary><c>:MM:SS</c> with an optional fraction, from a <c>:</c>. Null if the shape is wrong.</summary>
    private (int Minute, int Second)? TimeRest()
    {
        if (Code() != Colon)
        {
            return null;
        }

        index++;
        int minute = TwoDigits();
        if (minute < 0 || Code() != Colon)
        {
            return null;
        }

        index++;
        int second = TwoDigits();
        if (second < 0)
        {
            return null;
        }

        if (Code() == Dot)
        {
            index++;
            if (!IsDecimal(Code()))
            {
                return null;
            }

            while (IsDecimal(Code()))
            {
                index++;
            }
        }

        return (minute, second);
    }

    private static int DaysIn(int year, int month)
    {
        if (month == 2)
        {
            bool leap = (year % 4 == 0 && year % 100 != 0) || year % 400 == 0;
            return leap ? 29 : 28;
        }

        return month is 4 or 6 or 9 or 11 ? 30 : 31;
    }

    private static bool ValidDate(int year, int month, int day) => month is >= 1 and <= 12 && day >= 1 && day <= DaysIn(year, month);

    // Second 60 is a leap second, which RFC 3339 permits.
    private static bool ValidTime(int hour, int minute, int second) => hour <= 23 && minute <= 59 && second <= 60;

    /// <summary>
    /// A date or date-time (<paramref name="dated"/>, at the <c>-</c> after a four-digit year) or a
    /// time (at the <c>:</c> after a two-digit hour). A wrong shape is XFER0304; a right shape
    /// that denotes no instant is XFER0305.
    /// </summary>
    private void Temporal(int start, int firstTrivia, int triviaCount, bool dated)
    {
        XferTokenKind kind;
        bool valid;

        if (dated)
        {
            int year = ((text[start] - Zero) * 1000) + ((text[start + 1] - Zero) * 100) + ((text[start + 2] - Zero) * 10) + (text[start + 3] - Zero);
            index++;
            int month = TwoDigits();
            bool dash = month >= 0 && Code() == Minus;
            if (dash)
            {
                index++;
            }

            int day = dash ? TwoDigits() : -1;
            if (day < 0)
            {
                Malformed(XferDiagnosticCode.XFER0304, start, firstTrivia, triviaCount, "a date is YYYY-MM-DD");
                return;
            }

            valid = ValidDate(year, month, day);
            kind = XferTokenKind.Date;
            if (Code() == UpperT)
            {
                index++;
                int hour = TwoDigits();
                (int Minute, int Second)? time = hour < 0 ? null : TimeRest();
                if (time is null)
                {
                    Malformed(XferDiagnosticCode.XFER0304, start, firstTrivia, triviaCount, "a date-time is YYYY-MM-DDTHH:MM:SS");
                    return;
                }

                valid = valid && ValidTime(hour, time.Value.Minute, time.Value.Second);
                kind = XferTokenKind.LocalDateTime;
                int o = Code();
                if (o == UpperZ)
                {
                    index++;
                    kind = XferTokenKind.DateTime;
                }
                else if (o is Plus or Minus)
                {
                    index++;
                    int offsetHour = TwoDigits();
                    bool colon = offsetHour >= 0 && Code() == Colon;
                    if (colon)
                    {
                        index++;
                    }

                    int offsetMinute = colon ? TwoDigits() : -1;
                    if (offsetMinute < 0)
                    {
                        Malformed(XferDiagnosticCode.XFER0304, start, firstTrivia, triviaCount, "an offset is Z, +HH:MM or -HH:MM");
                        return;
                    }

                    valid = valid && offsetHour <= 23 && offsetMinute <= 59;
                    kind = XferTokenKind.DateTime;
                }
            }
        }
        else
        {
            int hour = ((text[start] - Zero) * 10) + (text[start + 1] - Zero);
            (int Minute, int Second)? time = TimeRest();
            if (time is null)
            {
                Malformed(XferDiagnosticCode.XFER0304, start, firstTrivia, triviaCount, "a time is HH:MM:SS");
                return;
            }

            valid = ValidTime(hour, time.Value.Minute, time.Value.Second);
            kind = XferTokenKind.Time;
        }

        if (!valid)
        {
            Report(XferDiagnosticCode.XFER0305, start);
            Scalar(XferTokenKind.Error, start, firstTrivia, triviaCount);
            return;
        }

        Scalar(kind, start, firstTrivia, triviaCount);
    }

    // Identifiers and keywords (specification 4.2 and 4.3).

    private void Word(int start, int firstTrivia, int triviaCount)
    {
        Advance();
        while (Unicode.IsIdentifierContinue(Point()))
        {
            Advance();
        }

        if (text[index - 1] == Dot)
        {
            Report(XferDiagnosticCode.XFER0105, start);
        }

        XferKeyword keyword = KeywordOf(text.AsSpan(start, index - start));
        if (keyword != XferKeyword.None)
        {
            Scalar(XferTokenKind.Keyword, start, firstTrivia, triviaCount, keyword: keyword);
            return;
        }

        Scalar(XferTokenKind.Identifier, start, firstTrivia, triviaCount);
    }

    /// <summary>
    /// The bare words that are not symbols (specification 4.2). <c>-inf</c> is also a keyword, but
    /// it begins with <c>-</c>, which no identifier may, so it is lexed as a number.
    /// </summary>
    internal static XferKeyword KeywordOf(ReadOnlySpan<byte> word)
    {
        return word switch
        {
            [(byte)'t', (byte)'r', (byte)'u', (byte)'e'] => XferKeyword.True,
            [(byte)'f', (byte)'a', (byte)'l', (byte)'s', (byte)'e'] => XferKeyword.False,
            [(byte)'n', (byte)'u', (byte)'l', (byte)'l'] => XferKeyword.Null,
            [(byte)'i', (byte)'n', (byte)'f'] => XferKeyword.Inf,
            [(byte)'n', (byte)'a', (byte)'n'] => XferKeyword.Nan,
            _ => XferKeyword.None,
        };
    }

    // Messages.

    /// <summary>The detail for a diagnostic about an unexpected character, with a hint when there is one.</summary>
    private static string Found(int codePoint)
    {
        string quoted = Quoted(codePoint);
        return codePoint >= 0 && Hints.TryGetValue(codePoint, out string? hint)
            ? $"found {quoted}; {hint}"
            : $"found {quoted}";
    }

    /// <summary>A character written as JavaScript's <c>JSON.stringify</c> writes it, so messages match xfer-js.</summary>
    private static string Quoted(int codePoint)
    {
        if (codePoint < 0)
        {
            return "end of input";
        }

        string escaped = codePoint switch
        {
            '"' => "\\\"",
            '\\' => "\\\\",
            '\b' => "\\b",
            '\f' => "\\f",
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            < 0x20 => "\\u" + codePoint.ToString("x4", CultureInfo.InvariantCulture),
            _ => char.ConvertFromUtf32(codePoint),
        };
        return "\"" + escaped + "\"";
    }
}
