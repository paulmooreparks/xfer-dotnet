using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ParksComputing.XferLang.Tests.Harness;
using Xunit;

namespace ParksComputing.XferLang.Tests;

/// <summary>Tests of the harness itself: if these fail, the conformance results mean nothing.</summary>
public class HarnessTests
{
    [Fact]
    public void TxtarIgnoresTextBeforeTheFirstEntryAndKeepsEachFinalNewline()
    {
        var entries = Txtar.Read("comment\nmore\n-- a.xfer --\n42\n-- b.xfer --\n[ 1 ]\n");
        Assert.Equal(
            [("a.xfer", "42\n"), ("b.xfer", "[ 1 ]\n")],
            entries.Select(e => (e.Key, e.Value)).ToArray());
    }

    [Fact]
    public void TxtarHandlesAFileWithNoFinalNewline()
    {
        Assert.Equal([("a", "x")], Txtar.Read("-- a --\nx").Select(e => (e.Key, e.Value)).ToArray());
    }

    [Fact]
    public void TxtarGivesAnEmptyEntryTheEmptyStringAndKeepsBlankLines()
    {
        var entries = Txtar.Read("-- empty --\n-- blank --\n\n\nx\n\n").ToDictionary(e => e.Key, e => e.Value);
        Assert.Equal(string.Empty, entries["empty"]);
        Assert.Equal("\n\nx\n\n", entries["blank"]);
    }

    [Fact]
    public void TxtarTreatsALineThatOnlyResemblesAHeaderAsContent()
    {
        var entries = Txtar.Read("-- a --\n-- not a header\n--x--\n").ToDictionary(e => e.Key, e => e.Value);
        Assert.Equal("-- not a header\n--x--\n", entries["a"]);
    }

    [Fact]
    public void TxtarRejectsADuplicateEntryName()
    {
        FormatException e = Assert.Throws<FormatException>(() => Txtar.Read("-- a --\n1\n-- a --\n2\n"));
        Assert.Contains("duplicate entry \"a\"", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSuiteLoadsAndIsNotEmpty()
    {
        // The counts are floors, not the exact numbers, so adding a case never breaks the build.
        Assert.True(SuiteCases.Valid.Count >= 45, $"valid: {SuiteCases.Valid.Count}");
        Assert.True(SuiteCases.Invalid.Count >= 80, $"invalid: {SuiteCases.Invalid.Count}");
        Assert.True(SuiteCases.ToJson.Count >= 14, $"to-json: {SuiteCases.ToJson.Count}");
        Assert.True(SuiteCases.FromJson.Count >= 40, $"from-json: {SuiteCases.FromJson.Count}");
        Assert.Equal(SuiteCases.AllIds.Count, SuiteCases.AllIds.Distinct().Count());
    }

    [Fact]
    public void EveryCaseIdHasAKnownGroup()
    {
        string[] groups = ["valid/", "invalid/", "bytes/", "to-json/", "from-json/"];
        Assert.All(SuiteCases.AllIds, id => Assert.Contains(groups, g => id.StartsWith(g, StringComparison.Ordinal)));
    }

    [Fact]
    public void ByteLevelCasesKeepTheirBytes()
    {
        // These files exist to protect bytes that an editor or version control would change.
        ValidCase bom = SuiteCases.Valid.Single(c => c.Id == "bytes/310-bom");
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bom.Input.Take(3).ToArray());
        ValidCase crlf = SuiteCases.Valid.Single(c => c.Id == "bytes/320-crlf-normalized");
        Assert.Contains((byte)'\r', crlf.Input);
    }

    [Fact]
    public void EveryExpectedFailureNamesARealCase()
    {
        var real = new HashSet<string>(SuiteCases.AllIds, StringComparer.Ordinal);
        string[] stale = ExpectedFailures.All.Where(id => !real.Contains(id)).ToArray();
        Assert.True(stale.Length == 0, "expected-failures.txt lists cases that do not exist: " + string.Join(", ", stale));
    }

    [Fact]
    public void AnUnlistedCaseRunsItsBody()
    {
        const string unlisted = "valid/__ratchet_test_unlisted__";
        Assert.DoesNotContain(unlisted, ExpectedFailures.All);
        Assert.Throws<InvalidOperationException>(() => ExpectedFailures.Run(unlisted, () => throw new InvalidOperationException("fails")));
        ExpectedFailures.Run(unlisted, () => { });
    }

    [Fact]
    public void AListedCaseMustFailAndItsPassingIsAnError()
    {
        // Uses a real listed id (the list is empty only when the project is finished), and skips
        // itself when there is none. A listed case whose body passes must fail the run.
        string? listed = ExpectedFailures.All.FirstOrDefault();
        if (listed is null)
        {
            return;
        }

        ExpectedFailures.Run(listed, () => throw new InvalidOperationException("still failing"));
        Xunit.Sdk.XunitException error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ExpectedFailures.Run(listed, () => { }));
        Assert.Contains("now passes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TaggedComparisonSeesMemberOrderAndValues()
    {
        JsonNode a = JsonNode.Parse("""{"type":"object","value":{"x":{"type":"integer","value":"1"},"y":{"type":"integer","value":"2"}}}""")!;
        JsonNode same = JsonNode.Parse("""{"type":"object","value":{"x":{"type":"integer","value":"1"},"y":{"type":"integer","value":"2"}}}""")!;
        JsonNode reordered = JsonNode.Parse("""{"type":"object","value":{"y":{"type":"integer","value":"2"},"x":{"type":"integer","value":"1"}}}""")!;
        JsonNode different = JsonNode.Parse("""{"type":"object","value":{"x":{"type":"integer","value":"1"},"y":{"type":"integer","value":"3"}}}""")!;
        Assert.True(Tagged.Equal(a, same));
        Assert.False(Tagged.Equal(a, reordered));
        Assert.False(Tagged.Equal(a, different));
        Assert.False(Tagged.Equal(a, null));
    }

    [Fact]
    public void TheSubjectIsNotImplementedYet()
    {
        // Documents the state at the end of step 2. Deleted when the parser lands in step 5.
        Assert.Throws<NotImplementedException>(() => Subject.Parse([]));
    }
}
