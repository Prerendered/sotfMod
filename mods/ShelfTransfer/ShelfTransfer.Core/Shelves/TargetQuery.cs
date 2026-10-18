// purpose: reads the target part of `<count> <target>`: an index into the listed rows, or a shelf label to look up
// owns:    TargetQuery (SplitFirstWord, IsIndex, FindByLabel)
// deps:    ShelfRow, ShelfLabel
// caution: pure; labels are only read (I5). Rows are the caller's list without the source shelf, so the source is never a match

using System;
using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Labels;

namespace ShelfTransfer.Core.Shelves;

public static class TargetQuery
{
    /// <summary>
    /// The first whitespace-separated word and everything after it, both trimmed; a label may contain spaces, so Remainder
    /// keeps its inner whitespace as typed. Empty strings when there is no such part.
    /// </summary>
    public static (string First, string Remainder) SplitFirstWord(string text)
    {
        string trimmed = text.Trim();
        int end = 0;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]))
        {
            end++;
        }

        return (trimmed[..end], trimmed[end..].Trim());
    }

    /// <summary>
    /// True when the target is only ASCII digits: it is then an index even if a shelf carries that label, and an
    /// out-of-range number such as 0 stays an index error rather than a label miss.
    /// </summary>
    public static bool IsIndex(string target) => target.Length > 0 && target.All(c => c is >= '0' and <= '9');

    /// <summary>1-based positions in <paramref name="rows"/> whose stored label equals the whole query, ignoring case.</summary>
    public static IReadOnlyList<int> FindByLabel(
        IReadOnlyList<ShelfRow> rows,
        IReadOnlyDictionary<string, ShelfLabel> labels,
        string label)
    {
        string query = label.Trim();
        List<int> indexes = new();
        if (query.Length == 0)
        {
            return indexes;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            if (labels.TryGetValue(rows[i].Shelf.Key, out ShelfLabel? stored)
                && string.Equals(stored.Label.Trim(), query, StringComparison.OrdinalIgnoreCase))
            {
                indexes.Add(i + 1);
            }
        }

        return indexes;
    }
}
