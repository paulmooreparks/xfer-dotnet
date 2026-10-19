using System.Collections.Frozen;
using System.Collections.Generic;

namespace ParksComputing.XferLang;

public static partial class XferDiagnostics
{
    // Transcribed from the table in specification section 10. A test compares it with the
    // specification text row for row, so a change to one without the other fails the build.
    private static readonly FrozenDictionary<XferDiagnosticCode, (XferSeverity Severity, string Summary)> Table =
        new Dictionary<XferDiagnosticCode, (XferSeverity, string)>
        {
            [XferDiagnosticCode.XFER0101] = (XferSeverity.Error, "scalar token not followed by a separator"),
            [XferDiagnosticCode.XFER0102] = (XferSeverity.Error, "`<` not followed by `/` or `!`"),
            [XferDiagnosticCode.XFER0103] = (XferSeverity.Error, "character cannot begin a token"),
            [XferDiagnosticCode.XFER0104] = (XferSeverity.Error, "unterminated comment"),
            [XferDiagnosticCode.XFER0105] = (XferSeverity.Error, "identifier ends with `.`"),
            [XferDiagnosticCode.XFER0201] = (XferSeverity.Error, "unterminated string"),
            [XferDiagnosticCode.XFER0202] = (XferSeverity.Error, "`#` not followed by `#` or `\"`"),
            [XferDiagnosticCode.XFER0301] = (XferSeverity.Error, "leading zero on a decimal integer"),
            [XferDiagnosticCode.XFER0302] = (XferSeverity.Error, "malformed number"),
            [XferDiagnosticCode.XFER0303] = (XferSeverity.Error, "number out of range for the requested type (conversion, not parsing)"),
            [XferDiagnosticCode.XFER0304] = (XferSeverity.Error, "malformed temporal"),
            [XferDiagnosticCode.XFER0305] = (XferSeverity.Error, "temporal denotes no valid instant"),
            [XferDiagnosticCode.XFER0401] = (XferSeverity.Error, "document has no element"),
            [XferDiagnosticCode.XFER0402] = (XferSeverity.Error, "content after the root element"),
            [XferDiagnosticCode.XFER0403] = (XferSeverity.Error, "duplicate key"),
            [XferDiagnosticCode.XFER0404] = (XferSeverity.Error, "object body has a key without a value"),
            [XferDiagnosticCode.XFER0405] = (XferSeverity.Error, "unterminated object or sequence"),
            [XferDiagnosticCode.XFER0406] = (XferSeverity.Error, "unexpected closing bracket"),
            [XferDiagnosticCode.XFER0407] = (XferSeverity.Error, "token in key position is not an identifier or string"),
            [XferDiagnosticCode.XFER0501] = (XferSeverity.Error, "annotation value carries an annotation"),
            [XferDiagnosticCode.XFER0502] = (XferSeverity.Error, "annotation has no target"),
            [XferDiagnosticCode.XFER0503] = (XferSeverity.Error, "`xfer` annotation is not the first token"),
            [XferDiagnosticCode.XFER0504] = (XferSeverity.Error, "unsupported major version"),
            [XferDiagnosticCode.XFER0505] = (XferSeverity.Error, "duplicate `id`"),
            [XferDiagnosticCode.XFER0506] = (XferSeverity.Error, "unknown annotation"),
            [XferDiagnosticCode.XFER0601] = (XferSeverity.Error, "document exceeds size limit"),
            [XferDiagnosticCode.XFER0602] = (XferSeverity.Error, "nesting exceeds depth limit"),
            [XferDiagnosticCode.XFER0603] = (XferSeverity.Error, "scalar token exceeds length limit"),
            [XferDiagnosticCode.XFER0604] = (XferSeverity.Error, "delimiter run exceeds length limit"),
            [XferDiagnosticCode.XFER0605] = (XferSeverity.Error, "element count exceeds limit"),
            [XferDiagnosticCode.XFER0701] = (XferSeverity.Error, "invalid UTF-8"),
            [XferDiagnosticCode.XFER0801] = (XferSeverity.Error, "value has the wrong kind for its target"),
            [XferDiagnosticCode.XFER0802] = (XferSeverity.Error, "key matches no member of the target"),
            [XferDiagnosticCode.XFER0803] = (XferSeverity.Error, "required member missing"),
            [XferDiagnosticCode.XFER0804] = (XferSeverity.Error, "two keys match one member"),
            [XferDiagnosticCode.XFER0901] = (XferSeverity.Warning, "integer outside JSON's safe range"),
            [XferDiagnosticCode.XFER0902] = (XferSeverity.Error, "special float has no JSON mapping"),
            [XferDiagnosticCode.XFER0903] = (XferSeverity.Info, "symbol mapped to JSON string"),
            [XferDiagnosticCode.XFER0904] = (XferSeverity.Info, "temporal mapped to JSON string"),
            [XferDiagnosticCode.XFER0905] = (XferSeverity.Warning, "duplicate key in JSON source; last value kept"),
            [XferDiagnosticCode.XFER0906] = (XferSeverity.Error, "malformed JSON"),
            [XferDiagnosticCode.XFER0907] = (XferSeverity.Error, "JSON string holds a character XferLang cannot represent"),
        }.ToFrozenDictionary();
}
