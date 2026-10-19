// purpose: on-disk shape of <worldId>.pruned.json beside the label file: every label an overlay-open prune removed (I5)
// owns:    PrunedLabelArchive, PrunedLabelEntry
// deps:    none
// caution: append-only from this mod; a key may repeat (a restored label pruned again). Property names are the wire
//          format, as in LabelFile — renaming one is a version bump

using System.Collections.Generic;

namespace ShelfTransfer.Core.Labels;

public sealed class PrunedLabelArchive
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string WorldId { get; set; } = string.Empty;

    /// <summary>Oldest first.</summary>
    public List<PrunedLabelEntry> Pruned { get; set; } = new();
}

/// <summary><paramref name="PrunedAt"/> is UTC, ISO 8601 with a Z suffix.</summary>
public sealed record PrunedLabelEntry(string Key, string Label, string? Group, string PrunedAt);
