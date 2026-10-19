using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ParksComputing.XferLang.Tests.Harness;

internal sealed record ExpectedDiagnostic(string Code, int? Line, int? Column);

/// <summary>A document the parser must accept, with its canonical form and tagged value model.</summary>
internal sealed record ValidCase(string Id, byte[] Input, string Canonical, JsonNode Value);

/// <summary>A document the parser must reject, with the diagnostics it must report.</summary>
internal sealed record InvalidCase(string Id, byte[] Input, IReadOnlyList<ExpectedDiagnostic> Diagnostics);

internal sealed record ToJsonCase(string Id, string Xfer, string Json, IReadOnlyList<string> Codes, bool StringifyBigIntegers);

internal sealed record FromJsonCase(string Id, string Json, string? Xfer, IReadOnlyList<string> Codes);

/// <summary>Loads the conformance suite from the <c>suite</c> submodule.</summary>
internal static partial class SuiteCases
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // Longest first, so that ".canonical.xfer" is not mistaken for ".xfer".
    private static readonly string[] MainSuffixes = [".canonical.xfer", ".value.json", ".diag", ".xfer"];
    private static readonly string[] ToJsonSuffixes = [".xfer", ".json", ".diag", ".options"];
    private static readonly string[] FromJsonSuffixes = [".json", ".xfer", ".diag"];

    private static readonly Lazy<Loaded> Cache = new(LoadAll);

    public static IReadOnlyList<ValidCase> Valid => Cache.Value.Valid;

    public static IReadOnlyList<InvalidCase> Invalid => Cache.Value.Invalid;

    public static IReadOnlyList<ToJsonCase> ToJson => Cache.Value.ToJson;

    public static IReadOnlyList<FromJsonCase> FromJson => Cache.Value.FromJson;

    /// <summary>The id of every case, in the form <c>group/stem</c>, sorted.</summary>
    public static IReadOnlyList<string> AllIds => Cache.Value.AllIds;

    public static string SuiteDirectory { get; } = FindSuite();

    [GeneratedRegex(@"^(XFER\d{4}) (\*|(\d+):(\d+))$")]
    private static partial Regex DiagnosticLine();

    private sealed record Loaded(
        List<ValidCase> Valid,
        List<InvalidCase> Invalid,
        List<ToJsonCase> ToJson,
        List<FromJsonCase> FromJson,
        List<string> AllIds);

    private static string FindSuite()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "global.json")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        string tests = Path.Combine(
            directory ?? throw new InvalidOperationException("global.json not found above the test binaries"),
            "suite",
            "tests");
        return Directory.Exists(tests)
            ? tests
            : throw new DirectoryNotFoundException($"{tests} is missing; run: git submodule update --init");
    }

    private static Loaded LoadAll()
    {
        var loaded = new Loaded([], [], [], [], []);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Claim(string id)
        {
            if (!seen.Add(id))
            {
                throw new InvalidDataException($"suite: duplicate case {id}");
            }

            loaded.AllIds.Add(id);
        }

        foreach ((string group, Dictionary<string, Files> stems) in new[]
        {
            ("valid", Group(ReadArchive("valid.txtar"), MainSuffixes)),
            ("invalid", Group(ReadArchive("invalid.txtar"), MainSuffixes)),
            ("bytes", Group(ReadBytes(), MainSuffixes)),
        })
        {
            foreach ((string stem, Files files) in stems)
            {
                string id = $"{group}/{stem}";
                Claim(id);
                AddMain(loaded, id, files);
            }
        }

        foreach ((string stem, Files files) in Group(ReadArchive("to-json.txtar"), ToJsonSuffixes))
        {
            string id = $"to-json/{stem}";
            Claim(id);
            loaded.ToJson.Add(MakeToJson(id, files));
        }

        foreach ((string stem, Files files) in Group(ReadArchive("from-json.txtar"), FromJsonSuffixes))
        {
            string id = $"from-json/{stem}";
            Claim(id);
            loaded.FromJson.Add(new FromJsonCase(
                id,
                files.Text(".json") ?? throw new InvalidDataException($"suite: {id} has no .json"),
                files.Text(".xfer"),
                ParseCodes(id, files.Text(".diag"))));
        }

        loaded.AllIds.Sort(StringComparer.Ordinal);
        return loaded;
    }

    private static void AddMain(Loaded loaded, string id, Files files)
    {
        byte[] input = files.Bytes(".xfer") ?? throw new InvalidDataException($"suite: {id} has no .xfer input");
        string? diag = files.Text(".diag");
        string? canonical = files.Text(".canonical.xfer");
        string? value = files.Text(".value.json");
        if (diag is not null)
        {
            if (canonical is not null || value is not null)
            {
                throw new InvalidDataException($"suite: {id} has both .diag and valid-case files");
            }

            loaded.Invalid.Add(new InvalidCase(id, input, ParseDiagnostics(id, diag, anyPositionOnly: false)));
        }
        else
        {
            if (canonical is null || value is null)
            {
                throw new InvalidDataException($"suite: {id} needs .canonical.xfer and .value.json (or a .diag)");
            }

            JsonNode node = JsonNode.Parse(value, nodeOptions: null, documentOptions: new JsonDocumentOptions { MaxDepth = 100_000 }) ?? throw new InvalidDataException($"suite: {id}: .value.json is null");
            loaded.Valid.Add(new ValidCase(id, input, canonical, node));
        }
    }

    private static ToJsonCase MakeToJson(string id, Files files)
    {
        string xfer = files.Text(".xfer") ?? throw new InvalidDataException($"suite: {id} has no .xfer");
        string json = files.Text(".json") ?? throw new InvalidDataException($"suite: {id} has no .json");
        if (!json.EndsWith('\n'))
        {
            throw new InvalidDataException($"suite: {id}: .json must end with a newline");
        }

        string[] options = (files.Text(".options") ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string option in options)
        {
            if (option != "stringify-big-ints")
            {
                throw new InvalidDataException($"suite: {id}: unknown option {option}");
            }
        }

        return new ToJsonCase(id, xfer, json[..^1], ParseCodes(id, files.Text(".diag")), options.Contains("stringify-big-ints"));
    }

    private static List<string> ParseCodes(string id, string? diag)
    {
        return diag is null ? [] : ParseDiagnostics(id, diag, anyPositionOnly: true).Select(d => d.Code).ToList();
    }

    private static List<ExpectedDiagnostic> ParseDiagnostics(string id, string text, bool anyPositionOnly)
    {
        var result = new List<ExpectedDiagnostic>();
        foreach (string line in text.Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            Match match = DiagnosticLine().Match(line);
            if (!match.Success)
            {
                throw new InvalidDataException($"suite: {id}: bad diagnostic line \"{line}\"");
            }

            if (match.Groups[2].Value == "*")
            {
                result.Add(new ExpectedDiagnostic(match.Groups[1].Value, null, null));
            }
            else if (anyPositionOnly)
            {
                throw new InvalidDataException($"suite: {id}: a JSON case lists only CODE * lines, found \"{line}\"");
            }
            else
            {
                result.Add(new ExpectedDiagnostic(
                    match.Groups[1].Value,
                    int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture)));
            }
        }

        if (result.Count == 0)
        {
            throw new InvalidDataException($"suite: {id}: a .diag file lists no diagnostics");
        }

        return result;
    }

    private sealed class Files
    {
        private readonly Dictionary<string, byte[]> bytes = [];

        public void Add(string suffix, byte[] content) => bytes[suffix] = content;

        public byte[]? Bytes(string suffix) => bytes.GetValueOrDefault(suffix);

        public string? Text(string suffix) => bytes.TryGetValue(suffix, out byte[]? content) ? Utf8.GetString(content) : null;
    }

    private static IEnumerable<KeyValuePair<string, byte[]>> ReadArchive(string file)
    {
        string text = Utf8.GetString(File.ReadAllBytes(Path.Combine(SuiteDirectory, file)));
        return Txtar.Read(text).Select(e => new KeyValuePair<string, byte[]>(e.Key, Utf8.GetBytes(e.Value)));
    }

    private static IEnumerable<KeyValuePair<string, byte[]>> ReadBytes()
    {
        string directory = Path.Combine(SuiteDirectory, "bytes");
        foreach (string path in Directory.EnumerateFiles(directory).OrderBy(p => p, StringComparer.Ordinal))
        {
            yield return new KeyValuePair<string, byte[]>(Path.GetFileName(path), File.ReadAllBytes(path));
        }
    }

    private static Dictionary<string, Files> Group(IEnumerable<KeyValuePair<string, byte[]>> entries, string[] suffixes)
    {
        var stems = new Dictionary<string, Files>(StringComparer.Ordinal);
        foreach ((string name, byte[] content) in entries)
        {
            string? suffix = suffixes.FirstOrDefault(s => name.EndsWith(s, StringComparison.Ordinal));
            if (suffix is null)
            {
                continue; // README and the like
            }

            string stem = name[..^suffix.Length];
            if (!stems.TryGetValue(stem, out Files? files))
            {
                files = new Files();
                stems[stem] = files;
            }

            files.Add(suffix, content);
        }

        if (stems.Count == 0)
        {
            throw new InvalidDataException("suite: no cases found in a group");
        }

        return stems;
    }
}
