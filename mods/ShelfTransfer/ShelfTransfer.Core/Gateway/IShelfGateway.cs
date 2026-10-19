// purpose: the only door between Core and the game; the plugin implements it over interop, tests implement it with a fake
// owns:    IShelfGateway
// deps:    ShelfRef, SlotRef, ItemId, GatewayOutcome, ShelfStockEntry
// caution: TakeOne/StoreOne must call the game's own take/store (invariant 2). Implementations never
//          write storage state directly and never throw — they return Failed. TakeOne moves a whole chunk or nothing.

using System.Collections.Generic;

namespace ShelfTransfer.Core.Gateway;

public interface IShelfGateway
{
    /// <summary>False once the game object is null or destroyed. Checked before every call.</summary>
    bool IsAlive(ShelfRef shelf);

    int GetCount(ShelfRef shelf, ItemId item);

    /// <summary>How many more of <paramref name="item"/> the shelf accepts right now. 0 when it cannot hold the type.</summary>
    int GetFreeCapacity(ShelfRef shelf, ItemId item);

    bool CanHold(ShelfRef shelf, ItemId item);

    int GetInventoryCount(ItemId item);

    /// <summary>Items one <see cref="TakeOne"/> moves from the slot: the game's chunk (1 for most items, 10 for ammo).
    /// 0 when no stocked slot holds the item.</summary>
    int GetTransferUnit(ShelfRef shelf, SlotRef slot, ItemId item);

    /// <summary>Items repeated <see cref="TakeOne"/> calls with this slot can move: each matching slot's count rounded down to
    /// whole chunks of its unit, summed (one slot when Index is set). Reads only.</summary>
    int GetMovableCount(ShelfRef shelf, SlotRef slot, ItemId item);

    /// <summary>Every item type with stock on the shelf (<see cref="ShelfStockEntry.BuildList"/>), read fresh. Empty, never
    /// null, when the shelf is empty or gone. Reads only.</summary>
    IReadOnlyList<ShelfStockEntry> ListStock(ShelfRef shelf);

    /// <summary>The vanilla take of one chunk (<see cref="GetTransferUnit"/> items) from the aimed slot (what E does).</summary>
    GatewayOutcome TakeOne(ShelfRef shelf, SlotRef slot, ItemId item);

    /// <summary>The vanilla store of one item onto the shelf (what C does).</summary>
    GatewayOutcome StoreOne(ShelfRef shelf, ItemId item);
}
