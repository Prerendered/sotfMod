// purpose: the final accounting of a Send all queue: one result per listed type plus queue totals
// owns:    SendAllReport, SendAllTypeResult, SendAllTypeOutcome, SendAllStopReason
// deps:    ShelfStockEntry, TransferReport, StopReason
// caution: invariant 1 per type — Moved + Remaining + Stranded equals that type's starting source count; a type never
//          started reports its listed count as Remaining (not re-read)

using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Gateway;

namespace ShelfTransfer.Core.Transfer;

public enum SendAllStopReason
{
    /// <summary>Every listed type was reached and each ended Completed or was skipped.</summary>
    Completed,
    /// <summary>Esc: the current chunk was stored, no further type started (D7).</summary>
    Cancelled,
    /// <summary>A type's run ended on anything but Completed, BlockedBeforeStart or Cancelled, or left items in the inventory.</summary>
    SafetyStop,
}

public enum SendAllTypeOutcome
{
    NotStarted,
    /// <summary>The run ended Completed.</summary>
    Moved,
    /// <summary>Doesn't fit: the run ended BlockedBeforeStart, before any take.</summary>
    Skipped,
    Cancelled,
    /// <summary>The run ended on an unsafe reason with nothing in the inventory.</summary>
    Stopped,
    /// <summary>The run left items in the inventory, whatever its reason.</summary>
    Stranded,
}

/// <summary><paramref name="Run"/> is null until the type's run finished.</summary>
public sealed record SendAllTypeResult(ShelfStockEntry Entry, SendAllTypeOutcome Outcome, TransferReport? Run)
{
    public ItemId Item => Entry.Item;

    public int Moved => Run?.Moved ?? 0;

    public int Remaining => Run?.Remaining ?? Entry.Count;

    public int Stranded => Run?.StrandedInInventory ?? 0;
}

/// <summary><paramref name="Detail"/> is the first unsafe stop, null unless <see cref="SendAllStopReason.SafetyStop"/>.</summary>
public sealed record SendAllReport(SendAllStopReason Reason, IReadOnlyList<SendAllTypeResult> Types, string? Detail)
{
    /// <summary>Types whose run finished, skipped ones included; each has one queue.type log line.</summary>
    public int TypesDone => Types.Count(type => type.Outcome != SendAllTypeOutcome.NotStarted);

    public int Moved => Types.Sum(type => type.Moved);

    public int Stranded => Types.Sum(type => type.Stranded);
}
