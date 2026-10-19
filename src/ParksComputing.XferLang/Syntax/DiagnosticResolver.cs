using System;
using System.Collections.Generic;
using ParksComputing.XferLang.Text;

namespace ParksComputing.XferLang.Syntax;

/// <summary>
/// Turns pending diagnostics (byte offsets) into diagnostics with positions, in order of offset,
/// in one forward pass over the text (DESIGN.md section 5, step 5). Diagnostics at the same offset
/// keep the order they were reported in.
/// </summary>
internal static class DiagnosticResolver
{
    public static List<XferDiagnostic> Resolve(List<PendingDiagnostic> pending, LineMap lines)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(lines);

        var order = new int[pending.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        // A stable sort by offset: the index breaks ties.
        Array.Sort(order, (a, b) =>
        {
            int byOffset = pending[a].Offset.CompareTo(pending[b].Offset);
            return byOffset != 0 ? byOffset : a.CompareTo(b);
        });

        var offsets = new int[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            offsets[i] = pending[order[i]].Offset;
        }

        XferPosition[] positions = lines.Resolve(offsets);
        var resolved = new List<XferDiagnostic>(order.Length);
        for (int i = 0; i < order.Length; i++)
        {
            PendingDiagnostic d = pending[order[i]];
            resolved.Add(XferDiagnostics.Create(d.Code, positions[i], null, d.Detail));
        }

        return resolved;
    }
}
