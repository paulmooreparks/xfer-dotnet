using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace ParksComputing.XferLang.Tests.Harness;

/// <summary>
/// The ratchet. <c>expected-failures.txt</c> lists the ids of cases the implementation is not yet
/// expected to pass. A listed case must fail, and a listed case that passes fails the build, so a
/// line has to be deleted the moment its case starts passing and the list only ever shrinks.
/// </summary>
internal static class ExpectedFailures
{
    private static readonly Lazy<HashSet<string>> Ids = new(Load);

    public static IReadOnlySet<string> All => Ids.Value;

    private static HashSet<string> Load()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string path = Path.Combine(AppContext.BaseDirectory, "expected-failures.txt");
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length > 0 && !line.StartsWith('#'))
            {
                ids.Add(line);
            }
        }

        return ids;
    }

    /// <summary>Runs a case body under the ratchet.</summary>
    public static void Run(string id, Action body)
    {
        if (!All.Contains(id))
        {
            body();
            return;
        }

        try
        {
            body();
        }
        catch (Exception)
        {
            return; // failing, as listed
        }

        Assert.Fail($"{id} now passes: remove it from expected-failures.txt");
    }
}
