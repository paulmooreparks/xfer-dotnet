using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace ParksComputing.XferLang.Text;

/// <summary>What normalization produced: the text every later stage reads, or the reason it could not.</summary>
/// <param name="Text">The normalized UTF-8: no byte order mark, LF line endings. Empty when <paramref name="Fault"/> is set.</param>
/// <param name="Lines">Positions in <paramref name="Text"/>.</param>
/// <param name="Fault">The one diagnostic that stopped normalization, or null.</param>
/// <param name="InputBytes">The size of the input as supplied, before the byte order mark and line endings are touched.</param>
internal sealed record NormalizeResult(byte[] Text, LineMap Lines, XferDiagnostic? Fault, int InputBytes);

/// <summary>
/// Turns supplied input into the normalized text every later stage works on (specification 3.1
/// and 3.2): valid UTF-8, one leading byte order mark removed, and LF line endings.
/// </summary>
/// <remarks>
/// Invalid UTF-8 is reported at the first byte that is not part of a valid sequence (specification
/// 9): its offset is the raw byte offset counted from after the byte order mark (so, unlike every
/// other offset, it is not an offset in the normalized text when a CR LF precedes it), and its
/// line and column are as for the normalized text before it. A lone surrogate in a string is reported where it is in the
/// normalized text.
/// </remarks>
internal static class Normalizer
{
    private const char ByteOrderMark = '﻿';

    public static NormalizeResult Normalize(ReadOnlySpan<byte> input, XferLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        int inputBytes = input.Length;
        if (inputBytes > limits.DocumentBytes)
        {
            return Rejected(XferDiagnosticCode.XFER0601, inputBytes);
        }

        ReadOnlySpan<byte> body = HasByteOrderMark(input) ? input[3..] : input;

        int bad = FirstInvalidByte(body);
        if (bad >= 0)
        {
            return Rejected(XferDiagnosticCode.XFER0701, inputBytes, PositionBeforeNormalization(body, bad, bad));
        }

        try
        {
            return Build(body, inputBytes);
        }
        catch (OutOfMemoryException)
        {
            // The platform cannot hold the document: a size problem, not an encoding one.
            return Rejected(XferDiagnosticCode.XFER0601, inputBytes, detail: "larger than this platform can hold");
        }
    }

    public static NormalizeResult Normalize(string input, XferLimits limits)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);
        long size = Unicode.Utf8ByteLength(input);
        if (size > limits.DocumentBytes)
        {
            return Rejected(XferDiagnosticCode.XFER0601, (int)Math.Min(size, int.MaxValue));
        }

        int inputBytes = (int)size;
        string unmarked = input.Length > 0 && input[0] == ByteOrderMark ? input[1..] : input;

        XferPosition? lone = FirstLoneSurrogate(unmarked);
        if (lone is { } at)
        {
            return Rejected(XferDiagnosticCode.XFER0701, inputBytes, at);
        }

        try
        {
            // Encode first (a lone surrogate is already excluded, so this cannot fail), then share
            // the byte path's single-pass line-ending rewrite instead of making string copies.
            byte[] utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(unmarked);
            return Build(utf8, inputBytes);
        }
        catch (OutOfMemoryException)
        {
            return Rejected(XferDiagnosticCode.XFER0601, inputBytes, detail: "larger than this platform can hold");
        }
    }

    private static NormalizeResult Rejected(XferDiagnosticCode code, int inputBytes, XferPosition? at = null, string? detail = null)
    {
        return new NormalizeResult([], LineMap.Empty, XferDiagnostics.Create(code, at ?? XferPosition.Origin, null, detail), inputBytes);
    }

    private static bool HasByteOrderMark(ReadOnlySpan<byte> input) =>
        input.Length >= 3 && input[0] == 0xEF && input[1] == 0xBB && input[2] == 0xBF;

    /// <summary>The offset of the first byte that is not part of valid UTF-8, or -1 if all of it is.</summary>
    private static int FirstInvalidByte(ReadOnlySpan<byte> bytes)
    {
        if (System.Text.Unicode.Utf8.IsValid(bytes))
        {
            return -1;
        }

        // IsValid says where not, so find it with the documented decoder: each call reports the
        // status for the sequence at the start of the span and how many bytes it covers.
        int offset = 0;
        while (offset < bytes.Length)
        {
            OperationStatus status = Rune.DecodeFromUtf8(bytes[offset..], out _, out int consumed);
            if (status != OperationStatus.Done)
            {
                return offset; // InvalidData, or NeedMoreData for a sequence cut off by the end
            }

            offset += consumed;
        }

        return 0; // unreachable when IsValid is false; a defensive answer that is still an error
    }

    /// <summary>
    /// The position of a byte offset as the specification wants an invalid-UTF-8 fault reported:
    /// the offset as given, the line and column as the normalized text before it would count them.
    /// </summary>
    private static XferPosition PositionBeforeNormalization(ReadOnlySpan<byte> body, int rawOffset, int upTo)
    {
        int line = 1;
        int column = 1;
        for (int i = 0; i < upTo; i++)
        {
            byte value = body[i];
            if (value == 0x0D)
            {
                line++;
                column = 1;
                if (i + 1 < upTo && body[i + 1] == 0x0A)
                {
                    i++; // CR LF is one line end
                }
            }
            else if (value == 0x0A)
            {
                line++;
                column = 1;
            }
            else if ((value & 0xC0) != 0x80)
            {
                column++;
            }
        }

        return new XferPosition(rawOffset, line, column);
    }

    private static NormalizeResult Build(ReadOnlySpan<byte> body, int inputBytes)
    {
        int crCount = body.Count((byte)0x0D);
        byte[] text;
        if (crCount == 0)
        {
            text = body.ToArray();
        }
        else
        {
            // Every CR LF and every lone CR becomes one LF: the length shrinks by one per CR LF.
            int crlf = 0;
            for (int i = 0; i + 1 < body.Length; i++)
            {
                if (body[i] == 0x0D && body[i + 1] == 0x0A)
                {
                    crlf++;
                }
            }

            text = new byte[body.Length - crlf];
            int written = 0;
            for (int i = 0; i < body.Length; i++)
            {
                byte value = body[i];
                if (value == 0x0D)
                {
                    text[written++] = 0x0A;
                    if (i + 1 < body.Length && body[i + 1] == 0x0A)
                    {
                        i++;
                    }
                }
                else
                {
                    text[written++] = value;
                }
            }
        }

        return new NormalizeResult(text, LineMap.Create(text), null, inputBytes);
    }

    /// <summary>
    /// The position, in the normalized text, of the first lone surrogate in a string, or null.
    /// The text is walked as normalization would leave it: CR LF and lone CR each one LF.
    /// </summary>
    private static XferPosition? FirstLoneSurrogate(string text)
    {
        int offset = 0;
        int line = 1;
        int column = 1;
        for (int i = 0; i < text.Length; i++)
        {
            char unit = text[i];
            if (char.IsHighSurrogate(unit))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                    offset += 4;
                    column++;
                    continue;
                }

                return new XferPosition(offset, line, column);
            }

            if (char.IsLowSurrogate(unit))
            {
                return new XferPosition(offset, line, column);
            }

            if (unit == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                offset += 1;
                line++;
                column = 1;
            }
            else if (unit == '\n')
            {
                offset += 1;
                line++;
                column = 1;
            }
            else
            {
                offset += unit < 0x80 ? 1 : unit < 0x800 ? 2 : 3;
                column++;
            }
        }

        return null;
    }
}
