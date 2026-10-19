using System;

namespace ParksComputing.XferLang;

/// <summary>
/// The limits a parser enforces (specification 3.4). Each defaults to the specification's minimum,
/// which every conformant parser must accept in its default configuration. A deployment may choose
/// stricter limits, and a caller may raise a limit as well as lower it; a value of zero or less is
/// refused when it is set.
/// </summary>
public sealed record XferLimits
{
    private int documentBytes = 268_435_455;
    private int depth = 256;
    private int tokenBytes = 268_435_455;
    private int runLength = 64;
    private int elements = int.MaxValue;

    /// <summary>The limits at the specification's minimums.</summary>
    public static XferLimits Default { get; } = new();

    /// <summary>The largest document, in bytes as supplied. Diagnostic <c>XFER0601</c>.</summary>
    public int DocumentBytes
    {
        get => documentBytes;
        init => documentBytes = Positive(value, nameof(DocumentBytes));
    }

    /// <summary>The deepest nesting of objects and sequences, the root included. Diagnostic <c>XFER0602</c>.</summary>
    public int Depth
    {
        get => depth;
        init => depth = Positive(value, nameof(Depth));
    }

    /// <summary>The longest scalar token, in bytes. Diagnostic <c>XFER0603</c>.</summary>
    public int TokenBytes
    {
        get => tokenBytes;
        init => tokenBytes = Positive(value, nameof(TokenBytes));
    }

    /// <summary>The longest run of quotes, hashes or slashes in a delimiter. Diagnostic <c>XFER0604</c>.</summary>
    public int RunLength
    {
        get => runLength;
        init => runLength = Positive(value, nameof(RunLength));
    }

    /// <summary>The most elements in a document. Diagnostic <c>XFER0605</c>.</summary>
    public int Elements
    {
        get => elements;
        init => elements = Positive(value, nameof(Elements));
    }

    private static int Positive(int value, string name)
    {
        return value > 0 ? value : throw new ArgumentOutOfRangeException(name, value, "a limit must be greater than zero");
    }
}
