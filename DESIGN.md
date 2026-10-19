# xfer-dotnet: implementation design

This is the .NET implementation of XferLang 1.0, to be published to NuGet as `ParksComputing.XferLang`. It contains a parser, a lossless tree, a value model, a canonical writer, a conversion to and from JSON, an object mapper, and the `xfer` command line tool. The document is written so that an engineer or an agent can build the package from it without making further design decisions.

Read these first, in this order:

1. `../xferlang/spec/xferlang.md`, the normative specification. This document does not restate the language.
2. `../xferlang/history/FIRST_IMPLEMENTATION_FINDINGS.md`, which lists what the first implementation learned, including traps this one would otherwise fall into.
3. `../xfer-js/DESIGN.md` sections 4 to 9, and the source files they describe. xfer-js passes the whole suite and has been through six adversarial reviews. Its lexer, parser, canonical writer and JSON code are the algorithmic reference for this implementation. Where this document says "port", it means: reproduce the behaviour of the named xfer-js file, including its diagnostics, positions and recovery, in idiomatic C#, and where the two runtimes differ, follow this document.

Written 2026-10-19. Section 17 records decisions made during implementation. Section 16 lists the questions that need Paul.

## 1. Non-negotiables

1. **No runtime dependencies.** The core package references nothing outside the .NET base class library. Test and build dependencies are pinned to exact versions.
2. **The core never throws for input content.** `XferDocument.Parse` returns diagnostics for every fault in the input. The methods that throw on input (`ParseValue`, the mapper's `Deserialize`) throw `XferException`, which carries the same diagnostics. A wrong argument (a null where none is allowed, a value no document could hold) throws `ArgumentException` or one of its subclasses.
3. **No recursion on input-controlled depth.** The lexer, the parser, the value projection, the canonical writer, both JSON directions, the object mapper in both directions, and the test helpers all use explicit stacks. Each is tested at 200,000 levels with the depth limit raised. The depth limit is a diagnostic, not a guard against stack overflow, and a `StackOverflowException` in .NET cannot be caught, so this rule is what keeps a hostile document from ending the process.
4. **Strings are verbatim.** No code path interprets a backslash or any other character inside a string literal.
5. **The suite is the oracle, and xfer-js is the second oracle.** A test that passes by special-casing a suite file is a defect. Outside the suite, this implementation and xfer-js must report the same codes at the same positions and write the same bytes for the same input; section 12.6 describes the differential test that enforces it. A disagreement is resolved in the specification and suite first, as `../xfer-js/DESIGN.md` section 15 describes.
6. **Never rely on undocumented behaviour of the platform.** Every decision that rests on a .NET API rests on its documented contract. Where a documented route seems unavailable, stop and record the question in section 16 rather than measuring the platform and branching on what it did.

## 2. Repository and solution layout

```
xfer-dotnet/
  DESIGN.md
  README.md  LICENSE  SECURITY.md  CHANGELOG.md
  Directory.Build.props          shared settings: LangVersion, Nullable, warnings as errors, SourceLink, version
  Directory.Packages.props       central package management; every version pinned
  global.json                    SDK version, rollForward latestFeature
  xfer-dotnet.slnx
  suite/                         git submodule: github.com/paulmooreparks/xferlang
  src/
    ParksComputing.XferLang/
      Text/                      Normalizer, Utf8 helpers, Unicode classes, LineMap
      Syntax/                    Lexer, Token, Trivia, Parser, the tree (XferSyntaxTree and its node types)
      Model/                     XferValue and its subclasses, XferAnnotation, projection from the tree
      Diagnostics/               XferDiagnostic, XferDiagnosticCode, XferPosition, XferSeverity, Diagnostics table
      Writing/                   CanonicalWriter, StringDelimiters
      Json/                      XferJsonReader, XferJsonWriter
      Serialization/             XferSerializer, options, attributes, type metadata, converters
      XferDocument.cs  XferException.cs  XferLimits.cs  XferParseOptions.cs
    ParksComputing.XferLang.Tool/   the `xfer` dotnet tool (section 11)
  test/
    ParksComputing.XferLang.Tests/  unit, conformance, property and differential tests
    ParksComputing.XferLang.Tool.Tests/
  bench/
    ParksComputing.XferLang.Benchmarks/   BenchmarkDotNet; not run in CI
  .github/workflows/ci.yml  release.yaml
```

One package carries the core and the mapper, because the first consumer needs both and splitting them would add a package without removing any dependency. The tool is a second package (`ParksComputing.XferLang.Tool`, command `xfer`).

## 3. Targets and language settings

- **Target frameworks: `net8.0` and `net10.0`.** Both are long-term-support releases; parkscomputing.com runs on `net10.0`. `netstandard2.0` is not targeted: it lacks `DateOnly` and `TimeOnly`, needs `System.Memory` for spans (a dependency), and no consumer needs it. Section 16, question 1 asks Paul to confirm, because an earlier roadmap note named `netstandard2.0`.
- **C# 12** on both targets. Microsoft supports a language version only on the runtime it shipped with or a later one, so C# 12 is the newest version that is supported on `net8.0`, and the code is written to it on both targets. `Nullable` enabled, `TreatWarningsAsErrors`, `AnalysisLevel` latest-recommended, `EnforceCodeStyleInBuild`, `Deterministic`, `ContinuousIntegrationBuild` in CI, SourceLink, embedded symbols.
- **Trimming and AOT.** `IsAotCompatible` is set for `net10.0`. The core (everything except the mapper) is trim- and AOT-safe and must build without trim warnings. The reflection-based mapper is annotated with `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]` on its entry points; section 10.8 describes the seam for a source generator later.
- **Public API tracking.** `Microsoft.CodeAnalysis.PublicApiAnalyzers` with `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`, so every change to the public surface is visible in review. Package validation (`EnablePackageValidation`) is on from the first release.
- **Local SDK.** The development machine has only the .NET 8 SDK (8.0.425 on 2026-10-19). Building the `net10.0` target needs the .NET 10 SDK installed; `global.json` pins it. CI installs it with `actions/setup-dotnet`.

## 4. Public API

The namespace is `ParksComputing.XferLang`, with `.Syntax`, `.Serialization` and `.Json` for the larger areas. The shapes below are normative for names and semantics; parameter order and overload sets may be extended when there is a reason, recorded in section 17.

### 4.1 Parsing

```csharp
public static class XferDocument
{
    public static XferParseResult Parse(ReadOnlySpan<byte> utf8, XferParseOptions? options = null);   // also normalizes; see section 5
    public static XferParseResult Parse(string text, XferParseOptions? options = null);
    public static ValueTask<XferParseResult> ParseAsync(Stream stream, XferParseOptions? options = null, CancellationToken ct = default);
    public static XferValue ParseValue(ReadOnlySpan<byte> utf8, XferParseOptions? options = null);   // throws XferException
    public static XferValue ParseValue(string text, XferParseOptions? options = null);
}

public sealed class XferParseResult
{
    public bool Ok { get; }                                   // no diagnostic of severity Error
    public XferValue? Value { get; }                          // non-null exactly when Ok
    public int Version { get; }                               // from the xfer head, or 1
    public XferSyntaxTree Tree { get; }                       // always present
    public IReadOnlyList<XferDiagnostic> Diagnostics { get; } // in order of position
}

public sealed record XferParseOptions { public XferLimits Limits { get; init; } = XferLimits.Default; }

public sealed record XferLimits
{
    public static XferLimits Default { get; }   // the specification's minimums
    public int DocumentBytes { get; init; }     // each init accessor throws ArgumentOutOfRangeException for a value of zero or less
    public int Depth { get; init; }
    public int TokenBytes { get; init; }
    public int RunLength { get; init; }
    public int Elements { get; init; }
}
```

`XferLimits` is a class with validating `init` accessors, not a positional record struct, because `default(T)` and `with` on a struct bypass a constructor, so validation there could not hold. Every limit is an `int`: the document is a span, spans are indexed by `int`, and the specification's minimums (2^28-1 bytes, 2^31-1 elements) fit. `int.MaxValue` is the largest value a caller can set, and it means "no limit beyond the platform's".

`ParseAsync` reads the stream to the end (refusing with `XFER0601` as soon as it exceeds `DocumentBytes`, without reading the rest) and then parses. There is no incremental parser, and the documentation says so. A limit may be raised or lowered (specification 3.4); a value of zero or less is an `ArgumentOutOfRangeException` when it is set, which is the .NET idiom where xfer-js silently ignores it.

### 4.2 Value model

```csharp
public abstract class XferValue
{
    public abstract XferValueKind Kind { get; }
    public IReadOnlyList<XferAnnotation> Annotations { get; }     // empty, never null
    // Equality is structural (Equals, GetHashCode), including annotations; integers compare by value and lexeme.
}
public sealed class XferObject : XferValue        // ordered, keys unique, IReadOnlyDictionary<string, XferValue>
public sealed class XferSequence : XferValue      // IReadOnlyList<XferValue>
public sealed class XferString : XferValue        { public string Value { get; } }
public sealed class XferInteger : XferValue       { public BigInteger Value { get; } public string Lexeme { get; } }
public sealed class XferFractional : XferValue    { public string Lexeme { get; } }   // includes inf, -inf, nan
public sealed class XferBool : XferValue          { public bool Value { get; } }
public sealed class XferNull : XferValue
public sealed class XferSymbol : XferValue        { public string Name { get; } }
public sealed class XferTemporal : XferValue      { public XferTemporalKind TemporalKind { get; } public string Lexeme { get; } }
public sealed record XferAnnotation(string Name, XferValue Value);
public enum XferValueKind { Object, Sequence, String, Integer, Fractional, Bool, Null, Symbol, Date, Time, LocalDateTime, DateTime }
```

The value model is immutable. `XferObject` keeps insertion order with its own small ordered map (a `Dictionary<string, int>` index plus a list), because `OrderedDictionary<TKey, TValue>` exists only from .NET 9. Constructors are public, so a program can build a value; a value that no document could hold (specification 9, "Values built by a program") is refused by the writer, not by the constructor, so that a program can build and inspect a value before deciding what to do with it. That matches xfer-js.

Helpers, ported from xfer-js `src/value.ts` and extended for .NET types. Each `TryGet` returns false rather than an inexact or changed value:

```csharp
// On XferInteger
public bool TryGetInt64(out long value);   // and Int32, UInt64, etc., via TryGet<T> for the integral types
// On XferInteger and XferFractional
public double ToDouble(out bool exact);    // exactness as xfer-js asNumber: exact binary expansion compared in BigInteger arithmetic
public bool TryGetDecimal(out decimal value);   // false if System.Decimal cannot hold the value exactly
// On XferFractional
public bool TryGetDecimalParts(out BigInteger digits, out int exponent);   // xfer-js asDecimal; false for inf, -inf, nan
// On XferTemporal
public bool TryGetDateOnly(out DateOnly value);
public bool TryGetTimeOnly(out TimeOnly value);
public bool TryGetDateTime(out DateTime value);              // local-datetime as Unspecified; datetime as Utc
public bool TryGetDateTimeOffset(out DateTimeOffset value);  // datetime only; keeps the offset
```

Temporal helpers return false, and never round or clamp, for every value a .NET type cannot hold exactly:

- second 60, because no .NET date or time type holds a leap second;
- a non-zero fractional-second digit after the seventh, because the .NET tick is 100 ns (`.250000000` is held exactly and is accepted);
- the year 0000, which the specification allows and `DateOnly` and `DateTime` do not;
- an offset beyond plus or minus 14:00, which the specification allows up to 23:59 and `DateTimeOffset` refuses; and
- a datetime whose conversion to UTC falls before 0001-01-01 or after 9999-12-31. `TryGetDateTimeOffset` keeps the offset, which the JavaScript `Temporal.Instant` cannot; this is the one helper where .NET is better than the reference.

`ToPlain()` from xfer-js becomes the mapper's handling of `object` (section 10.3); there is no separate projection.

### 4.3 Lossless tree

The tree is the xfer-js tree (`../xfer-js/src/types.ts`, "Lossless tree"), with one change of representation for memory (section 6.3): tokens and trivia are stored in flat arrays of small structs, and positions are computed from a line table when asked for. The public surface is read-only views:

```csharp
public sealed class XferSyntaxTree
{
    public XferAnnotationNode? Head { get; }
    public XferNode? Root { get; }
    public IReadOnlyList<XferToken> Trailing { get; }
    public XferToken EndOfFile { get; }
    public string Text { get; }                    // the normalized text
    public XferPosition GetPosition(int utf8Offset);
    public IEnumerable<XferToken> Tokens { get; }  // every token in order, ending with EndOfFile
}
public readonly struct XferToken                 // Kind, Span (offset, length in UTF-8 bytes), Text, LeadingTrivia, Content, Delimiter, Keyword
public readonly struct XferTrivia                // Kind (Whitespace, Comment), Span, Text, Run
public abstract class XferNode                   // XferObjectNode, XferSequenceNode, XferScalarNode, XferErrorNode, each with Annotations
public sealed class XferPairNode                 // Key?, Value?, Annotations, Skipped (tokens and annotation nodes, in order)
public sealed class XferAnnotationNode           // Open, Name?, Value?, Close?
```

The lossless property holds as in xfer-js: concatenating every token's leading trivia and text, in order and ending with `EndOfFile`, reproduces `Text` exactly. A test asserts it for every suite document.

### 4.4 Diagnostics

```csharp
public enum XferSeverity { Error, Warning, Info }
public readonly record struct XferPosition(int Offset, int Line, int Column);   // UTF-8 byte offset, 1-based line and scalar-value column
public sealed record XferDiagnostic(XferDiagnosticCode Code, XferSeverity Severity, string Message, XferPosition Start, XferPosition End);
public enum XferDiagnosticCode { XFER0101 = 101, ... }   // the table in specification 10, one member per row
public static class XferDiagnostics { public static XferSeverity SeverityOf(XferDiagnosticCode code); public static string SummaryOf(XferDiagnosticCode code); }
```

Messages are English and follow xfer-js: the summary from the specification's table, then `: ` and the specifics, including the hints xfer-js gives for characters brought from other formats (`,`, `:`, `'`, parentheses, `+`, a leading point, `~`, `?`). The suite does not compare messages, and the differential test compares codes and positions only, but the messages should match where practical so the two CLIs read alike.

`XferException : Exception` has `IReadOnlyList<XferDiagnostic> Diagnostics`, and its `Message` is built as xfer-js builds `XferError`'s.

### 4.5 Writing

```csharp
public static class XferWriter
{
    public static string Canonical(XferParseResult result, XferWriteOptions? options = null);   // keeps the head; throws XferException if !result.Ok
    public static string Canonical(XferValue value, XferWriteOptions? options = null);         // validates (specification 9)
    public static void Canonical(XferValue value, TextWriter writer, XferWriteOptions? options = null);
}
public sealed record XferWriteOptions { public XferLimits Limits { get; init; } = XferLimits.Default; }
```

A value that no document could hold is an `ArgumentException` naming the fault, as xfer-js throws `TypeError`. A value nested past the depth limit, or whose output would exceed the size limit, is an `XferException` with `XFER0602` or `XFER0601` (specification 6), never an `OutOfMemoryException`.

### 4.6 JSON

```csharp
public static class XferJson
{
    public static XferJsonResult<string> ToJson(XferValue value, XferToJsonOptions? options = null);
    public static XferJsonResult<XferValue?> FromJson(string json, XferFromJsonOptions? options = null);
    public static XferJsonResult<XferValue?> FromJson(ReadOnlySpan<byte> utf8Json, XferFromJsonOptions? options = null);
}
public readonly record struct XferJsonResult<T>(T Value, IReadOnlyList<XferDiagnostic> Diagnostics);
public sealed record XferToJsonOptions { public bool StringifyBigIntegers { get; init; } public int Indent { get; init; } }   // Indent clamped 0..10
public sealed record XferFromJsonOptions { public XferLimits Limits { get; init; } = XferLimits.Default; }   // only Depth applies
```

### 4.7 Object mapper

```csharp
public static class XferSerializer
{
    public static string Serialize<T>(T value, XferSerializerOptions? options = null);
    public static string Serialize(object? value, Type type, XferSerializerOptions? options = null);
    public static Task SerializeAsync<T>(Stream stream, T value, XferSerializerOptions? options = null, CancellationToken ct = default);
    public static XferValue SerializeToValue<T>(T value, XferSerializerOptions? options = null);
    public static T? Deserialize<T>(string text, XferSerializerOptions? options = null);
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8, XferSerializerOptions? options = null);
    public static object? Deserialize(string text, Type type, XferSerializerOptions? options = null);
    public static ValueTask<T?> DeserializeAsync<T>(Stream stream, XferSerializerOptions? options = null, CancellationToken ct = default);
    public static ValueTask<object?> DeserializeAsync(Stream stream, Type type, XferSerializerOptions? options = null, CancellationToken ct = default);   // the engine's input formatter uses this
    public static Task SerializeAsync(Stream stream, object? value, Type type, XferSerializerOptions? options = null, CancellationToken ct = default);
    public static T? Deserialize<T>(XferValue value, XferSerializerOptions? options = null);
}
```

The shape follows `System.Text.Json.JsonSerializer` on purpose, so that a .NET developer already knows it. Section 10 is the mapping.

## 5. Text: normalization, UTF-8 and positions (`Text/`)

The core works on UTF-8 bytes. This is the main difference from xfer-js, which works on a JavaScript string and computes UTF-8 offsets from code points. Working on bytes makes the offset the index, makes the size limit a length check, and avoids a UTF-16 copy of the document.

1. **Bytes in.** If the input is longer than `DocumentBytes`, report `XFER0601` at the origin and stop. Validate with `System.Text.Unicode.Utf8.IsValid` (`net8.0` and later, documented to reject overlong forms, surrogate code points and bytes past U+10FFFF). On failure report `XFER0701` at the origin, as xfer-js does, although .NET could give the position (`Utf8.ToUtf16` reports where it stopped). Section 16, question 2 asks whether both implementations should report the real position; the suite uses `*` for these cases, so either is conformant today.
2. **String in.** Scan for a lone surrogate (`char.IsHighSurrogate`/`IsLowSurrogate` pairs). If there is one, report `XFER0701` at its position, measured as xfer-js measures it (`../xfer-js/src/normalize.ts`). Otherwise encode to UTF-8 with `Encoding.UTF8.GetBytes` into a pooled buffer and continue as for bytes. The size limit is checked on the UTF-8 length.
3. **Byte order mark.** Drop a leading `EF BB BF`.
4. **Line endings.** Replace CRLF and lone CR with LF, into a new buffer only if the input has a CR. Positions are in this normalized text (specification 9).
5. **Line map.** Record the offset of each line start during normalization, so that one position is a binary search plus a count of scalar values (bytes that are not `10xxxxxx` continuation bytes) from the line start to the offset. Counting from the line start for each diagnostic would be quadratic on a one-line document with many faults, so the parser does not resolve positions as it goes. It records byte offsets, and at the end resolves all of them in one forward pass in sorted order, as `../xfer-js/src/json/read.ts` does. `GetPosition` for a single offset uses the line map directly.

`Text/Unicode.cs` holds the identifier and whitespace classes from specification 4.3 and 3.3, ported from `../xfer-js/src/unicode.ts`, working on `Rune` values decoded with `Rune.DecodeFromUtf8`.

## 6. Lexer and parser (`Syntax/`)

### 6.1 Lexer

Port `../xfer-js/src/lexer.ts` and `../xfer-js/DESIGN.md` section 5, working over `ReadOnlySpan<byte>`. The lexer is a `ref struct` over the normalized bytes that appends tokens and trivia to the arrays of section 6.3 and diagnostics to a list. Every ASCII decision is a byte comparison; a non-ASCII byte starts a `Rune` decode for the identifier class test. String content is sliced from the byte span and decoded to a `string` once, when the value is projected.

The xfer-js behaviours that are not in the specification are part of the port: the hints, the recovery after an over-long delimiter run (read on to the exact closer), the separation check, and the handling of `-inf`, signed temporals and uppercase radix prefixes. `../xfer-js/test/lexer.test.ts` is a list of cases to port as unit tests.

### 6.2 Parser

Port `../xfer-js/src/parser.ts` and `../xfer-js/DESIGN.md` section 7, including the departures recorded in its section 16 (stray separators skipped, a wrong closer closing the collection it was meant for, the overflow frame past the depth limit, the open-bracket counts that keep wrong-closer handling constant time, annotation completeness before validation). `../xfer-js/test/parser.test.ts` is a list of cases to port.

The value projection (`Model/Projection.cs`) ports `../xfer-js/src/value.ts`: iterative, refusing a tree with an error node, a missing value, an unclosed container, or trailing tokens; a repeated key keeps the last value.

### 6.3 Memory

The xfer-js tree costs about 100 bytes of heap per byte of input, because each token holds objects for its positions and an array for its trivia. This implementation stores the token stream as a `Token[]` of structs (kind, start offset, length, first trivia index, trivia count, and the delimiter run or keyword packed into one field) and a `Trivia[]` of structs (kind, start, length, run). Positions are not stored; `XferSyntaxTree.GetPosition` computes them from the line map. Nodes refer to tokens by index. Strings for token text are created when asked for.

The target is at most 24 bytes of managed heap per input byte for the tree of a typical document, measured by a test that parses a generated 10 MB document and reads `GC.GetAllocatedBytesForCurrentThread` before and after. Section 17 records the measured figure. This changes nothing visible to a caller; it exists because specification 3.4 now advises that a parser of untrusted input choose limits well below the minimums, and a smaller tree raises the limit a server can afford.

## 7. Diagnostics table

`Diagnostics/XferDiagnosticCode.cs` and the table behind `XferDiagnostics` are transcribed from specification section 10. A test parses the table out of `suite/spec/xferlang.md` and compares it with the enum and the table, row for row and summary for summary, as `../xfer-js/test/diagnostics.test.ts` does. A second test parses the exclusion sentence in specification 4.3, expands it to its code points, and compares the identifier class with it for every code point from U+0080 to U+10FFFF, as `../xfer-js/test/unicode.test.ts` does.

## 8. JSON (`Json/`)

**Reader.** Port `../xfer-js/src/json/read.ts`: iterative, strict RFC 8259, number lexemes kept (`1.50` is the fractional `1.50`, `-0` the integer `-0`), `XFER0905` at the key for a duplicate (last value kept), `XFER0906` at the fault for malformed JSON, `XFER0907` for a string or key holding U+000D or a lone surrogate (reading continues so every such string is reported), `XFER0602` once for nesting past the depth limit (specification 8.2), diagnostics in order of position. The reader works on UTF-8 bytes; a `string` overload encodes first, after checking for a lone surrogate in the raw text, which is `XFER0907` like an escaped one.

xfer-js has no byte overload, so its CLI defines the behaviour to match. The byte overload drops a leading UTF-8 byte order mark (RFC 8259 permits a parser to ignore one, and the xfer-js CLI's decoder drops it). Invalid UTF-8 is `XFER0906` at the origin with the message "input is not valid UTF-8", and no value. The `string` overload treats a leading U+FEFF as malformed JSON (`XFER0906`), as xfer-js `fromJson` does.

`System.Text.Json.Utf8JsonReader` is not used. It does not keep the integer and fractional distinction or report duplicate keys, and its handling of an escaped lone surrogate is an exception from `GetString` whose message is not a contract, so distinguishing `XFER0907` from malformed JSON would rest on undocumented behaviour. It is used in tests as a differential oracle for well-formedness only.

**Writer.** Port `../xfer-js/src/json/write.ts`: the compact form of specification 8.1 byte for byte, an indented form (indent clamped to 0..10), `XFER0901` above 2^53 or below -2^53 (2^53 itself not flagged), `XFER0902` with `null` written in place, `XFER0903` and `XFER0904` once each with a count and the first location, JSON Pointers in messages.

## 9. Writers (`Writing/`)

### 9.1 Canonical writer

Port `../xfer-js/src/canonical.ts` and `../xfer-js/src/strings.ts`, with the validation of specification 9 for a value built by a program and the depth and size refusals of specification 6. A `XferParseResult` is trusted and keeps its head; a result with an error is refused with its own diagnostics. Output is built in a pooled `StringBuilder` or written to a `TextWriter`.

### 9.2 String delimiters

`StringDelimiters.Choose(ReadOnlySpan<char> content, int runLimit)` implements rule 10 as xfer-js `chooseDelimiter` does, including the fallback past the limit (specification 6, rule 10).

There is no compact (single-line) writer. The engine's service has a `compact` flag, but nothing in the engine passes it, and its output formatter writes the indented form, so no consumer needs one. If one is wanted later it should be defined in the specification first, so that every implementation writes the same bytes.

## 10. Object mapper (`Serialization/`)

### 10.1 Model

Deserialization is two steps: parse to an `XferValue` with the core, then map the value onto the CLR type. Serialization is the reverse: map the CLR value to an `XferValue`, then write it canonically. The mapper never sees text, so it cannot disagree with the parser about what a document says, and a mapping error has the position of the value it is about, taken from a side table the projection keeps when the mapper asks for it.

### 10.2 Options and attributes

```csharp
public sealed class XferSerializerOptions
{
    public XferNamingPolicy? PropertyNamingPolicy { get; init; }   // null: the CLR name as declared
    public bool PropertyNameCaseInsensitive { get; init; }         // false
    public XferUnmappedMemberHandling UnmappedMemberHandling { get; init; }   // Skip (default) or Disallow
    public XferIgnoreCondition DefaultIgnoreCondition { get; init; }          // Never (default) or WhenWritingNull or WhenWritingDefault
    public XferLimits Limits { get; init; } = XferLimits.Default;
    public IList<XferConverter> Converters { get; }
}
[XferPropertyName("slug")]   [XferPropertyName("url", Aliases = new[] { "link" })]   [XferIgnore]   [XferRequired]   [XferConstructor]   [XferConverter(typeof(...))]   [XferSymbol]
public abstract class XferNamingPolicy        // as JsonNamingPolicy
{
    public static XferNamingPolicy CamelCase { get; }
    public static XferNamingPolicy KebabCaseLower { get; }
    public static XferNamingPolicy SnakeCaseLower { get; }
    public abstract string ConvertName(string name);
}
```

Attribute and option names follow `System.Text.Json` where the meaning is the same. C# `required` members and `[XferRequired]` both make a missing member an error. A type with a single public constructor whose parameters match members by name (records included) is constructed through it; `[XferConstructor]` chooses among several.

**Members.** A member is a public instance property with a public getter, or a public instance field. A property with a public getter and no setter (and no matching constructor parameter) is written but not read, and a key for it is treated as an unmapped key. A setter-only property is not a member. `[XferIgnore]` removes a member in both directions; `[XferPropertyName]` and the naming policy give its key, in that order of precedence. `Aliases` on `[XferPropertyName]` are further keys accepted on reading and never written; an object holding both the key and an alias, or two aliases, for one member is an error at the second, as for two case-insensitive matches. Two members of one type with the same key are an `InvalidOperationException` when the type's metadata is built, before any data is read, as in `System.Text.Json`.

**Errors while mapping.** Each of these is an `XferException` whose diagnostic has the position of the object or value concerned and a JSON Pointer in its message:

- an unknown key under `XferUnmappedMemberHandling.Disallow`, at the key;
- a missing required member, at the object;
- with `PropertyNameCaseInsensitive`, two keys of one object that match the same member, at the second key (the document is ambiguous, so neither value is chosen);
- a kind mismatch or a value out of range, at the value (section 10.3).

The code for all four is the one section 16, question 3 settles.

### 10.3 Reading: XferLang to CLR

| CLR type | Accepts | Notes |
|---|---|---|
| `string` | string, symbol | A symbol is accepted so that `mode dark` reads into a string member. |
| `char` | string or symbol of one UTF-16 unit | |
| `bool` | `true`, `false` | |
| `sbyte` to `ulong`, `nint`, `nuint` | integer | Out of range is `XFER0303`. |
| `BigInteger` | integer | |
| `double`, `float`, `Half` | integer, fractional, `inf`, `-inf`, `nan` | Nearest value. Losing precision is not an error, but a finite value that becomes infinite (`1e400`) or a non-zero value that becomes zero (`1e-400`) is `XFER0303`, because the type cannot hold it (specification 4.5). |
| `decimal` | integer, fractional | `XFER0303` when `decimal` cannot hold the value exactly. |
| enum | symbol or string matching a member name (through the naming policy), or integer | A `[Flags]` enum also accepts a sequence of names. |
| `DateOnly` | date | |
| `TimeOnly` | time | Every value the helpers of section 4.2 refuse is `XFER0303` here, for this row and the next three. |
| `DateTime` | date (midnight, Unspecified), local-datetime (Unspecified), datetime (converted to Utc) | |
| `DateTimeOffset` | datetime | The offset is kept. |
| `TimeSpan`, `Guid`, `Uri`, `Version` | string | Parsed with the type's invariant-culture `Parse`. |
| `T?` | `null` or what `T` accepts | |
| arrays, `List<T>`, `IEnumerable<T>`, `IReadOnlyList<T>`, `ICollection<T>`, `HashSet<T>`, `ImmutableArray<T>` | sequence | |
| `Dictionary<string, T>`, `IDictionary<string, T>`, `IReadOnlyDictionary<string, T>` | object | Key order is kept where the target keeps order. |
| `object` | anything | object to `Dictionary<string, object?>` (ordered), sequence to `List<object?>`, string and symbol to `string`, integer to `long` when it fits else `BigInteger`, fractional to `double`, temporal to `DateOnly`, `TimeOnly`, `DateTime` or `DateTimeOffset`, bool, null. |
| `XferValue` and subclasses | anything of that kind | The value as parsed, annotations included. |
| class, struct, record | object | Members by name; unknown keys skipped unless `Disallow`. |

A mismatch is an `XferException` with a diagnostic whose code is `XFER0303` when it is a range or precision failure. For a kind mismatch (a string where an integer is needed, for instance) there is no code in the specification, because the specification describes documents, not mappings. Section 16, question 3 proposes adding a code for it; until then the mapper uses `XFER0303` with a message that says what was expected and what was found, and the diagnostic's position is the value's position in the source with a JSON Pointer to it in the message.

Annotations are ignored by the mapper except through `XferValue`-typed members. A future version may use `tag` for polymorphism; nothing in this version reads it.

### 10.4 Writing: CLR to XferLang

| CLR value | XferLang |
|---|---|
| `string` | string; a symbol when the member has `[XferSymbol]` and the text is a valid symbol, otherwise a string |
| `char` | string of that one character |
| `bool` | `true` or `false` |
| integral types, `BigInteger` | integer in decimal |
| `double`, `float`, `Half` | fractional: the shortest round-trip spelling, `value.ToString("R", CultureInfo.InvariantCulture)`, with `.0` appended when that spelling has neither a `.` nor an exponent so that it reads back as fractional; infinities and NaN as `inf`, `-inf`, `nan` |
| `decimal` | fractional: `ToString(CultureInfo.InvariantCulture)`, with `.0` appended when there is no `.` (`1m` writes `1.0`) |
| enum | the symbol of the member name through the naming policy when the value is one defined member and the name is a valid symbol; for a `[Flags]` value that is a combination of defined members, a sequence of those symbols; otherwise (an undefined value) the integer |
| `DateOnly` | date |
| `TimeOnly` | time, fractional seconds only when non-zero, trailing zeros removed |
| `DateTime` | Utc: datetime with `Z`. Local: converted to UTC and written with `Z`, so that the bytes do not depend on the host's time zone. Unspecified: local-datetime, even at midnight; a member that holds a date should be `DateOnly` to round-trip as a date. |
| `DateTimeOffset` | datetime with its offset (`Z` when the offset is zero) |
| `TimeSpan`, `Guid`, `Uri`, `Version` | string, invariant culture |
| collections | sequence |
| dictionaries with string keys | object, in the dictionary's enumeration order |
| `null` | `null` (or omitted, under `DefaultIgnoreCondition`) |
| `XferValue` | the value itself, annotations included (validated as specification 9 requires) |
| objects, anonymous types included | object, members in declaration order, base class members first |

A member declared as `object` (or an interface, or a base class) is written by the rules for its runtime type, as `System.Text.Json` does. That is what the engine's Swagger filter needs for its `Dictionary<string, object?>` examples and anonymous types.

The `xfer` head is never written by the mapper.

### 10.5 Reference cycles

A cycle on write is detected with a depth check (the depth limit) and reported as `XFER0602`; there is no reference preservation. Specification 9 does not allow references and the format has no way to express them.

### 10.6 Converters

`XferConverter<T>` with `Read(XferValue value, XferReadContext context)` and `Write(T value, XferWriteContext context) : XferValue` lets a program add a type. Converters work on values, not text, so they cannot produce an invalid document.

### 10.7 Cache

Type metadata (`XferTypeInfo`: members, constructor, converters) is built once per type and options instance and cached, as `System.Text.Json` does. Options become immutable after first use.

### 10.8 The seam for a source generator

All reflection is in one place, the `XferTypeInfo` resolver. A later source generator produces resolvers for named types, through an options property `TypeInfoResolver`, without changing `XferSerializer`'s surface. It is out of scope for this version, and the trimming annotations of section 3 say so to callers.

## 11. The `xfer` tool (`ParksComputing.XferLang.Tool`)

A `dotnet tool` with command name `xfer`, implementing `../xfer-js/DESIGN.md` section 10 and the behaviour recorded in its section 16 for step 8: `validate`, `fmt [--write]`, `canon`, `to-json [--pretty] [--stringify-big-ints]`, `from-json`, `--json`, `--version`, `--help`, the same exit codes, the same GNU diagnostic format, `<stdin>` for standard input, informational diagnostics only with `--json`. It accepts the same aliases and conventions as the xfer-js CLI: `-h` for `--help`, `-` for standard input, and `--` to end options. For every command except `--version` (each tool prints its own package's version), its standard output and exit code are byte-identical to the xfer-js CLI's for every input, which the differential test checks. That includes `from-json` on invalid UTF-8, where xfer-js prints `FILE: error: input is not valid UTF-8` to standard error and exits 1. Argument parsing is by hand; there is no dependency.

## 12. Testing

### 12.1 Frameworks

xUnit v3 for tests, FsCheck for property tests, coverlet for coverage, BenchmarkDotNet for benchmarks. Versions are pinned in `Directory.Packages.props` at the time the scaffold is written.

### 12.2 The suite

`suite/` is the `xferlang` repository as a submodule, pinned to a commit. A txtar reader (`../xfer-js/test/txtar.ts` is the reference, and `history/FIRST_IMPLEMENTATION_FINDINGS.md` section 3 has the suffix trap) feeds theory data, one test case per stem, named by its id, for `valid.txtar`, `invalid.txtar`, `bytes/`, `to-json.txtar` and `from-json.txtar`. The assertions are those of `../xfer-js/DESIGN.md` section 11.4 and of `../xfer-js/test/json.test.ts` for the JSON archives: canonical form byte for byte, the tagged value structurally (with `System.Text.Json` reading the expected `.value.json`, which is safe because its numbers are strings), the lossless property, idempotence, and every listed diagnostic at its position.

### 12.3 The expected-failures ratchet

`test/expected-failures.txt` lists every case id at the start. A listed case is expected to fail, and the test fails if it passes, so a line must be deleted when a case starts passing. The list shrinks to empty by step 7, when the JSON archives are implemented.

### 12.4 Property and generated tests

Port these from xfer-js: the embedding property over the five alphabets and the run-assembled strings (`../xfer-js/test/strings.test.ts`), the whitespace invariant over every valid suite document, the seeded document generator with its coverage guard (`../xfer-js/test/generator.ts`), "never throws" over random strings and bytes, the JSON round trip and the differential well-formedness check (here against `Utf8JsonReader`), and the exactness property of `ToDouble`.

### 12.5 Mapper tests

Every row of the tables in sections 10.3 and 10.4, both directions, including each failure, and each error of section 10.2.

Every valid suite document is read with `Deserialize<object>`, written with `Serialize`, and read and written again. The mapping to `object` loses symbols, number spellings, temporal kinds and annotations, so the first output is not compared with the input. The assertions are that the second output equals the first byte for byte (the mapping is stable after one pass), and that `XferJson.ToJson` of the input and of the first output are equal apart from the loss the `object` table states.

The parkscomputing.com documents are test fixtures: step 8 copies `sitenav.xfer` and `admin-menu.xfer` into `test/fixtures/` both as they are today (0.x) and as converted by hand (section 15, step 1). A test deserializes the converted copies into a copy of the engine's `NavNode` and admin-menu types with this library, deserializes the 0.x copies with the legacy package `ParksComputing.Xfer.Lang` (a test-only reference, pinned to the version the engine uses), and compares the two object graphs member by member. The conversion is thus verified before the engine changes.

### 12.6 Differential test against xfer-js

A test project (`DifferentialTests`, skipped unless `XFER_JS_CLI` points at a built xfer-js `dist/cli.mjs` and `node` is on the path) generates documents with the ported generator and fuzzed texts, runs both CLIs on each (`validate --json`, `fmt`, `to-json`, `from-json`), and requires identical exit codes, identical standard output, and identical diagnostic codes and positions. CI runs it with xfer-js checked out at the version pinned in a file in this repository. This is the ROADMAP's requirement that the two implementations be run against each other.

### 12.7 Coverage

Line coverage of `src/ParksComputing.XferLang` at least 95%, enforced in CI.

## 13. Build, CI and release

- **CI (`ci.yml`):** on push and pull request; matrix ubuntu, windows, macos; `dotnet build -warnaserror`, `dotnet test` (both target frameworks), the tool tests, the trim check (`dotnet publish` of a small AOT console app that uses the core), coverage on ubuntu, and the differential job on ubuntu with Node 24 and xfer-js.
- **Versioning:** `Version` in `Directory.Build.props`, pre-release suffix `-next.N` until 1.0, matching xfer-js.
- **Release (`release.yaml`):** on a tag `v*`, build, test, pack both packages, and push to NuGet with NuGet trusted publishing (OIDC from GitHub Actions, no stored API key), then create the GitHub release. NuGet's trusted publishing policy names the workflow file; the file is `release.yaml` to match the xfer-js convention, so that the same mistake is not made twice.
- **Packages:** `ParksComputing.XferLang` and `ParksComputing.XferLang.Tool`, MIT, README in the package, icon, SourceLink, symbols, `PackageProjectUrl` `https://xfer.parkscomputing.com`, `RepositoryUrl` the GitHub repository. The package id differs from the legacy library's (`ParksComputing.Xfer.Lang`), so the two can be referenced side by side during the migration.

