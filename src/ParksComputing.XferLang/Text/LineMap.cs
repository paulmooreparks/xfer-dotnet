using System;

namespace ParksComputing.XferLang.Text;

/// <summary>
/// Turns byte offsets in the normalized text into positions (specification 9). The text is the
/// normalized UTF-8, so a line ends at every LF and a column counts scalar values from the start
/// of its line. Built once during normalization; no position is stored per token.
/// </summary>
internal sealed class LineMap
{
    /// <summary>The map of an empty text.</summary>
    public static readonly LineMap Empty = new([], [0]);

    private readonly byte[] text;
    private readonly int[] lineStarts;

    public LineMap(byte[] text, int[] lineStarts)
    {
        this.text = text;
        this.lineStarts = lineStarts;
    }

    /// <summary>The line map of a normalized text (LF line endings).</summary>
    public static LineMap Create(byte[] text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new LineMap(text, LineStarts(text));
    }

    private static int[] LineStarts(byte[] text)
    {
        // Count first and allocate once: a growing list would hold about three times the memory
        // at the specification's minimum document size.
        var starts = new int[text.AsSpan().Count((byte)0x0A) + 1];
        int line = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == 0x0A)
            {
                starts[line++] = i + 1;
            }
        }

        return starts;
    }

    /// <summary>The number of lines: one more than the number of line feeds.</summary>
    public int LineCount => lineStarts.Length;

    /// <summary>
    /// The position of one offset. An offset past the end gives the end. An offset inside a
    /// multi-byte character gives the position of that character, never a position after it.
    /// This costs a binary search and a count along one line, so it is for a few positions; use
    /// <see cref="Resolve"/> for many.
    /// </summary>
    public XferPosition GetPosition(int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        offset = BackUpToCharacterStart(offset);
        int line = LineIndex(offset);
        int start = lineStarts[line];
        return new XferPosition(offset, line + 1, 1 + CountScalarValues(start, offset));
    }

    /// <summary>
    /// The positions of offsets given in ascending order, in one forward pass over the text, so
    /// that a document with many faults on one long line does not cost a scan of that line each.
    /// </summary>
    public XferPosition[] Resolve(ReadOnlySpan<int> sortedOffsets)
    {
        var positions = new XferPosition[sortedOffsets.Length];
        int line = 0;
        int cursor = 0;
        int column = 1;
        int previous = -1;
        for (int i = 0; i < sortedOffsets.Length; i++)
        {
            int offset = BackUpToCharacterStart(Math.Clamp(sortedOffsets[i], 0, text.Length));
            if (offset < previous)
            {
                throw new ArgumentException("offsets must be in ascending order", nameof(sortedOffsets));
            }

            previous = offset;
            while (line + 1 < lineStarts.Length && lineStarts[line + 1] <= offset)
            {
                line++;
                cursor = lineStarts[line];
                column = 1;
            }

            // Advance from the cursor (a character start at or after the line start) to the offset.
            for (; cursor < offset; cursor++)
            {
                if (!IsContinuation(text[cursor]))
                {
                    column++;
                }
            }

            positions[i] = new XferPosition(offset, line + 1, column);
        }

        return positions;
    }

    private static bool IsContinuation(byte value) => (value & 0xC0) == 0x80;

    private int BackUpToCharacterStart(int offset)
    {
        while (offset > 0 && offset < text.Length && IsContinuation(text[offset]))
        {
            offset--;
        }

        return offset;
    }

    private int LineIndex(int offset)
    {
        // The last line start that is at or before the offset.
        int low = 0;
        int high = lineStarts.Length - 1;
        while (low < high)
        {
            int middle = low + ((high - low + 1) / 2);
            if (lineStarts[middle] <= offset)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    private int CountScalarValues(int from, int to)
    {
        int count = 0;
        for (int i = from; i < to; i++)
        {
            if (!IsContinuation(text[i]))
            {
                count++;
            }
        }

        return count;
    }
}
