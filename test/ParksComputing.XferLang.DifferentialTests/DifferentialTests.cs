using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ParksComputing.XferLang.Json;
using ParksComputing.XferLang.Tests.Harness;
using Xunit;

namespace ParksComputing.XferLang.DifferentialTests;

/// <summary>
/// DESIGN.md section 12.6: both CLIs on the same inputs, with the same exit code, standard output
/// and standard error, byte for byte. Standard error is compared too, since it is where the
/// diagnostics are; the one allowance is the operating system's reason a file cannot be read,
/// which Node and .NET word differently.
/// </summary>
public sealed partial class DifferentialTests : IDisposable
{
    private const int Batch = 300;

    private static readonly string? JsCli = Environment.GetEnvironmentVariable("XFER_JS_CLI");

    private static readonly string Tool = Path.Combine(
        typeof(DifferentialTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ToolDirectory").Value!,
        OperatingSystem.IsWindows() ? "xfer.exe" : "xfer");

    private static System.Threading.CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly string work =Directory.CreateTempSubdirectory("xfer-differential-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    private sealed record Outcome(int Exit, byte[] Stdout, byte[] Stderr)
    {
        public string Describe() => $"exit {Exit}\n--- stdout\n{Show(Stdout)}\n--- stderr\n{Show(Stderr)}";
    }

    /// <summary>
    /// Skips the test without the xfer-js CLI, except where XFER_DIFFERENTIAL_REQUIRED is set (in
    /// CI), where a missing CLI fails it, so the comparison can never be skipped there unnoticed.
    /// </summary>
    private static void RequireJs()
    {
        bool present = JsCli is not null && File.Exists(JsCli);
        if (!present && Environment.GetEnvironmentVariable("XFER_DIFFERENTIAL_REQUIRED") is { Length: > 0 })
        {
            Assert.Fail($"XFER_DIFFERENTIAL_REQUIRED is set, but XFER_JS_CLI ({JsCli ?? "unset"}) is not a file");
        }

        Assert.SkipUnless(present, "XFER_JS_CLI is not the path of a built xfer-js dist/cli.mjs");
    }

    private static async Task<Outcome> Run(bool js, string directory, IReadOnlyList<string> args, byte[]? stdin = null)
    {
        var start = new ProcessStartInfo(js ? "node" : Tool)
        {
            WorkingDirectory = directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (js)
        {
            start.ArgumentList.Add(JsCli!);
        }

        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(start)!;
        using System.Threading.CancellationTokenRegistration kill = Token.Register(() => process.Kill(entireProcessTree: true));
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        Task readOut = process.StandardOutput.BaseStream.CopyToAsync(stdout, Token);
        Task readErr = process.StandardError.BaseStream.CopyToAsync(stderr, Token);
        if (stdin is not null)
        {
            await process.StandardInput.BaseStream.WriteAsync(stdin, Token);
        }

        process.StandardInput.Close();
        await Task.WhenAll(readOut, readErr);
        await process.WaitForExitAsync(Token);
        return new Outcome(process.ExitCode, stdout.ToArray(), Normalize(stderr.ToArray()));
    }

    [GeneratedRegex(@"^(xfer: cannot (?:read|write) .*?): .*$", RegexOptions.Multiline)]
    private static partial Regex OperatingSystemReason();

    [GeneratedRegex(@"^(.*?):\d+:\d+: error XFER0701:", RegexOptions.Multiline)]
    private static partial Regex Utf8Gnu();

    [GeneratedRegex(@"(""code"":""XFER0701"".*?""start"":)\{[^}]*\},""end"":\{[^}]*\}")]
    private static partial Regex Utf8Json();

    /// <summary>
    /// Removes the two differences the design allows: the operating system's wording of why a file
    /// could not be read or written, and the position of an invalid-UTF-8 diagnostic, which this
    /// implementation gives at the first invalid byte and xfer-js at the origin, as specification
    /// 3.1 permits (DESIGN.md section 16, item 2).
    /// </summary>
    private static byte[] Normalize(byte[] stderr)
    {
        string text = Encoding.UTF8.GetString(stderr);
        string normalized = OperatingSystemReason().Replace(text, "$1: <reason>");
        normalized = Utf8Gnu().Replace(normalized, "$1:*:*: error XFER0701:");
        normalized = Utf8Json().Replace(normalized, "$1*");
        return normalized == text ? stderr : Encoding.UTF8.GetBytes(normalized);
    }

    private static string Show(byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        return text.Length > 4000 ? text[..4000] + "..." : text;
    }

    private static bool Same(Outcome a, Outcome b) => a.Exit == b.Exit && a.Stdout.AsSpan().SequenceEqual(b.Stdout) && a.Stderr.AsSpan().SequenceEqual(b.Stderr);

    private static async Task AssertSame(string directory, IReadOnlyList<string> args, byte[]? stdin = null)
    {
        Outcome js = await Run(js: true, directory, args, stdin);
        Outcome dotnet = await Run(js: false, directory, args, stdin);
        Assert.True(Same(js, dotnet), $"xfer {string.Join(' ', args)}\n=== xfer-js\n{js.Describe()}\n=== xfer-dotnet\n{dotnet.Describe()}");
    }

    /// <summary>
    /// Whether the library says a command succeeds on an input. It sorts the inputs into batches
    /// that should succeed and batches that should fail: a batch's exit code is that of its worst
    /// file, so in a batch of successes a file one CLI fails changes the exit code, and in a batch
    /// of failures a file one CLI accepts loses its diagnostics from standard error, since every
    /// failure writes one there.
    /// </summary>
    private static bool Succeeds(string command, byte[] input)
    {
        if (command == "from-json")
        {
            ReadOnlySpan<byte> body = input.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? input.AsSpan(3) : input;
            string text;
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(body);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }

            XferJsonResult<XferValue?> converted = XferJson.FromJson(text);
            return converted.Value is not null && Writes(converted.Value);
        }

        XferParseResult parsed = XferDocument.Parse(input);
        return command switch
        {
            "validate" => parsed.Ok,
            "to-json" => parsed is { Ok: true, Value: not null } && !XferJson.ToJson(parsed.Value).Diagnostics.Any(d => d.Severity == XferSeverity.Error),
            _ => parsed is { Ok: true, Value: not null } && Writes(parsed.Value),
        };

        static bool Writes(XferValue value)
        {
            try
            {
                _ = XferWriter.Canonical(value);
                return true;
            }
            catch (Exception e) when (e is XferException or ArgumentException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Runs each command over all the inputs, many files to an invocation, with the inputs the
    /// command should succeed on batched apart from the rest (see <see cref="Succeeds"/>); when an
    /// invocation differs, the inputs of that batch are run one at a time to say which one it was.
    /// </summary>
    private async Task AssertSameOnAll(IReadOnlyList<byte[]> inputs, string extension, IReadOnlyList<string[]> commands)
    {
        var names = new List<string>();
        for (int i = 0; i < inputs.Count; i++)
        {
            string name = $"i{i:D5}{extension}";
            await File.WriteAllBytesAsync(Path.Combine(work, name), inputs[i], Token);
            names.Add(name);
        }

        foreach (string[] command in commands)
        {
            bool[] succeeds = [.. inputs.Select(input => Succeeds(command[0], input))];
            Assert.True(succeeds.Count(s => s) >= 100, $"xfer {string.Join(' ', command)} should succeed on only {succeeds.Count(s => s)} inputs");
            Assert.True(succeeds.Count(s => !s) >= 100, $"xfer {string.Join(' ', command)} should fail on only {succeeds.Count(s => !s)} inputs");
            foreach (bool group in new[] { true, false })
            {
                await AssertSameOnBatches([.. names.Where((_, i) => succeeds[i] == group)], command, group);
            }
        }
    }

    private async Task AssertSameOnBatches(IReadOnlyList<string> names, string[] command, bool succeeding)
    {
        for (int from = 0; from < names.Count; from += Batch)
        {
            string[] args = [.. command, .. names.Skip(from).Take(Batch)];
            Outcome js = await Run(js: true, work, args);
            Outcome dotnet = await Run(js: false, work, args);
            if (Same(js, dotnet))
            {
                // A converter that printed nothing for a batch of successes compared nothing.
                Assert.True(!succeeding || command[0] == "validate" || js.Stdout.Length > 0, $"xfer {string.Join(' ', command)} printed nothing for a batch");
                Assert.Equal(succeeding ? 0 : 1, js.Exit);
                continue;
            }

            foreach (string name in names.Skip(from).Take(Batch))
            {
                js = await Run(js: true, work, [.. command, name]);
                dotnet = await Run(js: false, work, [.. command, name]);
                Assert.True(
                    Same(js, dotnet),
                    $"xfer {string.Join(' ', command)} on {name}, whose bytes are:\n{Convert.ToHexString(await File.ReadAllBytesAsync(Path.Combine(work, name), Token))}\n{Show(await File.ReadAllBytesAsync(Path.Combine(work, name), Token))}\n=== xfer-js\n{js.Describe()}\n=== xfer-dotnet\n{dotnet.Describe()}");
            }

            Assert.Fail($"xfer {string.Join(' ', command)} differed on a batch, but on no input alone");
        }
    }

    [Fact]
    public async Task ArgumentsAndStandardInput()
    {
        RequireJs();
        await File.WriteAllTextAsync(Path.Combine(work, "ok.xfer"), "{ a 1 b [x \"y\"] }", Token);
        await File.WriteAllTextAsync(Path.Combine(work, "bad.xfer"), "{ a 1 b ", Token);
        await File.WriteAllTextAsync(Path.Combine(work, "a.json"), "{\"a\": [1, 2.5]}", Token);
        byte[] stdin = "[1 2 <! tag t !> 3]"u8.ToArray();

        string[][] cases =
        [
            [], ["--help"], ["-h"], ["bogus"], ["--json"], ["-"],
            ["validate", "--bogus"], ["fmt", "--pretty"], ["to-json", "--write"], ["canon", "--write"], ["validate", "-h"],
            ["fmt", "--write"], ["fmt", "--write", "-"], ["fmt", "--write", "ok.xfer", "-"],
            ["validate", "missing.xfer"], ["validate", "ok.xfer", "missing.xfer"], ["validate", "--", "--json"],
            ["validate"], ["validate", "--json"], ["fmt"], ["fmt", "-"], ["canon", "ok.xfer", "-", "bad.xfer"], ["fmt", "--json", "bad.xfer", "ok.xfer"],
            ["to-json"], ["to-json", "--pretty", "--stringify-big-ints", "ok.xfer"], ["to-json", "bad.xfer"],
            ["from-json", "a.json"], ["from-json", "--json", "ok.xfer"], ["from-json"],
            ["validate", "ok.xfer", "ok.xfer"], ["validate", "--json", "--json", "bad.xfer"],
        ];
        foreach (string[] args in cases)
        {
            await AssertSame(work, args, stdin);
        }
    }

    [Fact]
    public async Task FmtWrite()
    {
        RequireJs();
        var outcomes = new List<(Outcome Outcome, byte[][] Files)>();
        foreach (bool js in new[] { true, false })
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, js ? "js" : "dotnet")).FullName;
            await File.WriteAllTextAsync(Path.Combine(directory, "loose.xfer"), "{a 1   b [x]}", Token);
            await File.WriteAllBytesAsync(Path.Combine(directory, "canonical.xfer"), Encoding.UTF8.GetBytes(XferWriter.Canonical(XferDocument.Parse("{a 1}"))), Token);
            await File.WriteAllTextAsync(Path.Combine(directory, "bad.xfer"), "{a ", Token);
            Outcome outcome = await Run(js, directory, ["fmt", "--write", "loose.xfer", "canonical.xfer", "bad.xfer"]);
            byte[][] files = [.. new[] { "loose.xfer", "canonical.xfer", "bad.xfer" }.Select(f => File.ReadAllBytes(Path.Combine(directory, f)))];
            outcomes.Add((outcome, files));
        }

        Assert.Equal(outcomes[0].Outcome.Exit, outcomes[1].Outcome.Exit);
        Assert.Equal(outcomes[0].Outcome.Stdout, outcomes[1].Outcome.Stdout);
        Assert.Equal(Encoding.UTF8.GetString(outcomes[0].Outcome.Stderr), Encoding.UTF8.GetString(outcomes[1].Outcome.Stderr));
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(outcomes[0].Files[i], outcomes[1].Files[i]);
        }
    }

    [Fact]
    public async Task ToJsonPastTheDocumentSizeLimit()
    {
        RequireJs();

        // Indentation makes this 2.2 MB document about 550 million characters of JSON, which both
        // refuse with XFER0601 (specification 8.1) rather than fail.
        await File.WriteAllTextAsync(Path.Combine(work, "deep.xfer"), new string('[', 250) + string.Concat(Enumerable.Repeat("1 ", 1_000_000)) + new string(']', 250), Token);
        await AssertSame(work, ["to-json", "--pretty", "deep.xfer"]);
        await AssertSame(work, ["to-json", "--pretty", "--json", "deep.xfer"]);
    }

    [Fact]
    public async Task StandardOutputClosedByItsReader()
    {
        RequireJs();
        await File.WriteAllTextAsync(Path.Combine(work, "many.xfer"), "[" + string.Concat(Enumerable.Repeat(" 1", 200_000)) + " ]", Token);
        foreach (bool js in new[] { true, false })
        {
            var start = new ProcessStartInfo(js ? "node" : Tool)
            {
                WorkingDirectory = work,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (js)
            {
                start.ArgumentList.Add(JsCli!);
            }

            start.ArgumentList.Add("fmt");
            start.ArgumentList.Add("many.xfer");
            using Process process = Process.Start(start)!;
            Task<string> stderr = process.StandardError.ReadToEndAsync(Token);

            // Read a little, then go away, as `| head -c 10` does.
            char[] buffer = new char[10];
            _ = await process.StandardOutput.ReadAsync(buffer, Token);
            process.StandardOutput.Close();
            await process.WaitForExitAsync(Token);
            Assert.True(process.ExitCode == 2, $"{(js ? "xfer-js" : "xfer-dotnet")} exited {process.ExitCode}");
            Assert.Contains("xfer: cannot write standard output", await stderr, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The legacy repository's sample files (the roadmap's milestone M2 asks for them): the 22
    /// 0.x documents of paulmooreparks/Xfer as they are, which both CLIs must reject in the same
    /// words, and the 13 of them that tools/fixtures/convert-legacy.mjs converts, whatever they
    /// then are. legacy-samples/README.md says where each came from.
    /// </summary>
    [Fact]
    public async Task TheLegacyRepositorysSampleFiles()
    {
        RequireJs();
        string samples = Path.Combine(AppContext.BaseDirectory, "legacy-samples");
        string[] names = [.. Directory.GetFiles(samples, "*.xfer").Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
        Assert.Equal(35, names.Length);
        foreach (string name in names)
        {
            File.Copy(Path.Combine(samples, name), Path.Combine(work, name));
        }

        foreach (string[] command in new[] { new[] { "validate", "--json" }, ["fmt", "--json"], ["canon"], ["to-json", "--json"], ["to-json", "--pretty"] })
        {
            await AssertSame(work, [.. command, .. names]);
        }
    }

    [Fact]
    public async Task AFileOpenForWritingElsewhere()
    {
        RequireJs();
        string path = Path.Combine(work, "held.xfer");
        await File.WriteAllTextAsync(path, "{a   1}", Token);

        // Another program has the file open for writing, sharing it as a log writer or an editor does.
        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            await AssertSame(work, ["validate", "held.xfer"]);
            await AssertSame(work, ["fmt", "held.xfer"]);
        }
    }

    [Fact]
    public async Task GeneratedDamagedAndRandomDocuments()
    {
        RequireJs();
        var inputs = new List<byte[]>();
        for (uint seed = 1; seed <= 400; seed++)
        {
            string text = Inputs.Generate(seed);
            inputs.Add(Encoding.UTF8.GetBytes(text));
            var random = new Mulberry32(seed * 104729);
            for (int k = 0; k < 3; k++)
            {
                inputs.Add(Encoding.UTF8.GetBytes(Inputs.Damage(text, random)));
            }
        }

        // The generator must make documents that parse, or the comparison of fmt and to-json
        // would compare only error output.
        int valid = Enumerable.Range(1, 400).Count(seed => XferDocument.Parse(Inputs.Generate((uint)seed)).Ok);
        Assert.True(valid == 400, $"only {valid} of 400 generated documents are valid");

        var soup = new Mulberry32(20261020);
        for (int i = 0; i < 1200; i++)
        {
            inputs.Add(Encoding.UTF8.GetBytes(Inputs.TokenSoup(soup, Inputs.XferSoup)));
        }

        for (int i = 0; i < 300; i++)
        {
            inputs.Add(Inputs.Bytes(soup));
        }

        await AssertSameOnAll(inputs, ".xfer",
        [
            ["validate"], ["validate", "--json"], ["fmt", "--json"], ["canon"], ["to-json", "--json"], ["to-json", "--pretty", "--stringify-big-ints"],
        ]);
    }

    [Fact]
    public async Task JsonTexts()
    {
        RequireJs();
        var inputs = new List<byte[]>();
        var random = new Mulberry32(424242);
        for (uint seed = 1; seed <= 400; seed++)
        {
            XferParseResult parsed = XferDocument.Parse(Inputs.Generate(seed));
            if (parsed.Value is null)
            {
                continue;
            }

            foreach (int indent in new[] { 0, 2 })
            {
                string? json = XferJson.ToJson(parsed.Value, new XferToJsonOptions { Indent = indent }).Value;
                if (json is not null)
                {
                    inputs.Add(Encoding.UTF8.GetBytes(json));
                    inputs.Add(Encoding.UTF8.GetBytes(Inputs.Damage(json, random)));
                }
            }
        }

        for (int i = 0; i < 1200; i++)
        {
            inputs.Add(Encoding.UTF8.GetBytes(Inputs.TokenSoup(random, Inputs.JsonPieces)));
        }

        for (int i = 0; i < 300; i++)
        {
            inputs.Add(Inputs.Bytes(random));
        }

        await AssertSameOnAll(inputs, ".json", [["from-json"], ["from-json", "--json"]]);
    }
}
