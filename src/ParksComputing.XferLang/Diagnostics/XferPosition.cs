namespace ParksComputing.XferLang;

/// <summary>
/// A place in the normalized text of a document (specification 9): after the byte order mark is
/// removed and line endings are replaced by LF.
/// </summary>
/// <param name="Offset">The offset in UTF-8 bytes, counted from 0.</param>
/// <param name="Line">The line, counted from 1.</param>
/// <param name="Column">The column, counted from 1 in scalar values (not bytes or UTF-16 units).</param>
public readonly record struct XferPosition(int Offset, int Line, int Column)
{
    /// <summary>The start of a document: offset 0, line 1, column 1.</summary>
    public static XferPosition Origin => new(0, 1, 1);
}
