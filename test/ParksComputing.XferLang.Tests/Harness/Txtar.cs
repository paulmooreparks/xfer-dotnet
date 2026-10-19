using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ParksComputing.XferLang.Tests.Harness;

/// <summary>
/// Reads a txtar archive (suite/tests/README.md): a line of the form <c>-- name --</c> starts an
/// entry, which runs to the next such line or the end of the file and includes its final newline.
/// Text before the first entry is ignored.
/// </summary>
internal static partial class Txtar
{
    [GeneratedRegex(@"^-- (.+?) --$")]
    private static partial Regex Header();

    public static IReadOnlyList<KeyValuePair<string, string>> Read(string text)
    {
        var entries = new List<KeyValuePair<string, string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? name = null;
        var body = new System.Text.StringBuilder();

        void Finish()
        {
            if (name is null)
            {
                return;
            }

            if (!seen.Add(name))
            {
                throw new FormatException($"txtar: duplicate entry \"{name}\"");
            }

            entries.Add(new KeyValuePair<string, string>(name, body.ToString()));
        }

        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            bool last = i == lines.Length - 1;
            Match header = Header().Match(line);
            if (header.Success)
            {
                Finish();
                name = header.Groups[1].Value;
                body.Clear();
            }
            else if (name is not null)
            {
                // Split leaves one empty string after a final newline; it is not a line.
                if (!last)
                {
                    body.Append(line).Append('\n');
                }
                else if (line.Length > 0)
                {
                    body.Append(line);
                }
            }
        }

        Finish();
        return entries;
    }
}
