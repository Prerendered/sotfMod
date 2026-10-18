// purpose: the player-facing result line for a finished transfer; the only source of stop-reason wording
// owns:    StopReasonText.Format, StopReasonText.GetReasonText, ResultLine
// deps:    StopReason, TransferReport
// caution: D4 - a stranded item is "in your inventory", never "lost"; numbers are the report's, never re-derived

using System.Globalization;

using ShelfTransfer.Core.Transfer;

namespace ShelfTransfer.Core.Ui;

/// <summary>AutoClose is true only for Completed; the renderer owns the delay.</summary>
public sealed record ResultLine(string Text, bool AutoClose);

public static class StopReasonText
{
    public const string StoppedPrefix = "Stopped: ";
    public const string StrandedOne = "1 item is in your inventory, the rest is on the source";
    public const string StrandedNone = "no item left in your inventory";

    public static ResultLine Format(TransferReport report)
    {
        string counts = string.Format(CultureInfo.InvariantCulture, "Moved {0}, remaining {1}", report.Moved, report.Remaining);
        if (report.Reason == StopReason.Completed)
        {
            return new ResultLine(counts, true);
        }

        string text = StoppedPrefix + GetReasonText(report.Reason) + ". " + counts + FormatStranded(report.StrandedInInventory) + ".";
        return new ResultLine(text, false);
    }

    // No discard arm: a StopReason added without wording fails the build (CS8509 under TreatWarningsAsErrors).
    // CS8524 only concerns unnamed casts like (StopReason)99, which throw SwitchExpressionException.
#pragma warning disable CS8524
    public static string GetReasonText(StopReason reason) => reason switch
    {
        StopReason.Completed => "completed",
        StopReason.Cancelled => "cancelled",
        StopReason.BlockedBeforeStart => "nothing could start, target full or nothing to move",
        StopReason.TakeRefused => "the game refused to take the item",
        StopReason.SourceMismatch => "source shelf changed during the transfer",
        StopReason.StoreRefused => "target full",
        StopReason.TargetMismatch => "target shelf changed during the transfer",
        StopReason.InventoryMismatch => "your inventory changed during the transfer",
        StopReason.ShelfGone => "a shelf is gone",
        StopReason.GatewayError => "something went wrong, see log",
    };
#pragma warning restore CS8524

    private static string FormatStranded(int stranded) => stranded switch
    {
        <= 0 => ", " + StrandedNone,
        1 => ". " + StrandedOne,
        // The runner caps this at 1; worded anyway so a broken report still reads as recoverable, never lost.
        _ => string.Format(CultureInfo.InvariantCulture, ". {0} items are in your inventory, the rest is on the source", stranded),
    };
}
