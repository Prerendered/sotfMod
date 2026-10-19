// purpose: pins the chunked runner (card 68): take one game chunk into the backpack as a queue, store it one item at a time, each step verified
// owns:    TransferRunnerChunkTests
// deps:    TransferRunner, FakeShelfGateway, FakeModLog
// caution: unit 10 throughout (rifle ammo); "k" is the 1-based StoreOne call number of chunk 1; the conservation sum is only asserted where the
//          fault itself loses nothing, since a deliberately lossy game call must surface as conservation.mismatch instead

using System;
using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Tests.Fakes;
using ShelfTransfer.Core.Transfer;

using Xunit;

namespace ShelfTransfer.Core.Tests.Transfer;

public sealed class TransferRunnerChunkTests
{
    private const int _unit = 10;

    private static readonly ShelfRef _src = new("src", "StorageShelf", default);
    private static readonly ShelfRef _dst = new("dst", "StorageShelf", default);
    private static readonly ItemId _item = new(312);

    private sealed class Rig
    {
        public Rig(int source, int amount = AmountClamp.All, int unit = _unit, int? capacity = null, int inventory = 0, int target = 0)
        {
            Gateway = new FakeShelfGateway { TransferUnit = unit, Inventory = inventory };
            Gateway.Add(_src, source);
            Dst = Gateway.Add(_dst, target);
            if (capacity is int cap)
            {
                Dst.Capacity = cap;
            }

            Log = new FakeModLog();
            SourceStart = source;
            InventoryStart = inventory;
            Runner = new TransferRunner(Gateway, Log, new TransferRequest(_src, new SlotRef(null), _dst, _item, amount));
            MaxInventory = inventory;
            Gateway.OnCall = call =>
            {
                MaxInventory = Math.Max(MaxInventory, Gateway.Inventory);
                Hook?.Invoke(call);
            };
        }

        public FakeShelfGateway Gateway { get; }

        public FakeShelf Dst { get; }

        public FakeShelf Src => Gateway.Shelf(_src);

        public FakeModLog Log { get; }

        public TransferRunner Runner { get; }

        public int SourceStart { get; }

        public int InventoryStart { get; }

        public int MaxInventory { get; private set; }

        public Action<string>? Hook { get; set; }

        public TransferReport Report => Assert.IsType<TransferReport>(Runner.Report);

        public List<string> Mutations() =>
            Gateway.Calls.Where(c => c.StartsWith("TakeOne", StringComparison.Ordinal) || c.StartsWith("StoreOne", StringComparison.Ordinal)).ToList();

        public void AssertReportAddsUp() => Assert.Equal(SourceStart, Report.Moved + Report.Remaining + Report.StrandedInInventory);

        public List<string> Lines(params string[] events) =>
            Log.Entries
                .Where(e => events.Length == 0 || events.Contains(e.Event))
                .Select(e => $"{e.Level} {e.Event} " + string.Join(" ", e.Fields.Select(f => $"{f.Key}={f.Value?.ToString() ?? "null"}")))
                .ToList();
    }

    // ---------------------------------------------------------------- criterion 1, 2, 3: planning and happy path

    // Criterion 1
    [Fact]
    public void Tick_TwentyOfThirtyAtUnitTen_LogsTakeThenTenVerifiedStoresPerChunk()
    {
        Rig rig = new(source: 30, amount: 20);

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(20, rig.Report.Moved);
        Assert.Equal(10, rig.Report.Remaining);
        Assert.Equal(0, rig.Report.StrandedInInventory);
        Assert.Equal(10, rig.Src.Count);
        Assert.Equal(20, rig.Dst.Count);
        Assert.Equal(0, rig.Gateway.Inventory);

        List<string> expected = new() { "Info transfer.start source=src target=dst item=312 amount=20 unit=10 s0=30 t0=0 i0=0" };
        for (int chunk = 0; chunk < 2; chunk++)
        {
            int srcBefore = 30 - (chunk * 10);
            expected.Add($"Info transfer.take n={chunk + 1} unit=10 src_before={srcBefore} src_after={srcBefore - 10} inv_before=0 inv_after=10 verify=ok");
            for (int item = 0; item < 10; item++)
            {
                int stored = (chunk * 10) + item;
                expected.Add($"Info transfer.store n={stored + 1} dst_before={stored} dst_after={stored + 1} inv_before={10 - item} inv_after={9 - item} verify=ok");
            }
        }

        expected.Add("Info transfer.stop reason=Completed s0=30 moved=20 remaining=10 stranded=0 detail=null");
        Assert.Equal(expected, rig.Lines("transfer.start", "transfer.take", "transfer.store", "transfer.stop"));
        Assert.Empty(rig.Log.Named("conservation.mismatch"));
        Assert.Equal(2, rig.Gateway.TakeCalls);
        Assert.Equal(20, rig.Gateway.StoreCalls);
    }

