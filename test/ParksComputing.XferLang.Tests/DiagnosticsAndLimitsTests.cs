using System;
using Xunit;

namespace ParksComputing.XferLang.Tests;

public class DiagnosticsAndLimitsTests
{
    private static readonly XferPosition Start = new(4, 2, 3);
    private static readonly XferPosition End = new(6, 2, 5);

    [Fact]
    public void CreateTakesTheSeverityAndMessageFromTheTable()
    {
        XferDiagnostic d = XferDiagnostics.Create(XferDiagnosticCode.XFER0403, Start, End);
        Assert.Equal(new XferDiagnostic(XferDiagnosticCode.XFER0403, XferSeverity.Error, "duplicate key", Start, End), d);
        Assert.Equal(XferSeverity.Warning, XferDiagnostics.Create(XferDiagnosticCode.XFER0901, Start).Severity);
        Assert.Equal(XferSeverity.Info, XferDiagnostics.Create(XferDiagnosticCode.XFER0903, Start).Severity);
    }

    [Fact]
    public void CreateAddsTheDetailAfterAColonAndDefaultsTheEndToTheStart()
    {
        XferDiagnostic d = XferDiagnostics.Create(XferDiagnosticCode.XFER0101, Start, detail: "found \"h\"");
        Assert.Equal("scalar token not followed by a separator: found \"h\"", d.Message);
        Assert.Equal(Start, d.End);
    }

    [Fact]
    public void CodesAreWrittenAndParsedInTheSpecificationsForm()
    {
        Assert.Equal("XFER0404", XferDiagnostics.Format(XferDiagnosticCode.XFER0404));
        Assert.Throws<ArgumentOutOfRangeException>(() => XferDiagnostics.Format((XferDiagnosticCode)(-5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => XferDiagnostics.Format((XferDiagnosticCode)12345));
        Assert.Equal("XFER0101", XferDiagnostics.Format(XferDiagnosticCode.XFER0101));
        Assert.Equal("XFER0404", XferDiagnostics.Create(XferDiagnosticCode.XFER0404, Start).CodeText);
        Assert.True(XferDiagnostics.TryParse("XFER0404", out XferDiagnosticCode code));
        Assert.Equal(XferDiagnosticCode.XFER0404, code);
        foreach (string? bad in new string?[] { null, string.Empty, "XFER404", "xfer0404", "XFER0000", "XFER9999", "XFER04040", "XFER040x", "XFER-404" })
        {
            Assert.False(XferDiagnostics.TryParse(bad, out _), bad ?? "null");
        }
    }

    [Fact]
    public void AValueThatIsNotACodeIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => XferDiagnostics.SeverityOf((XferDiagnosticCode)1));
        Assert.Throws<ArgumentOutOfRangeException>(() => XferDiagnostics.SummaryOf((XferDiagnosticCode)99999));
    }

    [Fact]
    public void EveryCodeHasANumberThatMatchesItsName()
    {
        foreach (XferDiagnosticCode code in Enum.GetValues<XferDiagnosticCode>())
        {
            Assert.Equal(code.ToString(), XferDiagnostics.Format(code));
        }
    }

    [Fact]
    public void TheDefaultLimitsAreTheSpecificationsMinimums()
    {
        XferLimits d = XferLimits.Default;
        Assert.Equal(268_435_455, d.DocumentBytes);
        Assert.Equal(256, d.Depth);
        Assert.Equal(268_435_455, d.TokenBytes);
        Assert.Equal(64, d.RunLength);
        Assert.Equal(int.MaxValue, d.Elements);
        Assert.Equal(new XferLimits(), d);
    }

    [Fact]
    public void ALimitMayBeRaisedOrLowered()
    {
        var limits = new XferLimits { Depth = 10, RunLength = 1, DocumentBytes = 5, Elements = 7, TokenBytes = 3 };
        Assert.Equal((10, 1, 5, 7, 3), (limits.Depth, limits.RunLength, limits.DocumentBytes, limits.Elements, limits.TokenBytes));
        Assert.Equal(1000, (XferLimits.Default with { Depth = 1000 }).Depth);
        Assert.Equal(256, XferLimits.Default.Depth);
    }

    [Fact]
    public void ALimitOfZeroOrLessIsRefusedWhenItIsSet()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new XferLimits { Depth = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new XferLimits { RunLength = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new XferLimits { DocumentBytes = int.MinValue });
        Assert.Throws<ArgumentOutOfRangeException>(() => XferLimits.Default with { Elements = 0 });
        ArgumentOutOfRangeException e = Assert.Throws<ArgumentOutOfRangeException>(() => new XferLimits { TokenBytes = 0 });
        Assert.Equal("TokenBytes", e.ParamName);
    }

    [Fact]
    public void PositionsCompareByValue()
    {
        Assert.Equal(new XferPosition(0, 1, 1), XferPosition.Origin);
        Assert.NotEqual(new XferPosition(1, 1, 2), XferPosition.Origin);
    }
}
