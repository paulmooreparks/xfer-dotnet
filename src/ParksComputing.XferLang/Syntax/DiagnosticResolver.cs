using System;
using System.Collections.Generic;
using ParksComputing.XferLang.Text;

namespace ParksComputing.XferLang.Syntax;

/// <summary>
/// Turns pending diagnostics (byte offsets) into diagnostics with positions, in order of offset,
/// with positions resolved in sorted passes over the text (DESIGN.md section 5, step 5).
/// Diagnostics at the same offset keep the order they were reported in.
/// </summary>
internal static class DiagnosticResolver
{
    public static List<XferDiagnostic> Resolve(List<PendingDiagnostic> pending, LineMap lines)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(lines);

        int count = pending.Count;
        var order = new int[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
        }

        // A stable sort by offset: the index breaks ties.
        Array.Sort(order, (a, b) =>
        {
            int byOffset = pending[a].Offset.CompareTo(pending[b].Offset);
            return byOffset != 0 ? byOffset : a.CompareTo(b);
        });

        var starts = new int[count];
        for (int i = 0; i < count; i++)
        {
            starts[i] = pending[order[i]].Offset;
        }

        XferPosition[] startPositions = lines.Resolve(starts);

        // Ends are not in order, so they get a sorted pass of their own. A point diagnostic
        // (End -1) ends where it starts.
        var endIndex = new List<int>();
        for (int i = 0; i < count; i++)
        {
            if (pending[order[i]].End >= 0)
            {
                endIndex.Add(i);
            }
        }

        endIndex.Sort((a, b) => pending[order[a]].End.CompareTo(pending[order[b]].End));
        var ends = new int[endIndex.Count];
        for (int i = 0; i < ends.Length; i++)
        {
            ends[i] = pending[order[endIndex[i]]].End;
        }

        XferPosition[] endPositions = lines.Resolve(ends);
        var endOf = new XferPosition?[count];
        for (int i = 0; i < ends.Length; i++)
        {
            endOf[endIndex[i]] = endPositions[i];
        }

        var resolved = new List<XferDiagnostic>(count);
        for (int i = 0; i < count; i++)
        {
            PendingDiagnostic d = pending[order[i]];
            resolved.Add(XferDiagnostics.Create(d.Code, startPositions[i], endOf[i], d.Detail));
        }

        return resolved;
    }
}
