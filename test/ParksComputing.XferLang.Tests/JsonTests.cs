using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ParksComputing.XferLang.Tests;

public class JsonTests
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 100_000 };

    [Fact]
    public void AgreesWithTheReferenceImplementationOnEveryFixtureCase()
    {
        // fixtures/json.jsonl is written by tools/fixtures/json-fixture.mjs from xfer-js: JSON texts
        // that are valid, damaged, or token soup (fromJson), and XferLang documents (toJson).
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "json.jsonl");
        var failures = new List<string>();
        int from = 0;
        int to = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            JsonNode c = JsonNode.Parse(line, null, DocumentOptions)!;
            string label = c["label"]!.GetValue<string>();
            string? problem;
            if (c["kind"]!.GetValue<string>() == "from")
            {
                from++;
                problem = CompareFrom(c);
            }
            else
            {
                to++;
                problem = CompareTo(c);
            }

            if (problem is not null)
            {
                failures.Add($"{label}: {problem}");
                if (failures.Count == 10)
                {
                    break;
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.True(from > 5000 && to > 700, $"only {from} from and {to} to cases");
    }

    private static string? CompareFrom(JsonNode c)
    {
        // The JSON text is stored as base64 of its UTF-16 code units, since it may hold a lone surrogate.
        byte[] units = Convert.FromBase64String(c["json"]!.GetValue<string>());
        string json = new(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(units));
        XferFromJsonOptions? options = null;
        XferLimits limits = XferLimits.Default;
        if (c["limits"] is JsonObject tight)
        {
            limits = new XferLimits { Depth = tight["depth"]!.GetValue<int>() };
            options = new XferFromJsonOptions { Limits = limits };
        }

        XferJsonResult<XferValue?> result = XferJson.FromJson(json, options);
        JsonArray want = c["diagnostics"]!.AsArray();
        if (want.Count != result.Diagnostics.Count)
        {
            return $"{JsonSerializer.Serialize(json)}: diagnostics {string.Join(", ", result.Diagnostics.Select(d => $"{d.CodeText}@{d.Start.Offset}"))}, expected {want.ToJsonString()}";
        }

        for (int i = 0; i < want.Count; i++)
        {
            XferDiagnostic d = result.Diagnostics[i];
            var got = new JsonArray(d.CodeText, d.Start.Offset, d.Start.Line, d.Start.Column, d.Message);
            if (!JsonNode.DeepEquals(got, want[i]))
            {
                return $"{JsonSerializer.Serialize(json)}: diagnostic {i} {got.ToJsonString()}, expected {want[i]!.ToJsonString()}";
            }
        }

        string? wantCanonical = c["canonical"]?.GetValue<string>();
        if (result.Value is null)
        {
            return wantCanonical is null ? null : $"{JsonSerializer.Serialize(json)}: no value, expected {JsonSerializer.Serialize(wantCanonical)}";
        }

        string gotCanonical = XferWriter.Canonical(result.Value, new XferWriteOptions { Limits = limits });
        return gotCanonical == wantCanonical ? null : $"{JsonSerializer.Serialize(json)}: canonical {JsonSerializer.Serialize(gotCanonical)}, expected {JsonSerializer.Serialize(wantCanonical)}";
    }

    private static string? CompareTo(JsonNode c)
    {
        string xfer = c["xfer"]!.GetValue<string>();
        XferValue value = XferDocument.ParseValue(xfer);
        (string name, XferToJsonOptions options)[] runs =
        [
            ("compact", new XferToJsonOptions()),
            ("big", new XferToJsonOptions { StringifyBigIntegers = true }),
            ("indent2", new XferToJsonOptions { Indent = 2 }),
            ("indent15", new XferToJsonOptions { Indent = 15 }),
        ];
        foreach ((string name, XferToJsonOptions options) in runs)
        {
            XferJsonResult<string> result = XferJson.ToJson(value, options);
            JsonArray want = c[name]!.AsArray();
            if (result.Value != want[0]!.GetValue<string>())
            {
                return $"{name} of {JsonSerializer.Serialize(xfer)}: {JsonSerializer.Serialize(result.Value)}, expected {want[0]!.ToJsonString()}";
            }

            JsonArray diagnostics = want[1]!.AsArray();
            if (diagnostics.Count != result.Diagnostics.Count)
            {
                return $"{name} of {JsonSerializer.Serialize(xfer)}: {result.Diagnostics.Count} diagnostics, expected {diagnostics.ToJsonString()}";
            }

            for (int i = 0; i < diagnostics.Count; i++)
            {
                XferDiagnostic d = result.Diagnostics[i];
                if (d.CodeText != diagnostics[i]![0]!.GetValue<string>() || d.Message != diagnostics[i]![4]!.GetValue<string>())
                {
                    return $"{name} of {JsonSerializer.Serialize(xfer)}: diagnostic {i} {d.CodeText} \"{d.Message}\", expected {diagnostics[i]!.ToJsonString()}";
                }
            }
        }

        return null;
    }

    [Fact]
    public void ToJsonOutputIsAlwaysWellFormedJsonThatTheSystemReaderAccepts()
    {
        foreach (string xfer in new[] { "{ a [ 1 2.5 true null x \"s\\\" 2025-08-01 ] \"é日\" \"\\t\" }", "[ inf nan ]", "{ <! tag t !> k 9007199254740993 }", "\"line\nbreak\\\"" })
        {
            string text = XferJson.ToJson(XferDocument.ParseValue(xfer)).Value;
            using JsonDocument doc = JsonDocument.Parse(text);
            Assert.NotEqual(JsonValueKind.Undefined, doc.RootElement.ValueKind);
        }
    }

    [Fact]
    public void FromJsonAndTheSystemReaderAgreeOnWhatIsWellFormed()
    {
        var random = new Random(20261019);
        string[] pieces = ["{", "}", "[", "]", ",", ":", "\"a\"", "1", "-1", "1.5", "1e5", "true", "false", "null", " ", "\n", "\"\\u0041\"", "\"\\n\"", "01", "1.", ".5", "+1", "tru", "\"unterminated", "'x'", "//c", "/*c*/", "\"\\x\"", "NaN"];
        for (int run = 0; run < 20_000; run++)
        {
            var text = new StringBuilder();
            int n = random.Next(0, 10);
            for (int i = 0; i < n; i++)
            {
                text.Append(pieces[random.Next(pieces.Length)]);
            }

            bool systemAccepts;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(text.ToString());
                systemAccepts = true;
            }
            catch (JsonException)
            {
                systemAccepts = false;
            }

            bool malformed = XferJson.FromJson(text.ToString()).Diagnostics.Any(d => d.Code == XferDiagnosticCode.XFER0906);
            Assert.True(malformed == !systemAccepts, text.ToString());
        }
    }

    [Fact]
    public void ReadsBytesWithOrWithoutAByteOrderMarkAndRefusesInvalidUtf8()
    {
        XferJsonResult<XferValue?> plain = XferJson.FromJson("{\"a\":1}"u8);
        XferJsonResult<XferValue?> marked = XferJson.FromJson([0xEF, 0xBB, 0xBF, .. "{\"a\":1}"u8]);
        Assert.Equal(plain.Value, marked.Value);
        Assert.NotNull(plain.Value);

        XferJsonResult<XferValue?> bad = XferJson.FromJson([0x22, 0xFF, 0x22]);
        Assert.Null(bad.Value);
        XferDiagnostic d = Assert.Single(bad.Diagnostics);
        Assert.Equal((XferDiagnosticCode.XFER0906, XferPosition.Origin), (d.Code, d.Start));

        // A byte order mark in a string is malformed, as it is in the reference implementation.
        Assert.Contains(XferJson.FromJson("\uFEFF{}").Diagnostics, x => x.Code == XferDiagnosticCode.XFER0906);
    }

    [Fact]
    public void DeepNestingIsConvertedWithoutRecursionWhenTheLimitAllowsIt()
    {
        const int depth = 200_000;
        string json = new string('[', depth) + new string(']', depth);
        var options = new XferFromJsonOptions { Limits = new XferLimits { Depth = depth } };
        XferJsonResult<XferValue?> result = XferJson.FromJson(json, options);
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Value);
        string back = XferJson.ToJson(result.Value).Value;
        Assert.Equal(json, back);

        XferJsonResult<XferValue?> refused = XferJson.FromJson(json);
        Assert.Null(refused.Value);
        Assert.Equal(XferDiagnosticCode.XFER0602, Assert.Single(refused.Diagnostics).Code);
    }

    [Fact]
    public void ADiagnosticPointerForADeepValueDoesNotCostMemoryQuadraticInTheDepth()
    {
        XferValue value = new XferSymbol("s");
        for (int i = 0; i < 100_000; i++)
        {
            value = new XferSequence([value]);
        }

        var timer = System.Diagnostics.Stopwatch.StartNew();
        XferJsonResult<string> result = XferJson.ToJson(value);
        timer.Stop();
        Assert.Single(result.Diagnostics);
        Assert.True(timer.ElapsedMilliseconds < 5_000, $"{timer.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void AVeryLongDecimalIntegerRoundTripsWithoutBeingConvertedToABigInteger()
    {
        string digits = "1" + new string('7', 3_000_000);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        XferJsonResult<XferValue?> read = XferJson.FromJson("[" + digits + "]");
        XferJsonResult<string> written = XferJson.ToJson(read.Value!);
        timer.Stop();
        Assert.Equal("[" + digits + "]", written.Value);
        Assert.Equal(XferDiagnosticCode.XFER0901, Assert.Single(written.Diagnostics).Code);
        Assert.True(timer.ElapsedMilliseconds < 2_000, $"{timer.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ARefusedValueOfAnUnknownKindIsAnArgumentException()
    {
        Assert.Throws<ArgumentNullException>(() => XferJson.ToJson(null!));
        Assert.Throws<ArgumentNullException>(() => XferJson.FromJson((string)null!));
    }

    [Fact]
    public void IntegerHelpersNeverRoundOrWrap()
    {
        var big = (XferInteger)XferDocument.ParseValue("9223372036854775808");
        Assert.False(big.TryGetInt64(out _));
        Assert.True(big.TryGet(out ulong u));
        Assert.Equal(9223372036854775808UL, u);
        var small = (XferInteger)XferDocument.ParseValue("-129");
        Assert.False(small.TryGet(out sbyte _));
        Assert.True(small.TryGet(out short s));
        Assert.Equal(-129, s);
        Assert.True(((XferInteger)XferDocument.ParseValue("0xFF")).TryGet(out byte b));
        Assert.Equal(255, b);
        Assert.False(((XferInteger)XferDocument.ParseValue("256")).TryGet(out byte _));
        Assert.False(((XferInteger)XferDocument.ParseValue("-1")).TryGet(out uint _));

        Assert.Equal(1024.0, ((XferInteger)XferDocument.ParseValue("1024")).ToDouble(out bool exact));
        Assert.True(exact);
        ((XferInteger)XferDocument.ParseValue("9007199254740993")).ToDouble(out exact);
        Assert.False(exact);
        ((XferInteger)XferDocument.ParseValue("9007199254740992")).ToDouble(out exact);
        Assert.True(exact);
        ((XferInteger)XferDocument.ParseValue("1" + new string('0', 400))).ToDouble(out exact);
        Assert.False(exact);

        Assert.True(((XferInteger)XferDocument.ParseValue("79228162514264337593543950335")).TryGetDecimal(out decimal dm));
        Assert.Equal(decimal.MaxValue, dm);
        Assert.False(((XferInteger)XferDocument.ParseValue("79228162514264337593543950336")).TryGetDecimal(out _));
    }

    [Fact]
    public void FractionalHelpersSayWhenAConversionIsExact()
    {
        static XferFractional Frac(string lexeme) => new(lexeme);

        Assert.True(Frac("1.50").TryGetDecimalParts(out BigInteger digits, out int exponent));
        Assert.Equal((new BigInteger(150), -2), (digits, exponent));
        Assert.True(Frac("6.02e23").TryGetDecimalParts(out digits, out exponent));
        Assert.Equal((new BigInteger(602), 21), (digits, exponent));
        Assert.True(Frac("-1_0.5E-1").TryGetDecimalParts(out digits, out exponent));
        Assert.Equal((new BigInteger(-105), -2), (digits, exponent));
        Assert.False(Frac("inf").TryGetDecimalParts(out _, out _));
        Assert.False(Frac("nan").TryGetDecimalParts(out _, out _));
        Assert.True(Frac("0.00").TryGetDecimalParts(out digits, out exponent));
        Assert.Equal((BigInteger.Zero, 0), (digits, exponent));

        Assert.Equal(0.5, Frac("0.5").ToDouble(out bool exact));
        Assert.True(exact);
        Frac("0.1").ToDouble(out exact);
        Assert.False(exact);
        Assert.True(double.IsPositiveInfinity(Frac("1e400").ToDouble(out exact)));
        Assert.False(exact);
        Assert.Equal(0.0, Frac("1e-400").ToDouble(out exact));
        Assert.False(exact);
        Assert.Equal(0.0, Frac("0e99999999999999999999").ToDouble(out exact));
        Assert.True(exact);
        Assert.True(double.IsNegativeInfinity(Frac("-inf").ToDouble(out exact)));
        Assert.True(exact);
        Assert.True(double.IsNaN(Frac("nan").ToDouble(out exact)));
        Assert.True(exact);

        Assert.True(Frac("1.50").TryGetDecimal(out decimal d));
        Assert.Equal("1.50", d.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(Frac("-0.0000000000000000000000000001").TryGetDecimal(out d));
        Assert.Equal(-0.0000000000000000000000000001m, d);
        Assert.False(Frac("0.00000000000000000000000000001").TryGetDecimal(out _)); // 29 places
        Assert.False(Frac("1e30").TryGetDecimal(out _));
        Assert.True(Frac("1e28").TryGetDecimal(out d));
        Assert.Equal(10_000_000_000_000_000_000_000_000_000m, d);
        Assert.False(Frac("inf").TryGetDecimal(out _));
        Assert.True(Frac("1.000000000000000000000000000000e0").TryGetDecimal(out d));
        Assert.Equal(1m, d);
    }

    [Fact]
    public void ExactnessAgreesWithAnIndependentExpansionForRandomDoubles()
    {
        var random = new Random(5);
        for (int i = 0; i < 2000; i++)
        {
            double x = BitConverter.Int64BitsToDouble(random.NextInt64() & 0x7FEFFFFFFFFFFFFFL);
            if (x == 0)
            {
                continue;
            }

            // The exact decimal expansion of a double is exact, and a one-digit perturbation is not.
            BigInteger m = new(Math.Abs(x) * Math.Pow(2, 0)); // placeholder to keep the type used
            string exactText = ExactDecimal(x);
            Assert.Equal(x, new XferFractional(exactText).ToDouble(out bool exact));
            Assert.True(exact, exactText);
            Assert.False(new XferFractional(exactText + "1").ToDouble(out exact) != x && exact);
            GC.KeepAlive(m);
        }
    }

    private static string ExactDecimal(double x)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        int biased = (int)((bits >> 52) & 0x7FF);
        long fraction = bits & ((1L << 52) - 1);
        BigInteger mantissa = biased == 0 ? fraction : fraction | (1L << 52);
        int e = (biased == 0 ? 1 : biased) - 1075;
        string sign = bits < 0 ? "-" : string.Empty;
        return e >= 0
            ? sign + (mantissa * BigInteger.Pow(2, e)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : sign + (mantissa * BigInteger.Pow(5, -e)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "e" + e.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void TemporalHelpersRefuseWhatNoDotNetTypeCanHoldInsteadOfRoundingIt()
    {
        static XferTemporal T(XferTemporalKind kind, string lexeme) => new(kind, lexeme);

        Assert.True(T(XferTemporalKind.Date, "2025-08-01").TryGetDateOnly(out DateOnly date));
        Assert.Equal(new DateOnly(2025, 8, 1), date);
        Assert.False(T(XferTemporalKind.Date, "0000-01-01").TryGetDateOnly(out _));
        Assert.False(T(XferTemporalKind.Date, "2025-02-30").TryGetDateOnly(out _));
        Assert.False(T(XferTemporalKind.Time, "09:30:00").TryGetDateOnly(out _));

        Assert.True(T(XferTemporalKind.Time, "09:30:00.250").TryGetTimeOnly(out TimeOnly time));
        Assert.Equal(new TimeOnly(9, 30, 0, 250), time);
        Assert.True(T(XferTemporalKind.Time, "09:30:00.2500000000").TryGetTimeOnly(out time)); // zeros past the seventh digit are exact
        Assert.True(T(XferTemporalKind.Time, "09:30:00.1234567").TryGetTimeOnly(out time));
        Assert.Equal(1234567, time.Ticks % TimeSpan.TicksPerSecond);
        Assert.False(T(XferTemporalKind.Time, "09:30:00.12345671").TryGetTimeOnly(out _));
        Assert.False(T(XferTemporalKind.Time, "23:59:60").TryGetTimeOnly(out _));
        Assert.False(T(XferTemporalKind.Time, "09:30:00.").TryGetTimeOnly(out _));
        Assert.False(T(XferTemporalKind.Time, "24:00:00").TryGetTimeOnly(out _));

        Assert.True(T(XferTemporalKind.Date, "2025-08-01").TryGetDateTime(out DateTime dt));
        Assert.Equal((new DateTime(2025, 8, 1), DateTimeKind.Unspecified), (dt, dt.Kind));
        Assert.True(T(XferTemporalKind.LocalDateTime, "2025-08-01T09:30:00.5").TryGetDateTime(out dt));
        Assert.Equal((new DateTime(2025, 8, 1, 9, 30, 0, 500), DateTimeKind.Unspecified), (dt, dt.Kind));
        Assert.True(T(XferTemporalKind.DateTime, "2025-08-01T09:30:00+08:00").TryGetDateTime(out dt));
        Assert.Equal((new DateTime(2025, 8, 1, 1, 30, 0), DateTimeKind.Utc), (dt, dt.Kind));
        Assert.False(T(XferTemporalKind.Time, "09:30:00").TryGetDateTime(out _));
        Assert.False(T(XferTemporalKind.DateTime, "0001-01-01T00:00:00+01:00").TryGetDateTime(out _)); // UTC before year 1
        Assert.False(T(XferTemporalKind.DateTime, "9999-12-31T23:59:59-01:00").TryGetDateTime(out _)); // UTC after year 9999
        Assert.True(T(XferTemporalKind.DateTime, "9999-12-31T23:59:59.9999999Z").TryGetDateTime(out dt));
        Assert.Equal(DateTime.MaxValue, dt);
        Assert.False(T(XferTemporalKind.DateTime, "2025-08-01T00:00:00+14:01").TryGetDateTime(out _)); // beyond 14:00
        Assert.True(T(XferTemporalKind.DateTime, "2025-08-01T00:00:00+14:00").TryGetDateTime(out _));
        Assert.False(T(XferTemporalKind.DateTime, "2025-08-01T00:00:00-23:59").TryGetDateTime(out _));
        Assert.False(T(XferTemporalKind.DateTime, "2025-08-01T00:00:00+08:60").TryGetDateTime(out _));
        Assert.False(T(XferTemporalKind.LocalDateTime, "0000-12-31T00:00:00").TryGetDateTime(out _));

        Assert.True(T(XferTemporalKind.DateTime, "2025-08-01T09:30:00+08:00").TryGetDateTimeOffset(out DateTimeOffset dto));
        Assert.Equal(new DateTimeOffset(2025, 8, 1, 9, 30, 0, TimeSpan.FromHours(8)), dto);
        Assert.Equal(TimeSpan.FromHours(8), dto.Offset);
        Assert.True(T(XferTemporalKind.DateTime, "2025-08-01T09:30:00Z").TryGetDateTimeOffset(out dto));
        Assert.Equal(TimeSpan.Zero, dto.Offset);
        Assert.False(T(XferTemporalKind.LocalDateTime, "2025-08-01T09:30:00").TryGetDateTimeOffset(out _));
        Assert.False(T(XferTemporalKind.Date, "2025-08-01").TryGetDateTimeOffset(out _));
        Assert.False(T(XferTemporalKind.DateTime, "2025-08-01T23:59:60Z").TryGetDateTimeOffset(out _));
        Assert.False(T(XferTemporalKind.DateTime, "garbage").TryGetDateTimeOffset(out _));
    }

    [Fact]
    public void EveryParsedTemporalIsEitherConvertedToTheSameInstantOrRefused()
    {
        var random = new Random(11);
        for (int i = 0; i < 20_000; i++)
        {
            string text = $"{random.Next(0, 10000):D4}-{random.Next(1, 13):D2}-{random.Next(1, 29):D2}T{random.Next(0, 24):D2}:{random.Next(0, 60):D2}:{random.Next(0, 61):D2}"
                + (random.Next(3) == 0 ? "." + random.Next(0, 1_000_000_000).ToString("D9", System.Globalization.CultureInfo.InvariantCulture) : string.Empty)
                + random.Next(4) switch { 0 => "Z", 1 => "+08:00", 2 => "-05:30", _ => string.Empty };
            XferParseResult result = XferDocument.Parse(text);
            if (!result.Ok)
            {
                continue;
            }

            var t = (XferTemporal)result.Value!;
            bool ok = t.TryGetDateTime(out DateTime dt);
            bool hasFractionPast7 = text.Contains('.', StringComparison.Ordinal) && text[(text.IndexOf('.', StringComparison.Ordinal) + 1)..].TakeWhile(char.IsDigit).Skip(7).Any(c => c != '0');
            bool leap = text.Substring(17, 2) == "60";
            bool yearZero = text.StartsWith("0000", StringComparison.Ordinal);
            if (hasFractionPast7 || leap || yearZero)
            {
                Assert.False(ok, text);
            }
            else if (ok && t.TemporalKind == XferTemporalKind.DateTime)
            {
                Assert.True(t.TryGetDateTimeOffset(out DateTimeOffset dto), text);
                Assert.Equal(dto.UtcDateTime, dt);
            }
        }
    }
}
