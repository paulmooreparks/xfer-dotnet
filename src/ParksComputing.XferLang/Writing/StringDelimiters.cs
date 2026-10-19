using System;
using System.Collections.Generic;
using ParksComputing.XferLang.Syntax;

namespace ParksComputing.XferLang.Writing;

/// <summary>
/// Chooses how to delimit a string (specification 6, rule 10). The choice is deterministic, and
/// the delimiter is always visibly longer than anything in the content. A port of xfer-js
/// <c>src/strings.ts</c>.
/// </summary>
internal static class StringDelimiters
{
    private const char Quote = '"';
    private const char Hash = '#';

    /// <summary>
    /// The delimiter for a content:
    /// no quote in the content, one quote on each side;
    /// quotes inside but not at either end, repetition one longer than the longest run of quotes
    /// and never two, because a run of exactly two quotes is the empty string;
    /// a quote at an end, hash-run with one more hash than the longest run that follows a quote.
    /// When that length would pass the run-length limit, the smallest length of the same form that
    /// no run in the content uses is taken instead. That can still pass the limit, but only for
    /// content with a quote run (or a hash run after a quote) of every length up to it, which no
    /// real document has. It is used anyway, because a string must always be writable; a parser
    /// enforcing the limit reports XFER0604 for it, and one that accepts longer runs reads it.
    /// </summary>
    public static (XferStringForm Form, int Run) Choose(ReadOnlySpan<char> content, int runLimit)
    {
        if (!content.Contains(Quote))
        {
            return (XferStringForm.Repetition, 1);
        }

        if (content[0] != Quote && content[^1] != Quote)
        {
            HashSet<int> runs = QuoteRuns(content);
            int preferred = Longest(runs) + 1 == 2 ? 3 : Longest(runs) + 1;
            return (XferStringForm.Repetition, preferred <= runLimit ? preferred : SmallestMissing(runs, skipTwo: true));
        }

        HashSet<int> hashes = HashRunsAfterQuote(content);
        int wanted = Longest(hashes) + 1;
        return (XferStringForm.Hash, wanted <= runLimit ? wanted : SmallestMissing(hashes, skipTwo: false));
    }

    /// <summary>The text of a string literal for the content, with no escaping of any kind.</summary>
    public static string Write(ReadOnlySpan<char> content, int runLimit)
    {
        (XferStringForm form, int run) = Choose(content, runLimit);
        if (form == XferStringForm.Repetition)
        {
            string quotes = new(Quote, run);
            return string.Concat(quotes.AsSpan(), content, quotes.AsSpan());
        }

        string hashes = new(Hash, run);
        return new System.Text.StringBuilder(content.Length + (2 * run) + 2).Append(hashes).Append('"').Append(content).Append('"').Append(hashes).ToString();
    }

    /// <summary>The lengths of the maximal runs of <c>"</c> in the content.</summary>
    private static HashSet<int> QuoteRuns(ReadOnlySpan<char> content)
    {
        var runs = new HashSet<int>();
        int n = 0;
        foreach (char c in content)
        {
            if (c == Quote)
            {
                n++;
            }
            else if (n > 0)
            {
                runs.Add(n);
                n = 0;
            }
        }

        if (n > 0)
        {
            runs.Add(n);
        }

        return runs;
    }

    /// <summary>The lengths of the runs of <c>#</c> that immediately follow a <c>"</c> in the content.</summary>
    private static HashSet<int> HashRunsAfterQuote(ReadOnlySpan<char> content)
    {
        var runs = new HashSet<int>();
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == Quote)
            {
                int j = i + 1;
                while (j < content.Length && content[j] == Hash)
                {
                    j++;
                }

                if (j > i + 1)
                {
                    runs.Add(j - i - 1);
                }

                i = j - 1;
            }
        }

        return runs;
    }

    private static int Longest(HashSet<int> runs)
    {
        int longest = 0;
        foreach (int run in runs)
        {
            longest = Math.Max(longest, run);
        }

        return longest;
    }

    /// <summary>The smallest positive length that is not in the set, and is not 2 if <paramref name="skipTwo"/>.</summary>
    private static int SmallestMissing(HashSet<int> runs, bool skipTwo)
    {
        int n = 1;
        while (runs.Contains(n) || (skipTwo && n == 2))
        {
            n++;
        }

        return n;
    }
}
