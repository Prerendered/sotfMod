// purpose: on-disk shape of BepInEx/config/ShelfTransfer/<worldId>.json (invariant 5: labels never touch game state)
// owns:    LabelFile
// deps:    ShelfLabel
// caution: serialised with System.Text.Json; property names are the wire format — renaming one is a version bump

using System.Collections.Generic;

namespace ShelfTransfer.Core.Labels;

public sealed class LabelFile
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string WorldId { get; set; } = string.Empty;

    /// <summary>Shelf key (see ShelfKey.GetShelfKey) to label.</summary>
    public Dictionary<string, ShelfLabel> Shelves { get; set; } = new();
}
