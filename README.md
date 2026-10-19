# ParksComputing.XferLang

ParksComputing.XferLang is the .NET implementation of XferLang, a data format with the data model of JSON, typed values, no escape sequences anywhere in its grammar, and no meaning in line breaks or indentation. Any text can be embedded in an XferLang string exactly as it is, at any depth, including another XferLang document.

The package targets .NET 10 and has no dependencies. It contains a parser that reports every fault as a diagnostic with a stable code and never throws on bad input, a lossless syntax tree, a canonical writer, a conversion to and from JSON, and an object mapper modelled on `System.Text.Json`. A second package, ParksComputing.XferLang.Tool, is the `xfer` command line tool.

The specification and its conformance suite live in the [xferlang](https://github.com/paulmooreparks/xferlang) repository, which this repository includes as the `suite` submodule. This package passes every case in the suite, and its command line tool gives the same output, byte for byte, as the one in [xfer-js](https://github.com/paulmooreparks/xfer-js), the JavaScript implementation.

The first release will be `0.1.0-next.0`, a pre-release. Nothing is published yet.

## A document

```xfer
<! xfer 1 !>
{
    title """She said "hello" and left"""
    count 42
    ratio 1.50
    released 2025-08-01
    mode dark
    tags [ alpha beta ]
    script """
        const s = "no escaping needed";
    """
}
```

Strings are written between runs of quotation marks, and a string that contains quotation marks is simply written with a longer run, so nothing inside it is ever escaped. Bare words are symbols, `true`, `false` and `null` are keywords, integers keep their exact value at any size, and dates and times are values of their own type.

## Using it

```sh
dotnet add package ParksComputing.XferLang --prerelease
```

```csharp
// Parsing never throws. Every fault is a diagnostic with a stable code and a position.
XferParseResult result = XferDocument.Parse("{ name \"Ada\" langs [ en fr ] }");
foreach (XferDiagnostic d in result.Diagnostics)
{
    Console.Error.WriteLine($"{d.Start.Line}:{d.Start.Column} {d.CodeText} {d.Message}");
}

// Or take the value, and let an error throw an XferException that carries the diagnostics.
var value = (XferObject)XferDocument.ParseValue("{ n 12345678901234567890 }");
var n = (XferInteger)value["n"];

// The canonical form: the one way to write a document, byte for byte.
string canonical = XferWriter.Canonical(XferDocument.Parse("{a 1 b[2 3]}"));

// JSON in both directions. Each reports what it could not map exactly.
XferJsonResult<string?> json = XferJson.ToJson(value, new XferToJsonOptions { StringifyBigIntegers = true });
XferValue? back = XferJson.FromJson("{\"a\":[1,2.5,null]}").Value;

// Objects, mapped as System.Text.Json maps them.
Person? ada = XferSerializer.Deserialize<Person>("{ Name \"Ada\" Langs [ en fr ] }");
string written = XferSerializer.Serialize(ada);
```

The namespaces are `ParksComputing.XferLang` and, for the mapper, `ParksComputing.XferLang.Serialization`. Parser limits (nesting depth, document size, token length, delimiter run length, element count) default to the specification's minimums and can be raised or lowered through `XferLimits`. The value model has helpers that convert numbers and temporals exactly or say they cannot (`TryGetInt64`, `TryGet<T>`, `TryGetDecimal`, `ToDouble(out bool exact)`, `TryGetDateOnly` and the rest). The lossless tree is `result.Tree`, for tools that need every byte of the source, such as formatters and editors.

Everything except the object mapper is safe to trim and to compile ahead of time. The mapper uses reflection and is marked so.

## Command line

```sh
dotnet tool install --global ParksComputing.XferLang.Tool --prerelease

xfer validate file.xfer          # diagnostics as FILE:LINE:COL: error XFER0405: message
xfer fmt --write file.xfer       # rewrite in canonical form
xfer to-json --pretty file.xfer
xfer from-json < data.json
```

With no file, or with `-`, input is read from standard input. The exit status is 0 for success, 1 if any document had an error, and 2 for a usage error, an unreadable file, or output that could not be written. `--json` prints diagnostics as one JSON object per line.

## Development

Clone with submodules, then build and test with the .NET 10 SDK:

```sh
git clone --recurse-submodules https://github.com/paulmooreparks/xfer-dotnet
cd xfer-dotnet
dotnet build
dotnet test
```

The differential tests, which run this tool and the xfer-js one on the same inputs, are skipped unless `XFER_JS_CLI` is the path of a built xfer-js `dist/cli.mjs` and `node` is on the path. [DESIGN.md](DESIGN.md) describes how the library is built and records the decisions made along the way.

## Licence

MIT.
