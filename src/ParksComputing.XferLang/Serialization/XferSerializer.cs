using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ParksComputing.XferLang.Model;

namespace ParksComputing.XferLang.Serialization;

/// <summary>
/// Maps .NET objects to XferLang and back (DESIGN.md section 10). The shape follows
/// <c>System.Text.Json.JsonSerializer</c>. Reading parses the document first and then maps the
/// value, so the mapper never sees text and cannot disagree with the parser; a mapping error is an
/// <see cref="XferException"/> whose diagnostic (<c>XFER0801</c> to <c>XFER0804</c>, or
/// <c>XFER0303</c>) has the position of the value it is about and a JSON Pointer in its message.
/// Writing maps the object to a value and writes its canonical form.
/// </summary>
/// <remarks>The mapper uses reflection, so it is not safe for trimming or ahead-of-time compilation.</remarks>
public static class XferSerializer
{
    internal const string TrimmingMessage = "The XferLang object mapper uses reflection over the types it maps, which trimming may remove.";
    internal const string AotMessage = "The XferLang object mapper builds generic collection types at run time.";

    private static readonly XferSerializerOptions Defaults = new();

    /// <summary>Writes a value as an XferLang document.</summary>
    [RequiresUnreferencedCode(TrimmingMessage)]
    [RequiresDynamicCode(AotMessage)]
    public static string Serialize<T>(T value, XferSerializerOptions? options = null) => Serialize(value, typeof(T), options);

    /// <summary>Writes a value as an XferLang document.</summary>
    /// <exception cref="ArgumentException">The value is not of the type.</exception>
    [RequiresUnreferencedCode(TrimmingMessage)]
    [RequiresDynamicCode(AotMessage)]
    public static string Serialize(object? value, Type type, XferSerializerOptions? options = null)
    {
        CheckType(value, type);
        XferSerializerOptions o = options ?? Defaults;
        return XferWriter.Canonical(Mapper.Write(value, o), new XferWriteOptions { Limits = o.Limits });
    }

    /// <summary>The value must be of the type it is said to be, as <c>System.Text.Json</c> requires.</summary>
    private static void CheckType(object? value, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (value is null ? type.IsValueType && Nullable.GetUnderlyingType(type) is null : !type.IsInstanceOfType(value))
        {
            throw new ArgumentException($"the value is {(value is null ? "null" : $"a {value.GetType()}")}, not a {type}", nameof(value));
        }
    }

    /// <summary>The XferLang value a .NET value maps to.</summary>
    [RequiresUnreferencedCode(TrimmingMessage)]
    [RequiresDynamicCode(AotMessage)]
    public static XferValue SerializeToValue<T>(T value, XferSerializerOptions? options = null) => Mapper.Write(value, options ?? Defaults);

    /// <summary>Writes a value as an XferLang document to a stream, in UTF-8 with no byte order mark.</summary>
    [RequiresUnreferencedCode(TrimmingMessage)]
    [RequiresDynamicCode(AotMessage)]
    public static Task SerializeAsync<T>(Stream stream, T value, XferSerializerOptions? options = null, CancellationToken cancellationToken = default) =>
        SerializeAsync(stream, value, typeof(T), options, cancellationToken);