## 14. Order of implementation and definition of done

Each step is done when its tests pass in CI on every leg, a cold review by a separate agent with no prior context has been run on it, and its findings are fixed or recorded in section 17.

1. **Scaffold.** Repository, submodule, solution, `Directory.*.props`, `global.json`, CI green on an empty library.
2. **Harness.** txtar reader, tagged values, theory data for every archive, all listed in `expected-failures.txt`, all failing with "not implemented".
3. **Text and diagnostics.** Normalizer, line map, Unicode classes, the diagnostics table and its spec-text tests, limits.
4. **Lexer.** All lexical invalid cases at their positions; every valid document lexes and reproduces exactly; the ported lexer unit tests.
5. **Parser, tree and value model.** All invalid cases; the lossless property; the value model of every valid case; the generator; the memory measurement of section 6.3.
6. **Writers.** Canonical writer and delimiter choice; every canonical case; the embedding and whitespace properties.
7. **JSON.** Both directions; both JSON archives; the differential well-formedness test; `expected-failures.txt` empty.
8. **Object mapper.** Sections 10.1 to 10.7 with the tests of section 12.5, including the hand conversion of the parkscomputing.com documents into `test/fixtures/` (section 15, step 1, done here so that the mapper is tested against them).
9. **Tool and differential test.** The `xfer` tool and the cross-implementation test of section 12.6.
10. **Hardening and packaging.** Coverage floor, trim check, public API files, package validation, README, benchmarks recorded in section 17, release workflow.
11. **Publish `0.1.0-next.0`** to NuGet. This needs Paul: a NuGet account, the package id reserved under it, and the trusted publishing policy (section 13).
12. **parkscomputing.com migration** (section 15).

