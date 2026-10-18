// purpose: the final accounting of a transfer; StrandedInInventory is the D4 disclosure (0 or 1, never more)
// owns:    TransferReport
// deps:    StopReason
// caution: invariant 1 in numbers — Moved + Remaining + StrandedInInventory must equal the starting source count

namespace ShelfTransfer.Core.Transfer;

public sealed record TransferReport(
    StopReason Reason,
    int Requested,
    int Moved,
    int Remaining,
    int StrandedInInventory,
    string? Detail);
