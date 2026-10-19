// purpose: the runner's state machine positions; a cycle spans ticks only while awaiting a host confirm, Esc honoured only at ReadyForNext (D7)
// owns:    TransferState
// deps:    none

namespace ShelfTransfer.Core.Transfer;

public enum TransferState
{
    /// <summary>Clamp amount, read starting counts. Nothing has moved.</summary>
    Planning,
    /// <summary>Between chunks: cancel is checked here and only here.</summary>
    ReadyForNext,
    /// <summary>TakeOne issued; verify source-N and inventory+N, re-reading once per tick while the counts have not moved yet.
    /// Held until the first StoreOne, so a stop before it counts the whole chunk off the source.</summary>
    TakenAwaitingVerify,
    /// <summary>Between the store bursts of a chunk (N &gt; 1), the rest of it still in the inventory; never spans a tick.</summary>
    StoringQueue,
    /// <summary>A burst of B StoreOne calls issued (B = 1 per item); verify target+B and inventory-B before the next burst or cycle,
    /// re-reading once per tick while the counts have not moved yet.</summary>
    StoredAwaitingVerify,
    Finished,
}
