using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ParksComputing.XferLang.Serialization;
using ParksComputing.XferLang.Tests.Harness;
using Xunit;

namespace ParksComputing.XferLang.Tests;

/// <summary>
/// The README's examples are checked: its document must parse, and its C# must be the text of
/// <see cref="Example"/> below, which compiles and runs here, so the README cannot drift from the API.
/// </summary>
public sealed partial class ReadmeTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(SuiteCases.SuiteDirectory, "..", ".."));

    private static readonly string Readme = File.ReadAllText(Path.Combine(Root, "README.md")).ReplaceLineEndings("\n");

    [GeneratedRegex(@"^```(\w*)\n(.*?)^```", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex CodeBlock();

    private static List<(string Language, string Code)> Blocks() =>
        [.. CodeBlock().Matches(Readme).Select(m => (m.Groups[1].Value, m.Groups[2].Value))];

    [Fact]
    public void TheExampleDocumentParses()
    {
        (_, string document) = Blocks().First(b => b.Language == "xfer");
        XferParseResult result = XferDocument.Parse(document);
        Assert.True(result.Ok, string.Join("\n", result.Diagnostics));
    }

    [Fact]
    public void TheCSharpExampleIsTheCodeThatRunsHere()
    {
        (_, string inReadme) = Blocks().First(b => b.Language == "csharp");
        string source = File.ReadAllText(Path.Combine(Root, "test", "ParksComputing.XferLang.Tests", "ReadmeTests.cs")).ReplaceLineEndings("\n");
        // The markers are looked for after the method's name, since this method spells them too.
        int method = source.IndexOf("public void Example()", StringComparison.Ordinal);
        int start = source.IndexOf("// README begin\n", method, StringComparison.Ordinal) + "// README begin\n".Length;
        int end = source.IndexOf("        // README end", start, StringComparison.Ordinal);
        string[] lines = source[start..end].Split('\n');
        string dedented = string.Join("\n", lines.Select(l => l.Length >= 8 ? l[8..] : l.TrimStart()));
        Assert.Equal(dedented, inReadme);
    }

    public sealed class Person
    {
        public string? Name { get; set; }

        public string[]? Langs { get; set; }
    }

    [Fact]
    public void Example()
    {
        // README begin
        // Parsing never throws. Every fault is a diagnostic with a stable code and a position.
        XferParseResult result = XferDocument.Parse("{ name \"Ada\" langs [ en fr ] }");
        foreach (XferDiagnostic d in result.Diagnostics)
        {
            Console.Error.WriteLine($"{d.Start.Line}:{d.Start.Column} {d.CodeText} {d.Message}");
        }

        // Or take the value, and let an error throw an XferException that carries the diagnostics.
        var value = (XferObject)XferDocument.ParseValue("{ n 12345678901234567890 }");
        var n = (XferInteger)value["n"];

        // The canonical form: the one way to write a document, byte for byte.
        string canonical = XferWriter.Canonical(XferDocument.Parse("{a 1 b[2 3]}"));

        // JSON in both directions. Each reports what it could not map exactly.
        XferJsonResult<string?> json = XferJson.ToJson(value, new XferToJsonOptions { StringifyBigIntegers = true });
        XferValue? back = XferJson.FromJson("{\"a\":[1,2.5,null]}").Value;

        // Objects, mapped as System.Text.Json maps them.
        Person? ada = XferSerializer.Deserialize<Person>("{ Name \"Ada\" Langs [ en fr ] }");
        string written = XferSerializer.Serialize(ada);
        // README end

        Assert.Empty(result.Diagnostics);
        Assert.Equal("12345678901234567890", n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("{\n    a 1\n    b [\n        2\n        3\n    ]\n}\n", canonical);
        Assert.Equal("{\"n\":\"12345678901234567890\"}", json.Value);
        Assert.NotNull(back);
        Assert.Equal(["en", "fr"], ada!.Langs!);
        Assert.Contains("Name \"Ada\"", written, StringComparison.Ordinal);
    }
}