Model plan, as for xfer-js: Opus for steps 4, 5 and 8 (the lexer, the parser and the mapper, where subtle behaviour lives) and for every review; Sonnet for the rest.

## 15. Migrating parkscomputing.com

The engine (`../paulmooreparks/parkscomputing/Application/parkscomputing-engine`, `net10.0`) uses the legacy library through `XferConvert.Serialize` and `XferConvert.Deserialize`, the `Formatting.Pretty` and `Formatting.None` options, `[XferProperty]` attributes on `NavNode` and its admin-menu types, an MVC input and output formatter for `application/xfer`, and Swagger filters. Almost all of it goes through the engine's own `IXferService`, which was written to confine a change like this one.

`AdminMenu.Parse` and `NavService.ParseRoot` catch every exception and fall back to a default, so a 0.x document that the migration misses does not fail loudly. It shows up as a default menu or an empty site. The list below is therefore exhaustive on purpose, and step 5 adds the check that makes a miss visible.

1. **Every 0.x document.** Each is converted by hand, because they are small and their comments matter. The known changes are the head (`<! document { xferlang "0.15" } !>` becomes `<! xfer 1 !>`) and `~true` becoming `true`; a search for every other 0.x sigil in specification Appendix A comes first. The documents are:
   - `wwwroot/sitenav.xfer` in the site content (`OneDrive/Documents/parkscomputing.com`, 422 lines);
   - `etc/admin-menu.xfer` in the site content;
   - the engine repository's own `wwwroot/sitenav.xfer`;
   - `ServerFiles.DefaultAdminMenu`, a 0.x document held as a C# string constant, which the engine writes to `/etc` on first start;
   - each administrator's `~/.config/admin-menu.xfer` on the live server, which only Paul can reach; and
   - the 0.x text inside the engine's `AdminMenuTests` and `SiteMenuTests`.
