using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace ParksComputing.XferLang.Tests.Harness;

internal sealed record DiagnosticInfo(string Code, int Line, int Column);

/// <summary>What the conformance tests need to know about one parse.</summary>
internal sealed record ParseOutcome(
    bool Ok,
    IReadOnlyList<DiagnosticInfo> Diagnostics,
    string? Canonical,
    JsonNode? Tagged,
    string? Reconstructed,
    string? NormalizedText);

internal sealed record ToJsonOutcome(string Text, IReadOnlyList<string> Codes);

internal sealed record FromJsonOutcome(string? Canonical, IReadOnlyList<string> Codes);

/// <summary>
/// The seam between the conformance tests and the implementation. The tests are written once,
/// against this class; each implementation step replaces a method body with real calls, and the
/// cases that start passing come off expected-failures.txt. Until a step lands, its methods throw,
/// which is what makes every case fail with "not implemented" at the end of step 2.
/// </summary>
internal static class Subject
{
    /// <summary>Parses the bytes; on success also writes the canonical form and the tagged model.</summary>
    public static ParseOutcome Parse(byte[] input) => throw new NotImplementedException("parser (step 5)");

    /// <summary>The canonical form of the text parsed as a document, or null if it does not parse.</summary>
    public static string? Canonicalize(string text) => throw new NotImplementedException("canonical writer (step 6)");

    public static ToJsonOutcome ToJson(string xfer, bool stringifyBigIntegers) => throw new NotImplementedException("JSON (step 7)");

    public static FromJsonOutcome FromJson(string json) => throw new NotImplementedException("JSON (step 7)");
}
