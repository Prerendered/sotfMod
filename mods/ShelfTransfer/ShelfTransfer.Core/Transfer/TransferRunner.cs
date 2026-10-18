// purpose: the transfer state machine (flow F2): plan, then take -> verify -> store -> verify one item per cycle; any fault stops, never retries
// owns:    TransferRunner
// deps:    IShelfGateway, IModLog, AmountClamp, TransferRequest, TransferReport, TransferState, StopReason
// caution: a cycle never splits across ticks (D7); only TakeOne/StoreOne mutate (I2); Tick never catches; after a throw, Abort or the next Tick finishes (P2)

using System;
using System.Collections.Generic;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Logging;

namespace ShelfTransfer.Core.Transfer;

public sealed class TransferRunner
{
    private readonly IShelfGateway _gateway;
    private readonly IModLog _log;
    private readonly TransferRequest _request;
    private bool _hasBaseline;
    private int _sourceStart;
    private int _targetStart;
    private int _inventoryStart;
    private bool _isCancelRequested;
    private bool _isTickInFlight;
    private bool _isTickOnStack;
    private bool _hasStoreVerdict;

    public TransferRunner(IShelfGateway gateway, IModLog log, TransferRequest request)
    {
        _gateway = gateway;
        _log = log;
        _request = request;
    }

    public TransferState State { get; private set; } = TransferState.Planning;

    /// <summary>The clamped amount; 0 until Planning has run.</summary>
    public int Requested { get; private set; }

    public int Moved { get; private set; }

    /// <summary>Null until <see cref="State"/> is <see cref="TransferState.Finished"/>.</summary>
    public TransferReport? Report { get; private set; }

    private bool IsFinished => State == TransferState.Finished;

    /// <summary>Honoured at the next <see cref="TransferState.ReadyForNext"/>, never mid-cycle (D7).</summary>
    public void RequestCancel() => _isCancelRequested = true;

    /// <summary>Finishes from any state with a freshly read report; the driver calls it after a throw (P2). No-op once finished.</summary>
    public void Abort(StopReason reason, string? detail) => Finish(reason, detail);

