using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using ParksComputing.XferLang;
using ParksComputing.XferLang.Json;
using ParksComputing.XferLang.Writing;

namespace ParksComputing.XferLang.Tool;

/// <summary>
/// The <c>xfer</c> command (DESIGN.md section 11), a port of the xfer-js CLI (<c>cli/main.ts</c>).
/// For every command except <c>--version</c>, its standard output and exit code are byte for byte
/// those of the xfer-js CLI, which the differential tests check.
/// </summary>
internal static class Program
{
    // The line endings of a raw literal are the source file's, so they are fixed here rather than
    // left to how the file was checked out.
    private static readonly string Usage = UsageText.ReplaceLineEndings("\n");

    private const string UsageText = """
        usage: xfer <command> [options] [FILE...]

        commands:
          validate [--json]                      check documents; print diagnostics
          fmt [--write]                          canonical form to stdout, or in place with --write
          canon                                  same as fmt without --write
          to-json [--pretty] [--stringify-big-ints]
                                                 convert XferLang to JSON
          from-json                              convert JSON to XferLang (canonical form)

        options:
          --json       print diagnostics as one JSON object per line
          --version    print the version
          --help       print this text

        With no FILE, or with -, input is read from standard input.
        Exit status: 0 success, 1 a document had an error, 2 usage error or unreadable file.

        """;

