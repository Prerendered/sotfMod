// purpose: builds the overlay's target list: shelves grouped by label group, sorted by distance, with bearing and fit count
// owns:    ShelfListBuilder.Build, ShelfCandidate, ShelfGroup, ShelfList
// deps:    AmountClamp, Bearing, ShelfRow, ShelfLabel, ShelfRef, WorldPosition
// caution: pure — the plugin reads the gateway and label store and hands plain values in. StaleLabelKeys is a report only;
//          an empty or partial shelf list makes every label look stale, so the caller guards before LabelStore.Prune.

using System;
using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Labels;
using ShelfTransfer.Core.Transfer;

namespace ShelfTransfer.Core.Shelves;

/// <summary>A live shelf with the gateway's FreeCapacity and CanHold already read for the item being moved.</summary>
public sealed record ShelfCandidate(ShelfRef Shelf, int FreeCapacity, bool CanHold);

public sealed record ShelfGroup(string Name, bool Expanded, IReadOnlyList<ShelfRow> Rows);

/// <summary>StaleLabelKeys: stored label keys matching no live shelf, ordinal order, for the caller to prune.</summary>
public sealed record ShelfList(IReadOnlyList<ShelfGroup> Groups, IReadOnlyList<string> StaleLabelKeys)
{
    public bool IsEmpty => Groups.Count == 0;
}

public static class ShelfListBuilder
{
    public const string DefaultLabel = "Shelf";

    /// <summary>Shelves with no group list here; a player group literally named "Other" shares the bucket.</summary>
    public const string OtherGroupName = "Other";

    /// <summary>
    /// Every shelf except the source becomes a row. Groups are ordered by their nearest row, the nearest expanded;
    /// distance ties fall back to ordinal name/key order so the list is stable frame to frame.
    /// </summary>
    public static ShelfList Build(
        IReadOnlyList<ShelfCandidate> shelves,
        string sourceKey,
        int sourceCount,
        WorldPosition playerPosition,
        IReadOnlyDictionary<string, ShelfLabel> labels)
    {
        var liveKeys = new HashSet<string>(StringComparer.Ordinal) { sourceKey };
        var rowsByGroup = new Dictionary<string, List<ShelfRow>>(StringComparer.Ordinal);
        foreach (ShelfCandidate candidate in shelves)
        {
            liveKeys.Add(candidate.Shelf.Key);
            if (string.Equals(candidate.Shelf.Key, sourceKey, StringComparison.Ordinal))
            {
                continue;
            }

            labels.TryGetValue(candidate.Shelf.Key, out ShelfLabel? stored);
            string groupName = GetGroupName(stored?.Group);
            if (!rowsByGroup.TryGetValue(groupName, out List<ShelfRow>? rows))
            {
                rows = new List<ShelfRow>();
                rowsByGroup.Add(groupName, rows);
            }

            rows.Add(BuildRow(candidate, stored?.Label, sourceCount, playerPosition));
        }

        List<ShelfGroup> groups = rowsByGroup
            .Select(pair => new ShelfGroup(pair.Key, false, SortRows(pair.Value)))
            .OrderBy(group => group.Rows[0].Distance)
            .ThenBy(group => group.Name, StringComparer.Ordinal)
            .Select((group, index) => group with { Expanded = index == 0 })
            .ToList();

        List<string> staleLabelKeys = labels.Keys
            .Where(key => !liveKeys.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        return new ShelfList(groups, staleLabelKeys);
    }

    private static ShelfRow BuildRow(ShelfCandidate candidate, string? storedLabel, int sourceCount, WorldPosition playerPosition)
    {
        int fits = CalculateFits(candidate, sourceCount);
        string label = string.IsNullOrWhiteSpace(storedLabel) ? DefaultLabel : storedLabel.Trim();
        WorldPosition position = candidate.Shelf.Position;
        return new ShelfRow(
            candidate.Shelf,
            label,
            CalculateDistance(playerPosition, position),
            Bearing.Format(playerPosition, position),
            fits,
            fits > 0);
    }

    // Same clamp the runner's Planning uses (invariant 4), so the row never promises more than a transfer will move.
    private static int CalculateFits(ShelfCandidate candidate, int sourceCount)
    {
        if (!candidate.CanHold)
        {
            return 0;
        }

        ClampResult clamp = AmountClamp.Clamp(AmountClamp.All, sourceCount, candidate.FreeCapacity);
        return clamp.Reason == ClampReason.Ok ? clamp.Amount : 0;
    }

    private static float CalculateDistance(WorldPosition from, WorldPosition to)
    {
        double dx = (double)to.X - from.X;
        double dy = (double)to.Y - from.Y;
        double dz = (double)to.Z - from.Z;
        return (float)Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private static string GetGroupName(string? storedGroup) =>
        string.IsNullOrWhiteSpace(storedGroup) ? OtherGroupName : storedGroup.Trim();

    private static IReadOnlyList<ShelfRow> SortRows(List<ShelfRow> rows) =>
        rows
            .OrderBy(row => row.Distance)
            .ThenBy(row => row.Shelf.Key, StringComparer.Ordinal)
            .ToList();
}