    /// <summary>Writes a value as an XferLang document to a stream, in UTF-8 with no byte order mark.</summary>
    [RequiresUnreferencedCode(TrimmingMessage)]
    [RequiresDynamicCode(AotMessage)]
    public static async Task SerializeAsync(Stream stream, object? value, Type type, XferSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        CheckType(value, type);
        XferSerializerOptions o = options ?? Defaults;
        XferValue mapped = Mapper.Write(value, o);

        // The canonical writer is synchronous, so it writes into memory in chunks, and only the
        // chunks go to the caller's stream, asynchronously: a stream that refuses synchronous
        // writes (an ASP.NET Core response) is never written synchronously.
        var chunks = new ChunkStream();
        using (var writer = new StreamWriter(chunks, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 65536, leaveOpen: true))
        {
            XferWriter.Canonical(mapped, writer, new XferWriteOptions { Limits = o.Limits });
        }

        foreach (ReadOnlyMemory<byte> chunk in chunks.Chunks)
        {
            await stream.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads an XferLang document as a value of a type.</summary>
    /// <exception cref="XferException">The document has an error, or its value does not map onto the type.</exception>
    [RequiresUnreferencedCode(TrimmingMessage)]
    [RequiresDynamicCode(AotMessage)]
    public static T? Deserialize<T>(string text, XferSerializerOptions? options = null) => (T?)Deserialize(text, typeof(T), options);

    /// <summary>Reads an XferLang document given as UTF-8 bytes as a value of a type.</summary>
    /// <exception cref="XferException">The document has an error, or its value does not map onto the type.</exception>
    [RequiresUnreferencedCode(TrimmingMessage)]
    [RequiresDynamicCode(AotMessage)]
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8, XferSerializerOptions? options = null)
    {
        XferSerializerOptions o = options ?? Defaults;
        (XferParseResult result, SourceMap? map) = XferDocument.ParseForMapping(utf8, o.Limits);
        return (T?)Map(result, map, typeof(T), o);
    }

    /// <summary>Reads an XferLang document as a value of a type.</summary>
    /// <exception cref="XferException">The document has an error, or its value does not map onto the type.</exception>
    [RequiresUnreferencedCode(TrimmingMessage)]
    [RequiresDynamicCode(AotMessage)]
    public static object? Deserialize(string text, Type type, XferSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(type);
        XferSerializerOptions o = options ?? Defaults;
        (XferParseResult result, SourceMap? map) = XferDocument.ParseForMapping(text, o.Limits);
        return Map(result, map, type, o);
    }

    /// <summary>Maps a value already parsed onto a type. Diagnostics then have no source position.</summary>
    /// <exception cref="XferException">The value does not map onto the type.</exception>
    [RequiresUnreferencedCode(TrimmingMessage)]
    [RequiresDynamicCode(AotMessage)]
    public static T? Deserialize<T>(XferValue value, XferSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return (T?)Mapper.Read(value, typeof(T), options ?? Defaults, positions: null);
    }

    /// <summary>Reads a stream to its end and maps the document onto a type.</summary>
    /// <exception cref="XferException">The document has an error, or its value does not map onto the type.</exception>
    [RequiresUnreferencedCode(TrimmingMessage)]
    [RequiresDynamicCode(AotMessage)]
    public static async ValueTask<T?> DeserializeAsync<T>(Stream stream, XferSerializerOptions? options = null, CancellationToken cancellationToken = default) =>
        (T?)await DeserializeAsync(stream, typeof(T), options, cancellationToken).ConfigureAwait(false);

    /// <summary>Reads a stream to its end and maps the document onto a type.</summary>
    /// <exception cref="XferException">The document has an error, or its value does not map onto the type.</exception>
    [RequiresUnreferencedCode(TrimmingMessage)]
    [RequiresDynamicCode(AotMessage)]
    public static async ValueTask<object?> DeserializeAsync(Stream stream, Type type, XferSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(type);
        XferSerializerOptions o = options ?? Defaults;

        // Read the bytes, refusing an oversized stream as XferDocument.ParseAsync does, then parse
        // once, with positions kept for the mapper.
        (MemoryStream? buffer, XferParseResult? refused) = await XferDocument.ReadBoundedAsync(stream, o.Limits, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
        {
            throw new XferException(refused.Diagnostics);
        }

        using (buffer)
        {
            (XferParseResult result, SourceMap? map) = XferDocument.ParseForMapping(buffer!.GetBuffer().AsSpan(0, (int)buffer.Length), o.Limits);
            return Map(result, map, type, o);
        }
    }

    [RequiresUnreferencedCode(TrimmingMessage)]
    [RequiresDynamicCode(AotMessage)]
    private static object? Map(XferParseResult result, SourceMap? map, Type type, XferSerializerOptions options)
    {
        if (!result.Ok || result.Value is null)
        {
            throw new XferException(result.Diagnostics);
        }

        return Mapper.Read(result.Value, type, options, map);
    }

    /// <summary>A write-only stream that keeps what is written as a list of chunks, so no single array has to hold the whole output.</summary>
    private sealed class ChunkStream : Stream
    {
        private const int ChunkSize = 65536;
        private readonly List<byte[]> full = [];
        private byte[] current = new byte[ChunkSize];
        private int used;

        /// <summary>What was written, in order.</summary>
        public IEnumerable<ReadOnlyMemory<byte>> Chunks
        {
            get
            {
                foreach (byte[] chunk in full)
                {
                    yield return chunk;
                }

                yield return current.AsMemory(0, used);
            }
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                if (used == ChunkSize)
                {
                    full.Add(current);
                    current = new byte[ChunkSize];
                    used = 0;
                }

                int n = Math.Min(buffer.Length, ChunkSize - used);
                buffer[..n].CopyTo(current.AsSpan(used));
                used += n;
                buffer = buffer[n..];
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

}
