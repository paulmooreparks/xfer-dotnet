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

- **Target framework: `net10.0` only** (ruled by Paul, 2026-10-19). .NET 10 is the current long-term-support release, supported until November 2028, and parkscomputing.com runs on it. .NET 8 and .NET 9 both leave support on 10 November 2026, before this package ships, so a target for either would serve only unpatched runtimes. A library built for `net10.0` runs unchanged on .NET 11 and later. `netstandard2.0` is not targeted: it lacks `DateOnly` and `TimeOnly`, needs `System.Memory` for spans (a dependency), and no consumer needs it. A program still on .NET 8 can stay on the legacy library until it upgrades.
- **C# 14,** the language version that shipped with .NET 10 and is supported on it. `Nullable` enabled, `TreatWarningsAsErrors`, `AnalysisLevel` latest-recommended, `EnforceCodeStyleInBuild`, `Deterministic`, `ContinuousIntegrationBuild` in CI, SourceLink, embedded symbols.
- **Trimming and AOT.** `IsAotCompatible` is set. The core (everything except the mapper) is trim- and AOT-safe and must build without trim warnings. The reflection-based mapper is annotated with `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]` on its entry points; section 10.8 describes the seam for a source generator later.
- **Public API tracking.** `Microsoft.CodeAnalysis.PublicApiAnalyzers` with `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`, so every change to the public surface is visible in review. Package validation (`EnablePackageValidation`) is on from the first release.
- **SDK.** `global.json` pins the .NET 10 SDK feature band (10.0.400 or later, `rollForward: latestFeature`). The development machine has 10.0.402 as of 2026-10-19. CI installs it with `actions/setup-dotnet`.

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

The value model is immutable. `XferObject` keeps insertion order in an `OrderedDictionary<string, XferValue>` (in the base class library from .NET 9). Constructors are public, so a program can build a value; a value that no document could hold (specification 9, "Values built by a program") is refused by the writer, not by the constructor, so that a program can build and inspect a value before deciding what to do with it. That matches xfer-js.

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

1. **Bytes in.** If the input is longer than `DocumentBytes`, report `XFER0601` at the origin and stop. Validate with `System.Text.Unicode.Utf8.IsValid` (documented to reject overlong forms, surrogate code points and bytes past U+10FFFF). On failure, find the first invalid byte with `Utf8.ToUtf16(source, destination, out bytesRead, out _, replaceInvalidSequences: false)`, which is documented to return `OperationStatus.InvalidData` with `bytesRead` counting the valid bytes before the fault, and report `XFER0701` there (specification 9: SHOULD be at the first invalid byte). The offset is counted from after a byte order mark, and the line and column as for the normalized text before it. xfer-js reports the origin, which the specification permits for a platform that cannot locate the fault, so the differential test (section 12.6) compares only the code for this diagnostic.
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

**Reader.** Port `../xfer-js/src/json/read.ts`: iterative, strict RFC 8259, number lexemes kept (`1.50` is the fractional `1.50`, `-0` the integer `-0`), `XFER0905` at the key for a duplicate (last value kept), `XFER0906` at the fault for malformed JSON, `XFER0907` for a string or key holding U+000D or a lone surrogate (reading continues so every such string is reported), `XFER0602` once for nesting past the depth limit (specification 8.2), diagnostics in order of position. The reader works on a string (corrected 2026-10-19, see section 17, step 7): the byte overload decodes strictly and reads the string, so that every position and message matches xfer-js. A lone surrogate in the raw text of a string is `XFER0907` like an escaped one.

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
    // Settable until the options are first used, then frozen (setting one throws), as in System.Text.Json.
    public XferNamingPolicy? PropertyNamingPolicy { get; set; }   // null: the CLR name as declared
    public bool PropertyNameCaseInsensitive { get; set; }         // false
    public XferUnmappedMemberHandling UnmappedMemberHandling { get; set; }   // Skip (default) or Disallow
    public XferIgnoreCondition DefaultIgnoreCondition { get; set; }          // Never (default) or WhenWritingNull or WhenWritingDefault
    public XferLimits Limits { get; set; } = XferLimits.Default;
    public IList<XferConverter> Converters { get; }               // refuses changes once frozen
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

