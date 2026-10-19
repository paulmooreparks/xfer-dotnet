namespace ParksComputing.XferLang;

/// <summary>
/// The diagnostic codes of specification section 10. The numeric value is the number in the code,
/// so <c>XFER0404</c> is 404. A code is never reused for a different meaning.
/// </summary>
public enum XferDiagnosticCode
{
    /// <summary>scalar token not followed by a separator</summary>
    XFER0101 = 101,

    /// <summary>`&lt;` not followed by `/` or `!`</summary>
    XFER0102 = 102,

    /// <summary>character cannot begin a token</summary>
    XFER0103 = 103,

    /// <summary>unterminated comment</summary>
    XFER0104 = 104,

    /// <summary>identifier ends with `.`</summary>
    XFER0105 = 105,

    /// <summary>unterminated string</summary>
    XFER0201 = 201,

    /// <summary>`#` not followed by `#` or `"`</summary>
    XFER0202 = 202,

    /// <summary>leading zero on a decimal integer</summary>
    XFER0301 = 301,

    /// <summary>malformed number</summary>
    XFER0302 = 302,

    /// <summary>number out of range for the requested type (conversion, not parsing)</summary>
    XFER0303 = 303,

    /// <summary>malformed temporal</summary>
    XFER0304 = 304,

    /// <summary>temporal denotes no valid instant</summary>
    XFER0305 = 305,

    /// <summary>document has no element</summary>
    XFER0401 = 401,

    /// <summary>content after the root element</summary>
    XFER0402 = 402,

    /// <summary>duplicate key</summary>
    XFER0403 = 403,

    /// <summary>object body has a key without a value</summary>
    XFER0404 = 404,

    /// <summary>unterminated object or sequence</summary>
    XFER0405 = 405,

    /// <summary>unexpected closing bracket</summary>
    XFER0406 = 406,

    /// <summary>token in key position is not an identifier or string</summary>
    XFER0407 = 407,

    /// <summary>annotation value carries an annotation</summary>
    XFER0501 = 501,

    /// <summary>annotation has no target</summary>
    XFER0502 = 502,

    /// <summary>`xfer` annotation is not the first token</summary>
    XFER0503 = 503,

    /// <summary>unsupported major version</summary>
    XFER0504 = 504,

    /// <summary>duplicate `id`</summary>
    XFER0505 = 505,

    /// <summary>unknown annotation</summary>
    XFER0506 = 506,

    /// <summary>document exceeds size limit</summary>
    XFER0601 = 601,

    /// <summary>nesting exceeds depth limit</summary>
    XFER0602 = 602,

    /// <summary>scalar token exceeds length limit</summary>
    XFER0603 = 603,

    /// <summary>delimiter run exceeds length limit</summary>
    XFER0604 = 604,

    /// <summary>element count exceeds limit</summary>
    XFER0605 = 605,

    /// <summary>invalid UTF-8</summary>
    XFER0701 = 701,

    /// <summary>value has the wrong kind for its target</summary>
    XFER0801 = 801,

    /// <summary>key matches no member of the target</summary>
    XFER0802 = 802,

    /// <summary>required member missing</summary>
    XFER0803 = 803,

    /// <summary>two keys match one member</summary>
    XFER0804 = 804,

    /// <summary>integer outside JSON's safe range</summary>
    XFER0901 = 901,

    /// <summary>special float has no JSON mapping</summary>
    XFER0902 = 902,

    /// <summary>symbol mapped to JSON string</summary>
    XFER0903 = 903,

    /// <summary>temporal mapped to JSON string</summary>
    XFER0904 = 904,

    /// <summary>duplicate key in JSON source; last value kept</summary>
    XFER0905 = 905,

    /// <summary>malformed JSON</summary>
    XFER0906 = 906,

    /// <summary>JSON string holds a character XferLang cannot represent</summary>
    XFER0907 = 907,
}
