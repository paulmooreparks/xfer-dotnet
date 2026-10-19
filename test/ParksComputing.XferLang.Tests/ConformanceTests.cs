using System.Collections.Generic;
using System.Linq;
using ParksComputing.XferLang.Tests.Harness;
using Xunit;

namespace ParksComputing.XferLang.Tests;

/// <summary>
/// One generated test per suite case, named by its id. A case on expected-failures.txt must fail
/// and the rest must pass (see <see cref="ExpectedFailures"/>).
/// </summary>
public class ConformanceTests
{
    private static readonly Dictionary<string, ValidCase> ValidById = SuiteCases.Valid.ToDictionary(c => c.Id);
    private static readonly Dictionary<string, InvalidCase> InvalidById = SuiteCases.Invalid.ToDictionary(c => c.Id);
    private static readonly Dictionary<string, ToJsonCase> ToJsonById = SuiteCases.ToJson.ToDictionary(c => c.Id);
    private static readonly Dictionary<string, FromJsonCase> FromJsonById = SuiteCases.FromJson.ToDictionary(c => c.Id);

    public static TheoryData<string> ValidIds => Ids(SuiteCases.Valid.Select(c => c.Id));

    public static TheoryData<string> InvalidIds => Ids(SuiteCases.Invalid.Select(c => c.Id));

    public static TheoryData<string> ToJsonIds => Ids(SuiteCases.ToJson.Select(c => c.Id));

    public static TheoryData<string> FromJsonIds => Ids(SuiteCases.FromJson.Select(c => c.Id));

    private static TheoryData<string> Ids(IEnumerable<string> ids)
    {
        var data = new TheoryData<string>();
        foreach (string id in ids)
        {
            data.Add(id);
        }

        return data;
    }

    private static string Describe(IEnumerable<DiagnosticInfo> diagnostics)
    {
        string text = string.Join("; ", diagnostics.Select(d => $"{d.Code} {d.Line}:{d.Column}"));
        return text.Length == 0 ? "no diagnostics" : text;
    }

    [Theory]
    [MemberData(nameof(ValidIds))]
    public void ValidDocument(string id)
    {
        ExpectedFailures.Run(id, () =>
        {
            ValidCase c = ValidById[id];
            ParseOutcome result = Subject.Parse(c.Input);
            Assert.True(result.Ok, Describe(result.Diagnostics));
            Assert.Equal(c.Canonical, result.Canonical);
            Assert.True(Tagged.Equal(c.Value, result.Tagged), "the value model differs from .value.json, or its member order does");
            Assert.Equal(result.NormalizedText, result.Reconstructed);
            Assert.Equal(c.Canonical, Subject.Canonicalize(c.Canonical));
        });
    }

    [Theory]
    [MemberData(nameof(InvalidIds))]
    public void InvalidDocument(string id)
    {
        ExpectedFailures.Run(id, () =>
        {
            InvalidCase c = InvalidById[id];
            ParseOutcome result = Subject.Parse(c.Input);
            Assert.False(result.Ok, "the document must be rejected");
            foreach (ExpectedDiagnostic want in c.Diagnostics)
            {
                bool found = result.Diagnostics.Any(d =>
                    d.Code == want.Code && (want.Line is null || (d.Line == want.Line && d.Column == want.Column)));
                string where = want.Line is null ? "*" : $"{want.Line}:{want.Column}";
                Assert.True(found, $"missing {want.Code} at {where}; got {Describe(result.Diagnostics)}");
            }
        });
    }

    [Theory]
    [MemberData(nameof(ToJsonIds))]
    public void ToJson(string id)
    {
        ExpectedFailures.Run(id, () =>
        {
            ToJsonCase c = ToJsonById[id];
            ToJsonOutcome result = Subject.ToJson(c.Xfer, c.StringifyBigIntegers);
            Assert.Equal(c.Json, result.Text);
            foreach (string want in c.Codes)
            {
                Assert.Contains(want, result.Codes);
            }
        });
    }

    [Theory]
    [MemberData(nameof(FromJsonIds))]
    public void FromJson(string id)
    {
        ExpectedFailures.Run(id, () =>
        {
            FromJsonCase c = FromJsonById[id];
            FromJsonOutcome result = Subject.FromJson(c.Json);
            foreach (string want in c.Codes)
            {
                Assert.Contains(want, result.Codes);
            }

            if (c.Xfer is null)
            {
                Assert.Null(result.Canonical);
            }
            else
            {
                Assert.Equal(c.Xfer, result.Canonical);
            }
        });
    }
}
