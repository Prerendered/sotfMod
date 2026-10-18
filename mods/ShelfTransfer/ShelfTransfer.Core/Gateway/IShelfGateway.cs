// purpose: the only door between Core and the game; the plugin implements it over interop, tests implement it with a fake
// owns:    IShelfGateway
// deps:    ShelfRef, SlotRef, ItemId, GatewayOutcome
// caution: TakeOne/StoreOne must call the game's own take/store (invariant 2). Implementations never
//          write storage state directly and never throw — they return Failed.

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

    /// <summary>The vanilla take of one item from the aimed slot (what E does).</summary>
    GatewayOutcome TakeOne(ShelfRef shelf, SlotRef slot, ItemId item);

    /// <summary>The vanilla store of one item onto the shelf (what C does).</summary>
    GatewayOutcome StoreOne(ShelfRef shelf, ItemId item);
}