    // Criterion 1, invariant 1 amended: the inventory never holds more than one chunk, and a take only ever starts from an empty queue
    [Fact]
    public void Tick_FiftyOfHundred_InventoryNeverExceedsOneChunkAndEveryTakeStartsFromBaseline()
    {
        Rig rig = new(source: 100, amount: 53, inventory: 7);
        List<int> inventoryAtTake = new();
        rig.Hook = call =>
        {
            if (call.StartsWith("TakeOne", StringComparison.Ordinal))
            {
                inventoryAtTake.Add(rig.Gateway.Inventory);
            }
        };

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(50, rig.Report.Moved);
        Assert.Equal(7 + _unit, rig.MaxInventory);
        Assert.Equal(7, rig.Gateway.Inventory);
        Assert.Equal(new[] { 7, 7, 7, 7, 7 }, inventoryAtTake);
        Assert.Equal(5, rig.Gateway.TakeCalls);
        Assert.Equal(50, rig.Gateway.StoreCalls);
        Assert.Equal(0, rig.Report.StrandedInInventory);
        Assert.Empty(rig.Log.Named("conservation.mismatch"));
    }

    // Criterion 1: take and store sequence per chunk, no interleaving
    [Fact]
    public void Tick_TwoChunks_MutationOrderIsTakeTenStoresTakeTenStores()
    {
        Rig rig = new(source: 20);

        rig.Runner.Tick(100);

        List<string> expected = new();
        for (int chunk = 0; chunk < 2; chunk++)
        {
            expected.Add("TakeOne(src)");
            expected.AddRange(Enumerable.Repeat("StoreOne(dst)", 10));
        }

        Assert.Equal(expected, rig.Mutations());
    }

    // Criterion 2
    [Fact]
    public void Tick_FiftyThreeRequested_PlansFiftyAndLogsAmountAndUnit()
    {
        Rig rig = new(source: 100, amount: 53);

        rig.Runner.Tick(1);

        Assert.Equal(50, rig.Runner.Requested);
        Assert.Equal("Info transfer.start source=src target=dst item=312 amount=50 unit=10 s0=100 t0=0 i0=0", rig.Lines("transfer.start").Single());
    }

    // Criterion 2
    [Fact]
    public void Tick_FiftyThreeRequested_MovesExactlyFiftyAndLeavesThreeOfTheAskUntouched()
    {
        Rig rig = new(source: 100, amount: 53);

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(50, rig.Report.Requested);
        Assert.Equal(50, rig.Report.Moved);
        Assert.Equal(50, rig.Src.Count);
        Assert.Equal(50, rig.Dst.Count);
    }

    // Criterion 3
    [Fact]
    public void Tick_TargetFitsTwentyFive_PlansTwentyAndMovesTwenty()
    {
        Rig rig = new(source: 30, capacity: 25);

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(20, rig.Report.Requested);
        Assert.Equal(20, rig.Report.Moved);
        Assert.Equal(10, rig.Report.Remaining);
        Assert.Equal(20, rig.Dst.Count);
        Assert.Equal(2, rig.Gateway.TakeCalls);
    }

