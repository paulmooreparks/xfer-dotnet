using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ParksComputing.XferLang.Json;
using ParksComputing.XferLang.Serialization;
using ParksComputing.XferLang.Tests.Engine;
using ParksComputing.XferLang.Tests.Harness;
using Xunit;

namespace ParksComputing.XferLang.Tests;

#pragma warning disable CA1812, CA1819, CA2227, CA1034, CA1720, CA1711, CA1008, CA1028, CA1069, CA1051, CA2263 // test types

public class MapperTests
{
    private static T? Read<T>(string text, XferSerializerOptions? options = null) => XferSerializer.Deserialize<T>(text, options);

    private static string Write<T>(T value, XferSerializerOptions? options = null) => CollapseSpaces(XferSerializer.Serialize(value, options).ReplaceLineEndings(" "));

    private static XferDiagnostic Fails<T>(string text, XferSerializerOptions? options = null)
    {
        var e = Assert.Throws<XferException>(() => XferSerializer.Deserialize<T>(text, options));
        return Assert.Single(e.Diagnostics);
    }

    private static void FailsAt<T>(string text, XferDiagnosticCode code, int line, int column, string pointer, XferSerializerOptions? options = null)
    {
        XferDiagnostic d = Fails<T>(text, options);
        Assert.Equal(code, d.Code);
        Assert.Equal((line, column), (d.Start.Line, d.Start.Column));
        Assert.EndsWith($"(at {(pointer.Length == 0 ? "the root" : pointer)})", d.Message, StringComparison.Ordinal);
    }

    // ---- Section 10.3, reading ----