2. **Hand-written XferLang.** `Api/ApiNotFoundMiddleware.cs` builds an XferLang document with string interpolation and escapes `\` and `"` with backslashes, which is wrong under verbatim strings: the backslashes become part of the value. `Api/ErrorExamplesOperationFilter.cs` also writes XferLang by hand. Both change to building a value (or an object) and calling `XferWriter.Canonical` or `XferSerializer.Serialize`.
3. **Package and attributes.** Replace `ParksComputing.Xfer.Lang` with `ParksComputing.XferLang`. Change `XferService` to call `XferSerializer`; its `compact` flag has no caller and is removed. Rename `[XferProperty]` to `[XferPropertyName]`. On `NavNode`, mark the aliases `Link` (of `Url`) and `Links` (of `Nav`) with `[XferIgnore]` for writing; they must still be read, so the alias becomes a second accepted key on the main member rather than a second member. That needs a small mapper feature, `[XferPropertyName("url", Aliases = new[] { "link" })]`, which section 10.2 gains. `DerivedUrl` has no attribute and is computed, so it gets `[XferIgnore]`.
4. **Formatters and Swagger.** The input formatter calls `DeserializeAsync(Stream, Type)` and the output formatter `SerializeAsync(Stream, object?, Type)`, both in section 4.7. The Swagger filter serializes dictionaries and anonymous types, which section 10.4 covers. The media type stays `application/xfer`.
5. **Making a miss visible.** A test in the engine reads every `.xfer` file it ships and the `DefaultAdminMenu` constant with the new library and fails on any diagnostic. Before deploying, a one-off run over the live `/etc` and every home's `.config/admin-menu.xfer` does the same. `NavService` and `AdminMenu` log at error level, not warning, when a document fails to parse.
6. **Deploy** through the engine's normal release process, after a test that deserializes the converted files with the new library and compares the result, member by member, with what the legacy library produced from the old files.