Attribute and option names follow `System.Text.Json` where the meaning is the same. C# `required` members and `[XferRequired]` both make a missing member an error, and so does a constructor parameter without a default value (`System.Text.Json` passes `default(T)` there unless `RespectRequiredConstructorParameters` is set, which this library has no compatibility reason to copy). A type with a single public constructor whose parameters match members by name, ignoring case (records included), is constructed through it; `[XferConstructor]` chooses among several. A type that cannot be constructed (abstract, an interface, several constructors and none marked, a parameter that matches no member) is an `InvalidOperationException` when an object is read into it, since the fault is in the program and not the document; the same type can still be written, and read from `null`.

**Members.** A member is a public instance property with a public getter, or a public instance field. A property with a public getter and no setter (and no matching constructor parameter) is written but not read, and a key for it is treated as an unmapped key. A setter-only property is not a member. `[XferIgnore]` removes a member in both directions; `[XferPropertyName]` and the naming policy give its key, in that order of precedence. `Aliases` on `[XferPropertyName]` are further keys accepted on reading and never written; an object holding both the key and an alias, or two aliases, for one member is an error at the second, as for two case-insensitive matches. Two members of one type with the same key are an `InvalidOperationException` when the type's metadata is built, before any data is read, as in `System.Text.Json`.

**Errors while mapping.** Each of these is an `XferException` whose diagnostic has the position of the object or value concerned and a JSON Pointer in its message:

- an unknown key under `XferUnmappedMemberHandling.Disallow`, at the key;
- a missing required member, at the object;
- with `PropertyNameCaseInsensitive`, two keys of one object that match the same member, at the second key (the document is ambiguous, so neither value is chosen);
- a kind mismatch or a value out of range, at the value (section 10.3).

The codes are the specification's object-mapper codes (section 9 and 10): `XFER0802` for the unknown key, `XFER0803` for the missing member, `XFER0804` for two keys matching one member, and `XFER0801` or `XFER0303` for the value, as section 10.3 says.

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
| `object` | anything | object to `Dictionary<string, object?>` (ordered), sequence to `List<object?>`, string and symbol to `string`, integer to `long` when it fits else `BigInteger`, fractional to `double`, temporal to `DateOnly`, `TimeOnly`, `DateTime` or `DateTimeOffset` (or, for a value none of them can hold, such as a leap second or the year 0000, the parsed `XferTemporal` itself, which is exact and is written back unchanged), bool, null. |
| `XferValue` and subclasses | anything of that kind | The value as parsed, annotations included. |
| class, struct, record | object | Members by name; unknown keys skipped unless `Disallow`. |

A mismatch is an `XferException` with a diagnostic whose code is `XFER0303` when it is a range or precision failure. A kind mismatch (a string where an integer is needed, for instance) is `XFER0801`. The message says what was expected and what was found, and the diagnostic's position is the value's position in the source, with a JSON Pointer to it in the message.

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

- **CI (`ci.yml`):** on push and pull request; matrix ubuntu, windows, macos; `dotnet build -warnaserror`, `dotnet test`, the tool tests, the trim check (`dotnet publish` of a small AOT console app that uses the core), coverage on ubuntu, and the differential job on ubuntu with Node 24 and xfer-js.
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

1. **Target frameworks.** Settled 2026-10-19: `net10.0` only, C# 14 (section 3).
2. **The position of an invalid-UTF-8 diagnostic.** Settled 2026-10-19 (xferlang dffcd6b): SHOULD be at the first invalid byte, MAY be at the origin where the platform cannot locate it. This implementation reports the real position (section 5).
3. **Codes for mapping errors.** Settled 2026-10-19 (xferlang dffcd6b): `XFER0801` to `XFER0804` in specification 9 and 10 (section 10.2 and 10.3).