    // Criterion 3
    [Theory]
    [InlineData(9)]
    [InlineData(1)]
    public void Tick_TargetFitsLessThanOneChunk_BlockedBeforeStartWithNoMutation(int capacity)
    {
        Rig rig = new(source: 30, capacity: capacity);

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.BlockedBeforeStart, rig.Report.Reason);
        Assert.Equal("TargetFull, less than one chunk of 10", rig.Report.Detail);
        Assert.Equal(0, rig.Report.Requested);
        Assert.Equal(0, rig.Gateway.Mutations);
        Assert.Equal(30, rig.Report.Remaining);
        Assert.Equal(0, rig.Report.StrandedInInventory);
        Assert.Equal("Info transfer.start source=src target=dst item=312 amount=0 unit=10 s0=30 t0=0 i0=0", rig.Lines("transfer.start").Single());
    }

    [Fact]
    public void Tick_TargetCannotHoldItem_BlockedBeforeStartWithNoMutation()
    {
        Rig rig = new(source: 30);
        rig.Dst.CanHold = false;

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.BlockedBeforeStart, rig.Report.Reason);
        Assert.Equal("target cannot hold item", rig.Report.Detail);
        Assert.Equal(0, rig.Gateway.Mutations);
    }

    // Planning detail, gap 6
    [Fact]
    public void Tick_SourceHoldsLessThanOneChunk_BlockedBeforeStartNamesTheChunk()
    {
        Rig rig = new(source: 5);

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.BlockedBeforeStart, rig.Report.Reason);
        Assert.Equal("NothingToMove, less than one chunk of 10", rig.Report.Detail);
        Assert.Equal(0, rig.Gateway.Mutations);
        Assert.Equal(5, rig.Report.Remaining);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(1)]
    public void Tick_RequestUnderOneChunk_BlockedBeforeStartNamesTheChunk(int amount)
    {
        Rig rig = new(source: 30, amount: amount);

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.BlockedBeforeStart, rig.Report.Reason);
        Assert.Equal("InvalidAmount, less than one chunk of 10", rig.Report.Detail);
        Assert.Equal(0, rig.Gateway.Mutations);
    }

    [Fact]
    public void Tick_NonPositiveRequestAtUnitTen_BlockedBeforeStartWithNoMutation()
    {
        Rig rig = new(source: 30, amount: 0);

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.BlockedBeforeStart, rig.Report.Reason);
        Assert.Equal(0, rig.Gateway.Mutations);
    }

    // The source is not a multiple of the chunk: whole chunks only, the odd remainder stays on the shelf
    [Fact]
    public void Tick_SourceTwentyFiveRequestAll_MovesTwentyAndLeavesFive()
    {
        Rig rig = new(source: 25);

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(20, rig.Report.Moved);
        Assert.Equal(5, rig.Report.Remaining);
        Assert.Equal(0, rig.Report.StrandedInInventory);
        Assert.Equal(5, rig.Src.Count);
    }

    [Fact]
    public void Tick_ExactlyOneChunkOnSource_MovesItAndEmptiesTheSource()
    {
        Rig rig = new(source: 10);

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(10, rig.Report.Moved);
        Assert.Equal(0, rig.Src.Count);
        Assert.Equal(10, rig.Dst.Count);
    }

    // The slot is passed to every take (the group's chunk is taken from the aimed slot)
    [Fact]
    public void Tick_EveryTake_PassesTheRequestedSlot()
    {
        Rig rig = new(source: 30);

        rig.Runner.Tick(100);

        Assert.Equal(3, rig.Gateway.TakeSlots.Count);
        Assert.All(rig.Gateway.TakeSlots, slot => Assert.Equal(new SlotRef(null), slot));
    }

    // Tick budget: one cycle is one whole chunk, never a part of one
    [Fact]
    public void Tick_BudgetOneCycle_EachTickMovesExactlyOneWholeChunkAndLeavesTheBackpackAtBaseline()
    {
        Rig rig = new(source: 30, inventory: 4);

        rig.Runner.Tick(1);
        Assert.Equal(10, rig.Runner.Moved);
        Assert.Equal(4, rig.Gateway.Inventory);
        Assert.Equal(TransferState.ReadyForNext, rig.Runner.State);

        rig.Runner.Tick(1);
        Assert.Equal(20, rig.Runner.Moved);
        Assert.Equal(4, rig.Gateway.Inventory);

        rig.Runner.Tick(1);
        Assert.Equal(30, rig.Runner.Moved);
        Assert.Equal(TransferState.Finished, rig.Runner.State);
        Assert.Equal(StopReason.Completed, rig.Report.Reason);
    }

    // ---------------------------------------------------------------- criterion 4: failure points

    // Gap 1: store k refused / failed, for k = 1, 2, 5, 10
    [Theory]
    [InlineData(1, GatewayStatus.Refused)]
    [InlineData(2, GatewayStatus.Refused)]
    [InlineData(5, GatewayStatus.Refused)]
    [InlineData(10, GatewayStatus.Refused)]
    [InlineData(1, GatewayStatus.Failed)]
    [InlineData(2, GatewayStatus.Failed)]
    [InlineData(5, GatewayStatus.Failed)]
    [InlineData(10, GatewayStatus.Failed)]
    public void Tick_StoreKFailsMidQueue_StopsWithTheRestStrandedAndNothingLost(int k, GatewayStatus status)
    {
        Rig rig = new(source: 30);
        List<(int Call, TransferState State)> stateAtStore = new();
        rig.Hook = call =>
        {
            if (call == "StoreOne(dst)")
            {
                stateAtStore.Add((rig.Gateway.StoreCalls, rig.Runner.State));
            }
        };
        rig.Gateway.StoreBehavior = call => call == k
            ? (status == GatewayStatus.Refused ? GatewayOutcome.Refused("full") : GatewayOutcome.Failed("boom"))
            : null;

        rig.Runner.Tick(100);

        Assert.Equal(status == GatewayStatus.Refused ? StopReason.StoreRefused : StopReason.GatewayError, rig.Report.Reason);
        Assert.Equal(k - 1, rig.Report.Moved);
        Assert.Equal(20, rig.Report.Remaining);
        Assert.Equal(_unit - k + 1, rig.Report.StrandedInInventory);
        Assert.InRange(rig.Report.StrandedInInventory, 1, _unit);
        rig.AssertReportAddsUp();
        Assert.Equal(1 + k, rig.Gateway.Mutations);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal("StoreOne(dst)", rig.Mutations().Last());
        Assert.Equal(30, rig.Gateway.Total(_src, _dst));
        Assert.Empty(rig.Log.Named("conservation.mismatch"));

        // k = 1 runs straight after the verified take; later stores run between verified stores
        Assert.Equal(k, stateAtStore.Count);
        Assert.Equal(TransferState.TakenAwaitingVerify, stateAtStore[0].State);
        Assert.All(stateAtStore.Skip(1), entry => Assert.Equal(TransferState.StoringQueue, entry.State));
        Assert.Equal(TransferState.Finished, rig.Runner.State);
    }

    // Gap 1: store k lands wrong on the target (lost: inventory drops, target does not) or doubled (target +2)
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(5, 0)]
    [InlineData(10, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(5, 2)]
    [InlineData(10, 2)]
    public void Tick_StoreKTargetCountWrong_StopsTargetMismatchAndFlagsConservation(int k, int targetStep)
    {
        Rig rig = new(source: 30);
        rig.Gateway.StoreBehavior = call =>
        {
            if (call != k)
            {
                return null;
            }

            rig.Gateway.Inventory--;
            rig.Dst.Count += targetStep;
            return GatewayOutcome.Ok;
        };

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.TargetMismatch, rig.Report.Reason);
        Assert.Equal(k - 1, rig.Report.Moved);
        Assert.Equal(1 + k, rig.Gateway.Mutations);
        Assert.Contains(rig.Log.Named("conservation.mismatch"), e => e.Text("check") == "store");
        Assert.Equal("Warn", rig.Log.Named("transfer.store").Last().Level.ToString());
        Assert.Equal("fail", rig.Log.Named("transfer.store").Last().Text("verify"));
        Assert.Equal(TransferState.Finished, rig.Runner.State);
    }

    // Gap 1: store k drops the inventory by two
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(10)]
    public void Tick_StoreKInventoryDropsByTwo_StopsInventoryMismatch(int k)
    {
        Rig rig = new(source: 30);
        rig.Gateway.StoreBehavior = call =>
        {
            if (call != k)
            {
                return null;
            }

            rig.Gateway.Inventory -= 2;
            rig.Dst.Count++;
            return GatewayOutcome.Ok;
        };

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.InventoryMismatch, rig.Report.Reason);
        Assert.Equal(k - 1, rig.Report.Moved);
        Assert.Equal(1 + k, rig.Gateway.Mutations);
        Assert.Contains(rig.Log.Named("conservation.mismatch"), e => e.Text("check") == "store");
        Assert.InRange(rig.Report.StrandedInInventory, 0, _unit);
    }

    // Gap 1: take faults
    [Fact]
    public void Tick_FirstTakeRefused_StopsTakeRefusedWithNothingMoved()
    {
        Rig rig = new(source: 30);
        rig.Gateway.TakeBehavior = _ => GatewayOutcome.Refused("carry cap");

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.TakeRefused, rig.Report.Reason);
        Assert.Equal(0, rig.Report.Moved);
        Assert.Equal(30, rig.Report.Remaining);
        Assert.Equal(0, rig.Report.StrandedInInventory);
        Assert.Equal(1, rig.Gateway.Mutations);
        rig.AssertReportAddsUp();
    }

    [Fact]
    public void Tick_SecondTakeRefused_KeepsTheFirstChunkMovedAndStrandsNothing()
    {
        Rig rig = new(source: 30);
        rig.Gateway.TakeBehavior = call => call == 2 ? GatewayOutcome.Refused("carry cap") : null;

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.TakeRefused, rig.Report.Reason);
        Assert.Equal(10, rig.Report.Moved);
        Assert.Equal(20, rig.Report.Remaining);
        Assert.Equal(0, rig.Report.StrandedInInventory);
        Assert.Equal(11 + 1, rig.Gateway.Mutations);
        rig.AssertReportAddsUp();
        Assert.Equal(30, rig.Gateway.Total(_src, _dst));
    }

    [Fact]
    public void Tick_TakeFailsWithGatewayError_StopsGatewayErrorWithNothingStored()
    {
        Rig rig = new(source: 30);
        rig.Gateway.TakeBehavior = _ => GatewayOutcome.Failed("boom");

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.GatewayError, rig.Report.Reason);
        Assert.Equal(1, rig.Gateway.Mutations);
        rig.AssertReportAddsUp();
    }

    // Gap 1: the take moves the wrong amount (source -20, inventory +10): stops before any store, the report reads the truth
    [Fact]
    public void Tick_TakeDropsSourceByTwoChunks_StopsSourceMismatchBeforeAnyStore()
    {
        Rig rig = new(source: 30);
        rig.Gateway.TakeBehavior = _ =>
        {
            rig.Src.Count -= 20;
            rig.Gateway.Inventory += 10;
            return GatewayOutcome.Ok;
        };

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.SourceMismatch, rig.Report.Reason);
        Assert.Equal(1, rig.Gateway.Mutations);
        Assert.Equal(0, rig.Report.Moved);
        Assert.Equal(10, rig.Report.Remaining);
        Assert.Equal(10, rig.Report.StrandedInInventory);
        Assert.Contains(rig.Log.Named("conservation.mismatch"), e => e.Text("check") == "report");
        Assert.Equal("fail", rig.Log.Named("transfer.take").Single().Text("verify"));
    }

    [Fact]
    public void Tick_TakeRaisesInventoryByHalfAChunk_StopsInventoryMismatchBeforeAnyStore()
    {
        Rig rig = new(source: 30);
        rig.Gateway.TakeBehavior = _ =>
        {
            rig.Src.Count -= 10;
            rig.Gateway.Inventory += 5;
            return GatewayOutcome.Ok;
        };

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.InventoryMismatch, rig.Report.Reason);
        Assert.Equal(1, rig.Gateway.Mutations);
        Assert.Equal(5, rig.Report.StrandedInInventory);
        Assert.Equal(20, rig.Report.Remaining);
        Assert.Contains(rig.Log.Named("conservation.mismatch"), e => e.Text("check") == "report");
    }

    // The game moves fewer than a whole chunk (the group had less than the unit): the take must not be accepted as a chunk
    [Fact]
    public void Tick_TakeMovesOneItemInsteadOfAChunk_StopsAndNeverStores()
    {
        Rig rig = new(source: 30);
        rig.Gateway.TakeBehavior = _ =>
        {
            rig.Src.Count -= 1;
            rig.Gateway.Inventory += 1;
            return GatewayOutcome.Ok;
        };

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.SourceMismatch, rig.Report.Reason);
        Assert.Equal(1, rig.Gateway.Mutations);
        Assert.Equal(0, rig.Report.Moved);
    }

    // Gap 1: throws out of StoreOne at k = 1 and k = 2
    [Theory]
    [InlineData(1, TransferState.TakenAwaitingVerify)]
    [InlineData(2, TransferState.StoringQueue)]
    public void Tick_StoreOneThrows_AbortReportsTheQueueAsStrandedAndNothingLost(int k, TransferState stateAtThrow)
    {
        Rig rig = new(source: 30);
        rig.Hook = call =>
        {
            if (call == "StoreOne(dst)" && rig.Gateway.StoreCalls == k)
            {
                throw new InvalidOperationException("boom");
            }
        };

        Assert.Throws<InvalidOperationException>(() => rig.Runner.Tick(100));
        Assert.Equal(stateAtThrow, rig.Runner.State);

        rig.Runner.Abort(StopReason.GatewayError, "driver caught");

        Assert.Equal(StopReason.GatewayError, rig.Report.Reason);
        Assert.Equal(k - 1, rig.Report.Moved);
        Assert.Equal(20, rig.Report.Remaining);
        Assert.Equal(_unit - k + 1, rig.Report.StrandedInInventory);
        rig.AssertReportAddsUp();
        Assert.Empty(rig.Log.Named("conservation.mismatch"));
        Assert.Equal(k, rig.Gateway.StoreCalls);
    }

    [Theory]
    [InlineData(1, "previous tick threw during TakenAwaitingVerify")]
    [InlineData(2, "previous tick threw during StoringQueue")]
    public void Tick_StoreOneThrowsThenNextTick_FinishesWithTheThrowDetailAndNoFurtherMutation(int k, string detail)
    {
        Rig rig = new(source: 30);
        rig.Hook = call =>
        {
            if (call == "StoreOne(dst)" && rig.Gateway.StoreCalls == k)
            {
                throw new InvalidOperationException("boom");
            }
        };
        Assert.Throws<InvalidOperationException>(() => rig.Runner.Tick(100));
        int mutations = rig.Gateway.Mutations;

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.GatewayError, rig.Report.Reason);
        Assert.Contains(detail, rig.Report.Detail);
        Assert.Equal(mutations, rig.Gateway.Mutations);
        Assert.Equal(_unit - k + 1, rig.Report.StrandedInInventory);
        rig.AssertReportAddsUp();
    }

    // A StoreOne that applied and then threw (the item left the backpack, the throw came after): honest report, flagged
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Tick_StoreOneAppliesThenThrows_ReportIsReadFromTheCountsAndConservationIsFlagged(int k)
    {
        Rig rig = new(source: 30);
        rig.Gateway.StoreBehavior = call =>
        {
            if (call != k)
            {
                return null;
            }

            rig.Gateway.ApplyStore(_dst);
            throw new InvalidOperationException("boom after apply");
        };
        Assert.Throws<InvalidOperationException>(() => rig.Runner.Tick(100));

        rig.Runner.Abort(StopReason.GatewayError, "driver caught");

        // The item is on the target and out of the backpack, so what the player holds is what the report says; any gap
        // against S0 must be flagged, never silent.
        Assert.Equal(_unit - k, rig.Report.StrandedInInventory);
        Assert.Equal(20, rig.Report.Remaining);
        int sum = rig.Report.Moved + rig.Report.Remaining + rig.Report.StrandedInInventory;
        if (sum != rig.SourceStart)
        {
            Assert.Contains(rig.Log.Named("conservation.mismatch"), e => e.Text("check") == "report");
        }

        Assert.Equal(30, rig.Gateway.Total(_src, _dst));
    }

    // Gap 1: re-entrant Abort and Tick from inside StoreOne mid-queue
    [Fact]
    public void Tick_AbortReentrantlyInsideStoreOneMidQueue_FirstStopWinsAndNoLaterCallIsMade()
    {
        Rig rig = new(source: 30);
        rig.Hook = call =>
        {
            if (call == "StoreOne(dst)" && rig.Gateway.StoreCalls == 3)
            {
                rig.Runner.Abort(StopReason.GatewayError, "reentrant abort");
            }
        };

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.GatewayError, rig.Report.Reason);
        Assert.Contains("reentrant abort", rig.Report.Detail);
        Assert.Equal(3, rig.Gateway.StoreCalls);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(2, rig.Report.Moved);
        Assert.Single(rig.Log.Named("transfer.stop"));
    }

    [Fact]
    public void Tick_TickReentrantlyInsideStoreOneMidQueue_StopsGatewayErrorNamingTheState()
    {
        Rig rig = new(source: 30);
        rig.Hook = call =>
        {
            if (call == "StoreOne(dst)" && rig.Gateway.StoreCalls == 3)
            {
                rig.Runner.Tick(1);
            }
        };

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.GatewayError, rig.Report.Reason);
        Assert.Equal("re-entrant tick during StoringQueue", rig.Report.Detail);
        Assert.Equal(3, rig.Gateway.StoreCalls);
        Assert.Single(rig.Log.Named("transfer.stop"));
    }

    // Gap 1: no take while the queue holds items, even across an unlucky ordering (a store fault never leads to another take)
    [Fact]
    public void Tick_StoreFaultMidQueue_NeverTakesAgain()
    {
        Rig rig = new(source: 100);
        rig.Gateway.StoreBehavior = call => call == 14 ? GatewayOutcome.Refused("full") : null;

        rig.Runner.Tick(100);

        Assert.Equal(2, rig.Gateway.TakeCalls);
        Assert.Equal(13, rig.Report.Moved);
        Assert.Equal(7, rig.Report.StrandedInInventory);
        Assert.Equal(80, rig.Report.Remaining);
        rig.AssertReportAddsUp();
        Assert.Equal(100, rig.Gateway.Total(_src, _dst));
    }

    // ---------------------------------------------------------------- D7: cancel and shelf gone

    // Gap 2
    [Theory]
    [InlineData("TakeOne(src)", 1)]
    [InlineData("StoreOne(dst)", 1)]
    [InlineData("StoreOne(dst)", 3)]
    [InlineData("StoreOne(dst)", 10)]
    public void RequestCancel_MidChunk_ChunkCompletesThenCancelledWithNothingStranded(string raiseOn, int callNumber)
    {
        Rig rig = new(source: 30);
        int seen = 0;
        rig.Hook = call =>
        {
            if (call == raiseOn && ++seen == callNumber)
            {
                rig.Runner.RequestCancel();
            }
        };

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.Cancelled, rig.Report.Reason);
        Assert.Equal(10, rig.Report.Moved);
        Assert.Equal(20, rig.Report.Remaining);
        Assert.Equal(0, rig.Report.StrandedInInventory);
        Assert.Equal(11, rig.Gateway.Mutations);
        Assert.Equal(0, rig.Gateway.Inventory);
        rig.AssertReportAddsUp();
    }

    [Fact]
    public void RequestCancel_MidSecondChunk_SecondChunkCompletesThenCancelled()
    {
        Rig rig = new(source: 50);
        rig.Hook = call =>
        {
            if (call == "StoreOne(dst)" && rig.Gateway.StoreCalls == 15)
            {
                rig.Runner.RequestCancel();
            }
        };

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.Cancelled, rig.Report.Reason);
        Assert.Equal(20, rig.Report.Moved);
        Assert.Equal(30, rig.Report.Remaining);
        Assert.Equal(0, rig.Report.StrandedInInventory);
        Assert.Equal(2, rig.Gateway.TakeCalls);
        Assert.Equal(20, rig.Gateway.StoreCalls);
    }

    [Fact]
    public void RequestCancel_BeforeTheFirstTick_CancelsWithoutTakingAChunk()
    {
        Rig rig = new(source: 30);
        rig.Runner.RequestCancel();

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.Cancelled, rig.Report.Reason);
        Assert.Equal(0, rig.Gateway.Mutations);
    }

    // Gap 3: shelf gone mid-queue
    [Fact]
    public void Tick_TargetDestroyedMidQueue_StoreFailsAndTheRestIsReportedStranded()
    {
        Rig rig = new(source: 30);
        rig.Hook = call =>
        {
            if (call == "StoreOne(dst)" && rig.Gateway.StoreCalls == 4)
            {
                rig.Dst.IsAlive = false;
            }
        };
        rig.Gateway.StoreBehavior = _ => rig.Dst.IsAlive ? null : GatewayOutcome.Failed("target destroyed");

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.GatewayError, rig.Report.Reason);
        Assert.Equal(3, rig.Report.Moved);
        Assert.Equal(7, rig.Report.StrandedInInventory);
        Assert.Equal(20, rig.Report.Remaining);
        rig.AssertReportAddsUp();
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(4, rig.Gateway.StoreCalls);
    }

    [Fact]
    public void Tick_SourceDestroyedMidQueue_ChunkCompletesThenShelfGoneWithNothingStranded()
    {
        Rig rig = new(source: 30);
        rig.Hook = call =>
        {
            if (call == "StoreOne(dst)" && rig.Gateway.StoreCalls == 5)
            {
                rig.Src.IsAlive = false;
            }
        };

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.ShelfGone, rig.Report.Reason);
        Assert.Equal(10, rig.Report.Moved);
        Assert.Equal(0, rig.Report.StrandedInInventory);
        Assert.Equal(20, rig.Report.Remaining);
        Assert.Equal(11, rig.Gateway.Mutations);
        rig.AssertReportAddsUp();
    }

    [Fact]
    public void Tick_TargetDestroyedBetweenChunks_ShelfGoneBeforeTheNextTake()
    {
        Rig rig = new(source: 30);
        rig.Runner.Tick(1);
        rig.Dst.IsAlive = false;

        rig.Runner.Tick(1);

        Assert.Equal(StopReason.ShelfGone, rig.Report.Reason);
        Assert.Equal(10, rig.Report.Moved);
        Assert.Equal(0, rig.Report.StrandedInInventory);
        Assert.Equal(11, rig.Gateway.Mutations);
    }

    // Another player touches a shelf or the backpack between chunks: the run stops before the next take
    [Fact]
    public void Tick_SourceChangedBetweenChunks_SourceMismatchBeforeTheNextTake()
    {
        Rig rig = new(source: 30);
        rig.Runner.Tick(1);
        rig.Src.Count -= 1;

        rig.Runner.Tick(1);

        Assert.Equal(StopReason.SourceMismatch, rig.Report.Reason);
        Assert.Equal(11, rig.Gateway.Mutations);
        Assert.Equal(10, rig.Report.Moved);
    }

    [Fact]
    public void Tick_TargetChangedBetweenChunks_TargetMismatchBeforeTheNextTake()
    {
        Rig rig = new(source: 30);
        rig.Runner.Tick(1);
        rig.Dst.Count += 1;

        rig.Runner.Tick(1);

        Assert.Equal(StopReason.TargetMismatch, rig.Report.Reason);
        Assert.Equal(11, rig.Gateway.Mutations);
    }

    [Fact]
    public void Tick_BackpackChangedBetweenChunks_InventoryMismatchBeforeTheNextTake()
    {
        Rig rig = new(source: 30);
        rig.Runner.Tick(1);
        rig.Gateway.Inventory += 1;

        rig.Runner.Tick(1);

        Assert.Equal(StopReason.InventoryMismatch, rig.Report.Reason);
        Assert.Equal(11, rig.Gateway.Mutations);
        Assert.Equal(1, rig.Report.StrandedInInventory);
    }

    // ---------------------------------------------------------------- criterion 5

    // Criterion 5: unit 1 logs no unit field and stays one take, one store
    [Fact]
    public void Tick_UnitOne_LogsNoUnitAndAlternatesTakeAndStore()
    {
        Rig rig = new(source: 3, unit: 1);

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal("Info transfer.start source=src target=dst item=312 amount=3 s0=3 t0=0 i0=0", rig.Lines("transfer.start").Single());
        Assert.All(rig.Log.Named("transfer.take"), entry => Assert.False(entry.Fields.ContainsKey("unit")));
        Assert.Equal(
            new[]
            {
                "Info transfer.take n=1 src_before=3 src_after=2 inv_before=0 inv_after=1 verify=ok",
                "Info transfer.store n=1 dst_before=0 dst_after=1 inv_before=1 inv_after=0 verify=ok",
                "Info transfer.take n=2 src_before=2 src_after=1 inv_before=0 inv_after=1 verify=ok",
                "Info transfer.store n=2 dst_before=1 dst_after=2 inv_before=1 inv_after=0 verify=ok",
                "Info transfer.take n=3 src_before=1 src_after=0 inv_before=0 inv_after=1 verify=ok",
                "Info transfer.store n=3 dst_before=2 dst_after=3 inv_before=1 inv_after=0 verify=ok",
            },
            rig.Lines("transfer.take", "transfer.store"));
        Assert.Equal(
            new[] { "TakeOne(src)", "StoreOne(dst)", "TakeOne(src)", "StoreOne(dst)", "TakeOne(src)", "StoreOne(dst)" },
            rig.Mutations());
        Assert.Equal(1, rig.MaxInventory);
    }

    // Criterion 5: unit-1 store refused strands exactly one, never passing through StoringQueue
    [Fact]
    public void Tick_UnitOneStoreRefused_StrandsOneAndNeverEntersStoringQueue()
    {
        Rig rig = new(source: 3, unit: 1);
        List<TransferState> states = new();
        rig.Hook = _ => states.Add(rig.Runner.State);
        rig.Gateway.StoreBehavior = call => call == 2 ? GatewayOutcome.Refused("full") : null;

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.StoreRefused, rig.Report.Reason);
        Assert.Equal(1, rig.Report.Moved);
        Assert.Equal(1, rig.Report.StrandedInInventory);
        Assert.Equal(1, rig.Report.Remaining);
        Assert.DoesNotContain(TransferState.StoringQueue, states);
        rig.AssertReportAddsUp();
    }

    // The unit may change between planning and later chunks only in the game; the runner fixes it once: a later GetTransferUnit change is ignored
    [Fact]
    public void Tick_TransferUnitChangesAfterPlanning_RunnerKeepsThePlannedUnit()
    {
        Rig rig = new(source: 30);
        rig.Runner.Tick(1);
        rig.Gateway.TransferUnit = 5;

        rig.Runner.Tick(100);

        // The fake now takes 5 per take, which the runner (planned at 10) must refuse to accept as a chunk.
        Assert.Equal(StopReason.SourceMismatch, rig.Report.Reason);
        Assert.Equal(10, rig.Report.Moved);
        Assert.Equal(5, rig.Report.StrandedInInventory);
    }
}
