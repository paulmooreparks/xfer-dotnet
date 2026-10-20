using System;
using System.Linq;
using ParksComputing.XferLang;
using ParksComputing.XferLang.Json;
using ParksComputing.XferLang.Syntax;
using ParksComputing.XferLang.Writing;

// Uses each part of the core once (parse, tree, value model, helpers, canonical writer, JSON in
// both directions) and checks the results, so that a trimmed or AOT-compiled build that lost
// something fails here and not in a user's program.

const string Document = """
    <! xfer 1 !>
    </ a comment />
    { name "Ada" <! tag t !> born 1815-12-10 big 123456789012345678901234567890 ratio 1.5 tags [ a "b c" ] }
    """;

int failures = 0;

void Check(bool condition, string what)
{
    if (!condition)
    {
        Console.Error.WriteLine($"failed: {what}");
        failures++;
    }
}

XferParseResult result = XferDocument.Parse(Document);
Check(result.Ok, "the document parses");
Check(result.Tree.Tokens.Any(t => t.Kind == XferTokenKind.String && t.Content == "Ada"), "the tree has the string token");

var root = (XferObject)result.Value!;
Check(((XferString)root["name"]).Value == "Ada", "the value model reads a string");
Check(((XferTemporal)root["born"]).TryGetDateOnly(out DateOnly born) && born == new DateOnly(1815, 12, 10), "a date converts");
Check(!((XferInteger)root["big"]).TryGetInt64(out _), "a big integer does not fit a long");
Check(root["born"].Annotations.Count == 1, "an annotation is kept");

string canonical = XferWriter.Canonical(result);
Check(XferWriter.Canonical(XferDocument.Parse(canonical)) == canonical, "the canonical form is stable");

string? json = XferJson.ToJson(root).Value;
Check(json is not null && json.StartsWith("{\"name\":\"Ada\"", StringComparison.Ordinal), "the value converts to JSON");
XferValue? back = XferJson.FromJson(json ?? "null").Value;
Check(back is XferObject o && o.Count == root.Count, "the JSON converts back");

XferParseResult bad = XferDocument.Parse("{ a 1");
Check(!bad.Ok && bad.Diagnostics.Any(d => d.Severity == XferSeverity.Error), "a broken document is reported, not thrown");

Console.WriteLine(failures == 0 ? "ok" : $"{failures} checks failed");
return failures == 0 ? 0 : 1;