4. **Member write order rests on undocumented behaviour.** Open, raised 2026-10-20. Section 10.4 says members are written in declaration order. Reflection does not promise that: `Type.GetProperties` and `GetFields` are documented as returning members in no particular order, and `MetadataToken` order is what compilers happen to emit, not a contract. `System.Text.Json` relies on it anyway. Today the mapper sorts by `[XferPropertyOrder]`, then base class first, then the order reflection gives, so output bytes for a type without explicit orders depend on undocumented behaviour. The documented alternative is to sort members of equal order by key, ordinally: deterministic on every runtime, at the cost of output that no longer follows the class as written (`{age 36 name "Ada"}`), and anonymous types would sort too. A middle course keeps reflection order but makes canonical byte stability a promise only for types whose members all have an order. Recommendation: sort by key; the engine's documents are written by hand, not by the mapper, so it loses nothing there.

New questions go here as they arise.

## 17. Decisions recorded

The design was reviewed cold by a separate Opus agent on 2026-10-19 before any code was written. It found thirteen problems, all fixed in this document: C# 14 is unsupported on `net8.0` (at the time C# 12; since superseded by the move to `net10.0` only and C# 14); the temporal limits missed year 0000, offsets beyond 14:00 and UTC overflow; the migration missed four places that hold 0.x text, two places that write XferLang by hand, and the `NavNode` aliases; the compact writer had no consumer (removed); three API shapes could not compile or keep their promise (`XferNamingPolicy`, `XferLimits`, the `long` sizes); the write table had a culture-dependent number format, a host-dependent `DateTime` and missing rows; member rules and mapping errors were unstated; per-diagnostic position lookup was quadratic; the JSON byte overload had no rule for a BOM or invalid UTF-8; an async overload the engine uses was missing; byte-identical CLI output was impossible for `--version`; the step order needed the JSON archives and the fixtures earlier; and the `object` round trip had no oracle.

### Step 2: the conformance harness

Done 2026-10-19 (Sonnet, then a cold Opus review whose findings are applied). 184 suite cases (valid 48, invalid 82, to-json 14, from-json 40) are generated as one xUnit test each and all are on `expected-failures.txt`. `Subject` is the single seam between the tests and the library: later steps replace its method bodies. What the review changed:

- **The lossless check is computed by the harness.** `Subject.Parse` returns the tokens with their leading trivia, and `Lossless.Reconstruct` concatenates them and is compared with `Lossless.Normalize` of the input, computed independently from the bytes, so an implementation cannot satisfy the check by returning the same string twice.
- **Member order is compared only inside XferLang objects.** The order of a tagged wrapper's own keys (`type`, `value`, `annotations`) is not specified by the suite's README, so the earlier all-keys comparison could have failed a correct implementation.
- **The JSON archives and `valid.txtar` and `invalid.txtar` are loaded strictly:** an entry of an unknown kind is an error, not skipped. Only `bytes/` tolerates stray files (it holds a README).
- **A `to-json` case's input must parse without error** before it is converted, and a `from-json` case with no expected output must report an error, as in xfer-js.

### Step 3: text, diagnostics and limits

