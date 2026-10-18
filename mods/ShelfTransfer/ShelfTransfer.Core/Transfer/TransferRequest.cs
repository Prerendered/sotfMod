// purpose: everything the runner needs to move one item type from one shelf to another
// owns:    TransferRequest
// deps:    ShelfRef, SlotRef, ItemId
// caution: Amount is the player's ask; the runner clamps it (invariant 4) before the first take

using ShelfTransfer.Core.Gateway;

namespace ShelfTransfer.Core.Transfer;

public sealed record TransferRequest(ShelfRef Source, SlotRef SourceSlot, ShelfRef Target, ItemId Item, int Amount);
