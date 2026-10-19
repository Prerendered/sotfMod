// purpose: the one clamp both the runner's Planning and the overlay stepper call, so UI and runner cannot disagree (invariant 4)
// owns:    AmountClamp.Clamp, AmountClamp.RoundDownToChunk, AmountClamp.All, ClampResult, ClampReason
// deps:    none

using System;

namespace ShelfTransfer.Core.Transfer;

public enum ClampReason
{
    Ok,
    /// <summary>Requested amount was zero or negative, or less than one chunk.</summary>
    InvalidAmount,
    /// <summary>Source holds none of the item, or less than one chunk.</summary>
    NothingToMove,
    /// <summary>Target has no free capacity for the item (or not a whole chunk), or cannot hold it at all.</summary>
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

    /// <summary>As <see cref="Clamp(int, int, int)"/>, rounded down to whole chunks of <paramref name="unit"/> (I1/I4 amended:
    /// the game takes a chunk at once, so a target must fit a whole chunk before a take).</summary>
    public static ClampResult Clamp(int requested, int sourceCount, int targetFree, int unit)
    {
        RequireUnit(unit);
        ClampResult clamp = Clamp(requested, sourceCount, targetFree);
        if (clamp.Reason != ClampReason.Ok || unit == 1)
        {
            return clamp;
        }

        int whole = RoundDownToChunk(clamp.Amount, unit);
        if (whole > 0)
        {
            return new ClampResult(whole, ClampReason.Ok);
        }

        // The target side first: "target cannot fit a chunk" is the one the player can fix by choosing another shelf.
        ClampReason reason = targetFree < unit ? ClampReason.TargetFull
            : sourceCount < unit ? ClampReason.NothingToMove
            : ClampReason.InvalidAmount;
        return new ClampResult(0, reason);
    }

    public static int RoundDownToChunk(int amount, int unit)
    {
        RequireUnit(unit);
        return amount / unit * unit;
    }

    private static void RequireUnit(int unit)
    {
        if (unit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(unit), unit, "must be at least 1");
        }
    }
}
