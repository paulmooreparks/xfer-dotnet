using System;
using System.IO;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using ParksComputing.XferLang.Serialization;
using ParksComputing.XferLang.Tests.Engine;

namespace ParksComputing.XferLang.Benchmarks;

/// <summary>dotnet run -c Release --project bench/ParksComputing.XferLang.Benchmarks -- --filter '*'</summary>
public static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

/// <summary>The core on the parkscomputing.com navigation document (22 KB) and on a generated 1 MB document.</summary>
[MemoryDiagnoser]
public class CoreBenchmarks
{
    private byte[] siteNav = [];
    private byte[] large = [];
    private XferValue siteNavValue = null!;
    private string siteNavJson = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        siteNav = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "sitenav.xfer"));

        // About a megabyte of objects, sequences, strings, numbers and dates.
        var sb = new StringBuilder("[\n");
        for (int i = 0; sb.Length < 1_000_000; i++)
        {
            sb.Append(Item(i)).Append('\n');
        }

        large = Encoding.UTF8.GetBytes(sb.Append(']').ToString());
        siteNavValue = XferDocument.ParseValue(siteNav);
        siteNavJson = XferJson.ToJson(siteNavValue).Value!;

        static string Item(int i) =>
            $$"""{ id {{i}} name "item {{i}}" ratio {{i}}.5 when 2025-08-01T09:30:00Z tags [ a b "c d" ] nested { on true off null hex 0x{{i:x}} } }""";
    }

    [Benchmark]
    public XferParseResult ParseSiteNav() => XferDocument.Parse(siteNav);

    [Benchmark]
    public XferParseResult ParseLarge() => XferDocument.Parse(large);

    [Benchmark]
    public string CanonicalSiteNav() => XferWriter.Canonical(siteNavValue);

    [Benchmark]
    public string? ToJsonSiteNav() => XferJson.ToJson(siteNavValue).Value;

    [Benchmark]
    public XferValue? FromJsonSiteNav() => XferJson.FromJson(siteNavJson).Value;
}

/// <summary>Reading and writing the engine's navigation document as objects, against the legacy 0.x library.</summary>
[MemoryDiagnoser]
public class MapperBenchmarks
{
    private string siteNav = string.Empty;
    private string legacySiteNav = string.Empty;
    private NavNode node = null!;
    private LegacyNavNode legacyNode = null!;

    [GlobalSetup]
    public void Setup()
    {
        siteNav = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "sitenav.xfer"));
        legacySiteNav = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "sitenav.legacy.xfer"));
        node = XferSerializer.Deserialize<NavNode>(siteNav)!;
        legacyNode = ParksComputing.Xfer.Lang.XferConvert.Deserialize<LegacyNavNode>(legacySiteNav)!;
        if (node.Nav!.Length != legacyNode.Nav!.Length)
        {
            throw new InvalidOperationException("the two libraries read different documents");
        }
    }

    [Benchmark(Baseline = true)]
    public LegacyNavNode? DeserializeLegacy() => ParksComputing.Xfer.Lang.XferConvert.Deserialize<LegacyNavNode>(legacySiteNav);

    [Benchmark]
    public NavNode? Deserialize() => XferSerializer.Deserialize<NavNode>(siteNav);

    [Benchmark]
    public string SerializeLegacy() => ParksComputing.Xfer.Lang.XferConvert.Serialize(legacyNode);

    [Benchmark]
    public string Serialize() => XferSerializer.Serialize(node);
}
