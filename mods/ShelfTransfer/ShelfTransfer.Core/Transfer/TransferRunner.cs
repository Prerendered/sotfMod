// purpose: the transfer state machine (flow F2): plan, then per cycle take one chunk -> verify -> (store a burst of single
//          items -> verify) until the chunk is stored; any fault stops, never retries
// owns:    TransferRunner
// deps:    IShelfGateway, IModLog, AmountClamp, TransferRequest, TransferReport, TransferState, StopReason
// caution: a cycle splits across ticks only while awaiting a host confirm, and then only re-reads (I1, D7); only TakeOne/StoreOne mutate (I2);
//          at most one chunk is ever in the inventory (I1 amended); Tick never catches; after a throw, Abort or the next Tick finishes (P2);
//          burst size 1 is the per-item path (I3); above 1 each burst is verified as a whole (I3 amended, card 91)

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

    // Most single stores sent before one verify; 1 = verify every item.
    private readonly int _burstSize;
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

    // Ok StoreOne calls of the current burst; set from its first store until the next burst starts.
    private int _inFlight;

    // A burst cut short by a non-Ok store; the stores before it are verified first, then the run stops with this.
    private (StopReason Reason, string Detail)? _pendingStoreFault;

    /// <summary><paramref name="confirmTicks"/>: ticks to keep re-reading after an Ok take or store whose counts have not moved yet
    /// (a friend-hosted client sees them only once the host applies it); 0 verifies immediately.
    /// <paramref name="burstSize"/>: most single stores sent back to back before one verify (card 91); 1 verifies each item.</summary>
    public TransferRunner(IShelfGateway gateway, IModLog log, TransferRequest request, int confirmTicks = 0, int burstSize = 1)
    {
        if (confirmTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(confirmTicks), confirmTicks, "must not be negative");
        }

        if (burstSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(burstSize), burstSize, "must be at least 1");
        }

        _gateway = gateway;
        _log = log;
        _request = request;
        _confirmTicks = confirmTicks;
        _burstSize = burstSize;
    }

    public TransferState State { get; private set; } = TransferState.Planning;

    /// <summary>The clamped amount; 0 until Planning has run.</summary>
    public int Requested { get; private set; }

    public int Moved { get; private set; }

    /// <summary>Null until <see cref="State"/> is <see cref="TransferState.Finished"/>.</summary>
    public TransferReport? Report { get; private set; }

    private bool IsFinished => State == TransferState.Finished;

    private bool IsBursting => _burstSize > 1;

    // Valid between chunks and during a take wait, where Moved is a whole number of chunks.
    private int ChunkNumber => Moved / _unit + 1;

    // The item number of a store step; a burst of several shows its range ("11..20").
    private object StoreNumber => IsBursting && _inFlight > 1 ? $"{Moved + 1}..{Moved + _inFlight}" : Moved + 1;

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
        ClampResult clamp = canHold
            ? AmountClamp.Clamp(_request.Amount, _sourceStart, _gateway.GetFreeCapacity(_request.Target, _request.Item), _unit)
            : new ClampResult(0, ClampReason.TargetFull);
        if (IsFinished)
        {
            return;
        }

        // A take moves a whole chunk from one slot, so short slots (15 + 15 at 10) hold less than the rounded total (card 85).
        // Unit 1 always moves the whole count, so it skips the read.
        bool isBlockedOnSplit = false;
        if (_unit > 1 && clamp.Reason == ClampReason.Ok)
        {
            int movable = _gateway.GetMovableCount(_request.Source, _request.SourceSlot, _request.Item);
            if (IsFinished)
            {
                return;
            }

            int capped = AmountClamp.RoundDownToChunk(Math.Min(clamp.Amount, Math.Max(0, movable)), _unit);
            isBlockedOnSplit = capped == 0;
            clamp = clamp with { Amount = capped };
        }

        // The gateway answers 0 for an emptied slot too, so a count reason (NothingToMove) wins over "unit unknown".
        bool isBlockedOnUnit = !isUnitKnown && clamp.Reason == ClampReason.Ok;
        Requested = isBlockedOnUnit ? 0 : clamp.Amount;

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
        else if (isBlockedOnUnit)
        {
            // Taking blind could put a whole unknown chunk in the inventory and misreport it (I1).
            Finish(StopReason.BlockedBeforeStart, "transfer unit unknown");
        }
        else if (isBlockedOnSplit)
        {
            // Not "NothingToMove": a whole chunk's worth is there, only split, so Send all skips it as DoesntFit (card 85).
            Finish(StopReason.BlockedBeforeStart, $"no slot holds a whole chunk of {_unit}");
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
    // StoringQueue never waits: between the stores of a chunk no tick boundary passes without a store wait.
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
            : VerifyStore() && TryStoreChunk();
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

    // Stores the queued items in bursts of single stores, each burst verified before the next (I3 amended); cancel never
    // lands mid-chunk (D7). True once the queue is empty; false while awaiting a store confirm or after a stop.
    private bool TryStoreChunk()
    {
        while (true)
        {
            if (_pendingStoreFault is (StopReason reason, string detail))
            {
                Finish(reason, detail);
                return false;
            }

            if (_queued == 0)
            {
                return true;
            }

            int burst = GetBurstSize();
            if (IsFinished || !TryStoreBurst(burst))
            {
                return false;
            }
        }
    }

    // On a client every store of a burst lands on the hook the first one picks (local counts stay stale until the host
    // confirms), so the burst must fit that hook (I4). An empty hook gets its group from the first store: it goes alone.
    private int GetBurstSize()
    {
        if (!IsBursting)
        {
            return 1;
        }

        int hookFree = _gateway.GetReceivingHookFree(_request.Target, _request.Item);
        return hookFree < 1 ? 1 : Math.Min(Math.Min(_burstSize, _queued), hookFree);
    }

    // burst = 1 is the per-item store. A non-Ok store ends the burst; the Ok ones before it still get their verify.
    private bool TryStoreBurst(int burst)
    {
        _inFlight = 0;
        for (int i = 0; i < burst; i++)
        {
            GatewayOutcome store = _gateway.StoreOne(_request.Target, _request.Item);
            if (IsFinished)
            {
                return false;
            }

            if (!store.IsOk)
            {
                StopReason reason = GetFaultReason(store.Status, StopReason.StoreRefused);
                string detail = $"store {store.Status}: {store.Detail}";
                if (_inFlight == 0)
                {
                    Finish(reason, detail);
                    return false;
                }

                _pendingStoreFault = (reason, detail);
                break;
            }

            _inFlight++;
            State = TransferState.StoredAwaitingVerify;
            _hasStoreVerdict = false;
        }

        return VerifyStore();
    }

    // Same as VerifyTake, for the whole burst: target +n, inventory -n. While awaiting, the verdict stays out so Finish
    // settles the burst from fresh reads.
    private bool VerifyStore()
    {
        object storeNumber = StoreNumber;
        int targetBefore = _targetStart + Moved;
        int targetExpected = targetBefore + _inFlight;
        int inventoryBefore = _inventoryStart + _queued;
        int inventoryExpected = inventoryBefore - _inFlight;
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

        bool isTargetVerified = targetAfter == targetExpected;
        bool isInventoryVerified = inventoryAfter == inventoryExpected;
        bool isVerified = isTargetVerified && isInventoryVerified;
        bool isAwaitable = IsAwaitable(targetAfter, targetBefore, targetExpected)
            && IsAwaitable(inventoryAfter, inventoryBefore, inventoryExpected);
        if (TryAwaitConfirm(isVerified, isAwaitable))
        {
            return false;
        }

        _hasStoreVerdict = true;
        string waitNote = GetNoConfirmNote(targetAfter == targetBefore && inventoryAfter == inventoryBefore);
        EndWait(storeNumber, "store", isVerified);
        LogStep("transfer.store", storeNumber, "dst", targetBefore, targetAfter, inventoryBefore, inventoryAfter, isVerified,
            burst: IsBursting ? _inFlight : null);
        if (isVerified)
        {
            Moved += _inFlight;
            _queued -= _inFlight;
            if (_queued > 0)
            {
                // The rest of the chunk is still in the inventory; Finish counts it stranded if the next store fails.
                State = TransferState.StoringQueue;
            }

            return true;
        }

        LogStoreMismatch(storeNumber, targetBefore, targetAfter, inventoryBefore, inventoryAfter, _inFlight);
        int sent = _inFlight;
        int confirmed = GetConfirmedStores(targetBefore, targetAfter, inventoryBefore, inventoryAfter);
        CreditConfirmedStores(confirmed);
        string burstNote = sent > 1 ? $"; {confirmed} of {sent} confirmed" : "";
        if (_pendingStoreFault is (_, string cutBy))
        {
            burstNote += $"; burst cut by {cutBy}";
        }

        if (!isTargetVerified)
        {
            Finish(StopReason.TargetMismatch, $"store: target expected {targetExpected} read {targetAfter}{waitNote}{burstNote}");
        }
        else
        {
            Finish(StopReason.InventoryMismatch,
                $"store: inventory expected {inventoryExpected} read {inventoryAfter}{waitNote}{burstNote}");
        }

        return false;
    }

    // Stores of the burst the counts prove landed: the inventory gave up the whole burst (the client consumes at once) and
    // the target rose by no more than it. Anything else is drift, and nothing is credited. For one store that is all or none.
    private int GetConfirmedStores(int targetBefore, int? targetAfter, int inventoryBefore, int? inventoryAfter)
    {
        int? landed = targetAfter - targetBefore;
        return inventoryAfter == inventoryBefore - _inFlight && landed >= 0 && landed <= _inFlight ? landed.Value : 0;
    }

    // Keeps the tracked fallback in Finish consistent with the credit: credited stores leave both the queue and the burst.
    private void CreditConfirmedStores(int confirmed)
    {
        Moved += confirmed;
        _queued -= confirmed;
        _inFlight -= confirmed;
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

    // number: the chunk for a take wait, the item (or burst range) for a store wait (the same for one-item chunks).
    private void EndWait(object number, string step, bool isVerified)
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
        EndWait(isTakeWait ? ChunkNumber : StoreNumber, isTakeWait ? "take" : "store", false);

        // An unverified take is assumed to have moved its whole chunk; after a verified take the queue says what is off
        // the source, and the stores in flight are assumed to have left the inventory (the client consumes at once).
        bool isStoreUnsettled = stoppedIn == TransferState.StoredAwaitingVerify && !_hasStoreVerdict;
        int offSource = isTakeWait ? _unit : _queued;
        int trackedRemaining = _sourceStart - Moved - offSource;
        int trackedStranded = stoppedIn == TransferState.StoredAwaitingVerify ? Math.Max(0, _queued - _inFlight) : offSource;

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

        // The game said Ok but the stop came before the verify; settle the burst from the fresh reads, never on the game's word.
        // A burst can be part-applied by the host: only what the counts prove landed is moved.
        string? storeNote = null;
        if (isStoreUnsettled)
        {
            object storeNumber = StoreNumber;
            int sent = _inFlight;
            int targetBefore = _targetStart + Moved;
            int inventoryBefore = _inventoryStart + _queued;
            int confirmed = GetConfirmedStores(targetBefore, targetNow, inventoryBefore, inventoryNow);
            CreditConfirmedStores(confirmed);
            if (confirmed < sent)
            {
                LogStoreMismatch(storeNumber, targetBefore, targetNow, inventoryBefore, inventoryNow, sent);
                storeNote = sent > 1 ? $"burst unverified, {confirmed} of {sent} confirmed" : "store unverified";
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
    private void LogStoreMismatch(
        object storeNumber,
        int targetBefore,
        int? targetAfter,
        int inventoryBefore,
        int? inventoryAfter,
        int sent)
    {
        List<KeyValuePair<string, object?>> line = new() { new("check", "store"), new("n", storeNumber) };
        if (IsBursting)
        {
            line.Add(new("burst", sent));
        }

        line.Add(new("dst_before", targetBefore));
        line.Add(new("dst_after", targetAfter));
        line.Add(new("inv_before", inventoryBefore));
        line.Add(new("inv_after", inventoryAfter));
        _log.Write(LogLevel.Warn, "conservation.mismatch", line);
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
        object number,
        string shelfPrefix,
        int shelfBefore,
        int shelfAfter,
        int inventoryBefore,
        int inventoryAfter,
        bool isVerified,
        int? unit = null,
        int? burst = null)
    {
        List<KeyValuePair<string, object?>> line = new() { new("n", number) };
        if (unit is int chunk)
        {
            line.Add(new("unit", chunk));
        }

        if (burst is int sent)
        {
            line.Add(new("burst", sent));
        }

        line.Add(new(shelfPrefix + "_before", shelfBefore));
        line.Add(new(shelfPrefix + "_after", shelfAfter));
        line.Add(new("inv_before", inventoryBefore));
        line.Add(new("inv_after", inventoryAfter));
        line.Add(new("verify", isVerified ? "ok" : "fail"));
        _log.Write(isVerified ? LogLevel.Info : LogLevel.Warn, eventName, line);
    }
}
