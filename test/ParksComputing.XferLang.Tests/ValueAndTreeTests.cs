using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ParksComputing.XferLang.Syntax;
using ParksComputing.XferLang.Writing;
using ParksComputing.XferLang.Tests.Harness;
using Xunit;

namespace ParksComputing.XferLang.Tests;

/// <summary>The value model's equality and hashing, its collection surface, and the tree's token and trivia views.</summary>
public class ValueAndTreeTests
{
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
    public void AValueEqualsTheSameDocumentParsedAgainAndHashesTheSame(string id)
    {
        ValidCase c = SuiteCases.Valid.Single(v => v.Id == id);
        XferValue a = XferDocument.ParseValue(c.Input);
        XferValue b = XferDocument.ParseValue(c.Input);
        Assert.True(a.Equals(b));
        Assert.True(a.Equals((object)b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        // The canonical form may respell a number (0xFF as 0xff), which is a different value, but
        // the canonical form of the canonical form is the same value.
        XferValue canonical = XferDocument.ParseValue(c.Canonical);
        XferValue again = XferDocument.ParseValue(XferWriter.Canonical(canonical));
        Assert.True(canonical.Equals(again));
        Assert.Equal(canonical.GetHashCode(), again.GetHashCode());
    }

    [Fact]
    public void ValuesThatDifferAreNotEqual()
    {
        string[] distinct =
        [
            "1", "2", "0x1", "1.5", "1.50", "inf", "nan", "true", "false", "null", "a", "b", "\"a\"", "\"b\"",
            "2025-08-01", "2025-08-02", "09:30:00", "2025-08-01T09:30:00", "2025-08-01T09:30:00Z",
            "[]", "[1]", "[1 2]", "[2 1]", "{}", "{a 1}", "{a 2}", "{b 1}", "{a 1 b 2}", "{b 2 a 1}",
            "<! tag t !> 1", "<! tag u !> 1", "<! id \"t\" !> 1", "<! tag t !> <! id \"x\" !> 1", "[<! tag t !> 1]", "{<! tag t !> a 1}",
        ];
        XferValue[] values = [.. distinct.Select(d => XferDocument.ParseValue(d))];
        for (int i = 0; i < values.Length; i++)
        {
            for (int j = 0; j < values.Length; j++)
            {
                Assert.True(values[i].Equals(values[j]) == (i == j), $"{distinct[i]} and {distinct[j]}");
            }

            Assert.False(values[i].Equals(null));
            Assert.False(values[i].Equals((object)"not a value"));
        }
    }

    [Fact]
    public void BuiltValuesEqualParsedOnes()
    {
        var built = new XferObject(
        [
            new("s", new XferString("x")),
            new("i", new XferInteger(new BigInteger(5), "5")),
            new("f", new XferFractional("1.5")),
            new("b", new XferBool(true)),
            new("n", new XferNull()),
            new("y", new XferSymbol("dark", [new XferAnnotation("tag", new XferSymbol("t"))])),
            new("d", new XferTemporal(XferTemporalKind.Date, "2025-08-01")),
            new("q", new XferSequence([new XferBool(false)])),
        ]);
        XferValue parsed = XferDocument.ParseValue("{s \"x\" i 5 f 1.5 b true n null <! tag t !> y dark d 2025-08-01 q [false]}");
        Assert.True(built.Equals(parsed));
        Assert.Equal(built.GetHashCode(), parsed.GetHashCode());
    }

    [Fact]
    public void AnObjectIsAReadOnlyOrderedMap()
    {
        var o = (XferObject)XferDocument.ParseValue("{z 1 a 2}");
        Assert.True(o.ContainsKey("z"));
        Assert.False(o.ContainsKey("q"));
        Assert.True(o.TryGetValue("a", out XferValue? a));
        Assert.Equal("2", ((XferInteger)a).Lexeme);
        Assert.False(o.TryGetValue("q", out _));
        Assert.Equal(["z", "a"], o.Keys);
        Assert.Equal(2, ((IEnumerable)o).Cast<object>().Count());
        Assert.Throws<ArgumentException>(() => new XferObject([new("k", new XferNull()), new("k", new XferNull())]));

        var s = (XferSequence)XferDocument.ParseValue("[1 2 3]");
        Assert.Equal(3, ((IEnumerable)s).Cast<object>().Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => new XferTemporal((XferTemporalKind)99, "x"));
        Assert.Equal(XferValueKind.Bool, new XferBool(true).Kind);
        Assert.Equal(XferValueKind.DateTime, new XferTemporal(XferTemporalKind.DateTime, "2025-08-01T00:00:00Z").Kind);
    }

    [Fact]
    public void TokensAndTriviaReportTheirTextAndPositions()
    {
        const string text = "</ lead />\n{ a \"s\"\n  b ##\"q\"## c true }  </// tail ///>";
        XferSyntaxTree tree = XferDocument.Parse(text).Tree;
        Assert.Equal(text, tree.Text);

        var tokens = tree.Tokens;
        Assert.Equal(tokens.Count, ((IEnumerable)tokens).Cast<object>().Count());
        for (int k = 0; k < tokens.Count; k++)
        {
            XferToken t = tokens[k];
            Assert.Equal(tree.GetPosition(t.Offset), t.Start);
            Assert.Equal(tree.GetPosition(t.Offset + t.Length), t.End);
            Assert.Equal(t.Length, t.End.Offset - t.Start.Offset);
            Assert.Equal(t.Kind == XferTokenKind.String, t.Content is not null);
            Assert.True(t == tokens[k]);
            Assert.Equal(t.GetHashCode(), tokens[k].GetHashCode());
        }

        XferToken hashed = tokens.Single(t => t.Content == "q");
        Assert.Equal((XferStringForm.Hash, 2), (hashed.Form, hashed.Run));
        Assert.Equal(XferKeyword.True, tokens.Single(t => t.Text == "true").Keyword);
        Assert.True(tokens[0] != tokens[1]);
        Assert.False(tokens[0].Equals((object)tokens[1]));
        Assert.True(tokens[0].Equals((object)tokens[0]));

        XferTrivia lead = tokens[0].LeadingTrivia[0];
        Assert.Equal((XferTriviaKind.Comment, 0, 10, 1), (lead.Kind, lead.Offset, lead.Length, lead.Run));
        Assert.Equal("</ lead />", lead.Text);
        Assert.Equal((1, 1), (lead.Start.Line, lead.Start.Column));
        Assert.Equal((1, 11), (lead.End.Line, lead.End.Column));
        XferTrivia tail = tree.EndOfFile.LeadingTrivia.Single(v => v.Kind == XferTriviaKind.Comment);
        Assert.Equal(3, tail.Run);
        Assert.True(lead == tokens[0].LeadingTrivia[0]);
        Assert.True(lead != tokens[0].LeadingTrivia[1]);
        Assert.True(lead.Equals((object)tokens[0].LeadingTrivia[0]));
        Assert.Equal(lead.GetHashCode(), tokens[0].LeadingTrivia[0].GetHashCode());
    }
}
