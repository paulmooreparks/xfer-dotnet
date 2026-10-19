namespace ParksComputing.XferLang;

/// <summary>A fault, or a note, found in a document or a conversion.</summary>
/// <param name="Code">The stable code from specification section 10.</param>
/// <param name="Severity">How serious it is.</param>
/// <param name="Message">An English sentence: the code's summary, then a colon and the specifics when there are any.</param>
/// <param name="Start">Where it begins.</param>
/// <param name="End">Where it ends, exclusive. Equal to <paramref name="Start"/> for a point diagnostic.</param>
public sealed record XferDiagnostic(
    XferDiagnosticCode Code,
    XferSeverity Severity,
    string Message,
    XferPosition Start,
    XferPosition End)
{
    /// <summary>The code in its written form, for example <c>XFER0404</c>.</summary>
    public string CodeText => XferDiagnostics.Format(Code);
}
