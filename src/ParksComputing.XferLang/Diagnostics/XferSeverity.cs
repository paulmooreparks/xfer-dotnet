namespace ParksComputing.XferLang;

/// <summary>How serious a diagnostic is. The specification calls <see cref="Info"/> "informational".</summary>
public enum XferSeverity
{
    /// <summary>The document or conversion is wrong; no value is produced.</summary>
    Error,

    /// <summary>Something was accepted that the caller may not want, such as a duplicate JSON key.</summary>
    Warning,

    /// <summary>A note that a conversion changed a type, such as a symbol written as a JSON string.</summary>
    Info,
}