Done 2026-10-19 (Sonnet, then a cold Opus review whose findings are applied). `Normalizer`, `LineMap`, `Unicode`, the diagnostic enum and table (generated once from the specification's table; `SpecTextTests` compares them row for row and also expands the 4.3 exclusion sentence and checks every code point from U+0080 to U+10FFFF), and `XferLimits`. 249 tests. What differs from the design text above, or was added to it:

- **The first invalid byte is found with a `Rune.DecodeFromUtf8` loop,** after `Utf8.IsValid` says there is one. Section 5 named `Utf8.ToUtf16` and its `bytesRead`; both are documented, and a randomized test (200,000 inputs of one to eight bytes from a pool heavy in CR, LF, the byte order mark and malformed sequences) checks the offset against `Utf8.ToUtf16` and the line and column against an independent computation from a string.
- **The offset of an invalid-UTF-8 fault is the raw byte offset after the byte order mark,** not an offset in the normalized text, while its line and column are the normalized text's. This is what specification 9 says; the `XferPosition` documentation now says it.
- **`XferDiagnostics.Format` and `TryParse` are public** (section 4.4 listed only `SeverityOf` and `SummaryOf`); `Format` refuses a value that is not a code.
- **The result owns its bytes.** The normalized text is always a new array, even when the input has no CR, because the input is a span the caller keeps. The pooled buffer of section 5 is not used; the string path encodes once and shares the byte path's one-pass CR rewrite.
- **Memory:** line starts are counted first and allocated once. A document of 268,435,455 line feeds (the default maximum) previously needed about 3.5 GB; it now needs the text, one `int` per line, and nothing that grows.
- **Both `Normalize` overloads refuse a null argument** with `ArgumentNullException`.

### Step 4: the lexer

Done 2026-10-19 (Opus). `Syntax/Lexer.cs` is a port of xfer-js `src/lexer.ts` over the normalized UTF-8 bytes, producing `Token` and `Trivia` structs in two flat lists (`Syntax/Tokens.cs`) and diagnostics as byte offsets (`PendingDiagnostic`), which `DiagnosticResolver` turns into positions in one sorted pass. Messages are built as xfer-js builds them, including its hints and its `JSON.stringify` quoting of the offending character, so the two lexers' messages are identical.

- **The reference lexer is the test oracle.** `tools/fixtures/lexer-fixture.mjs` runs the xfer-js lexer over every suite document (normalized) and 6,000 seeded random texts from an alphabet of awkward pieces, and writes `test/.../fixtures/lexer.jsonl` (6,184 cases, 2.4 MB): every token's kind, byte range, trivia, string content and delimiter, and keyword, and every diagnostic's code, offset, line, column and message. `LexerTests` requires the .NET lexer to agree on all of it. A deliberate fault (refusing second 60) was caught by it at once. The fixture is regenerated by hand when xfer-js's lexer changes, by building a bundle of its `lexer.ts`, `normalize.ts` and `limits.ts` with tsdown (command in the script's header).
- **The "ran on" error token** that xfer-js queues for the next call is appended directly after its scalar here, with no trivia, since the whole stream is built at once.
- **Inputs are trusted to be normalized:** valid UTF-8 with LF line endings. The lexer is internal and is only ever given `NormalizeResult.Text`.
- No conformance case leaves `expected-failures.txt` in this step, because `Subject.Parse` needs the parser (step 5).

Review of step 4 (Opus, cold): no behavioural difference from xfer-js was found in 240,000 further random texts over a much wider alphabet (every class of non-ASCII and control character, astral characters beside every ASCII decision) and 172 hand-written edge cases, under default and tight limits. Three findings, all applied:

- **`Token` was 32 bytes.** Its fields are now declared ints first and bytes last, which makes it 24 bytes; a test pins the size.
- **The lexer allocated about 31 bytes per input byte on a typical document,** two thirds of it arrays left behind when its lists doubled. The lists are now sized for one token per four bytes before lexing, and a test requires a typical document to allocate no more than 12 bytes per input byte in the lexer. A document of one-byte tokens (`[[[[`) still grows the list; the parser's memory measurement in step 5 will say whether a chunked buffer is worth it.
- **The fixture never exercised the length limits.** One random case in five now runs under tight limits (recorded in the fixture line), and the alphabet covers every class of non-ASCII and control character. The fixture now holds 670 `XFER0603` and 33 `XFER0604` diagnostics, and the comparison also requires the fields that belong to another kind of token to be empty.

The first regenerated run failed on every text that began with U+FEFF. The test helper had built its line map by normalizing the already normalized text again, which removed the U+FEFF (content at that point) and shifted every position by three bytes; the lexer was right. `LineMap.Create` now builds a map from normalized text directly.

### Step 5: the parser, the lossless tree and the value model

Done 2026-10-19 (Opus). `Syntax/Parser.cs` ports xfer-js `src/parser.ts` with its recorded departures; `Syntax/XferSyntaxTree.cs` and `Syntax/XferNodes.cs` are the public tree (nodes refer to tokens by index, and `XferToken` and `XferTrivia` are views that read the flat arrays); `Model/XferValue.cs` is the value model (immutable, structural equality and hashing without recursion, `XferObject` over `OrderedDictionary`); `Model/Projection.cs` ports `src/value.ts`; `XferDocument` is the entry point. All 82 invalid suite cases now pass and are off `expected-failures.txt`; the 48 valid cases parse to their value models and lossless trees (`ParserSuiteTests`) and stay listed only because the full check needs the canonical writer (step 6). 325 tests.

