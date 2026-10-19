using System;
using System.Globalization;

namespace ParksComputing.XferLang;

/// <summary>Facts about the diagnostic codes of specification section 10.</summary>
public static partial class XferDiagnostics
{
    /// <summary>The severity the specification gives a code.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined code.</exception>
    public static XferSeverity SeverityOf(XferDiagnosticCode code) => Lookup(code).Severity;

    /// <summary>The specification's one-line meaning of a code.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined code.</exception>
    public static string SummaryOf(XferDiagnosticCode code) => Lookup(code).Summary;

    /// <summary>Writes a code as the specification does, for example <c>XFER0404</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined code.</exception>
    public static string Format(XferDiagnosticCode code)
    {
        _ = Lookup(code); // refuses a value that is not a defined code, as SeverityOf and SummaryOf do
        return "XFER" + ((int)code).ToString("D4", CultureInfo.InvariantCulture);
    }

    /// <summary>Whether a text such as <c>XFER0404</c> is a defined code, and which.</summary>
    public static bool TryParse(string? text, out XferDiagnosticCode code)
    {
        code = default;
        if (text is { Length: 8 } && text.StartsWith("XFER", StringComparison.Ordinal)
            && int.TryParse(text.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            && Table.ContainsKey((XferDiagnosticCode)number))
        {
            code = (XferDiagnosticCode)number;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Builds a diagnostic. The message is the code's summary, then <c>: </c> and the detail when
    /// the caller has specifics to add. <paramref name="end"/> defaults to <paramref name="start"/>.
    /// </summary>
    internal static XferDiagnostic Create(XferDiagnosticCode code, XferPosition start, XferPosition? end = null, string? detail = null)
    {
        (XferSeverity severity, string summary) = Lookup(code);
        string message = detail is null ? summary : summary + ": " + detail;
        return new XferDiagnostic(code, severity, message, start, end ?? start);
    }

    internal static int Count => Table.Count;

    internal static System.Collections.Generic.IEnumerable<XferDiagnosticCode> All => Table.Keys;

    private static (XferSeverity Severity, string Summary) Lookup(XferDiagnosticCode code)
    {
        return Table.TryGetValue(code, out (XferSeverity, string) entry)
            ? entry
            : throw new ArgumentOutOfRangeException(nameof(code), code, "not a diagnostic code defined by the specification");
    }
}
