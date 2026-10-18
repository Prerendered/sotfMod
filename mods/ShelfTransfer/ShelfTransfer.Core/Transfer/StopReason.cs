// purpose: why a transfer finished; every value except Completed is a report the player and the log see
// owns:    StopReason
// deps:    none

namespace ShelfTransfer.Core.Transfer;

public enum StopReason
{
    Completed,
    /// <summary>Esc after the in-flight item was stored (D7).</summary>
    Cancelled,
    /// <summary>Amount clamped to 0, target cannot hold the item, or player at carry cap (D3). Nothing moved.</summary>
    BlockedBeforeStart,
    /// <summary>Take refused by the game. Nothing moved this cycle.</summary>
    TakeRefused,
    /// <summary>Source count did not drop by exactly one after a take.</summary>
    SourceMismatch,
    /// <summary>Store refused (target filled mid-run). One item stranded in inventory.</summary>
    StoreRefused,
    /// <summary>Target count did not rise by exactly one after a store.</summary>
    TargetMismatch,
    /// <summary>Inventory count disagrees with the expected +1/-1.</summary>
    InventoryMismatch,
    /// <summary>Source or target object is null or destroyed.</summary>
    ShelfGone,
    /// <summary>Gateway returned Failed or threw. Counts are re-read for the report.</summary>
    GatewayError,
}