    [Fact]
    public void ReadsStringsFromStringsAndSymbols()
    {
        Assert.Equal("dark", Read<string>("\"dark\""));
        Assert.Equal("dark", Read<string>("dark"));
        Assert.Null(Read<string>("null"));
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<string>("1").Code);
    }

    [Fact]
    public void ReadsCharFromOneUtf16Unit()
    {
        Assert.Equal('x', Read<char>("\"x\""));
        Assert.Equal('x', Read<char>("x"));
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<char>("\"xy\"").Code);
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<char>("\"\"").Code);
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<char>("\"😀\"").Code);
    }

    [Fact]
    public void ReadsBooleans()
    {
        Assert.True(Read<bool>("true"));
        Assert.False(Read<bool>("false"));
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<bool>("\"true\"").Code);
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<bool>("null").Code);
    }

    [Fact]
    public void ReadsEveryIntegralTypeAndRefusesOutOfRange()
    {
        Assert.Equal(sbyte.MinValue, Read<sbyte>("-128"));
        Assert.Equal(byte.MaxValue, Read<byte>("255"));
        Assert.Equal(short.MinValue, Read<short>("-32768"));
        Assert.Equal(ushort.MaxValue, Read<ushort>("65535"));
        Assert.Equal(int.MinValue, Read<int>("-2147483648"));
        Assert.Equal(uint.MaxValue, Read<uint>("4294967295"));
        Assert.Equal(long.MinValue, Read<long>("-9223372036854775808"));
        Assert.Equal(ulong.MaxValue, Read<ulong>("18446744073709551615"));
        Assert.Equal((nint)(-5), Read<nint>("-5"));
        Assert.Equal((nuint)5, Read<nuint>("5"));
        Assert.Equal(Int128.MaxValue, Read<Int128>(Int128.MaxValue.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(UInt128.MaxValue, Read<UInt128>(UInt128.MaxValue.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(255, Read<int>("0xff"));
        Assert.Equal(1_000, Read<int>("1_000"));

        Assert.Equal(XferDiagnosticCode.XFER0303, Fails<sbyte>("128").Code);
        Assert.Equal(XferDiagnosticCode.XFER0303, Fails<byte>("-1").Code);
        Assert.Equal(XferDiagnosticCode.XFER0303, Fails<int>("2147483648").Code);
        Assert.Equal(XferDiagnosticCode.XFER0303, Fails<ulong>("18446744073709551616").Code);
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<int>("1.5").Code);
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<int>("\"1\"").Code);
    }

    [Fact]
    public void ReadsBigIntegerOfAnySize()
    {
        string big = "123456789012345678901234567890123456789012345678901234567890";
        Assert.Equal(BigInteger.Parse(big, CultureInfo.InvariantCulture), Read<BigInteger>(big));
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<BigInteger>("1.0").Code);
    }

    [Fact]
    public void ReadsBinaryFloatingPointToTheNearestValue()
    {
        Assert.Equal(0.1, Read<double>("0.1"));
        Assert.Equal(3.0, Read<double>("3"));
        Assert.Equal(9007199254740992.0, Read<double>("9007199254740993")); // nearest, not an error
        Assert.Equal(double.PositiveInfinity, Read<double>("inf"));
        Assert.Equal(double.NegativeInfinity, Read<double>("-inf"));
        Assert.True(double.IsNaN(Read<double>("nan")));
        Assert.Equal(0.1f, Read<float>("0.1"));
        Assert.Equal((Half)0.5, Read<Half>("0.5"));
        Assert.Equal(-0.0, Read<double>("-0.0"));

        Assert.Equal(XferDiagnosticCode.XFER0303, Fails<double>("1e400").Code);
        Assert.Equal(XferDiagnosticCode.XFER0303, Fails<double>("1e-400").Code);
        Assert.Equal(XferDiagnosticCode.XFER0303, Fails<float>("1e39").Code);
        Assert.Equal(XferDiagnosticCode.XFER0303, Fails<Half>("70000").Code);
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<double>("\"1\"").Code);
    }

    [Fact]
    public void ReadsDecimalOnlyWhenExact()
    {
        Assert.Equal(1.25m, Read<decimal>("1.25"));
        Assert.Equal(7m, Read<decimal>("7"));
        Assert.Equal(XferDiagnosticCode.XFER0303, Fails<decimal>("1e400").Code);
        Assert.Equal(XferDiagnosticCode.XFER0303, Fails<decimal>("0.12345678901234567890123456789012345").Code);
        Assert.Equal(XferDiagnosticCode.XFER0303, Fails<decimal>("inf").Code);
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<decimal>("\"1\"").Code);
    }

    public enum Mode
    {
        Light,
        Dark,
        HighContrast,
    }

    [Flags]
    public enum Access
    {
        None = 0,
        Read = 1,
        Write = 2,
        Admin = 4,
    }

    public enum Signed : sbyte
    {
        Low = -100,
        High = 100,
    }

    [Fact]
    public void ReadsEnumsByNameThroughThePolicyOrByInteger()
    {
        Assert.Equal(Mode.Dark, Read<Mode>("Dark"));
        Assert.Equal(Mode.Dark, Read<Mode>("\"Dark\""));
        Assert.Equal(Mode.HighContrast, Read<Mode>("2"));
        Assert.Equal(Mode.HighContrast, Read<Mode>("high-contrast", new XferSerializerOptions { PropertyNamingPolicy = XferNamingPolicy.KebabCaseLower }));
        Assert.Equal(Signed.Low, Read<Signed>("-100"));
        Assert.Equal(Access.Read | Access.Admin, Read<Access>("[Read Admin]"));
        Assert.Equal(Access.Read | Access.Write, Read<Access>("3"));

        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<Mode>("Purple").Code);
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<Mode>("[Dark]").Code);
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<Access>("[Read Purple]").Code);
        Assert.Equal(XferDiagnosticCode.XFER0303, Fails<Signed>("200").Code);
    }

    [Fact]
    public void ReadsTemporalValues()
    {
        Assert.Equal(new DateOnly(2026, 10, 20), Read<DateOnly>("2026-10-20"));
        Assert.Equal(new TimeOnly(13, 45, 30, 500), Read<TimeOnly>("13:45:30.5"));

        DateTime date = Read<DateTime>("2026-10-20");
        Assert.Equal((new DateTime(2026, 10, 20), DateTimeKind.Unspecified), (date, date.Kind));
        DateTime local = Read<DateTime>("2026-10-20T13:45:00");
        Assert.Equal((new DateTime(2026, 10, 20, 13, 45, 0), DateTimeKind.Unspecified), (local, local.Kind));
        DateTime utc = Read<DateTime>("2026-10-20T13:45:00+08:00");
        Assert.Equal((new DateTime(2026, 10, 20, 5, 45, 0), DateTimeKind.Utc), (utc, utc.Kind));

        DateTimeOffset offset = Read<DateTimeOffset>("2026-10-20T13:45:00+08:00");
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 13, 45, 0, TimeSpan.FromHours(8)), offset);
        Assert.Equal(TimeSpan.FromHours(8), offset.Offset);

        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<DateOnly>("13:45:00").Code);
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<DateTimeOffset>("2026-10-20T13:45:00").Code);
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<DateTime>("\"2026-10-20\"").Code);
        Assert.Equal(XferDiagnosticCode.XFER0303, Fails<DateOnly>("0000-01-01").Code);
        Assert.Equal(XferDiagnosticCode.XFER0303, Fails<DateTime>("0001-01-01T00:00:00+01:00").Code);
    }

    [Fact]
    public void ReadsParsableTypesFromStrings()
    {
        Assert.Equal(TimeSpan.FromMinutes(90), Read<TimeSpan>("\"01:30:00\""));
        Guid guid = Guid.NewGuid();
        Assert.Equal(guid, Read<Guid>($"\"{guid}\""));
        Assert.Equal(new Uri("https://example.com/a"), Read<Uri>("\"https://example.com/a\""));
        Assert.Equal(new Version(1, 2, 3), Read<Version>("\"1.2.3\""));
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<Guid>("\"not a guid\"").Code);
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<Version>("1").Code);
    }

    [Fact]
    public void ReadsNullableTypes()
    {
        Assert.Null(Read<int?>("null"));
        Assert.Equal(4, Read<int?>("4"));
        Assert.Null(Read<Mode?>("null"));
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<int>("null").Code);
    }

    [Fact]
    public void ReadsEveryCollectionShape()
    {
        Assert.Equal([1, 2, 3], Read<int[]>("[1 2 3]")!);
        Assert.Equal([1, 2, 3], Read<List<int>>("[1 2 3]"));
        Assert.Equal([1, 2, 3], Read<IEnumerable<int>>("[1 2 3]"));
        Assert.Equal([1, 2, 3], Read<IReadOnlyList<int>>("[1 2 3]"));
        Assert.Equal([1, 2, 3], Read<IReadOnlyCollection<int>>("[1 2 3]"));
        Assert.Equal([1, 2, 3], Read<ICollection<int>>("[1 2 3]"));
        Assert.Equal([1, 2, 3], Read<IList<int>>("[1 2 3]"));
        Assert.Equal(new HashSet<int> { 1, 2 }, Read<HashSet<int>>("[1 2 1]"));
        Assert.Equal(new HashSet<int> { 1, 2 }, Read<ISet<int>>("[1 2 1]"));
        Assert.Equal([1, 2], Read<ImmutableArray<int>>("[1 2]"));
        Assert.Empty(Read<int[]>("[]")!);
        Assert.Equal([[1], [2, 3]], Read<int[][]>("[[1] [2 3]]"));
        Assert.Null(Read<int[]>("null"));
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<int[]>("{a 1}").Code);
    }

    [Fact]
    public void ReadsDictionariesKeepingOrder()
    {
        var d = Read<Dictionary<string, int>>("{z 1 a 2 m 3}")!;
        Assert.Equal(["z", "a", "m"], d.Keys);
        var i = Read<IDictionary<string, int>>("{z 1 a 2}")!;
        Assert.Equal(["z", "a"], i.Keys);
        var r = Read<IReadOnlyDictionary<string, int>>("{z 1 a 2}")!;
        Assert.Equal(["z", "a"], r.Keys);
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<Dictionary<string, int>>("[1]").Code);
    }

    [Fact]
    public void ReadsObjectAsThePlainTypes()
    {
        object? value = Read<object>("{s \"x\" y sym i 5 b 123456789012345678901234567890 f 1.5 d 2026-10-20 t 10:00:00 l 2026-10-20T10:00:00 z 2026-10-20T10:00:00Z o true n null q [1 x]}");
        var o = Assert.IsType<OrderedDictionary<string, object?>>(value);
        Assert.Equal(["s", "y", "i", "b", "f", "d", "t", "l", "z", "o", "n", "q"], o.Keys);
        Assert.Equal("x", o["s"]);
        Assert.Equal("sym", o["y"]);
        Assert.Equal(5L, o["i"]);
        Assert.IsType<BigInteger>(o["b"]);
        Assert.Equal(1.5, o["f"]);
        Assert.Equal(new DateOnly(2026, 10, 20), o["d"]);
        Assert.Equal(new TimeOnly(10, 0), o["t"]);
        Assert.Equal(new DateTime(2026, 10, 20, 10, 0, 0), o["l"]);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero), o["z"]);
        Assert.Equal(true, o["o"]);
        Assert.Null(o["n"]);
        Assert.Equal(new List<object?> { 1L, "x" }, Assert.IsType<List<object?>>(o["q"]));
        var unheld = Assert.IsType<List<object?>>(Read<object>("[0000-01-01 23:59:60]"));
        Assert.Equal(["0000-01-01", "23:59:60"], unheld.Select(v => Assert.IsType<XferTemporal>(v).Lexeme));
        Assert.Equal("[0000-01-01 23:59:60]", Write(unheld));
    }

    [Fact]
    public void ReadsXferValuesAsParsedWithAnnotations()
    {
        var value = Read<XferValue>("<! tag \"t\" !> 5");
        var integer = Assert.IsType<XferInteger>(value);
        Assert.Equal("5", integer.Lexeme);
        Assert.Single(integer.Annotations);
        Assert.IsType<XferObject>(Read<XferObject>("{a 1}"));
        Assert.Equal(XferDiagnosticCode.XFER0801, Fails<XferObject>("[1]").Code);
    }

    // ---- Classes, structs, records and constructors ----

    public sealed class Person
    {
        public string? Name { get; set; }

        public int Age { get; set; }

        public string[]? Tags { get; set; }

        public Person? Friend { get; set; }

        public int ReadOnly => Age * 2;
    }

    public sealed record Point(int X, int Y);

    public readonly record struct Pair(string Left, string Right);

    public struct Mutable
    {
        public int A;

        public int B { get; set; }
    }

    public sealed class Chosen
    {
        public Chosen(int a)
        {
            A = a;
            Via = "one";
        }

        [XferConstructor]
        public Chosen(int a, string b)
        {
            A = a;
            B = b;
            Via = "two";
        }

        public int A { get; }

        public string? B { get; }

        [XferIgnore]
        public string Via { get; }
    }

    public sealed record Defaults(int A, int B = 7);

    public sealed class Ambiguous
    {
        public Ambiguous(int a) => A = a;

        public Ambiguous(string b) => B = b;

        public int A { get; }

        public string? B { get; }
    }

    [Fact]
    public void ReadsClassesStructsAndRecords()
    {
        var p = Read<Person>("{Name \"Ada\" Age 36 Tags [a b] Friend {Name Bob}}")!;
        Assert.Equal(("Ada", 36, "Bob"), (p.Name, p.Age, p.Friend!.Name));
        Assert.Equal(["a", "b"], p.Tags!);

        Assert.Equal(new Point(1, 2), Read<Point>("{X 1 Y 2}"));
        Assert.Equal(new Point(1, 2), Read<Point>("{Y 2 X 1}"));
        Assert.Equal(new Pair("l", "r"), Read<Pair>("{Left l Right r}"));
        Mutable m = Read<Mutable>("{A 1 B 2}");
        Assert.Equal((1, 2), (m.A, m.B));

        var c = Read<Chosen>("{a 1 b x}", new XferSerializerOptions { PropertyNamingPolicy = XferNamingPolicy.CamelCase })!;
        Assert.Equal(("two", 1, "x"), (c.Via, c.A, c.B));

        Assert.Equal(new Defaults(1, 7), Read<Defaults>("{A 1}"));
        Assert.Equal(new Defaults(1, 2), Read<Defaults>("{A 1 B 2}"));
        Assert.Null(Read<Person>("null"));
    }

    [Fact]
    public void RefusesATypeItCannotConstruct()
    {
        Assert.Throws<InvalidOperationException>(() => Read<Ambiguous>("{A 1}"));
    }

    [Fact]
    public void TreatsAKeyForAReadOnlyMemberAsUnmapped()
    {
        Assert.Equal(3, Read<Person>("{Age 3 ReadOnly 99}")!.Age);
        FailsAt<Person>("{Age 3 ReadOnly 99}", XferDiagnosticCode.XFER0802, 1, 8, "/ReadOnly", new XferSerializerOptions { UnmappedMemberHandling = XferUnmappedMemberHandling.Disallow });
    }

    // ---- Section 10.2, errors with positions and pointers ----

    [Fact]
    public void ReportsAValueMismatchAtTheValueWithItsPointer()
    {
        FailsAt<Person>("{\n  Name \"Ada\"\n  Age \"old\"\n}", XferDiagnosticCode.XFER0801, 3, 7, "/Age");
        FailsAt<Person>("{Friend {Friend {Age 1.5}}}", XferDiagnosticCode.XFER0801, 1, 22, "/Friend/Friend/Age");
        FailsAt<Person>("{Tags [a b 3]}", XferDiagnosticCode.XFER0801, 1, 12, "/Tags/2");
        FailsAt<Dictionary<string, byte>>("{\"a/b\" 1 \"c~d\" 300}", XferDiagnosticCode.XFER0303, 1, 16, "/c~0d");
        FailsAt<Dictionary<string, byte>>("{\"a/b\" 300}", XferDiagnosticCode.XFER0303, 1, 8, "/a~1b");
        FailsAt<int>("\"x\"", XferDiagnosticCode.XFER0801, 1, 1, string.Empty);
    }

    [Fact]
    public void RefusesUnknownKeysOnlyWhenDisallowed()
    {
        Assert.Equal("Ada", Read<Person>("{Name Ada Unknown 1}")!.Name);
        FailsAt<Person>("{Name Ada\n Unknown 1}", XferDiagnosticCode.XFER0802, 2, 2, "/Unknown", new XferSerializerOptions { UnmappedMemberHandling = XferUnmappedMemberHandling.Disallow });
    }

    public sealed class WithRequired
    {
        [XferRequired]
        public string? Marked { get; set; }

        public required int Keyword { get; set; }

        public int Optional { get; set; }
    }

    [Fact]
    public void RefusesAMissingRequiredMemberAtTheObject()
    {
        Assert.Equal(("m", 1), (Read<WithRequired>("{Marked m Keyword 1}")!.Marked, Read<WithRequired>("{Marked m Keyword 1}")!.Keyword));
        FailsAt<WithRequired>("{Keyword 1}", XferDiagnosticCode.XFER0803, 1, 1, string.Empty);
        FailsAt<WithRequired[]>("[{Marked m Keyword 1} {Marked m}]", XferDiagnosticCode.XFER0803, 1, 23, "/1");
        FailsAt<Defaults>("{B 1}", XferDiagnosticCode.XFER0803, 1, 1, string.Empty);
    }

    [Fact]
    public void RefusesTwoKeysForOneMember()
    {
        var insensitive = new XferSerializerOptions { PropertyNameCaseInsensitive = true };
        Assert.Equal("Ada", Read<Person>("{name Ada}", insensitive)!.Name);
        Assert.Null(Read<Person>("{name Ada}")!.Name);
        FailsAt<Person>("{name Ada NAME Bob}", XferDiagnosticCode.XFER0804, 1, 11, "/NAME", insensitive);

        FailsAt<NavNode>("{url \"/a\" link \"/b\"}", XferDiagnosticCode.XFER0804, 1, 11, "/link");
        FailsAt<NavNode>("{links [] nav []}", XferDiagnosticCode.XFER0804, 1, 11, "/nav");
    }

    [Fact]
    public void ReadsAliasesAndNeverWritesThem()
    {
        var node = Read<NavNode>("{link \"/a\" links [{title t}]}")!;
        Assert.Equal("/a", node.Url);
        Assert.Equal("t", Assert.Single(node.Nav!).Title);
        string written = Write(node, new XferSerializerOptions { DefaultIgnoreCondition = XferIgnoreCondition.WhenWritingDefault });
        Assert.Contains("url \"/a\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("link", written, StringComparison.Ordinal);
    }

    public sealed class Duplicate
    {
        [XferPropertyName("x")]
        public int A { get; set; }

        [XferPropertyName("x")]
        public int B { get; set; }
    }

    [Fact]
    public void RefusesTwoMembersWithOneKeyBeforeReadingAnything()
    {
        Assert.Throws<InvalidOperationException>(() => Read<Duplicate>("{}"));
        Assert.Throws<InvalidOperationException>(() => Write(new Duplicate()));
    }

    public sealed class Named
    {
        public string? FirstName { get; set; }

        public int HTTPCode { get; set; }

        [XferPropertyName("explicit")]
        public int Overridden { get; set; }
    }

    [Theory]
    [InlineData("camel", "{firstName \"a\" httpCode 1 explicit 2}")]
    [InlineData("kebab", "{first-name \"a\" http-code 1 explicit 2}")]
    [InlineData("snake", "{first_name \"a\" http_code 1 explicit 2}")]
    public void AppliesNamingPoliciesBelowTheAttribute(string policy, string expected)
    {
        var options = new XferSerializerOptions
        {
            PropertyNamingPolicy = policy switch
            {
                "camel" => XferNamingPolicy.CamelCase,
                "kebab" => XferNamingPolicy.KebabCaseLower,
                _ => XferNamingPolicy.SnakeCaseLower,
            },
        };
        var value = new Named { FirstName = "a", HTTPCode = 1, Overridden = 2 };
        string written = Write(value, options).ReplaceLineEndings(" ");
        Assert.Equal(expected, CollapseSpaces(written));
        var back = Read<Named>(written, options)!;
        Assert.Equal(("a", 1, 2), (back.FirstName, back.HTTPCode, back.Overridden));
    }

    private static string CollapseSpaces(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            if (c == ' ' && (sb.Length == 0 || sb[^1] == ' ' || sb[^1] == '{' || sb[^1] == '['))
            {
                continue;
            }

            if ((c == '}' || c == ']') && sb.Length > 0 && sb[^1] == ' ')
            {
                sb.Length--;
            }

            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    // ---- Section 10.4, writing ----

    [Fact]
    public void WritesScalars()
    {
        Assert.Equal("\"a\"", Write("a"));
        Assert.Equal("\"x\"", Write('x'));
        Assert.Equal("true", Write(true));
        Assert.Equal("-128", Write(sbyte.MinValue));
        Assert.Equal("18446744073709551615", Write(ulong.MaxValue));
        Assert.Equal(UInt128.MaxValue.ToString(CultureInfo.InvariantCulture), Write(UInt128.MaxValue));
        Assert.Equal("123456789012345678901234567890", Write(BigInteger.Parse("123456789012345678901234567890", CultureInfo.InvariantCulture)));
        Assert.Equal("null", Write<string?>(null));
        Assert.Equal("null", Write<int?>(null));
    }

    [Fact]
    public void WritesFloatingPointAsReadableFractionals()
    {
        Assert.Equal("1.0", Write(1.0));
        Assert.Equal("0.1", Write(0.1));
        Assert.Equal("1E+300", Write(1e300));
        Assert.Equal("-0.0", Write(-0.0));
        Assert.Equal("inf", Write(double.PositiveInfinity));
        Assert.Equal("-inf", Write(double.NegativeInfinity));
        Assert.Equal("nan", Write(double.NaN));
        Assert.Equal("0.1", Write(0.1f));
        Assert.Equal("2.0", Write((Half)2));
        Assert.Equal("1.0", Write(1m));
        Assert.Equal("1.50", Write(1.50m));
        foreach (double d in new[] { 0.1, 1.0, 1e300, 5e-324, double.MaxValue, -2.5 })
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(d), BitConverter.DoubleToInt64Bits(Read<double>(Write(d))));
        }
    }

    [Fact]
    public void WritesEnums()
    {
        Assert.Equal("Dark", Write(Mode.Dark));
        Assert.Equal("high-contrast", Write(Mode.HighContrast, new XferSerializerOptions { PropertyNamingPolicy = XferNamingPolicy.KebabCaseLower }));
        Assert.Equal("[Read Admin]", Write(Access.Read | Access.Admin));
        Assert.Equal("Write", Write(Access.Write));
        Assert.Equal("None", Write(Access.None));
        Assert.Equal("9", Write((Mode)9));
        Assert.Equal("8", Write((Access)8));
        Assert.Equal("Low", Write(Signed.Low));
        Assert.Equal("-99", Write((Signed)(-99)));
        Assert.Equal(Access.Read | Access.Admin, Read<Access>(Write(Access.Read | Access.Admin)));
    }

    [Fact]
    public void WritesTemporalValues()
    {
        Assert.Equal("2026-10-20", Write(new DateOnly(2026, 10, 20)));
        Assert.Equal("13:45:00", Write(new TimeOnly(13, 45)));
        Assert.Equal("13:45:30.5", Write(new TimeOnly(13, 45, 30, 500)));
        Assert.Equal("2026-10-20T13:45:00Z", Write(new DateTime(2026, 10, 20, 13, 45, 0, DateTimeKind.Utc)));
        Assert.Equal("2026-10-20T00:00:00", Write(new DateTime(2026, 10, 20)));
        var local = new DateTime(2026, 10, 20, 13, 45, 0, DateTimeKind.Local);
        Assert.Equal(local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "Z", Write(local));
        Assert.Equal("2026-10-20T13:45:00+08:00", Write(new DateTimeOffset(2026, 10, 20, 13, 45, 0, TimeSpan.FromHours(8))));
        Assert.Equal("2026-10-20T13:45:00Z", Write(new DateTimeOffset(2026, 10, 20, 13, 45, 0, TimeSpan.Zero)));
        Assert.Equal("2026-10-20T13:45:00.25-05:30", Write(new DateTimeOffset(2026, 10, 20, 13, 45, 0, 250, TimeSpan.FromMinutes(-330))));
    }

    [Fact]
    public void WritesParsableTypesAsStrings()
    {
        Assert.Equal("\"01:30:00\"", Write(TimeSpan.FromMinutes(90)));
        Assert.Equal("\"00000000-0000-0000-0000-000000000000\"", Write(Guid.Empty));
        Assert.Equal("\"https://example.com/a\"", Write(new Uri("https://example.com/a")));
        Assert.Equal("\"1.2.3\"", Write(new Version(1, 2, 3)));
    }

    public sealed class Symbolic
    {
        [XferSymbol]
        public string? Mode { get; set; }
    }

    [Fact]
    public void WritesASymbolOnlyWhenAskedAndValid()
    {
        Assert.Equal("{Mode dark}", CollapseSpaces(Write(new Symbolic { Mode = "dark" }).ReplaceLineEndings(" ")));
        Assert.Equal("{Mode \"not a symbol\"}", CollapseSpaces(Write(new Symbolic { Mode = "not a symbol" }).ReplaceLineEndings(" ")));
        Assert.Equal("{Mode \"true\"}", CollapseSpaces(Write(new Symbolic { Mode = "true" }).ReplaceLineEndings(" ")));
    }

    public class Base
    {
        public int First { get; set; }
    }

    public sealed class Derived : Base
    {
        public int Second { get; set; }

        [XferPropertyOrder(-1)]
        public int Zeroth { get; set; }
    }

    [Fact]
    public void WritesMembersByOrderThenBaseFirst()
    {
        Assert.Equal("{Zeroth 0 First 1 Second 2}", CollapseSpaces(Write(new Derived { First = 1, Second = 2 }).ReplaceLineEndings(" ")));
    }

    [Fact]
    public void WritesCollectionsDictionariesAndRuntimeTypes()
    {
        Assert.Equal("[1 2 3]", CollapseSpaces(Write(new List<int> { 1, 2, 3 }).ReplaceLineEndings(" ")));
        Assert.Equal("[]", CollapseSpaces(Write(Array.Empty<int>()).ReplaceLineEndings(" ")));
        Assert.Equal("{z 1 a 2}", CollapseSpaces(Write(new Dictionary<string, int> { ["z"] = 1, ["a"] = 2 }).ReplaceLineEndings(" ")));
        Assert.Equal("{\"not bare\" 1}", CollapseSpaces(Write(new Dictionary<string, int> { ["not bare"] = 1 }).ReplaceLineEndings(" ")));
        object anonymous = new { A = 1, B = new object[] { "x", 2.5, null! } };
        Assert.Equal("{A 1 B [\"x\" 2.5 null]}", CollapseSpaces(Write(anonymous).ReplaceLineEndings(" ")));
        var holder = new Dictionary<string, object?> { ["p"] = new Point(1, 2), ["m"] = Mode.Dark };
        Assert.Equal("{p {X 1 Y 2} m Dark}", CollapseSpaces(Write(holder).ReplaceLineEndings(" ")));
        // A member declared as a base class is written by its runtime type.
        Assert.Equal("{b {Zeroth 0 First 1 Second 2}}", CollapseSpaces(Write(new Dictionary<string, Base> { ["b"] = new Derived { First = 1, Second = 2 } }).ReplaceLineEndings(" ")));
    }

    [Fact]
    public void WritesXferValuesAsTheyAre()
    {
        XferValue value = XferDocument.ParseValue("<! tag \"t\" !> {a 0x1F}");
        Assert.Equal(CollapseSpaces(XferWriterCanonical(value).ReplaceLineEndings(" ")), Write(value));
        Assert.Equal("{<! tag \"t\" !> v {a 0x1F}}", CollapseSpaces(Write(new Dictionary<string, XferValue> { ["v"] = value }).ReplaceLineEndings(" ")).Replace("0x1f", "0x1F", StringComparison.Ordinal));
    }

    private static string XferWriterCanonical(XferValue value) => XferWriter.Canonical(value).TrimEnd('\n');

    public sealed class Ignorable
    {
        public string? Text { get; set; }

        public int Number { get; set; }

        public int? Maybe { get; set; }

        [XferIgnore]
        public int Hidden { get; set; }
    }

    [Fact]
    public void HonoursIgnoreConditions()
    {
        var value = new Ignorable { Hidden = 5 };
        Assert.Equal("{Text null Number 0 Maybe null}", CollapseSpaces(Write(value).ReplaceLineEndings(" ")));
        Assert.Equal("{Number 0}", CollapseSpaces(Write(value, new XferSerializerOptions { DefaultIgnoreCondition = XferIgnoreCondition.WhenWritingNull }).ReplaceLineEndings(" ")));
        Assert.Equal("{}", CollapseSpaces(Write(value, new XferSerializerOptions { DefaultIgnoreCondition = XferIgnoreCondition.WhenWritingDefault }).ReplaceLineEndings(" ")));
        Assert.Equal(0, Read<Ignorable>("{Hidden 5}")!.Hidden);
    }

    [Fact]
    public void RoundTripsAnObjectGraph()
    {
        var p = new Person { Name = "Ada", Age = 36, Tags = ["a", "b c"], Friend = new Person { Name = "Bob" } };
        var back = Read<Person>(Write(p))!;
        Assert.Equal(JsonSerializer.Serialize(p), JsonSerializer.Serialize(back));
        Assert.Equal(new Point(3, 4), Read<Point>(Write(new Point(3, 4))));
    }

    // ---- Converters ----

    public sealed class Celsius
    {
        public double Degrees { get; init; }
    }

    public sealed class CelsiusConverter : XferConverter<Celsius>
    {
        public override Celsius Read(XferValue value, XferReadContext context) =>
            value is XferString s && s.Value.EndsWith('C')
                ? new Celsius { Degrees = double.Parse(s.Value[..^1], CultureInfo.InvariantCulture) }
                : throw new XferException($"expected a temperature at {context.Pointer}");

        public override XferValue Write(Celsius value, XferWriteContext context) =>
            new XferString(value.Degrees.ToString(CultureInfo.InvariantCulture) + "C");
    }

    [XferConverter(typeof(CelsiusConverter))]
    public sealed class Attributed
    {
        public double Degrees { get; init; }
    }

    public sealed class AttributedConverter : XferConverter<Attributed>
    {
        public override Attributed Read(XferValue value, XferReadContext context) => new() { Degrees = 1 };

        public override XferValue Write(Attributed value, XferWriteContext context) => new XferString("attributed");
    }

    public sealed class Weather
    {
        public Celsius? Today { get; set; }
    }

    [Fact]
    public void UsesConvertersFromOptions()
    {
        var options = new XferSerializerOptions();
        options.Converters.Add(new CelsiusConverter());
        Assert.Equal(21.5, Read<Weather>("{Today \"21.5C\"}", options)!.Today!.Degrees);
        Assert.Equal("{Today \"21.5C\"}", CollapseSpaces(Write(new Weather { Today = new Celsius { Degrees = 21.5 } }, options).ReplaceLineEndings(" ")));
        var e = Assert.Throws<XferException>(() => Read<Weather>("{\n Today 3}", options));
        Assert.Contains("/Today", e.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => options.Converters.Add(new CelsiusConverter()));
        Assert.Throws<InvalidOperationException>(() => options.PropertyNameCaseInsensitive = true);
    }

    [Fact]
    public void RefusesAConverterThatDoesNotFitItsType()
    {
        Assert.ThrowsAny<Exception>(() => Write(new Attributed()));
    }

    // ---- Depth and cycles ----

    public sealed class Node
    {
        public Node? Next { get; set; }
    }

    [Fact]
    public void ReadsAndWritesDeepNestingWithoutRecursion()
    {
        const int depth = 5000;
        var options = new XferSerializerOptions { Limits = new XferLimits { Depth = depth + 10 } };
        string text = string.Concat(Enumerable.Repeat("{Next ", depth)) + "null" + new string('}', depth);
        Node? node = Read<Node>(text, options);
        int count = 0;
        for (Node? n = node; n is not null; n = n.Next)
        {
            count++;
        }

        Assert.Equal(depth, count);
        Assert.Equal(CollapseSpaces(text), CollapseSpaces(Write(node, options).ReplaceLineEndings(" ")));
        string nested = string.Concat(Enumerable.Repeat("[", depth)) + new string(']', depth);
        Assert.NotNull(Read<object>(nested, options));
    }

    [Fact]
    public void RefusesACycleAsTooDeep()
    {
        var node = new Node();
        node.Next = node;
        var e = Assert.Throws<XferException>(() => Write(node));
        Assert.Equal(XferDiagnosticCode.XFER0602, Assert.Single(e.Diagnostics).Code);
        var list = new List<object>();
        list.Add(list);
        Assert.Equal(XferDiagnosticCode.XFER0602, Assert.Single(Assert.Throws<XferException>(() => Write(list)).Diagnostics).Code);
    }

    // ---- Entry points ----

    [Fact]
    public async Task EveryEntryPointAgrees()
    {
        const string text = "{X 1 Y 2}";
        var point = new Point(1, 2);
        Assert.Equal(point, XferSerializer.Deserialize<Point>(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(point, XferSerializer.Deserialize(text, typeof(Point)));
        Assert.Equal(point, XferSerializer.Deserialize<Point>(XferDocument.ParseValue(text)));
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
        {
            Assert.Equal(point, await XferSerializer.DeserializeAsync<Point>(stream, cancellationToken: TestContext.Current.CancellationToken));
        }

        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
        {
            Assert.Equal(point, await XferSerializer.DeserializeAsync(stream, typeof(Point), cancellationToken: TestContext.Current.CancellationToken));
        }

        string written = XferSerializer.Serialize(point);
        Assert.Equal(written, XferSerializer.Serialize(point, typeof(Point)));
        Assert.Equal(written, XferWriter.Canonical(XferSerializer.SerializeToValue(point)));
        using (var stream = new MemoryStream())
        {
            await XferSerializer.SerializeAsync(stream, point, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(written, Encoding.UTF8.GetString(stream.ToArray()));
        }

        using (var stream = new MemoryStream())
        {
            await XferSerializer.SerializeAsync(stream, point, typeof(Point), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(written, Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    [Fact]
    public void ReportsParseErrorsAsTheParserDoes()
    {
        var e = Assert.Throws<XferException>(() => Read<Point>("{X 1"));
        Assert.Contains(e.Diagnostics, d => d.Severity == XferSeverity.Error);
    }

    // ---- Section 12.5: the suite through object ----

    public static TheoryData<string> ValidIds
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (ValidCase c in SuiteCases.Valid)
            {
                data.Add(c.Id);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ValidIds))]
    public void EveryValidDocumentIsStableThroughObject(string id)
    {
        ValidCase c = SuiteCases.Valid.Single(v => v.Id == id);
        object? first = XferSerializer.Deserialize<object>(c.Input);
        string once = XferSerializer.Serialize(first);
        string twice = XferSerializer.Serialize(XferSerializer.Deserialize<object>(once));
        Assert.Equal(once, twice);

        // Apart from what the object table loses, the first output says what the input says.
        string? before = XferJson.ToJson(XferDocument.ParseValue(c.Input)).Value;
        string? after = XferJson.ToJson(XferDocument.ParseValue(once)).Value;
        Assert.NotNull(before);
        Assert.NotNull(after);
        var deep = new JsonDocumentOptions { MaxDepth = 1024 };
        AssertSameApartFromTheObjectLoss(System.Text.Json.Nodes.JsonNode.Parse(before, documentOptions: deep), System.Text.Json.Nodes.JsonNode.Parse(after, documentOptions: deep), string.Empty);
    }

    /// <summary>
    /// Compares two JSON trees allowing for the loss of section 10.3's object row: a number may
    /// change its spelling and, past double precision, its digits, but not the double it denotes;
    /// a time may lose trailing zeros of its fraction. Symbols and strings are both JSON strings.
    /// </summary>
    private static void AssertSameApartFromTheObjectLoss(System.Text.Json.Nodes.JsonNode? a, System.Text.Json.Nodes.JsonNode? b, string at)
    {
        switch (a)
        {
            case null:
                Assert.Null(b);
                break;
            case System.Text.Json.Nodes.JsonObject oa:
                var ob = Assert.IsType<System.Text.Json.Nodes.JsonObject>(b);
                Assert.Equal(oa.Select(p => p.Key), ob.Select(p => p.Key));
                foreach (var (key, value) in oa)
                {
                    AssertSameApartFromTheObjectLoss(value, ob[key], $"{at}/{key}");
                }

                break;
            case System.Text.Json.Nodes.JsonArray xa:
                var xb = Assert.IsType<System.Text.Json.Nodes.JsonArray>(b);
                Assert.Equal(xa.Count, xb.Count);
                for (int i = 0; i < xa.Count; i++)
                {
                    AssertSameApartFromTheObjectLoss(xa[i], xb[i], $"{at}/{i}");
                }

                break;
            default:
                var va = (System.Text.Json.Nodes.JsonValue)a;
                var vb = Assert.IsAssignableFrom<System.Text.Json.Nodes.JsonValue>(b);
                Assert.Equal(va.GetValueKind(), vb.GetValueKind());
                if (va.GetValueKind() == JsonValueKind.Number)
                {
                    Assert.True(
                        double.Parse(va.ToJsonString(), CultureInfo.InvariantCulture).Equals(double.Parse(vb.ToJsonString(), CultureInfo.InvariantCulture)),
                        $"{at}: {va.ToJsonString()} and {vb.ToJsonString()}");
                }
                else if (va.GetValueKind() == JsonValueKind.String && va.GetValue<string>() != vb.GetValue<string>())
                {
                    Assert.Equal(TrimFraction(va.GetValue<string>()), TrimFraction(vb.GetValue<string>()));
                }
                else
                {
                    Assert.Equal(va.ToJsonString(), vb.ToJsonString());
                }

                break;
        }
    }

    private static string TrimFraction(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"(\d{2}:\d{2}:\d{2})(\.\d*?)0*(?=$|[Zz+-])", m => m.Groups[1].Value + (m.Groups[2].Value == "." ? string.Empty : m.Groups[2].Value));

    // ---- Section 12.5: the parkscomputing.com documents ----

    [Theory]
    [InlineData("sitenav")]
    [InlineData("admin-menu")]
    public void TheConvertedSiteDocumentsReadAsTheLegacyLibraryReadsTheOriginals(string name)
    {
        string fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
        string converted = File.ReadAllText(Path.Combine(fixtures, name + ".xfer"));
        string legacy = File.ReadAllText(Path.Combine(fixtures, name + ".legacy.xfer"));

        NavNode? now = XferSerializer.Deserialize<NavNode>(converted);
        LegacyNavNode? then = ParksComputing.Xfer.Lang.XferConvert.Deserialize<LegacyNavNode>(legacy);

        Assert.NotNull(now);
        Assert.NotNull(then);
        var json = new JsonSerializerOptions { WriteIndented = true };
        Assert.Equal(JsonSerializer.Serialize(then, json), JsonSerializer.Serialize(now, json));
    }
}
