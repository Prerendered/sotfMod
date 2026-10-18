// purpose: one overlay row: a candidate target shelf with its display label, distance, bearing and fit count
// owns:    ShelfRow
// deps:    ShelfRef
// caution: Fits is AmountClamp's figure for "move all"; Selectable is false whenever Fits is 0 (invariant 4)

using ShelfTransfer.Core.Gateway;

namespace ShelfTransfer.Core.Shelves;

/// <summary>Distance is straight-line metres from the player; Bearing is a compass point, or empty when undefined.</summary>
public sealed record ShelfRow(ShelfRef Shelf, string Label, float Distance, string Bearing, int Fits, bool Selectable);
