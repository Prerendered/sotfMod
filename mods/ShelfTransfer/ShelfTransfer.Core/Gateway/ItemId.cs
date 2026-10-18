// purpose: the game's numeric item identifier, typed so it cannot be confused with a count
// owns:    ItemId
// deps:    none

namespace ShelfTransfer.Core.Gateway;

public readonly record struct ItemId(int Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
