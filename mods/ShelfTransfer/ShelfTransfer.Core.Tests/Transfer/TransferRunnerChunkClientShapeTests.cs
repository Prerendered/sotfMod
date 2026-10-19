// purpose: pins the chunked runner against a friend-hosted client (card 68): the take and each store are applied by the host some ticks later
// owns:    TransferRunnerChunkClientShapeTests
// deps:    TransferRunner, FakeShelfGateway, FakeModLog
// caution: unit 10; a host effect scheduled with delay k lands at the end of tick (issue + k - 1), so wait tick j sees it when j >= k;
//          StoringQueue must never be the state at a tick boundary

using System;
using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Tests.Fakes;
using ShelfTransfer.Core.Transfer;

using Xunit;

namespace ShelfTransfer.Core.Tests.Transfer;

public sealed class TransferRunnerChunkClientShapeTests
{
    private const int _unit = 10;

    private static readonly ShelfRef _src = new("src", "StorageShelf", default);
    private static readonly ShelfRef _dst = new("dst", "StorageShelf", default);
    private static readonly ItemId _item = new(312);

    private sealed class Rig
    {
        private readonly List<(int Tick, Action Action)> _scheduled = new();
        private int _tickNo;

        public Rig(int source, int amount, int confirmTicks)
        {
            Gateway = new FakeShelfGateway { TransferUnit = _unit };
            Gateway.Add(_src, source);
            Dst = Gateway.Add(_dst, 0);
            Log = new FakeModLog();
            SourceStart = source;
            Runner = new TransferRunner(Gateway, Log, new TransferRequest(_src, new SlotRef(null), _dst, _item, amount), confirmTicks);
        }

        public FakeShelfGateway Gateway { get; }

        public FakeShelf Dst { get; }

        public FakeModLog Log { get; }

        public TransferRunner Runner { get; }

        public int SourceStart { get; }

        public List<TransferState> StatesAtTickBoundary { get; } = new();

        public TransferReport Report => Assert.IsType<TransferReport>(Runner.Report);

        public void Schedule(int delayTicks, Action action) => _scheduled.Add((_tickNo + delayTicks - 1, action));

        /// <summary>The take as a client sees it: source and inventory both move after k ticks.</summary>
        public void UseClientTake(int k)
        {
            Gateway.TakeBehavior = _ =>
            {
                Schedule(k, () =>
                {
                    Gateway.Shelf(_src).Count -= _unit;
                    Gateway.Inventory += _unit;
                });
                return GatewayOutcome.Ok;
            };
        }

        /// <summary>The store as a client sees it: inventory drops now, the target rises after k ticks.</summary>
        public void UseClientStore(int k)
        {
            Gateway.StoreBehavior = _ =>
            {
                if (Gateway.Inventory <= 0)
                {
                    return GatewayOutcome.Refused("no item");
                }

                Gateway.Inventory--;
                Schedule(k, () => Dst.Count++);
                return GatewayOutcome.Ok;
            };
        }

        public void RunUntilFinished(int maxTicks = 500)
        {
            for (int i = 0; i < maxTicks && Runner.State != TransferState.Finished; i++)
            {
                TickOnce();
            }

            Assert.Equal(TransferState.Finished, Runner.State);
        }

        public void TickOnce()
        {
            _tickNo++;
            Runner.Tick(1);
            StatesAtTickBoundary.Add(Runner.State);
            foreach ((int Tick, Action Action) entry in _scheduled.Where(s => s.Tick <= _tickNo).ToList())
            {
                _scheduled.Remove(entry);
                entry.Action();
            }
        }

        public List<string> Mutations() =>
            Gateway.Calls.Where(c => c.StartsWith("TakeOne", StringComparison.Ordinal) || c.StartsWith("StoreOne", StringComparison.Ordinal)).ToList();
    }

    // Gap 4: take applied late, each store applied late
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    public void ClientShape_TakeAndEveryStoreAppliedLate_CompletesWithTheBackpackBackAtBaseline(int takeDelay, int storeDelay)
    {
        Rig rig = new(source: 20, amount: 20, confirmTicks: 6);
        rig.UseClientTake(takeDelay);
        rig.UseClientStore(storeDelay);

        rig.RunUntilFinished();

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(20, rig.Report.Moved);
        Assert.Equal(0, rig.Report.Remaining);
        Assert.Equal(0, rig.Report.StrandedInInventory);
        Assert.Equal(20, rig.Dst.Count);
        Assert.Equal(0, rig.Gateway.Inventory);
        Assert.Equal(2, rig.Gateway.TakeCalls);
        Assert.Equal(20, rig.Gateway.StoreCalls);
        Assert.Empty(rig.Log.Named("conservation.mismatch"));
        Assert.All(rig.Log.Named("transfer.store"), e => Assert.Equal("ok", e.Text("verify")));
        Assert.All(rig.Log.Named("transfer.take"), e => Assert.Equal("ok", e.Text("verify")));
    }

