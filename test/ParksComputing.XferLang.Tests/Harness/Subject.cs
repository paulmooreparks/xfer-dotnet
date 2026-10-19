using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace ParksComputing.XferLang.Tests.Harness;

internal sealed record DiagnosticInfo(string Code, int Line, int Column);

/// <summary>One token of the lossless tree: the trivia before it, and its own text.</summary>
internal sealed record TokenView(IReadOnlyList<string> LeadingTrivia, string Text);

/// <summary>
/// What the conformance tests need to know about one parse. <see cref="Tokens"/> is every token of
/// the tree in order, ending with the end-of-file token, so that the harness rebuilds the text
/// itself and does not trust the implementation to say it did.
/// </summary>
internal sealed record ParseOutcome(
    bool Ok,
    IReadOnlyList<DiagnosticInfo> Diagnostics,
    string? Canonical,
    JsonNode? Tagged,
    IReadOnlyList<TokenView>? Tokens);

internal sealed record ToJsonOutcome(string Text, IReadOnlyList<string> Codes);

/// <param name="Canonical">The canonical form of the converted value, or null when there is no value.</param>
/// <param name="Codes">The codes of every diagnostic reported.</param>
/// <param name="HasError">Whether any diagnostic has severity Error.</param>
internal sealed record FromJsonOutcome(string? Canonical, IReadOnlyList<string> Codes, bool HasError);

/// <summary>
/// The seam between the conformance tests and the implementation. The tests are written once,
/// against this class; each implementation step replaces a method body with real calls, and the
/// cases that start passing come off expected-failures.txt. Until a step lands, its methods throw,
/// which is what makes every case fail with "not implemented" at the end of step 2.
/// </summary>
internal static class Subject
{
    /// <summary>Parses the bytes; on success also writes the canonical form and the tagged model.</summary>
    public static ParseOutcome Parse(byte[] input)
    {
        XferParseResult result = XferDocument.Parse(input);
        DiagnosticInfo[] diagnostics = [.. result.Diagnostics.Select(d => new DiagnosticInfo(d.CodeText, d.Start.Line, d.Start.Column))];
        JsonNode? tagged = result.Ok ? TaggedWriter.ToTaggedDocument(result) : null;
        string? canonical = result.Ok ? XferWriter.Canonical(result) : null;
        return new ParseOutcome(result.Ok, diagnostics, canonical, tagged, TaggedWriter.TokensInOrder(result.Tree));
    }

    /// <summary>The canonical form of the text parsed as a document, or null if it does not parse.</summary>
    public static string? Canonicalize(string text)
    {
        XferParseResult result = XferDocument.Parse(text);
        return result.Ok ? XferWriter.Canonical(result) : null;
    }

    /// <summary>Converts a document that has already been checked to parse without error.</summary>
    public static ToJsonOutcome ToJson(string xfer, bool stringifyBigIntegers)
    {
        XferValue value = XferDocument.ParseValue(xfer);
        XferJsonResult<string> result = XferJson.ToJson(value, new XferToJsonOptions { StringifyBigIntegers = stringifyBigIntegers });
        return new ToJsonOutcome(result.Value, [.. result.Diagnostics.Select(d => d.CodeText)]);
    }

    public static FromJsonOutcome FromJson(string json)
    {
        XferJsonResult<XferValue?> result = XferJson.FromJson(json);
        return new FromJsonOutcome(
            result.Value is null ? null : XferWriter.Canonical(result.Value),
            [.. result.Diagnostics.Select(d => d.CodeText)],
            result.Diagnostics.Any(d => d.Severity == XferSeverity.Error));
    }
}