The sitenav file has no dates, so the `DateTime?` members (`Date`, `Updated`) are not exercised by it; their mapping in section 10.3 is still tested in section 12.5.

This step changes another repository and needs Paul's go-ahead when it is reached.

## 16. Questions for Paul

1. **Target frameworks.** This design targets `net8.0` and `net10.0` and drops `netstandard2.0` (section 3). Recommendation: confirm. Keeping `netstandard2.0` would add a dependency on `System.Memory` and remove `DateOnly`, `TimeOnly` and the UTF-8 validation API from that target.
2. **The position of an invalid-UTF-8 diagnostic.** xfer-js reports `XFER0701` at the origin because the JavaScript decoder does not say where it failed; .NET can report the first bad byte. Recommendation: have the specification say a parser SHOULD report the first invalid byte where it can and MAY report the origin, and leave xfer-js as it is.
3. **Codes for mapping errors.** The specification has `XFER0303` for a value out of range for its target and nothing for the other mapping errors of section 10.2. Recommendation: add a new `08xx` range to specification 10, reserved for object mappers, with `XFER0801` (value has the wrong kind for its target), `XFER0802` (key matches no member), `XFER0803` (required member missing) and `XFER0804` (two keys match one member), all errors, so that every implementation's mapper reports the same codes. The alternative is to leave mapping out of the specification and have this mapper use `XFER0303` for all of them with distinguishing messages.

## 17. Decisions recorded

The design was reviewed cold by a separate Opus agent on 2026-10-19 before any code was written. It found thirteen problems, all fixed in this document: C# 14 is unsupported on `net8.0` (now C# 12); the temporal limits missed year 0000, offsets beyond 14:00 and UTC overflow; the migration missed four places that hold 0.x text, two places that write XferLang by hand, and the `NavNode` aliases; the compact writer had no consumer (removed); three API shapes could not compile or keep their promise (`XferNamingPolicy`, `XferLimits`, the `long` sizes); the write table had a culture-dependent number format, a host-dependent `DateTime` and missing rows; member rules and mapping errors were unstated; per-diagnostic position lookup was quadratic; the JSON byte overload had no rule for a BOM or invalid UTF-8; an async overload the engine uses was missing; byte-identical CLI output was impossible for `--version`; the step order needed the JSON archives and the fixtures earlier; and the `object` round trip had no oracle.
