// purpose: the aimed slot on a shelf, identified as the game does: the hook's StructureStorage.StorageSlotIndex
// owns:    SlotRef
// deps:    none
// caution: Index is the game's hook index, not child order (seen 8..11). null = unknown, the game picks the hook.

namespace ShelfTransfer.Core.Gateway;

public sealed record SlotRef(int? Index);
