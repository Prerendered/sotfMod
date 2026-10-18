// purpose: the aimed slot on a shelf; Row/Column are null until step 1 discovery shows the game exposes them
// owns:    SlotRef
// deps:    none

namespace ShelfTransfer.Core.Gateway;

public sealed record SlotRef(int? Row, int? Column);