    // Gap 4: StoringQueue is a within-tick state only
    [Fact]
    public void ClientShape_AcrossManyTicks_StoringQueueIsNeverTheStateAtATickBoundary()
    {
        Rig rig = new(source: 30, amount: 30, confirmTicks: 6);
        rig.UseClientTake(2);
        rig.UseClientStore(2);

        rig.RunUntilFinished();

        Assert.DoesNotContain(TransferState.StoringQueue, rig.StatesAtTickBoundary);
        Assert.Contains(TransferState.StoredAwaitingVerify, rig.StatesAtTickBoundary);
        Assert.Contains(TransferState.TakenAwaitingVerify, rig.StatesAtTickBoundary);
    }

    // Gap 4: a take is never issued, nor a store re-issued, while the host has not applied the previous step
    [Fact]
    public void ClientShape_WhileWaiting_NoMutationIsIssuedBeyondTheOneInFlight()
    {
        Rig rig = new(source: 20, amount: 20, confirmTicks: 6);
        rig.UseClientTake(3);
        rig.UseClientStore(3);

        for (int tick = 0; tick < 3; tick++)
        {
            rig.TickOnce();
        }

        Assert.Equal(new[] { "TakeOne(src)" }, rig.Mutations());
        rig.TickOnce();
        Assert.Equal(new[] { "TakeOne(src)", "StoreOne(dst)" }, rig.Mutations());
        rig.TickOnce();
        rig.TickOnce();
        Assert.Equal(new[] { "TakeOne(src)", "StoreOne(dst)" }, rig.Mutations());
        rig.TickOnce();
        Assert.Equal(new[] { "TakeOne(src)", "StoreOne(dst)", "StoreOne(dst)" }, rig.Mutations());
    }

    // Gap 4: the take wait does not accept a half-applied chunk as verified
    [Fact]
    public void ClientShape_TakeInventoryNeverArrives_TimesOutInventoryMismatchBeforeAnyStore()
    {
        Rig rig = new(source: 20, amount: 20, confirmTicks: 3);
        rig.Gateway.TakeBehavior = _ =>
        {
            rig.Gateway.Shelf(_src).Count -= _unit;
            return GatewayOutcome.Ok;
        };

        rig.RunUntilFinished();

        Assert.Equal(StopReason.InventoryMismatch, rig.Report.Reason);
        Assert.Equal(1, rig.Gateway.Mutations);
        Assert.Equal(0, rig.Report.Moved);
        Assert.Equal(10, rig.Report.Remaining);
        Assert.Contains(rig.Log.Named("conservation.mismatch"), e => e.Text("check") == "report");
    }

