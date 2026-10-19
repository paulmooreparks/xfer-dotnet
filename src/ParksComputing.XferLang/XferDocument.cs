using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ParksComputing.XferLang.Model;
using ParksComputing.XferLang.Syntax;
using ParksComputing.XferLang.Text;

namespace ParksComputing.XferLang;

/// <summary>Options for parsing.</summary>
public sealed record XferParseOptions
{
    private XferLimits limits = XferLimits.Default;

    /// <summary>The limits to enforce. Defaults to the specification's minimums.</summary>
    public XferLimits Limits
    {
        get => limits;
        init => limits = value ?? throw new ArgumentNullException(nameof(value));
    }
}

/// <summary>The result of parsing a document: always a tree and diagnostics, and a value when there is no error.</summary>
public sealed class XferParseResult
{
    internal XferParseResult(XferValue? value, int version, XferSyntaxTree tree, IReadOnlyList<XferDiagnostic> diagnostics, bool ok)
    {
        Value = value;
        Version = version;
        Tree = tree;
        Diagnostics = diagnostics;
        Ok = ok;
    }

    /// <summary>Whether no diagnostic has severity <see cref="XferSeverity.Error"/>.</summary>
    public bool Ok { get; }

    /// <summary>The value of the document; non-null exactly when <see cref="Ok"/>.</summary>
    public XferValue? Value { get; }

    /// <summary>The version from the <c>xfer</c> head, or 1.</summary>
    public int Version { get; }

    /// <summary>The lossless tree; always present, partial when there are errors.</summary>
    public XferSyntaxTree Tree { get; }

    /// <summary>Every diagnostic, in order of position.</summary>
    public IReadOnlyList<XferDiagnostic> Diagnostics { get; }
}

/// <summary>Thrown when a value is asked for and the input could not produce one. Carries every diagnostic.</summary>
public sealed class XferException : Exception
{
    /// <summary>Creates an exception for these diagnostics.</summary>
    public XferException(IReadOnlyList<XferDiagnostic> diagnostics)
        : base(Describe(diagnostics, null))
    {
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    /// <summary>Creates an exception with a message, for an error that no diagnostic describes.</summary>
    public XferException(IReadOnlyList<XferDiagnostic> diagnostics, string message)
        : base(Describe(diagnostics, message))
    {
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    /// <summary>Creates an exception with no diagnostics.</summary>
    public XferException()
        : this([])
    {
    }

    /// <summary>Creates an exception with a message and no diagnostics.</summary>
    public XferException(string message)
        : this([], message)
    {
    }

    /// <summary>Creates an exception with a message, no diagnostics, and the exception that caused it.</summary>
    public XferException(string message, Exception innerException)
        : base(message, innerException)
    {
        Diagnostics = [];
    }

    /// <summary>The diagnostics; for a parse, every one it produced.</summary>
    public IReadOnlyList<XferDiagnostic> Diagnostics { get; }

    // As xfer-js builds XferError's message: the first error, then how many more there are.
    private static string Describe(IReadOnlyList<XferDiagnostic>? diagnostics, string? message)
    {
        if (diagnostics is null || diagnostics.Count == 0)
        {
            return message ?? "invalid XferLang";
        }

        XferDiagnostic first = diagnostics[0];
        foreach (XferDiagnostic d in diagnostics)
        {
            if (d.Severity == XferSeverity.Error)
            {
                first = d;
                break;
            }
        }

        string more = diagnostics.Count > 1 ? $" (and {diagnostics.Count - 1} more)" : string.Empty;
        return $"{first.CodeText} at {first.Start.Line}:{first.Start.Column}: {first.Message}{more}";
    }
}

/// <summary>Parses XferLang documents.</summary>
public static class XferDocument
{
    /// <summary>
    /// Parses a document from UTF-8 bytes. Never throws for any input: every fault is a diagnostic.
    /// The bytes are normalized first (specification 3.1 and 3.2): a byte order mark is removed and
    /// line endings become LF.
    /// </summary>
    public static XferParseResult Parse(ReadOnlySpan<byte> utf8, XferParseOptions? options = null)
    {
        XferLimits limits = (options ?? Defaults).Limits;
        return Finish(Normalizer.Normalize(utf8, limits), limits);
    }

    /// <summary>Parses a document from a string. Never throws for any input: every fault is a diagnostic.</summary>
    public static XferParseResult Parse(string text, XferParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        XferLimits limits = (options ?? Defaults).Limits;
        return Finish(Normalizer.Normalize(text, limits), limits);
    }

    /// <summary>
    /// Reads a stream to its end and parses it. A stream longer than the document size limit is
    /// refused with <c>XFER0601</c> as soon as it is longer, without reading the rest. There is no
    /// incremental parser: the whole document is read first.
    /// </summary>
    public static async ValueTask<XferParseResult> ParseAsync(Stream stream, XferParseOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        XferLimits limits = (options ?? Defaults).Limits;
        using var buffer = new MemoryStream();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > limits.DocumentBytes)
                {
                    // Over the limit: the normalizer reports XFER0601 for any input this long.
                    return Finish(Normalizer.Normalize(new byte[limits.DocumentBytes + 1L > int.MaxValue ? int.MaxValue : limits.DocumentBytes + 1], limits), limits);
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), options);
    }

    /// <summary>Parses a document and returns its value, or throws <see cref="XferException"/> with the diagnostics.</summary>
    public static XferValue ParseValue(ReadOnlySpan<byte> utf8, XferParseOptions? options = null) => ValueOf(Parse(utf8, options));

    /// <summary>Parses a document and returns its value, or throws <see cref="XferException"/> with the diagnostics.</summary>
    public static XferValue ParseValue(string text, XferParseOptions? options = null) => ValueOf(Parse(text, options));

    private static readonly XferParseOptions Defaults = new();

    private static XferValue ValueOf(XferParseResult result) => result.Ok && result.Value is not null ? result.Value : throw new XferException(result.Diagnostics);

    private static XferParseResult Finish(NormalizeResult normalized, XferLimits limits)
    {
        if (normalized.Fault is { } fault)
        {
            // A document for an input that could not be read at all: no head, no root, nothing after.
            var empty = new TokenStream([], [new Token(XferTokenKind.EndOfFile, 0, 0, 0, 0)], []);
            var tree = new XferSyntaxTree(new SyntaxSource(empty, LineMap.Empty), null, null, [], 0);
            return new XferParseResult(null, 1, tree, [fault], ok: false);
        }

        var pending = new List<PendingDiagnostic>();
        TokenStream stream = Lexer.Tokenize(normalized.Text, limits, pending);
        var source = new SyntaxSource(stream, normalized.Lines);
        (XferSyntaxTree parsed, int version) = Parser.Parse(source, limits, pending);
        List<XferDiagnostic> diagnostics = DiagnosticResolver.Resolve(pending, normalized.Lines);
        bool ok = !diagnostics.Exists(d => d.Severity == XferSeverity.Error);
        return new XferParseResult(ok ? Projection.ToValue(parsed) : null, version, parsed, diagnostics, ok);
    }
}
