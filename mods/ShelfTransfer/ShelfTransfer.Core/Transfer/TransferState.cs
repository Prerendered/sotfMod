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
    /// <summary>TakeOne issued; verify source-N and inventory+N before storing, re-reading once per tick while the counts have not moved yet.
    /// Also the state between the single stores of a verified chunk, while its items are still in the inventory.</summary>
    TakenAwaitingVerify,
    /// <summary>StoreOne issued; verify target+1 and inventory-1 before the next store or cycle, re-reading once per tick while the counts have not moved yet.</summary>
    StoredAwaitingVerify,
    Finished,
}
