using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ParksComputing.XferLang.Tests.Harness;
using ParksComputing.XferLang.Text;
using Xunit;

namespace ParksComputing.XferLang.Tests;

/// <summary>
/// Tests that tie the code to the text of the specification, so that a change to the
/// specification (which this repository includes as the <c>suite</c> submodule) fails the build
/// until the code follows.
/// </summary>
public partial class SpecTextTests
{
    private static readonly string Spec = File.ReadAllText(Path.Combine(SuiteCases.SuiteDirectory, "..", "spec", "xferlang.md"));

    [GeneratedRegex(@"^\| (XFER\d{4}) \| (\w+) \| (.*) \|$")]
    private static partial Regex TableRow();

    [GeneratedRegex(@"U\+([0-9A-F]{4,6})(?: to U\+([0-9A-F]{4,6}))?")]
    private static partial Regex CodePointRange();

    private static List<(string Code, string Severity, string Summary)> SpecRows()
    {
        var rows = new List<(string, string, string)>();
        foreach (string line in Spec.Split('\n'))
        {
            Match m = TableRow().Match(line);
            if (m.Success)
            {
                rows.Add((m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value.Trim()));
            }
        }

        return rows;
    }

    /// <summary>The excluded code points, read from the sentence in specification 4.3 that lists them.</summary>
    private static HashSet<int> SpecExclusions()
    {
        string line = Spec.Split('\n').FirstOrDefault(l => l.Contains("An admitted non-ASCII scalar value", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("the specification no longer has the sentence this test reads");

        // Everything after "except" lists the exclusions; the "U+0080" before it is the lower bound.
        string listed = line[line.IndexOf("except", StringComparison.Ordinal)..];
        var excluded = new HashSet<int>();
        foreach (Match m in CodePointRange().Matches(listed))
        {
            int low = Convert.ToInt32(m.Groups[1].Value, 16);
            int high = m.Groups[2].Success ? Convert.ToInt32(m.Groups[2].Value, 16) : low;
            for (int cp = low; cp <= high; cp++)
            {
                excluded.Add(cp);
            }
        }

        return excluded;
    }

    [Fact]
    public void TheDiagnosticTableIsTheSpecificationsTableRowForRow()
    {
        var fromSpec = SpecRows()
            .Select(r => (r.Code, Severity: r.Severity == "informational" ? "Info" : char.ToUpperInvariant(r.Severity[0]) + r.Severity[1..], r.Summary))
            .ToList();
        var fromCode = Enum.GetValues<XferDiagnosticCode>()
            .Select(c => (Code: XferDiagnostics.Format(c), Severity: XferDiagnostics.SeverityOf(c).ToString(), Summary: XferDiagnostics.SummaryOf(c)))
            .OrderBy(r => r.Code, StringComparer.Ordinal)
            .ToList();

        // Every table row must parse, so that a badly formatted new row cannot be skipped on both sides.
        int tableLines = Spec.Split('\n').Count(l => l.StartsWith("| XFER", StringComparison.Ordinal));
        Assert.Equal(tableLines, fromSpec.Count);
        Assert.True(fromSpec.Count > 30);
        Assert.Equal(fromSpec, fromCode);
        Assert.Equal(fromSpec.Count, XferDiagnostics.Count);
    }

    [Fact]
    public void EveryCodeTheSuiteRequiresIsDefined()
    {
        var missing = new HashSet<string>();
        IEnumerable<string> required = SuiteCases.Invalid.SelectMany(c => c.Diagnostics.Select(d => d.Code))
            .Concat(SuiteCases.ToJson.SelectMany(c => c.Codes))
            .Concat(SuiteCases.FromJson.SelectMany(c => c.Codes));
        foreach (string code in required)
        {
            if (!XferDiagnostics.TryParse(code, out _))
            {
                missing.Add(code);
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void TheExclusionListIsReadWholeFromTheSpecification()
    {
        // 19 Unicode space characters and 19 format and bidirectional controls.
        Assert.Equal(38, SpecExclusions().Count);
    }

    [Fact]
    public void AdmittedNonAsciiAgreesWithTheSpecificationForEveryCodePointFromU0080ToU10FFFF()
    {
        HashSet<int> excluded = SpecExclusions();
        var wrong = new List<string>();
        for (int cp = 0x80; cp <= 0x10FFFF; cp++)
        {
            bool surrogate = cp is >= 0xD800 and <= 0xDFFF;
            bool expected = !surrogate && !excluded.Contains(cp);
            if (Unicode.IsAdmittedNonAscii(cp) != expected)
            {
                wrong.Add($"U+{cp:X4}");
                if (wrong.Count == 10)
                {
                    break;
                }
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public void AdmittedNonAsciiRejectsAsciiValuesPastTheUnicodeRangeAndSurrogates()
    {
        foreach (int cp in new[] { 0x00, 0x20, 0x41, 0x7F, 0x110000, 0xD800, 0xDBFF, 0xDC00, 0xDFFF, -1, int.MaxValue })
        {
            Assert.False(Unicode.IsAdmittedNonAscii(cp), $"U+{cp:X}");
        }
    }

    [Fact]
    public void IdentifierClassesFollowSpecification43()
    {
        foreach (char c in "abzAYZ_")
        {
            Assert.True(Unicode.IsIdentifierStart(c), c.ToString());
        }

        foreach (char c in "0-. 9\"#{}[]<>!/\\\t\n,:;'()+~?@$%^&*=|`")
        {
            Assert.False(Unicode.IsIdentifierStart(c), c.ToString());
        }

        foreach (char c in "abzAYZ_09-.")
        {
            Assert.True(Unicode.IsIdentifierContinue(c), c.ToString());
        }

        Assert.True(Unicode.IsIdentifierStart('é'));
        Assert.True(Unicode.IsIdentifierStart(0x1F600));
        Assert.False(Unicode.IsIdentifierStart(0xA0));
        Assert.False(Unicode.IsIdentifierContinue(0x200B));
    }

    [Fact]
    public void WhitespaceIsSpaceTabLineFeedAndFormFeedOnly()
    {
        Assert.All(new[] { 0x20, 0x09, 0x0A, 0x0C }, cp => Assert.True(Unicode.IsWhitespace(cp)));
        Assert.All(new[] { 0x0D, 0x0B, 0x00, 0xA0, 0x2028, 0x85, 0x3000, 0x41 }, cp => Assert.False(Unicode.IsWhitespace(cp)));
    }

    [Fact]
    public void Utf8LengthsFollowTheEncoding()
    {
        Assert.Equal([1, 1, 2, 2, 3, 3, 4, 4], new[] { 0x00, 0x7F, 0x80, 0x7FF, 0x800, 0xFFFF, 0x10000, 0x10FFFF }.Select(Unicode.Utf8Length));
        Assert.Equal(0, Unicode.Utf8ByteLength(string.Empty));
        Assert.Equal(1 + 2 + 3 + 4, Unicode.Utf8ByteLength("aé日😀"));
        Assert.Equal(3, Unicode.Utf8ByteLength("\uD800")); // a lone surrogate counts as U+FFFD would
        Assert.Equal(3, Unicode.Utf8ByteLength("\uDC00"));
        Assert.Equal(7, Unicode.Utf8ByteLength("\uD83D😀"));
    }
}
