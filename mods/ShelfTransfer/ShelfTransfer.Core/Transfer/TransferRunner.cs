// purpose: the transfer state machine (flow F2): plan, then take → verify → store → verify one item per cycle
// owns:    TransferRunner
// deps:    IShelfGateway, IModLog, AmountClamp, TransferRequest, TransferReport, TransferState, StopReason
// caution: a cycle never yields between its take and its store (D7); TakeOne/StoreOne are the only mutations (invariant 2)

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
    private int _sourceStart;
    private int? _inventoryStart;
    private bool _isCancelRequested;

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

    /// <summary>Honoured at the next <see cref="TransferState.ReadyForNext"/>, never mid-cycle (D7).</summary>
    public void RequestCancel() => _isCancelRequested = true;

    /// <summary>Runs Planning if pending, then at most <paramref name="maxCycles"/> full cycles.</summary>
    public void Tick(int maxCycles)
    {
        if (maxCycles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCycles), maxCycles, "must be at least 1");
        }

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

        _sourceStart = _gateway.GetCount(_request.Source, _request.Item);
        int targetStart = _gateway.GetCount(_request.Target, _request.Item);
        _inventoryStart = _gateway.GetInventoryCount(_request.Item);
        bool canHold = _gateway.CanHold(_request.Target, _request.Item);
        ClampResult clamp = canHold
            ? AmountClamp.Clamp(_request.Amount, _sourceStart, _gateway.GetFreeCapacity(_request.Target, _request.Item))
            : new ClampResult(0, ClampReason.TargetFull);
        Requested = clamp.Amount;

        _log.Write(LogLevel.Info, "transfer.start", new KeyValuePair<string, object?>[]
        {
            new("source", _request.Source.Key),
            new("target", _request.Target.Key),
            new("item", _request.Item.Value),
            new("amount", Requested),
            new("s0", _sourceStart),
            new("t0", targetStart),
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

    private void RunCycle()
    {
        int itemNumber = Moved + 1;
        int sourceBefore = _gateway.GetCount(_request.Source, _request.Item);
        int inventoryBefore = _gateway.GetInventoryCount(_request.Item);

        GatewayOutcome take = _gateway.TakeOne(_request.Source, _request.SourceSlot, _request.Item);
        if (!take.IsOk)
        {
            StopOnFault($"take {take.Status}: {take.Detail}");
            return;
        }

        State = TransferState.TakenAwaitingVerify;
        int sourceAfter = _gateway.GetCount(_request.Source, _request.Item);
        int inventoryAfterTake = _gateway.GetInventoryCount(_request.Item);
        bool isTakeVerified = sourceAfter == sourceBefore - 1 && inventoryAfterTake == inventoryBefore + 1;
        LogStep("transfer.take", itemNumber, "src", sourceBefore, sourceAfter, inventoryBefore, inventoryAfterTake, isTakeVerified);
        if (!isTakeVerified)
        {
            StopOnFault("take verify failed");
            return;
        }

        int targetBefore = _gateway.GetCount(_request.Target, _request.Item);
        GatewayOutcome store = _gateway.StoreOne(_request.Target, _request.Item);
        if (!store.IsOk)
        {
            StopOnFault($"store {store.Status}: {store.Detail}");
            return;
        }

        State = TransferState.StoredAwaitingVerify;
        int targetAfter = _gateway.GetCount(_request.Target, _request.Item);
        int inventoryAfterStore = _gateway.GetInventoryCount(_request.Item);
        bool isStoreVerified = targetAfter == targetBefore + 1 && inventoryAfterStore == inventoryBefore;
        LogStep("transfer.store", itemNumber, "dst", targetBefore, targetAfter, inventoryAfterTake, inventoryAfterStore, isStoreVerified);
        if (!isStoreVerified)
        {
            StopOnFault("store verify failed");
            return;
        }

        Moved = itemNumber;
        State = TransferState.ReadyForNext;
    }

    // TODO(dev): card 7 splits this into TakeRefused, StoreRefused and the *Mismatch reasons; card 6 only guarantees the stop.
    private void StopOnFault(string detail) => Finish(StopReason.GatewayError, detail);

    private void Finish(StopReason reason, string? detail)
    {
        // Re-read rather than derive (invariant 1); a gone source cannot be read, so it falls back to the tracked count.
        int remaining = _gateway.IsAlive(_request.Source)
            ? _gateway.GetCount(_request.Source, _request.Item)
            : _sourceStart - Moved;
        int stranded = _inventoryStart is int inventoryStart
            ? Math.Clamp(_gateway.GetInventoryCount(_request.Item) - inventoryStart, 0, 1)
            : 0;

        Report = new TransferReport(reason, Requested, Moved, remaining, stranded, detail);
        State = TransferState.Finished;

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
            new("detail", detail),
        });
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