- **The reference parser is the test oracle.** `tools/fixtures/parser-fixture.mjs` runs xfer-js over 1,000 generated documents (its own `test/generator.ts`), each also damaged three ways, and 4,000 texts of random token soup (one in ten under tight depth and element limits), and writes `fixtures/parser.jsonl` (7,456 cases, 6 MB): the shape of the tree (every node, pair, annotation and skipped item, tokens by offset), every diagnostic (code, start, end and message), the version and the tagged value. The .NET parser agrees on all of it, including every message.
- **Diagnostics have ranges.** A parser diagnostic spans its token, as in xfer-js; lexer diagnostics are points. `PendingDiagnostic` gained an end offset, resolved in a second sorted pass.
- **Messages** use `Messages.Shorten` and `Messages.JsQuote`, which reproduce xfer-js's `shorten` (UTF-16 units) and `JSON.stringify` (lone surrogates from a shortened string included).
- **A version too large for an `int`** is held as `int.MaxValue`; it is reported as unsupported either way.
- **Integers:** hexadecimal and binary are parsed with a leading `0` (`BigInteger` reads them as two's complement otherwise), octal by rewriting each digit as three binary digits.
- **Pair annotations** are put before the value's own annotations when projected; in a document without errors the value has none of its own, so this matches xfer-js, which replaces them.
- **Memory,** measured on a 6.6 MB typical document: the tree retains 24 bytes per input byte (46 allocated, most of it frames and lists that do not survive the parse), and the value model allocates about 19 bytes per input byte. The lexer's lists are now trimmed after lexing, so the stream retains no slack. Both figures are pinned by tests.
- **The value helpers** of section 4.2 (`TryGetInt64`, `ToDouble`, the temporal helpers) are deferred to step 7, with JSON, as in xfer-js.

Review of step 5 (Opus, cold): no disagreement with xfer-js on trees, diagnostics or values, in the fixture or in 30,000 further token-soup cases over a wider alphabet, 65 targeted edge cases (as both string and byte input), every suite input and 27,782 random integer lexemes. Findings, all applied:

- **`ParseAsync` over the size limit** allocated a dummy array of the limit's size to produce `XFER0601` (805 MB at the defaults), and with `DocumentBytes = int.MaxValue` a stream over 2 GB threw. It now builds the diagnostic directly (`Normalizer.TooLarge`), caps the limit at `Array.MaxLength`, and refuses a seekable stream from its length without reading it. A test feeds a stream that claims 3 GB and fails if it is read.
- **A long decimal integer was converted during the parse,** and `BigInteger.Parse` is superlinear: 16 million digits took 30 s (xfer-js: 2.7 s), so one integer within the default token limit could hold a server for minutes. A parsed `XferInteger` now keeps only its lexeme and converts on the first read of `Value`, published through a `StrongBox` with `Volatile` so another thread sees all of it or none. Equality compares lexemes first (a parsed integer's value is fixed by its spelling) and hashes the lexeme. A test parses two 4-million-digit integers in under a second and compares them without converting either.
- **The memory test was flaky** under parallel test execution, because `GC.GetTotalMemory` counts the whole process. The measuring classes run in a collection with parallelization disabled. On CI the retained figure then read 38 bytes per input byte on macOS arm64 against 24 on Windows and Linux for the same objects; `GC.GetTotalMemory` is documented only as an approximation, so the test now reports the retained figure and asserts the allocated one (46 bytes per input byte, exact on every platform, limit 48).
- **Values and trees could be changed through a cast** of the `IReadOnlyList` properties to arrays. They now return `ReadOnlyCollection` views (cached, and the shared empty one when there is nothing).
- **A default `XferToken` or `XferTrivia`** threw `NullReferenceException` from every member. Members now throw `InvalidOperationException` saying it is a default value, and `ToString` returns `(default)`. `LeadingTrivia` no longer allocates for a token with none.
- **Shortened text in a message could cut a surrogate pair in two.** The fault was in xfer-js too, which this port reproduces, so it was fixed there first (xfer-js `dc12b4e`: cut one unit earlier rather than split a pair) and then here, and the fixture was regenerated.
- **The fixture now covers** non-ASCII and emoji keys longer than 24 units, CR and CRLF, a byte order mark mid-document, the head versions `01`, `1_0`, `0x1`, `0`, `-1` and `"1"`, and tight limits on generated documents as well as on token soup.
- **Smaller:** the parser reuses its skipped-items list instead of allocating one per pair.
- **Tooling:** the fixture bundles are built with `tsdown --no-config`. Without it tsdown also runs xfer-js's own two builds into the same directory, and the bundle was intermittently corrupt.

### Step 6: the canonical writer

Done 2026-10-19 (Sonnet). `Writing/StringDelimiters.cs` ports xfer-js `src/strings.ts` (specification 6, rule 10, including the choice past the run limit) and `Writing/XferWriter.cs` ports `src/canonical.ts` with the validation of specification 9 for a value built by a program. All 48 valid suite cases now pass, so `expected-failures.txt` holds only the 54 JSON cases (step 7). 347 tests.

- **Oracle:** the parser fixture now carries xfer-js's canonical form for every accepted document (about 3,400 of them, under default and tight limits), and the .NET writer must match it byte for byte and be stable when its output is parsed and written again.
- **Properties:** 30,000 random strings from a quote-and-hash-heavy piece set, and every string over the alphabet `"`, `#`, `a` up to length ten (88,000 strings), are written and parsed back to themselves; a document is embedded in a string six levels deep and read back at each level.
- **Errors:** a value no document could hold is an `ArgumentException` (string or key with U+000D or a lone surrogate; a symbol that is not a bare name or is a keyword; a number or temporal whose spelling is not exactly one token of its kind or does not denote its value; an annotation other than a scalar `id`, `tag` or `lang`, with a nested annotation, a wrong-kind value, or a repeated `id`). A value nested past the depth limit is an `XferException` with `XFER0602`, and output past the document size limit one with `XFER0601`. A parse result with an error is refused with its own diagnostics.
- **Size is counted in UTF-8 bytes** (xfer-js counts UTF-16 units); the two agree for ASCII and differ only near the limit for other text.
- **The checks for a parsed result are skipped** (it is trusted), except that a symbol is always checked, as in xfer-js.
- **`Compact`** is not written: the design dropped it (section 9.3).

Review of step 6 (Opus, cold): 69,397 hand-built values through both writers agreed except for one case; five confirmed defects, all applied (the two shared with xfer-js were fixed there first, `e001abe`):

- **A symbol with a lone surrogate was written bare** (the .NET-only defect): `EnumerateRunes` is documented to turn invalid UTF-16 into U+FFFD, an identifier character. `IsBareName` now decodes with `Rune.DecodeFromUtf16` and refuses invalid UTF-16.
- **An empty collection one level past the depth limit was written,** and the default parser refused the result. The depth check now precedes the empty-collection shortcut (both writers).
- **The size count left out the head and the final line feed** (both writers). It now starts with them.
- **A legal raised limit ended in `OutOfMemoryException`:** with `Depth` and `DocumentBytes` raised, the output of a deep value is larger than a string can hold, and the `TextWriter` overload built the whole string first. The `TextWriter` overload now writes in 64 KB chunks and has no ceiling of its own (the document size limit still applies); the methods that return a string refuse output over `XferWriter.MaxStringBytes` (512 MiB of UTF-8, the library's own documented ceiling, since no documentation promises a maximum string length) with `XFER0601` and say to use a `TextWriter`. The ceiling itself is not covered by a test, because reaching it needs half a gigabyte of output.
- **A parsed integer rebuilt into a new value was converted twice** (27 s at 10 million digits). A parsed integer that has not been read says its value follows from its spelling, and the writer skips the comparison.
- **Smaller:** a lexeme is judged under the writer's own limits (a raised `TokenBytes` no longer refuses a rebuilt long fractional); error messages shorten what they quote; two assertions that compared output with a re-reading of itself, and two that only asserted non-empty output, now assert the right thing.

### Step 7: the JSON mapping and the value helpers

Done 2026-10-19 (Sonnet). `Json/JsonReader.cs` and `Json/JsonWriter.cs` port xfer-js `src/json/read.ts` and `write.ts`; `Json/XferJson.cs` is the public surface (`ToJson`, `FromJson` over a string or UTF-8 bytes, `XferJsonResult<T>`, the two options records); `Model/XferNumbers.cs` and `Model/XferTemporals.cs` add the helpers of section 4.2. **`expected-failures.txt` is empty: every suite case passes.** 367 tests.

- **The reader works on a string, not bytes** (section 8 said bytes). The byte overload drops a byte order mark, decodes strictly (invalid UTF-8 is `XFER0906` at the origin with the message "input is not valid UTF-8"), and reads the string. This keeps every position and message identical to xfer-js, whose positions are offsets into UTF-8 but whose scanning is by UTF-16 unit; reading bytes directly would have meant re-deriving all of it.
- **Oracle:** `tools/fixtures/json-fixture.mjs` writes `fixtures/json.jsonl` (6,410 cases, 4.4 MB) from xfer-js: 5,629 JSON texts (valid, damaged, and token soup, some under tight depth limits) whose canonical result and every diagnostic (code, position, message) must match, and 781 XferLang documents whose compact, indented (2 and 15) and big-integers-as-strings JSON and diagnostics must match. The JSON text is stored as base64 of UTF-16 code units because it may hold a lone surrogate, which `System.Text.Json` refuses to read from a string. A second test compares `FromJson`'s idea of well-formed with `System.Text.Json` on 20,000 random texts (as a differential oracle for well-formedness only).
- **Duplicate keys** are replaced with `IndexOf` and `SetAt`, so the first key keeps its position, without relying on the indexer's behaviour for an existing key.
- **A diagnostic's JSON Pointer is built when it is needed,** from the current member of every open container, not carried per level: carrying one is memory quadratic in the depth (xfer-js gets away with it because JavaScript concatenates lazily). A test writes 100,000 levels.
- **A parsed decimal integer is written from its spelling,** without a `BigInteger` conversion (one of millions of digits would take minutes); a hand-built or radix integer is converted.
- **The integer helpers** are generic over `IBinaryInteger<T>` and `IMinMaxValue<T>`: `TryGet<T>(out T)` never rounds or wraps, with `TryGetInt64` as the common case; `ToDouble(out bool exact)` on integers and fractionals reports exactness by comparing the double's exact binary expansion with the decimal in `BigInteger` arithmetic (`double.Parse` is correctly rounded, documented since .NET Core 3.0); `TryGetDecimalParts` and `TryGetDecimal` (exact only; `1.50` keeps its scale).
- **The temporal helpers** refuse, and never round or clamp, the year 0000, second 60, a non-zero fractional digit past the seventh, an offset beyond 14:00, and a UTC time outside `DateTime`'s range. A date-time with an offset converts to UTC for `TryGetDateTime`; `TryGetDateTimeOffset` keeps the offset.
- **`ToJson` does not validate** a hand-built string for a lone surrogate (as xfer-js does not): JSON text with one is ill-formed, but the value came from the caller.

Review of step 7 (Opus, cold): the reader and writer matched xfer-js on every hostile input tried, and so did the temporal helpers. Six findings, all applied:

- **Integer `ToDouble` truncated instead of rounding to nearest** (the `(double)BigInteger` conversion truncates on .NET 10 and no documentation promises otherwise): 99,156 of 200,000 random 54-to-300-bit integers were not the nearest double. It now rounds by hand (top 54 bits, sticky bit, ties to even, `Math.ScaleB`), and a test compares 20,000 random integers with `double.Parse` of their text, which is documented as correctly rounded.
- **`TryGetDecimal` was quadratic in trailing zeros** (398 s for a 1 MB document). The spelling is now reduced to its significant digits and an exponent by string scanning, before any big-number work.
- **Helpers converted huge spellings in full before cheap checks** (4 to 12 s at 10 million digits). `TryGet<T>` and `TryGetDecimal` on a parsed integer are refused from the length of the spelling; fractional `ToDouble` uses at most 770 digits plus a sticky one (past 767 significant digits no number is exactly a double, and the rest can only mean "something is left over"). A test runs 3-million-digit spellings through them in under three seconds.
- **`TryGetDecimal` refused values `decimal` holds** (`79228162514264337593543950335.0`) and dropped the scale of zero. Trailing zeros are now stripped before the range test, the requested scale is kept where the value still fits with it, and zero keeps its sign and scale.
- **`ToDouble` threw `FormatException` on a hand-built spelling that is not a number.** A spelling that is not a number in the grammar of 4.5 now gives NaN with `exact` false (documented), and the other helpers return false.
- **The exactness test checked nothing** (its perturbation was never equal to the double, the sign bit and half the exponents were masked out, and a variable was dead). It now tests any finite nonzero double of either sign, including subnormals, with a one-digit perturbation that must still round to the same double and must not be exact.
- **Smaller:** `XferJsonResult<T>` is now a struct whose `Diagnostics` is never null, even for `default`; two weak assertions were strengthened; the fixture now has raw (unescaped) lone surrogates, U+2028, U+0085 and U+007F in JSON strings, and control characters in XferLang strings for `toJson`; section 8 is corrected. `TryGetDecimalParts` returns false for a number whose exponent does not fit an `int` (xfer-js returns a value there), which the doc comment now says.

### Step 8: the object mapper

Done 2026-10-20 (Opus). `Serialization/` holds the options and attributes, the type resolver (`TypeInfo.cs`, all reflection in one file as section 10.8 requires), the scalar conversions of tables 10.3 and 10.4 (`Scalars.cs`), the mapper (`Mapper.cs`) and `XferSerializer`. 464 tests, 94 of them the mapper's.

- **Reading and writing are iterative,** each with an explicit stack of frames, so a document as deep as the limits allow is mapped without recursion (a test reads and writes 5,000 levels). On writing, the depth limit also catches a reference cycle (section 10.5).
- **Positions come from a side table.** `Projection.ToValue` fills a `SourceMap` (value to offset, and object to key offsets) only when the mapper asks, through the internal `XferDocument.ParseForMapping`; a plain parse pays nothing. A mapping error's message ends with the JSON Pointer, `(at /a/0)`, or `(at the root)`.
- **Options are settable until first use, then frozen,** as `System.Text.Json` does, instead of the `init` properties section 10.2 first showed: a caller can build options in steps, and the cache keyed on the instance stays sound.
- **A constructor parameter without a default is required** (section 10.2 now says so). A type that cannot be constructed is an `InvalidOperationException` when an object is read into it, not a diagnostic.
- **`object` targets:** an integer is `long` when it fits (a `?:` between `long` and `BigInteger` had made every integer a `BigInteger`; a test now checks the type). A temporal no .NET type can hold stays an `XferTemporal`, because the suite's leap-second document would otherwise be refused.
- **Enums** read by name through the naming policy, by integer, and for `[Flags]` by a sequence of names; the signed underlying types are handled through one widening helper.
- **Member order is an open question** (section 16, item 4): beyond `[XferPropertyOrder]` it is the order reflection gives, which no documentation promises.
- **The parkscomputing.com documents** are in `test/fixtures/` as they are (`*.legacy.xfer`, from `C:\Users\paul\OneDrive\Documents\parkscomputing.com`) and converted by `tools/fixtures/convert-legacy.mjs`, which knows the four 0.x constructs they use (the head, `~true`, `~false`, the `<"..">` and `<'..'>` wrappers) and refuses anything else. A test reads the converted copies into a copy of the engine's `NavNode` with this library, reads the originals with `ParksComputing.Xfer.Lang` 0.15.25-prerelease (a test-only reference), and requires the two object graphs to serialize to the same `System.Text.Json` text. The new `NavNode` uses `Aliases` for the legacy `link` and `links` keys in place of the two shadow properties, which become `[XferIgnore]`.
- **The suite through `object`:** every valid document is read and written twice and the two outputs must be identical; `ToJson` of the input and of the first output must agree apart from number spelling (compared as doubles) and trailing zeros of a time's fraction.