    /// <summary>Runs Planning if pending, then at most <paramref name="maxCycles"/> full cycles.</summary>
    public void Tick(int maxCycles)
    {
        if (maxCycles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCycles), maxCycles, "must be at least 1");
        }

        if (IsFinished)
        {
            return;
        }

        if (_isTickOnStack)
        {
            // Called from a gateway call inside the running tick; carrying on would interleave two cycles (D7).
            Finish(StopReason.GatewayError, $"re-entrant tick during {State}");
            return;
        }

        if (_isTickInFlight)
        {
            // The last tick threw and nobody aborted; resuming would act on an unverified cycle (invariant 3).
            Finish(StopReason.GatewayError, $"previous tick threw during {State}");
            return;
        }

        // _isTickInFlight is cleared only on a normal return, so a throw leaves it set for Abort or the next Tick;
        // _isTickOnStack also clears on unwind, so only a call from inside the running tick sees it set.
        _isTickInFlight = true;
        _isTickOnStack = true;
        try
        {
            RunTick(maxCycles);
        }
        finally
        {
            _isTickOnStack = false;
        }

        _isTickInFlight = false;
    }

    private void RunTick(int maxCycles)
    {
        if (State == TransferState.Planning)
        {
            Plan();
        }

        for (int cycle = 0; State == TransferState.ReadyForNext; cycle++)
        {
            // Exits are checked before the budget so the tick that moves the last item also finishes.
            FinishIfDue();
            if (State != TransferState.ReadyForNext || cycle == maxCycles)
            {
                return;
            }

            RunCycle();
        }
    }

    private void Plan()
    {
        if (!_gateway.IsAlive(_request.Source) || !_gateway.IsAlive(_request.Target))
        {
            Finish(StopReason.ShelfGone, "shelf gone before start");
            return;
        }

        int sourceStart = _gateway.GetCount(_request.Source, _request.Item);
        int targetStart = _gateway.GetCount(_request.Target, _request.Item);
        int inventoryStart = _gateway.GetInventoryCount(_request.Item);
        if (IsFinished)
        {
            return;
        }

        _sourceStart = sourceStart;
        _targetStart = targetStart;
        _inventoryStart = inventoryStart;
        _hasBaseline = true;

        bool canHold = _gateway.CanHold(_request.Target, _request.Item);
        ClampResult clamp = canHold
            ? AmountClamp.Clamp(_request.Amount, _sourceStart, _gateway.GetFreeCapacity(_request.Target, _request.Item))
            : new ClampResult(0, ClampReason.TargetFull);
        if (IsFinished)
        {
            return;
        }

        Requested = clamp.Amount;

        _log.Write(LogLevel.Info, "transfer.start", new KeyValuePair<string, object?>[]
        {
            new("source", _request.Source.Key),
            new("target", _request.Target.Key),
            new("item", _request.Item.Value),
            new("amount", Requested),
            new("s0", _sourceStart),
            new("t0", _targetStart),
            new("i0", _inventoryStart),
        });

        if (!canHold)
        {
            Finish(StopReason.BlockedBeforeStart, "target cannot hold item");
        }
        else if (Requested == 0)
        {
            Finish(StopReason.BlockedBeforeStart, clamp.Reason.ToString());
        }
        else
        {
            State = TransferState.ReadyForNext;
        }
    }

    private void FinishIfDue()
    {
        if (Moved == Requested)
        {
            Finish(StopReason.Completed, null);
        }
        else if (_isCancelRequested)
        {
            Finish(StopReason.Cancelled, null);
        }
        else if (!_gateway.IsAlive(_request.Source) || !_gateway.IsAlive(_request.Target))
        {
            Finish(StopReason.ShelfGone, "shelf gone mid-run");
        }
    }

    // IsFinished checks after each gateway step: Abort can arrive re-entrantly, and a finished runner must not act again.
    private void RunCycle()
    {
        int itemNumber = Moved + 1;
        int sourceBefore = _gateway.GetCount(_request.Source, _request.Item);
        if (IsFinished)
        {
            return;
        }

        int targetBefore = _gateway.GetCount(_request.Target, _request.Item);
        if (IsFinished)
        {
            return;
        }

        int inventoryBefore = _gateway.GetInventoryCount(_request.Item);
        if (IsFinished || !IsCycleStartAsTracked(sourceBefore, targetBefore, inventoryBefore))
        {
            return;
        }

        if (!TryTake(itemNumber, sourceBefore, inventoryBefore) || !TryStore(itemNumber, targetBefore, inventoryBefore))
        {
            return;
        }

        Moved = itemNumber;
        State = TransferState.ReadyForNext;
    }

    // Another player can touch either shelf between ticks; drift from the tracked totals stops the run instead of
    // carrying on from a baseline that no longer holds. Inventory too, since Stranded is measured against I0.
    private bool IsCycleStartAsTracked(int source, int target, int inventory)
    {
        int expectedSource = _sourceStart - Moved;
        int expectedTarget = _targetStart + Moved;
        if (source != expectedSource)
        {
            Finish(StopReason.SourceMismatch, $"source changed between items: expected {expectedSource} read {source}");
        }
        else if (target != expectedTarget)
        {
            Finish(StopReason.TargetMismatch, $"target changed between items: expected {expectedTarget} read {target}");
        }
        else if (inventory != _inventoryStart)
        {
            Finish(StopReason.InventoryMismatch, $"inventory changed between items: expected {_inventoryStart} read {inventory}");
        }

        return !IsFinished;
    }

    private bool TryTake(int itemNumber, int sourceBefore, int inventoryBefore)
    {
        GatewayOutcome take = _gateway.TakeOne(_request.Source, _request.SourceSlot, _request.Item);
        if (IsFinished)
        {
            return false;
        }

        if (!take.IsOk)
        {
            Finish(GetFaultReason(take.Status, StopReason.TakeRefused), $"take {take.Status}: {take.Detail}");
            return false;
        }

        State = TransferState.TakenAwaitingVerify;
        int sourceAfter = _gateway.GetCount(_request.Source, _request.Item);
        if (IsFinished)
        {
            return false;
        }

        int inventoryAfter = _gateway.GetInventoryCount(_request.Item);
        if (IsFinished)
        {
            return false;
        }

        bool isSourceVerified = sourceAfter == sourceBefore - 1;
        bool isInventoryVerified = inventoryAfter == inventoryBefore + 1;
        LogStep("transfer.take", itemNumber, "src", sourceBefore, sourceAfter, inventoryBefore, inventoryAfter, isSourceVerified && isInventoryVerified);
        if (!isSourceVerified)
        {
            Finish(StopReason.SourceMismatch, $"take: source expected {sourceBefore - 1} read {sourceAfter}");
        }
        else if (!isInventoryVerified)
        {
            Finish(StopReason.InventoryMismatch, $"take: inventory expected {inventoryBefore + 1} read {inventoryAfter}");
        }

        return !IsFinished;
    }

    private bool TryStore(int itemNumber, int targetBefore, int inventoryBefore)
    {
        GatewayOutcome store = _gateway.StoreOne(_request.Target, _request.Item);
        if (IsFinished)
        {
            return false;
        }

        if (!store.IsOk)
        {
            Finish(GetFaultReason(store.Status, StopReason.StoreRefused), $"store {store.Status}: {store.Detail}");
            return false;
        }

        State = TransferState.StoredAwaitingVerify;
        _hasStoreVerdict = false;
        int targetAfter = _gateway.GetCount(_request.Target, _request.Item);
        if (IsFinished)
        {
            return false;
        }

        int inventoryAfter = _gateway.GetInventoryCount(_request.Item);
        if (IsFinished)
        {
            return false;
        }

        _hasStoreVerdict = true;
        bool isTargetVerified = targetAfter == targetBefore + 1;
        bool isInventoryVerified = inventoryAfter == inventoryBefore;
        LogStep("transfer.store", itemNumber, "dst", targetBefore, targetAfter, inventoryBefore + 1, inventoryAfter, isTargetVerified && isInventoryVerified);
        if (isTargetVerified && isInventoryVerified)
        {
            return true;
        }

        LogStoreMismatch(itemNumber, targetBefore, targetAfter, inventoryBefore + 1, inventoryAfter);
        if (!isTargetVerified)
        {
            Finish(StopReason.TargetMismatch, $"store: target expected {targetBefore + 1} read {targetAfter}");
        }
        else
        {
            Finish(StopReason.InventoryMismatch, $"store: inventory expected {inventoryBefore} read {inventoryAfter}");
        }

        return false;
    }

    private static StopReason GetFaultReason(GatewayStatus status, StopReason refusedReason) =>
        status == GatewayStatus.Refused ? refusedReason : StopReason.GatewayError;

    private void Finish(StopReason reason, string? detail)
    {
        if (IsFinished)
        {
            return;
        }

        // Marked first so a re-entrant Abort during the re-reads below is a no-op; the first stop wins.
        TransferState stoppedIn = State;
        State = TransferState.Finished;

        bool isItemOffSource = stoppedIn is TransferState.TakenAwaitingVerify or TransferState.StoredAwaitingVerify;
        bool isStoreUnsettled = stoppedIn == TransferState.StoredAwaitingVerify && !_hasStoreVerdict;
        int trackedRemaining = _sourceStart - Moved - (isItemOffSource ? 1 : 0);
        int trackedStranded = stoppedIn == TransferState.TakenAwaitingVerify ? 1 : 0;

        // Re-read rather than derive (invariant 1); tracked counts only when the gateway cannot answer, and the detail says so.
        string? countsNote = null;
        int? targetNow = isStoreUnsettled
            ? ReadOrTrack<int?>(() => _gateway.GetCount(_request.Target, _request.Item), null, ref countsNote)
            : null;
        bool isSourceReadable = ReadOrTrack(() => _gateway.IsAlive(_request.Source), false, ref countsNote);
        int? sourceNow = isSourceReadable
            ? ReadOrTrack<int?>(() => _gateway.GetCount(_request.Source, _request.Item), null, ref countsNote)
            : null;
        int remaining = sourceNow ?? trackedRemaining;

        // Before a baseline the tracked fallback is a 0 nobody counted; Remaining is an int, so the detail says so.
        string? sourceNote = null;
        if (sourceNow is null && !_hasBaseline)
        {
            sourceNote = "source never read, remaining unknown";
        }
        else if (!isSourceReadable)
        {
            countsNote ??= "source gone, remaining tracked";
        }

        int? inventoryNow = _hasBaseline
            ? ReadOrTrack<int?>(() => _gateway.GetInventoryCount(_request.Item), null, ref countsNote)
            : null;
        int stranded = inventoryNow is int inventory ? Math.Clamp(inventory - _inventoryStart, 0, 1) : trackedStranded;

        // The game said Ok but the stop came before the verify; settle the item from the fresh reads, never on the game's word.
        string? storeNote = null;
        if (isStoreUnsettled)
        {
            int targetBefore = _targetStart + Moved;
            if (targetNow == targetBefore + 1 && inventoryNow == _inventoryStart)
            {
                Moved++;
            }
            else
            {
                LogStoreMismatch(Moved + 1, targetBefore, targetNow, _inventoryStart + 1, inventoryNow);
                storeNote = "store unverified";
            }
        }

        string? fullDetail = JoinNotes(detail, storeNote, countsNote, sourceNote);
        Report = new TransferReport(reason, Requested, Moved, remaining, stranded, fullDetail);

        LogLevel level = reason is StopReason.Completed or StopReason.Cancelled or StopReason.BlockedBeforeStart
            ? LogLevel.Info
            : LogLevel.Warn;
        _log.Write(level, "transfer.stop", new KeyValuePair<string, object?>[]
        {
            new("reason", reason),
            new("s0", _sourceStart),
            new("moved", Moved),
            new("remaining", remaining),
            new("stranded", stranded),
            new("detail", fullDetail),
        });

        if (_hasBaseline && _sourceStart != remaining + Moved + stranded)
        {
            _log.Write(LogLevel.Warn, "conservation.mismatch", new KeyValuePair<string, object?>[]
            {
                new("check", "report"),
                new("reason", reason),
                new("s0", _sourceStart),
                new("moved", Moved),
                new("remaining", remaining),
                new("stranded", stranded),
            });
        }
    }

    // The report's S0 check sees only source + inventory, so a store the game called Ok that the counts contradict
    // could hide a target-side loss or duplicate; flag it separately.
    private void LogStoreMismatch(int itemNumber, int targetBefore, int? targetAfter, int inventoryBefore, int? inventoryAfter)
    {
        _log.Write(LogLevel.Warn, "conservation.mismatch", new KeyValuePair<string, object?>[]
        {
            new("check", "store"),
            new("n", itemNumber),
            new("dst_before", targetBefore),
            new("dst_after", targetAfter),
            new("inv_before", inventoryBefore),
            new("inv_after", inventoryAfter),
        });
    }

    private static string? JoinNotes(params string?[] notes)
    {
        string joined = string.Join("; ", Array.FindAll(notes, note => note is not null));
        return joined.Length == 0 ? null : joined;
    }

    // The one catch in Core: a gateway that just threw may throw again here, and the player must still get a report.
    private T ReadOrTrack<T>(Func<T> read, T tracked, ref string? countsNote)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            _log.WriteException("transfer.stop", ex);
            countsNote = $"counts tracked, re-read threw {ex.GetType().Name}";
            return tracked;
        }
    }

    private void LogStep(
        string eventName,
        int itemNumber,
        string shelfPrefix,
        int shelfBefore,
        int shelfAfter,
        int inventoryBefore,
        int inventoryAfter,
        bool isVerified)
    {
        _log.Write(isVerified ? LogLevel.Info : LogLevel.Warn, eventName, new KeyValuePair<string, object?>[]
        {
            new("n", itemNumber),
            new(shelfPrefix + "_before", shelfBefore),
            new(shelfPrefix + "_after", shelfAfter),
            new("inv_before", inventoryBefore),
            new("inv_after", inventoryAfter),
            new("verify", isVerified ? "ok" : "fail"),
        });
    }
}
