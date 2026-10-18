// purpose: what the player is looking at, resolved once per frame by the plugin's aim watcher
// owns:    AimTarget
// deps:    ShelfRef, SlotRef, ItemId

namespace ShelfTransfer.Core.Gateway;

public sealed record AimTarget(ShelfRef Shelf, SlotRef Slot, ItemId Item, int Count);
