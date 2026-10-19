using System.Collections.Generic;
using System.Linq;
using ParksComputing.XferLang.Tests.Harness;
using Xunit;

namespace ParksComputing.XferLang.Tests;

/// <summary>
/// The parts of the valid-document check that the parser alone can meet: acceptance, the value
/// model and the lossless tree. The full check in <see cref="ConformanceTests"/> also needs the
/// canonical writer (step 6), so those cases stay on expected-failures.txt until then.
/// </summary>
public class ParserSuiteTests
{
    private static readonly Dictionary<string, ValidCase> ValidById = SuiteCases.Valid.ToDictionary(c => c.Id);

    public static TheoryData<string> ValidIds
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (ValidCase c in SuiteCases.Valid)
            {
                data.Add(c.Id);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ValidIds))]
    public void ValidDocumentParsesToItsValueModelAndALosslessTree(string id)
    {
        ValidCase c = ValidById[id];
        ParseOutcome result = Subject.Parse(c.Input);
        Assert.True(result.Ok, string.Join("; ", result.Diagnostics.Select(d => $"{d.Code} {d.Line}:{d.Column}")));
        Assert.True(Tagged.Equal(c.Value, result.Tagged), $"value model differs:\nexpected {c.Value.ToJsonString()}\nactual   {result.Tagged?.ToJsonString()}");
        Assert.NotNull(result.Tokens);
        Assert.Equal(Lossless.Normalize(c.Input), Lossless.Reconstruct(result.Tokens));
    }
}
