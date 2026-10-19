// purpose: the Send all queue's positions; per-type progress lives in the current TransferRunner's own state
// owns:    SendAllState
// deps:    none

namespace ShelfTransfer.Core.Transfer;

public enum SendAllState
{
    /// <summary>Before the first tick: the source's stock is not read yet.</summary>
    Listing,
    /// <summary>Stock listed; one type's run is in progress or the next type starts on the next tick.</summary>
    Running,
    Finished,
}
