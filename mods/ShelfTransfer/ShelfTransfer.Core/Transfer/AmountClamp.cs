// purpose: the one clamp both the runner's Planning and the overlay stepper call, so UI and runner cannot disagree (invariant 4)
// owns:    AmountClamp.Clamp, AmountClamp.All, ClampResult, ClampReason
// deps:    none

using System;

namespace ShelfTransfer.Core.Transfer;

public enum ClampReason
{
    Ok,
    /// <summary>Requested amount was zero or negative.</summary>
    InvalidAmount,
    /// <summary>Source holds none of the item.</summary>
    NothingToMove,
    /// <summary>Target has no free capacity for the item, or cannot hold it at all.</summary>
    TargetFull,
}

public sealed record ClampResult(int Amount, ClampReason Reason);

public static class AmountClamp
{
    /// <summary>Request sentinel for "move everything that fits"; as the largest int it needs no special case in Min.</summary>
    public const int All = int.MaxValue;

    public static ClampResult Clamp(int requested, int sourceCount, int targetFree)
    {
        // Precedence when several apply: InvalidAmount, then NothingToMove, then TargetFull.
        if (requested <= 0)
        {
            return new ClampResult(0, ClampReason.InvalidAmount);
        }

        if (sourceCount <= 0)
        {
            return new ClampResult(0, ClampReason.NothingToMove);
        }

        if (targetFree <= 0)
        {
            return new ClampResult(0, ClampReason.TargetFull);
        }

        return new ClampResult(Math.Min(requested, Math.Min(sourceCount, targetFree)), ClampReason.Ok);
    }
}