    [Fact]
    public void ClientShape_TakeSourceDropsOnlyAfterTheBound_TimesOutWithoutStoring()
    {
        Rig rig = new(source: 20, amount: 20, confirmTicks: 2);
        rig.UseClientTake(5);

        rig.RunUntilFinished();

        Assert.NotEqual(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(0, rig.Gateway.StoreCalls);
        Assert.Equal(1, rig.Gateway.TakeCalls);
    }

    // Gap 4: the store wait does not accept target +2 (a duplicate) or inventory -2
    [Fact]
    public void ClientShape_StoreTargetArrivesPlusTwoMidQueue_TargetMismatchNotAccepted()
    {
        Rig rig = new(source: 20, amount: 20, confirmTicks: 6);
        rig.UseClientTake(1);
        rig.Gateway.StoreBehavior = call =>
        {
            if (rig.Gateway.Inventory <= 0)
            {
                return GatewayOutcome.Refused("no item");
            }

            rig.Gateway.Inventory--;
            int add = call == 2 ? 2 : 1;
            rig.Schedule(2, () => rig.Dst.Count += add);
            return GatewayOutcome.Ok;
        };

        rig.RunUntilFinished();

        Assert.Equal(StopReason.TargetMismatch, rig.Report.Reason);
        Assert.Equal(1, rig.Report.Moved);
        Assert.Equal(2, rig.Gateway.StoreCalls);
        Assert.Contains(rig.Log.Named("conservation.mismatch"), e => e.Text("check") == "store");
    }

    [Fact]
    public void ClientShape_StoreInventoryDropsByTwoMidQueue_StopsAtOnceWithoutWaiting()
    {
        Rig rig = new(source: 20, amount: 20, confirmTicks: 6);
        rig.UseClientTake(1);
        rig.Gateway.StoreBehavior = call =>
        {
            rig.Gateway.Inventory -= call == 3 ? 2 : 1;
            rig.Schedule(1, () => rig.Dst.Count++);
            return GatewayOutcome.Ok;
        };

        rig.RunUntilFinished();

        // The target is still unapplied (awaitable) while the inventory is already off by two, so the target side names the
        // reason (existing precedence); the stop is immediate either way.
        Assert.Equal(StopReason.TargetMismatch, rig.Report.Reason);
        Assert.Equal(3, rig.Gateway.StoreCalls);
        Assert.Equal("fail", rig.Log.Named("transfer.store").Last().Text("verify"));
        Assert.DoesNotContain(rig.Log.Named("transfer.wait"), w => w.Text("n") == "3");
        Assert.Contains(rig.Log.Named("conservation.mismatch"), e => e.Text("check") == "store");
    }

    // Gap 3: shelf gone while a mid-queue store waits for the host
    [Fact]
    public void ClientShape_TargetDestroyedDuringMidQueueStoreWait_ShelfGoneAndFlagged()
    {
        Rig rig = new(source: 20, amount: 20, confirmTicks: 6);
        rig.UseClientTake(1);
        rig.UseClientStore(4);
        while (rig.Gateway.StoreCalls < 3)
        {
            rig.TickOnce();
        }

        rig.Dst.IsAlive = false;
        rig.RunUntilFinished();

        Assert.Equal(StopReason.ShelfGone, rig.Report.Reason);
        Assert.Contains("store wait", rig.Report.Detail);
        Assert.Equal(3, rig.Gateway.StoreCalls);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.InRange(rig.Report.StrandedInInventory, 1, _unit);
        Assert.Equal(20 - 10, rig.Report.Remaining);
    }

    // Gap 2 with a wait: cancel during a store wait still lets the chunk finish
    [Fact]
    public void ClientShape_CancelDuringMidQueueStoreWait_ChunkCompletesThenCancelled()
    {
        Rig rig = new(source: 30, amount: 30, confirmTicks: 6);
        rig.UseClientTake(1);
        rig.UseClientStore(2);
        while (rig.Gateway.StoreCalls < 3)
        {
            rig.TickOnce();
        }

        rig.Runner.RequestCancel();
        rig.RunUntilFinished();

        Assert.Equal(StopReason.Cancelled, rig.Report.Reason);
        Assert.Equal(10, rig.Report.Moved);
        Assert.Equal(0, rig.Report.StrandedInInventory);
        Assert.Equal(20, rig.Report.Remaining);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(10, rig.Gateway.StoreCalls);
    }

    // Abort while the take wait is open: the whole chunk is counted off the source and stranded
    [Fact]
    public void ClientShape_AbortDuringTakeWaitAfterHostApplied_ReportsTheChunkStranded()
    {
        Rig rig = new(source: 20, amount: 20, confirmTicks: 6);
        rig.UseClientTake(1);
        rig.TickOnce();
        Assert.Equal(TransferState.TakenAwaitingVerify, rig.Runner.State);

        rig.Runner.Abort(StopReason.GatewayError, "disconnect");

        Assert.Equal(0, rig.Report.Moved);
        Assert.Equal(10, rig.Report.Remaining);
        Assert.Equal(10, rig.Report.StrandedInInventory);
        Assert.Empty(rig.Log.Named("conservation.mismatch"));
    }

    // Abort while a mid-queue store is unsettled: settled from fresh reads, never on the game's word
    [Fact]
    public void ClientShape_AbortDuringMidQueueStoreWaitAfterHostApplied_SettlesTheItemFromTheCounts()
    {
        Rig rig = new(source: 20, amount: 20, confirmTicks: 6);
        rig.UseClientTake(1);
        rig.UseClientStore(1);
        while (rig.Gateway.StoreCalls < 3)
        {
            rig.TickOnce();
        }

        // StoreOne 3 was issued this tick; the host applied it at the end of the tick, so the counts now show it.
        Assert.Equal(TransferState.StoredAwaitingVerify, rig.Runner.State);
        Assert.Equal(3, rig.Dst.Count);

        rig.Runner.Abort(StopReason.GatewayError, "disconnect");

        Assert.Equal(3, rig.Report.Moved);
        Assert.Equal(7, rig.Report.StrandedInInventory);
        Assert.Equal(10, rig.Report.Remaining);
        Assert.Equal(20, rig.Report.Moved + rig.Report.Remaining + rig.Report.StrandedInInventory);
        Assert.Empty(rig.Log.Named("conservation.mismatch"));
    }

    [Fact]
    public void ClientShape_AbortDuringMidQueueStoreWaitBeforeHostApplied_ReportsUnverifiedAndFlagsConservation()
    {
        Rig rig = new(source: 20, amount: 20, confirmTicks: 6);
        rig.UseClientTake(1);
        rig.UseClientStore(1);
        while (rig.Gateway.StoreCalls < 3)
        {
            rig.TickOnce();
        }

        // Undo the host's apply of store 3 to model "Ok but not yet applied".
        rig.Dst.Count--;

        rig.Runner.Abort(StopReason.GatewayError, "disconnect");

        Assert.Equal(2, rig.Report.Moved);
        Assert.Contains("store unverified", rig.Report.Detail);
        Assert.Contains(rig.Log.Named("conservation.mismatch"), e => e.Text("check") == "store");
    }
}