    private static readonly Dictionary<string, string[]> CommandOptions = new(StringComparer.Ordinal)
    {
        ["validate"] = ["--json"],
        ["fmt"] = ["--write", "--json"],
        ["canon"] = ["--json"],
        ["to-json"] = ["--pretty", "--stringify-big-ints", "--json"],
        ["from-json"] = ["--json"],
    };

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false); // for --write, as Node writes a string to a file

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private sealed class UsageException(string message) : Exception(message);

    private sealed record Options(bool Json, bool Write, bool Pretty, bool StringifyBigInts);

    private static int Main(string[] args)
    {
        using IDisposable? pipes = StandardStream.IgnoreBrokenPipes();
        StandardStream stdout = StandardStream.Output();
        StandardStream stderr = StandardStream.Error();
        int code;
        try
        {
            code = Run(args, stdout, stderr);
        }
        catch (UsageException e)
        {
            Write(stderr, $"xfer: {e.Message}\n");
            code = 2;
        }

        // A failed write to either stream is exit 2, whatever the documents said (section 11).
        if (stdout.Failed)
        {
            Write(stderr, "xfer: cannot write standard output\n");
        }

        return stdout.Failed || stderr.Failed ? 2 : code;
    }

    private static void Write(StandardStream stream, string text) => stream.Write(text);

    private static int Run(string[] args, StandardStream stdout, StandardStream stderr)
    {
        if (args.Length == 0)
        {
            Write(stderr, Usage);
            return 2;
        }

        string command = args[0];
        if (command is "--help" or "-h")
        {
            Write(stdout, Usage);
            return 0;
        }

        if (command == "--version")
        {
            Write(stdout, $"{Version()}\n");
            return 0;
        }

        if (!CommandOptions.TryGetValue(command, out string[]? allowed))
        {
            throw new UsageException($"unknown command \"{command}\"");
        }

        bool json = false, write = false, pretty = false, stringify = false;
        var files = new List<string>();
        bool optionsDone = false;
        foreach (string arg in args.Skip(1))
        {
            if (!optionsDone && arg == "--")
            {
                optionsDone = true;
            }
            else if (!optionsDone && arg.StartsWith('-') && arg != "-")
            {
                if (!allowed.Contains(arg))
                {
                    throw new UsageException($"{command}: unknown option \"{arg}\"");
                }

                switch (arg)
                {
                    case "--json":
                        json = true;
                        break;
                    case "--write":
                        write = true;
                        break;
                    case "--pretty":
                        pretty = true;
                        break;
                    default:
                        stringify = true;
                        break;
                }
            }
            else
            {
                files.Add(arg);
            }
        }

        if (write && (files.Count == 0 || files.Contains("-")))
        {
            throw new UsageException("fmt --write needs file names; it cannot write to standard input");
        }

        if (files.Count == 0)
        {
            files.Add("-");
        }

        var options = new Options(json, write, pretty, stringify);

        // Read everything first, so an unreadable file is a usage-level failure before any output.
        var inputs = new List<(string File, byte[] Bytes)>();
        foreach (string file in files)
        {
            try
            {
                inputs.Add((file, file == "-" ? ReadStandardInput() : ReadFile(file)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new UsageException($"cannot read {file}: {e.Message}");
            }
        }

        bool allOk = true;
        foreach ((string file, byte[] bytes) in inputs)
        {
            // Each file's output is written as soon as it is made, never joined with the others,
            // as xfer-js writes it.
            var output = new StringBuilder();
            (bool ok, string? text) = Process(command, file == "-" ? "<stdin>" : file, bytes, options, output, stderr);
            allOk &= ok;
            if (!write)
            {
                if (output.Length > 0)
                {
                    Write(stdout, output.ToString());
                }
            }
            else if (ok && text is not null)
            {
                byte[] written = Utf8.GetBytes(text);
                if (!written.AsSpan().SequenceEqual(bytes))
                {
                    try
                    {
                        using var stream = new FileStream(file, FileMode.Create, FileAccess.Write, Shared);
                        stream.Write(written);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        throw new UsageException($"cannot write {file}: {e.Message}");
                    }
                }
            }
        }

        return allOk ? 0 : 1;
    }

    private static string Version()
    {
        string version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus]; // the source revision is not part of the version
    }

    // A file another program has open, for writing as well (a log, an editor's buffer), is read
    // and written as Node reads and writes it. File.ReadAllBytes would refuse it on Windows, since
    // it shares the file for reading only.
    private const FileShare Shared = FileShare.ReadWrite | FileShare.Delete;

    private static byte[] ReadFile(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, Shared);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static byte[] ReadStandardInput()
    {
        using Stream stdin = Console.OpenStandardInput();
        using var buffer = new MemoryStream();
        stdin.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Processes one input and returns whether it was free of errors, with the canonical text for <c>fmt</c>.</summary>
    private static (bool Ok, string? Text) Process(string command, string file, byte[] bytes, Options options, StringBuilder output, StandardStream stderr)
    {
        switch (command)
        {
            case "validate":
            {
                XferParseResult result = XferDocument.Parse(bytes);
                Report(stderr, file, result.Diagnostics, options.Json);
                return (result.Ok, null);
            }

            case "fmt":
            case "canon":
            {
                XferParseResult result = XferDocument.Parse(bytes);
                Report(stderr, file, result.Diagnostics, options.Json);
                if (!result.Ok)
                {
                    return (false, null);
                }

                try
                {
                    string text = XferWriter.Canonical(result);
                    output.Append(text);
                    return (true, text);
                }
                catch (XferException e)
                {
                    Write(stderr, $"{file}: error: {e.Message}\n");
                    return (false, null);
                }
            }

            case "to-json":
            {
                XferParseResult result = XferDocument.Parse(bytes);
                Report(stderr, file, result.Diagnostics, options.Json);
                if (!result.Ok || result.Value is null)
                {
                    return (false, null);
                }

                XferJsonResult<string?> converted = XferJson.ToJson(result.Value, new XferToJsonOptions
                {
                    StringifyBigIntegers = options.StringifyBigInts,
                    Indent = options.Pretty ? 2 : 0,
                });
                Report(stderr, file, converted.Diagnostics, options.Json);
                if (converted.Value is null)
                {
                    return (false, null); // refused as longer than the document size limit (XFER0601)
                }

                output.Append(converted.Value).Append('\n');
                return (!converted.Diagnostics.Any(d => d.Severity == XferSeverity.Error), null);
            }

            default:
            {
                string text;
                try
                {
                    // TextDecoder in its default mode, as xfer-js uses it: strict, and a leading
                    // byte order mark is dropped.
                    ReadOnlySpan<byte> body = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes.AsSpan(3) : bytes;
                    text = StrictUtf8.GetString(body);
                }
                catch (DecoderFallbackException)
                {
                    Write(stderr, $"{file}: error: input is not valid UTF-8\n");
                    return (false, null);
                }

                XferJsonResult<XferValue?> converted = XferJson.FromJson(text);
                Report(stderr, file, converted.Diagnostics, options.Json);
                if (converted.Value is null)
                {
                    return (false, null);
                }

                try
                {
                    output.Append(XferWriter.Canonical(converted.Value));
                }
                catch (Exception e) when (e is XferException or ArgumentException)
                {
                    Write(stderr, $"{file}: error: {e.Message}\n");
                    return (false, null);
                }

                return (true, null);
            }
        }
    }

    private static void Report(StandardStream stderr, string file, IReadOnlyList<XferDiagnostic> diagnostics, bool json)
    {
        foreach (XferDiagnostic d in diagnostics)
        {
            if (json)
            {
                Write(stderr, DiagnosticJson(file, d) + "\n");
            }
            else if (d.Severity != XferSeverity.Info)
            {
                Write(stderr, $"{file}:{d.Start.Line}:{d.Start.Column}: {Severity(d.Severity)} {d.CodeText}: {d.Message}\n");
            }
        }
    }

    private static string Severity(XferSeverity severity) => severity switch
    {
        XferSeverity.Error => "error",
        XferSeverity.Warning => "warning",
        _ => "info",
    };

    /// <summary>The line xfer-js writes, <c>JSON.stringify({ file, ...diagnostic })</c>, with its fields in its order.</summary>
    private static string DiagnosticJson(string file, XferDiagnostic d)
    {
        var sb = new StringBuilder();
        sb.Append("{\"file\":");
        Stringify(sb, file);
        sb.Append(",\"code\":\"").Append(d.CodeText).Append("\",\"severity\":\"").Append(Severity(d.Severity)).Append("\",\"message\":");
        Stringify(sb, d.Message);
        sb.Append(",\"start\":");
        Position(sb, d.Start);
        sb.Append(",\"end\":");
        Position(sb, d.End);
        return sb.Append('}').ToString();

        static void Position(StringBuilder sb, XferPosition p) =>
            sb.Append("{\"offset\":").Append(p.Offset).Append(",\"line\":").Append(p.Line).Append(",\"column\":").Append(p.Column).Append('}');
    }

    /// <summary>
    /// A string as <c>JSON.stringify</c> writes it (ECMA-262, QuoteJSONString): the short escapes,
    /// other controls and lone surrogates as <c>\u</c> with lower-case hex, everything else as it is.
    /// </summary>
    private static void Stringify(StringBuilder sb, string text)
    {
        sb.Append('"');
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                default:
                    bool pair = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
                    if (pair)
                    {
                        sb.Append(c).Append(text[++i]);
                    }
                    else if (c < 0x20 || char.IsSurrogate(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }
}
