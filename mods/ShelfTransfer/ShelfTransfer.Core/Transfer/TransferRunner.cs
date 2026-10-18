// purpose: the transfer state machine (flow F2): plan, then per cycle take one chunk -> verify -> (store one -> verify) per item
//          of the chunk; any fault stops, never retries
// owns:    TransferRunner
// deps:    IShelfGateway, IModLog, AmountClamp, TransferRequest, TransferReport, TransferState, StopReason
// caution: a cycle splits across ticks only while awaiting a host confirm, and then only re-reads (I1, D7); only TakeOne/StoreOne mutate (I2);
//          at most one chunk is ever in the inventory (I1 amended); Tick never catches; after a throw, Abort or the next Tick finishes (P2)

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
    private readonly int _confirmTicks;
    private bool _hasBaseline;
    private int _sourceStart;
    private int _targetStart;
    private int _inventoryStart;
    private bool _isCancelRequested;
    private bool _isTickInFlight;
    private bool _isTickOnStack;
    private bool _hasStoreVerdict;
    private bool _isAwaitingConfirm;
    private int _waitTicks;

    // Items one take moves (the game's chunk); read once at Planning.
    private int _unit = 1;

    // Items of the current chunk whose take is verified and whose store is not yet verified (the backpack queue).
    private int _queued;

    /// <summary><paramref name="confirmTicks"/>: ticks to keep re-reading after an Ok take or store whose counts have not moved yet
    /// (a friend-hosted client sees them only once the host applies it); 0 verifies immediately.</summary>
    public TransferRunner(IShelfGateway gateway, IModLog log, TransferRequest request, int confirmTicks = 0)
    {
        if (confirmTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(confirmTicks), confirmTicks, "must not be negative");
        }

        _gateway = gateway;
        _log = log;
        _request = request;
        _confirmTicks = confirmTicks;
    }

    public TransferState State { get; private set; } = TransferState.Planning;

    /// <summary>The clamped amount; 0 until Planning has run.</summary>
    public int Requested { get; private set; }

    public int Moved { get; private set; }

    /// <summary>Null until <see cref="State"/> is <see cref="TransferState.Finished"/>.</summary>
    public TransferReport? Report { get; private set; }

    private bool IsFinished => State == TransferState.Finished;

    // Valid between chunks and during a take wait, where Moved is a whole number of chunks.
    private int ChunkNumber => Moved / _unit + 1;

    /// <summary>Honoured at the next <see cref="TransferState.ReadyForNext"/>, never mid-cycle (D7).</summary>
    public void RequestCancel() => _isCancelRequested = true;

    /// <summary>Finishes from any state with a freshly read report; the driver calls it after a throw (P2). No-op once finished.</summary>
    public void Abort(StopReason reason, string? detail) => Finish(reason, detail);

    /// <summary>Runs Planning if pending, then works on at most <paramref name="maxCycles"/> cycles; a cycle resumed from a confirm wait is one of them.</summary>
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

        int cycle = 0;
        if (_isAwaitingConfirm)
        {
            // The resumed cycle spends one unit of the budget, so a tick still ends at most maxCycles cycles.
            cycle = 1;
            ResumeWait();
        }

        for (; State == TransferState.ReadyForNext; cycle++)
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

        int unit = _gateway.GetTransferUnit(_request.Source, _request.SourceSlot, _request.Item);
        if (IsFinished)
        {
            return;
        }

        bool isUnitKnown = unit >= 1;
        _unit = isUnitKnown ? unit : 1;

        bool canHold = _gateway.CanHold(_request.Target, _request.Item);
        ClampResult clamp = canHold && isUnitKnown
            ? AmountClamp.Clamp(_request.Amount, _sourceStart, _gateway.GetFreeCapacity(_request.Target, _request.Item), _unit)
            : new ClampResult(0, ClampReason.TargetFull);
        if (IsFinished)
        {
            return;
        }

        Requested = clamp.Amount;

        List<KeyValuePair<string, object?>> start = new()
        {
            new("source", _request.Source.Key),
            new("target", _request.Target.Key),
            new("item", _request.Item.Value),
            new("amount", Requested),
        };
        if (_unit > 1)
        {
            start.Add(new("unit", _unit));
        }

        start.Add(new("s0", _sourceStart));
        start.Add(new("t0", _targetStart));
        start.Add(new("i0", _inventoryStart));
        _log.Write(LogLevel.Info, "transfer.start", start);

        if (!canHold)
        {
            Finish(StopReason.BlockedBeforeStart, "target cannot hold item");
        }
        else if (!isUnitKnown)
        {
            // Taking blind could put a whole unknown chunk in the inventory and misreport it (I1).
            Finish(StopReason.BlockedBeforeStart, "transfer unit unknown");
        }
        else if (Requested == 0)
        {
            string chunkNote = _unit > 1 ? $", less than one chunk of {_unit}" : "";
            Finish(StopReason.BlockedBeforeStart, clamp.Reason + chunkNote);
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
        int chunkNumber = ChunkNumber;
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

        if (TryTake(chunkNumber) && TryStoreChunk())
        {
            CompleteCycle();
        }
    }

    // A wait spans frames, so the shelves are re-checked as at ReadyForNext; the wait only re-reads, never takes or stores again (I1).
    // Only a take wait sits in TakenAwaitingVerify: between the stores of a chunk no tick boundary passes without a store wait.
    private void ResumeWait()
    {
        string step = State == TransferState.TakenAwaitingVerify ? "take" : "store";
        _waitTicks++;
        bool areShelvesAlive = _gateway.IsAlive(_request.Source) && _gateway.IsAlive(_request.Target);
        if (IsFinished)
        {
            return;
        }

        if (!areShelvesAlive)
        {
            Finish(StopReason.ShelfGone, $"shelf gone during {step} wait");
            return;
        }

        bool isCycleDone = State == TransferState.TakenAwaitingVerify
            ? VerifyTake(ChunkNumber) && IsTargetUnchangedSinceCycleStart() && TryStoreChunk()
            : VerifyStore(Moved + 1) && TryStoreChunk();
        if (isCycleDone)
        {
            CompleteCycle();
        }
    }

    // The target was last read at cycle start, ticks ago; drift during the take wait must stop before StoreOne, or an
    // outside change would surface as a false store mismatch or mask an unapplied store (F2, I3).
    // IsAlive(dst) was checked at the top of this same tick.
    private bool IsTargetUnchangedSinceCycleStart()
    {
        int expectedTarget = _targetStart + Moved;
        int target = _gateway.GetCount(_request.Target, _request.Item);
        if (IsFinished)
        {
            return false;
        }

        if (target != expectedTarget)
        {
            Finish(StopReason.TargetMismatch, $"target changed during take wait: expected {expectedTarget} read {target}");
        }

        return !IsFinished;
    }

    // Moved already counts every verified store of the chunk.
    private void CompleteCycle() => State = TransferState.ReadyForNext;

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

    private bool TryTake(int chunkNumber)
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
        return VerifyTake(chunkNumber);
    }

    // Before-counts are the tracked totals, which IsCycleStartAsTracked proved equal to this cycle's start reads.
    // True once the whole chunk is verified (it is then queued); false while awaiting the host's confirm or after a stop.
    private bool VerifyTake(int chunkNumber)
    {
        int sourceBefore = _sourceStart - Moved;
        int inventoryBefore = _inventoryStart;
        int sourceExpected = sourceBefore - _unit;
        int inventoryExpected = inventoryBefore + _unit;
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

        bool isSourceVerified = sourceAfter == sourceExpected;
        bool isInventoryVerified = inventoryAfter == inventoryExpected;
        bool isVerified = isSourceVerified && isInventoryVerified;
        bool isAwaitable = IsAwaitable(sourceAfter, sourceBefore, sourceExpected)
            && IsAwaitable(inventoryAfter, inventoryBefore, inventoryExpected);
        if (TryAwaitConfirm(isVerified, isAwaitable))
        {
            return false;
        }

        string waitNote = GetNoConfirmNote(sourceAfter == sourceBefore && inventoryAfter == inventoryBefore);
        EndWait(chunkNumber, "take", isVerified);
        LogStep("transfer.take", chunkNumber, "src", sourceBefore, sourceAfter, inventoryBefore, inventoryAfter, isVerified,
            _unit > 1 ? _unit : null);
        if (!isSourceVerified)
        {
            Finish(StopReason.SourceMismatch, $"take: source expected {sourceExpected} read {sourceAfter}{waitNote}");
        }
        else if (!isInventoryVerified)
        {
            Finish(StopReason.InventoryMismatch, $"take: inventory expected {inventoryExpected} read {inventoryAfter}{waitNote}");
        }
        else
        {
            _queued = _unit;
        }

        return !IsFinished;
    }

    // Stores the queued items one at a time, each verified before the next (I3); cancel never lands mid-chunk (D7).
    // True once the queue is empty; false while awaiting a store confirm or after a stop.
    private bool TryStoreChunk()
    {
        while (_queued > 0)
        {
            if (!TryStore(Moved + 1))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryStore(int itemNumber)
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
        return VerifyStore(itemNumber);
    }

    // Same as VerifyTake. While awaiting, the verdict stays out so Finish settles the item from fresh reads.
    private bool VerifyStore(int itemNumber)
    {
        int targetBefore = _targetStart + Moved;
        int inventoryBefore = _inventoryStart + _queued;
        int inventoryExpected = inventoryBefore - 1;
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

        bool isTargetVerified = targetAfter == targetBefore + 1;
        bool isInventoryVerified = inventoryAfter == inventoryExpected;
        bool isVerified = isTargetVerified && isInventoryVerified;
        bool isAwaitable = IsAwaitable(targetAfter, targetBefore, targetBefore + 1)
            && IsAwaitable(inventoryAfter, inventoryBefore, inventoryExpected);
        if (TryAwaitConfirm(isVerified, isAwaitable))
        {
            return false;
        }

        _hasStoreVerdict = true;
        string waitNote = GetNoConfirmNote(targetAfter == targetBefore && inventoryAfter == inventoryBefore);
        EndWait(itemNumber, "store", isVerified);
        LogStep("transfer.store", itemNumber, "dst", targetBefore, targetAfter, inventoryBefore, inventoryAfter, isVerified);
        if (isVerified)
        {
            Moved++;
            _queued--;
            if (_queued > 0)
            {
                // The rest of the chunk is still in the inventory; Finish counts it stranded if the next store fails.
                State = TransferState.TakenAwaitingVerify;
            }

            return true;
        }

        LogStoreMismatch(itemNumber, targetBefore, targetAfter, inventoryBefore, inventoryAfter);
        if (!isTargetVerified)
        {
            Finish(StopReason.TargetMismatch, $"store: target expected {targetBefore + 1} read {targetAfter}{waitNote}");
        }
        else
        {
            Finish(StopReason.InventoryMismatch, $"store: inventory expected {inventoryExpected} read {inventoryAfter}{waitNote}");
        }

        return false;
    }

    // A count between its before value and the expected one is one the host has not (fully) applied yet; any other value
    // is drift and stops at once. For one item that is exactly {before, expected}. The wait decides only when the verdict
    // lands, never which reason it gives.
    private static bool IsAwaitable(int read, int before, int expected) =>
        read >= Math.Min(before, expected) && read <= Math.Max(before, expected);

    private bool TryAwaitConfirm(bool isVerified, bool isAwaitable)
    {
        if (isVerified || !isAwaitable || _waitTicks >= _confirmTicks)
        {
            return false;
        }

        _isAwaitingConfirm = true;
        return true;
    }

    // Nothing moved by the timeout; says so, so a host apply that lands after the stop is explainable from the log.
    private string GetNoConfirmNote(bool isUnmoved) =>
        _isAwaitingConfirm && isUnmoved ? $"; no host confirm after {_waitTicks} ticks" : "";

    // number: the chunk for a take wait, the item for a store wait (the same for one-item chunks).
    private void EndWait(int number, string step, bool isVerified)
    {
        if (!_isAwaitingConfirm)
        {
            return;
        }

        _log.Write(isVerified ? LogLevel.Info : LogLevel.Warn, "transfer.wait", new KeyValuePair<string, object?>[]
        {
            new("n", number),
            new("step", step),
            new("ticks", _waitTicks),
        });
        _isAwaitingConfirm = false;
        _waitTicks = 0;
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

        // A stop mid-wait (Abort after a throw or disconnect, a re-entrant stop, shelf gone) still records the wait.
        bool isTakeWait = stoppedIn == TransferState.TakenAwaitingVerify;
        EndWait(isTakeWait ? ChunkNumber : Moved + 1, isTakeWait ? "take" : "store", false);

        // An unverified take is assumed to have moved its whole chunk; after a verified take the queue says what is off
        // the source, and a store in flight is assumed to have left the inventory (the client consumes at once).
        bool isTakeUnverified = isTakeWait && _queued == 0;
        bool isStoreUnsettled = stoppedIn == TransferState.StoredAwaitingVerify && !_hasStoreVerdict;
        int offSource = isTakeUnverified ? _unit : _queued;
        int trackedRemaining = _sourceStart - Moved - offSource;
        int trackedStranded = stoppedIn == TransferState.StoredAwaitingVerify ? _queued - 1 : offSource;

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
        int stranded = inventoryNow is int inventory ? Math.Clamp(inventory - _inventoryStart, 0, _unit) : trackedStranded;

        // The game said Ok but the stop came before the verify; settle the item from the fresh reads, never on the game's word.
        string? storeNote = null;
        if (isStoreUnsettled)
        {
            int targetBefore = _targetStart + Moved;
            int inventoryBefore = _inventoryStart + _queued;
            if (targetNow == targetBefore + 1 && inventoryNow == inventoryBefore - 1)
            {
                Moved++;
            }
            else
            {
                LogStoreMismatch(Moved + 1, targetBefore, targetNow, inventoryBefore, inventoryNow);
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
        int number,
        string shelfPrefix,
        int shelfBefore,
        int shelfAfter,
        int inventoryBefore,
        int inventoryAfter,
        bool isVerified,
        int? unit = null)
    {
        List<KeyValuePair<string, object?>> line = new() { new("n", number) };
        if (unit is int chunk)
        {
            line.Add(new("unit", chunk));
        }

        line.Add(new(shelfPrefix + "_before", shelfBefore));
        line.Add(new(shelfPrefix + "_after", shelfAfter));
        line.Add(new("inv_before", inventoryBefore));
        line.Add(new("inv_after", inventoryAfter));
        line.Add(new("verify", isVerified ? "ok" : "fail"));
        _log.Write(isVerified ? LogLevel.Info : LogLevel.Warn, eventName, line);
    }
}
