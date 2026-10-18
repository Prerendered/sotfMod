// purpose: a player's name and optional group for one shelf; null Group means it lists under "Other"
// owns:    ShelfLabel
// deps:    none

namespace ShelfTransfer.Core.Labels;

public sealed record ShelfLabel(string Label, string? Group);
