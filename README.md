# ParksComputing.XferLang

ParksComputing.XferLang is the .NET implementation of XferLang, a data format with the data model of JSON, typed values, no escape sequences anywhere in its grammar, and no meaning in line breaks or indentation. Any text can be embedded in an XferLang string exactly as it is, at any depth, including another XferLang document.

The package targets `net10.0` and has no dependencies. It will contain a parser that reports every fault as a diagnostic with a stable code and never throws on bad input, a lossless syntax tree, a canonical writer, a conversion to and from JSON, an object mapper modelled on `System.Text.Json`, and the `xfer` command line tool.

The specification and its conformance suite live in the [xferlang](https://github.com/paulmooreparks/xferlang) repository, which this repository includes as the `suite` submodule. The JavaScript implementation, [xfer-js](https://github.com/paulmooreparks/xfer-js), is published on npm as `xferlang`.

This repository is a scaffold. The parser and the rest are not written yet, and [DESIGN.md](DESIGN.md) describes how they will be built. Nothing here is ready to use, and nothing is published.

## Development

Clone with submodules, then build and test with the .NET 10 SDK:

```sh
git clone --recurse-submodules https://github.com/paulmooreparks/xfer-dotnet
cd xfer-dotnet
dotnet build
dotnet test
```

## Licence

MIT.
