using System;
using System.IO;
using Xunit;

namespace ParksComputing.XferLang.Tests;

public class ScaffoldTests
{
    [Fact]
    public void TheAssemblyUnderTestLoads()
    {
        Assert.Equal("ParksComputing.XferLang", typeof(AssemblyMarker).Assembly.GetName().Name);
    }

    [Fact]
    public void TheSuiteSubmoduleIsCheckedOut()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "global.json")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);
        Assert.True(
            File.Exists(Path.Combine(directory, "suite", "tests", "valid.txtar")),
            "suite/tests/valid.txtar is missing; run: git submodule update --init");
    }
}
