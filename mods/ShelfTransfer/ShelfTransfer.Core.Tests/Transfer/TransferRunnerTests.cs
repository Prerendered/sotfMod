// purpose: pins the TransferRunner state machine (F2): happy path, clamp, blocked start, cancel only between cycles, tick budget, shelf-gone, conservation
// owns:    TransferRunnerTests
// deps:    TransferRunner, FakeShelfGateway, FakeModLog
// caution: no test pins card-7 fault semantics (stop reason, throw handling); fault tests assert only invariants that must survive card 7

using System;
using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Logging;
using ShelfTransfer.Core.Tests.Fakes;
using ShelfTransfer.Core.Transfer;

using Xunit;

namespace ShelfTransfer.Core.Tests.Transfer;

public sealed class TransferRunnerTests
{
    private static readonly ShelfRef _src = new("src", "StorageShelf", default);
    private static readonly ShelfRef _dst = new("dst", "StorageShelf", default);
    private static readonly ItemId _item = new(312);

    private static Rig Build(int source, int target = 0, int amount = AmountClamp.All, int? targetCapacity = null)
    {
        FakeShelfGateway gateway = new();
        gateway.Add(_src, source);
        FakeShelf dst = gateway.Add(_dst, target);
        if (targetCapacity is int capacity)
        {
            dst.Capacity = capacity;
        }

        FakeModLog log = new();
        TransferRunner runner = new(gateway, log, new TransferRequest(_src, new SlotRef(null), _dst, _item, amount));
        return new Rig(gateway, log, runner, source, source + target);
    }

    private static List<string> Mutating(FakeShelfGateway gateway) =>
        gateway.Calls.Where(c => c.StartsWith("TakeOne", StringComparison.Ordinal) || c.StartsWith("StoreOne", StringComparison.Ordinal)).ToList();

    private sealed record Rig(FakeShelfGateway Gateway, FakeModLog Log, TransferRunner Runner, int SourceStart, int Start)
    {
        public int Source => Gateway.Shelf(_src).Count;

        public int Target => Gateway.Shelf(_dst).Count;

        public void AssertConserved() => Assert.Equal(Start, Gateway.Total(_src, _dst));

        public void AssertReportAddsUp()
        {
            TransferReport report = Assert.IsType<TransferReport>(Runner.Report);
            Assert.Equal(SourceStart, report.Moved + report.Remaining + report.StrandedInInventory);
        }
    }

    // Criterion 1
    [Fact]
    public void Tick_SixOnSourceRequestAll_FinishesCompletedWithFullReport()
    {
        Rig rig = Build(source: 6);

        rig.Runner.Tick(100);

        Assert.Equal(TransferState.Finished, rig.Runner.State);
        TransferReport report = Assert.IsType<TransferReport>(rig.Runner.Report);
        Assert.Equal(StopReason.Completed, report.Reason);
        Assert.Equal(6, report.Requested);
        Assert.Equal(6, report.Moved);
        Assert.Equal(0, report.Remaining);
        Assert.Equal(0, report.StrandedInInventory);
        Assert.Equal(0, rig.Source);
        Assert.Equal(6, rig.Target);
        Assert.Equal(6, rig.Runner.Moved);
        Assert.Equal(6, rig.Runner.Requested);
    }

    // Criterion 1
    [Fact]
    public void Tick_HappyPath_MutationsStrictlyAlternateTakeStore()
    {
        Rig rig = Build(source: 6);

        rig.Runner.Tick(100);

        List<string> mutations = Mutating(rig.Gateway);
        Assert.Equal(12, mutations.Count);
        for (int i = 0; i < mutations.Count; i++)
        {
            Assert.Equal(i % 2 == 0 ? "TakeOne(src)" : "StoreOne(dst)", mutations[i]);
        }
    }

    // Criterion 1
    [Fact]
    public void Tick_HappyPath_CountReadBetweenEveryMutationPair()
    {
        Rig rig = Build(source: 6);

        rig.Runner.Tick(100);

        List<string> calls = rig.Gateway.Calls;
        int[] mutationIndexes = Enumerable.Range(0, calls.Count)
            .Where(i => calls[i].StartsWith("TakeOne", StringComparison.Ordinal) || calls[i].StartsWith("StoreOne", StringComparison.Ordinal))
            .ToArray();
        for (int m = 0; m + 1 < mutationIndexes.Length; m++)
        {
            string between = string.Join(",", calls.Skip(mutationIndexes[m] + 1).Take(mutationIndexes[m + 1] - mutationIndexes[m] - 1));
            Assert.Contains("GetCount(", between);
        }
    }

    // Criterion 1 (log lines, F4)
    [Fact]
    public void Tick_HappyPath_LogsStartTakeStoreStopWithF4Fields()
    {
        Rig rig = Build(source: 6);

        rig.Runner.Tick(100);

        LogEntry start = Assert.Single(rig.Log.Named("transfer.start"));
        Assert.Equal(LogLevel.Info, start.Level);
        Assert.Equal("src", start.Text("source"));
        Assert.Equal("dst", start.Text("target"));
        Assert.Equal("312", start.Text("item"));
        Assert.Equal("6", start.Text("amount"));
        Assert.Equal("6", start.Text("s0"));
        Assert.Equal("0", start.Text("t0"));
        Assert.Equal("0", start.Text("i0"));

        List<LogEntry> takes = rig.Log.Named("transfer.take").ToList();
        List<LogEntry> stores = rig.Log.Named("transfer.store").ToList();
        Assert.Equal(6, takes.Count);
        Assert.Equal(6, stores.Count);
        Assert.Equal(("1", "6", "5", "0", "1", "ok"), (takes[0].Text("n"), takes[0].Text("src_before"), takes[0].Text("src_after"), takes[0].Text("inv_before"), takes[0].Text("inv_after"), takes[0].Text("verify")));
        Assert.Equal(("1", "0", "1", "1", "0", "ok"), (stores[0].Text("n"), stores[0].Text("dst_before"), stores[0].Text("dst_after"), stores[0].Text("inv_before"), stores[0].Text("inv_after"), stores[0].Text("verify")));
        Assert.Equal("6", takes[5].Text("n"));

        LogEntry stop = Assert.Single(rig.Log.Named("transfer.stop"));
        Assert.Equal(LogLevel.Info, stop.Level);
        Assert.Equal("Completed", stop.Text("reason"));
        Assert.Equal("6", stop.Text("moved"));
        Assert.Equal("0", stop.Text("remaining"));
        Assert.Equal("0", stop.Text("stranded"));
        Assert.Equal("transfer.stop", rig.Log.Entries[^1].Event);
    }

    // Criterion 2
    [Fact]
    public void Tick_RequestThreeOfSix_MovesThreeLeavesThree()
    {
        Rig rig = Build(source: 6, amount: 3);

        rig.Runner.Tick(100);

        TransferReport report = Assert.IsType<TransferReport>(rig.Runner.Report);
        Assert.Equal(StopReason.Completed, report.Reason);
        Assert.Equal(3, report.Requested);
        Assert.Equal(3, report.Moved);
        Assert.Equal(3, report.Remaining);
        Assert.Equal(0, report.StrandedInInventory);
        Assert.Equal(3, rig.Source);
        Assert.Equal(3, rig.Target);
        Assert.Equal(3, rig.Gateway.TakeCalls);
    }

    // Criterion 3
    [Fact]
    public void Tick_TargetFreeFourRequestSix_ClampsToFourBeforeFirstTake()
    {
        Rig rig = Build(source: 6, targetCapacity: 4);
        int? requestedAtFirstTake = null;
        rig.Gateway.OnCall = call =>
        {
            if (call == "TakeOne(src)" && requestedAtFirstTake is null)
            {
                requestedAtFirstTake = rig.Runner.Requested;
            }
        };

        rig.Runner.Tick(100);

        Assert.Equal(4, requestedAtFirstTake);
        TransferReport report = Assert.IsType<TransferReport>(rig.Runner.Report);
        Assert.Equal(StopReason.Completed, report.Reason);
        Assert.Equal(4, report.Requested);
        Assert.Equal(4, report.Moved);
        Assert.Equal(2, report.Remaining);
        Assert.Equal(4, rig.Gateway.TakeCalls);
        Assert.Equal(4, rig.Gateway.StoreCalls);
        Assert.Equal(4, rig.Target);
    }

    // Criterion 3 (ExplicitAmountAbovefree)
    [Fact]
    public void Tick_ExplicitAmountAboveTargetFree_ClampsToFree()
    {
        Rig rig = Build(source: 9, target: 1, amount: 8, targetCapacity: 4);

        rig.Runner.Tick(100);

        Assert.Equal(3, rig.Runner.Report!.Requested);
        Assert.Equal(3, rig.Runner.Report.Moved);
        Assert.Equal(4, rig.Target);
        rig.AssertConserved();
    }

    // Criterion 4
    [Fact]
    public void Tick_TargetCannotHold_BlockedBeforeStartWithZeroMutations()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.Shelf(_dst).CanHold = false;

        rig.Runner.Tick(10);

        Assert.Equal(TransferState.Finished, rig.Runner.State);
        TransferReport report = Assert.IsType<TransferReport>(rig.Runner.Report);
        Assert.Equal(StopReason.BlockedBeforeStart, report.Reason);
        Assert.Equal(0, report.Requested);
        Assert.Equal(0, report.Moved);
        Assert.Equal(6, report.Remaining);
        Assert.Equal(0, rig.Gateway.Mutations);
        Assert.Equal("BlockedBeforeStart", Assert.Single(rig.Log.Named("transfer.stop")).Text("reason"));
    }

    // Criterion 4
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(6, 6, 6)]
    [InlineData(6, 0, 0)]
    [InlineData(6, 0, -1)]
    [InlineData(6, 0, int.MinValue)]
    public void Tick_AmountClampsToZero_BlockedBeforeStartWithZeroMutations(int source, int targetCount, int amount)
    {
        // Row 2: target exactly full. Rows 3-5: invalid amount. Row 1: empty source.
        Rig rig = Build(source: source, target: targetCount, amount: amount, targetCapacity: source == 6 && targetCount == 6 ? 6 : null);

        rig.Runner.Tick(10);

        Assert.Equal(StopReason.BlockedBeforeStart, rig.Runner.Report!.Reason);
        Assert.Equal(0, rig.Runner.Report.Requested);
        Assert.Equal(0, rig.Gateway.Mutations);
        Assert.Equal(source, rig.Runner.Report.Remaining);
        rig.AssertConserved();
    }

    // Card 68 criterion 5: the gateway reports unit 0 for an emptied slot, as the plugin does; the count reason must win.
    [Fact]
    public void Tick_SourceEmptiedBeforePlanningWithUnitTen_BlocksNothingToMoveNotUnitUnknown()
    {
        Rig rig = Build(source: 0);
        rig.Gateway.TransferUnit = 10;

        rig.Runner.Tick(10);

        Assert.Equal(StopReason.BlockedBeforeStart, rig.Runner.Report!.Reason);
        Assert.Equal("NothingToMove", rig.Runner.Report.Detail);
        Assert.Equal(0, rig.Gateway.Mutations);
        Assert.False(Assert.Single(rig.Log.Named("transfer.start")).Fields.ContainsKey("unit"));
    }

    // Card 68: a stocked source whose unit the gateway cannot read blocks rather than take a chunk blind (I1).
    [Fact]
    public void Tick_UnitUnknownOnStockedSource_BlocksBeforeAnyTake()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.TransferUnit = 0;

        rig.Runner.Tick(10);

        Assert.Equal(StopReason.BlockedBeforeStart, rig.Runner.Report!.Reason);
        Assert.Equal("transfer unit unknown", rig.Runner.Report.Detail);
        Assert.Equal(0, rig.Runner.Report.Requested);
        Assert.Equal(0, rig.Gateway.Mutations);
    }

    // Criterion 5
    [Theory]
    [InlineData("TakeOne(src)", 3)]
    [InlineData("StoreOne(dst)", 3)]
    public void RequestCancel_RaisedMidCycle_HonouredOnlyAfterInFlightStoreCompletes(string raiseOn, int cycleNumber)
    {
        Rig rig = Build(source: 6);
        int seen = 0;
        rig.Gateway.OnCall = call =>
        {
            if (call == raiseOn && ++seen == cycleNumber)
            {
                rig.Runner.RequestCancel();
            }
        };

        rig.Runner.Tick(100);

        TransferReport report = Assert.IsType<TransferReport>(rig.Runner.Report);
        Assert.Equal(StopReason.Cancelled, report.Reason);
        Assert.Equal(3, report.Moved);
        Assert.Equal(6, report.Requested);
        Assert.Equal(3, report.Remaining);
        Assert.Equal(0, report.StrandedInInventory);
        Assert.Equal(3, rig.Gateway.TakeCalls);
        Assert.Equal(3, rig.Gateway.StoreCalls);
        Assert.Equal(3, rig.Source);
        Assert.Equal(3, rig.Target);
        Assert.Equal(0, rig.Gateway.Inventory);
        Assert.Equal("Cancelled", Assert.Single(rig.Log.Named("transfer.stop")).Text("reason"));
    }

    // Criterion 5
    [Fact]
    public void RequestCancel_RaisedBetweenTicks_FinishesAtNextTickWithoutFurtherMutation()
    {
        Rig rig = Build(source: 6);
        rig.Runner.Tick(2);
        int mutationsBefore = rig.Gateway.Mutations;

        rig.Runner.RequestCancel();
        rig.Runner.Tick(5);

        Assert.Equal(StopReason.Cancelled, rig.Runner.Report!.Reason);
        Assert.Equal(2, rig.Runner.Report.Moved);
        Assert.Equal(mutationsBefore, rig.Gateway.Mutations);
    }

    // Criterion 6
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void Tick_BudgetN_RunsAtMostNFullCycles(int budget)
    {
        Rig rig = Build(source: 6);

        rig.Runner.Tick(budget);

        Assert.Equal(budget, rig.Runner.Moved);
        Assert.Equal(budget, rig.Gateway.TakeCalls);
        Assert.Equal(budget, rig.Gateway.StoreCalls);
        Assert.Equal(TransferState.ReadyForNext, rig.Runner.State);
        Assert.Null(rig.Runner.Report);
        Assert.Equal(0, rig.Gateway.Inventory);
        rig.AssertConserved();
    }

    // Criterion 6
    [Fact]
    public void Tick_AnyTickBoundary_NeverSplitsATakeFromItsStore()
    {
        Rig rig = Build(source: 6);

        while (rig.Runner.State != TransferState.Finished)
        {
            rig.Runner.Tick(1);
            Assert.Equal(rig.Gateway.TakeCalls, rig.Gateway.StoreCalls);
            Assert.Equal(0, rig.Gateway.Inventory);
            Assert.NotEqual(TransferState.TakenAwaitingVerify, rig.Runner.State);
            Assert.NotEqual(TransferState.StoredAwaitingVerify, rig.Runner.State);
        }
    }

    // Criterion 6
    [Fact]
    public void Tick_PlanningDoesNotConsumeTheCycleBudget()
    {
        Rig rig = Build(source: 6);

        rig.Runner.Tick(1);

        Assert.Equal(1, rig.Runner.Moved);
        Assert.Equal(6, rig.Runner.Requested);
    }

    // Criterion 6
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Tick_BudgetBelowOne_Throws(int budget)
    {
        Rig rig = Build(source: 6);

        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Runner.Tick(budget));
        Assert.Empty(rig.Gateway.Calls);
    }

    // Criterion 6 (boundaries)
    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(int.MaxValue)]
    public void Tick_BudgetAtOrAboveNeeded_FinishesInThatTick(int budget)
    {
        Rig rig = Build(source: 6);

        rig.Runner.Tick(budget);

        Assert.Equal(TransferState.Finished, rig.Runner.State);
        Assert.Equal(StopReason.Completed, rig.Runner.Report!.Reason);
        Assert.Equal(6, rig.Gateway.TakeCalls);
    }

    // Criterion 7
    [Theory]
    [InlineData("src")]
    [InlineData("dst")]
    public void Tick_ShelfDestroyedMidRun_FinishesShelfGoneAtReadyForNext(string victim)
    {
        Rig rig = Build(source: 6);
        rig.Gateway.OnCall = call =>
        {
            if (call == "StoreOne(dst)" && rig.Gateway.StoreCalls == 2)
            {
                rig.Gateway.Shelf(victim == "src" ? _src : _dst).IsAlive = false;
            }
        };

        rig.Runner.Tick(100);

        TransferReport report = Assert.IsType<TransferReport>(rig.Runner.Report);
        Assert.Equal(StopReason.ShelfGone, report.Reason);
        Assert.Equal(2, report.Moved);
        Assert.Equal(2, rig.Gateway.TakeCalls);
        Assert.Equal(2, rig.Gateway.StoreCalls);
        Assert.Equal(0, report.StrandedInInventory);
        rig.AssertReportAddsUp();
    }

    // Criterion 7
    [Theory]
    [InlineData("src")]
    [InlineData("dst")]
    public void Tick_ShelfDeadBeforeStart_FinishesShelfGoneWithZeroMutations(string victim)
    {
        Rig rig = Build(source: 6);
        rig.Gateway.Shelf(victim == "src" ? _src : _dst).IsAlive = false;

        rig.Runner.Tick(10);

        Assert.Equal(StopReason.ShelfGone, rig.Runner.Report!.Reason);
        Assert.Equal(0, rig.Gateway.Mutations);
        Assert.Equal(0, rig.Runner.Report.Moved);
    }

    // Attack: I1 conservation after every Tick
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Tick_AfterEveryTick_SourcePlusTargetPlusInventoryEqualsStart(int budget)
    {
        Rig rig = Build(source: 7, target: 2);

        while (rig.Runner.State != TransferState.Finished)
        {
            rig.Runner.Tick(budget);
            rig.AssertConserved();
        }

        rig.AssertReportAddsUp();
    }

    // Attack: cancel timing
    [Fact]
    public void RequestCancel_BeforeFirstTick_CancelsWithZeroMutations()
    {
        Rig rig = Build(source: 6);

        rig.Runner.RequestCancel();
        rig.Runner.Tick(5);

        Assert.Equal(StopReason.Cancelled, rig.Runner.Report!.Reason);
        Assert.Equal(0, rig.Runner.Report.Moved);
        Assert.Equal(0, rig.Gateway.Mutations);
        rig.AssertConserved();
    }

    // Attack: cancel after finish
    [Fact]
    public void RequestCancel_AfterFinished_LeavesReportUntouched()
    {
        Rig rig = Build(source: 2);
        rig.Runner.Tick(10);
        TransferReport before = rig.Runner.Report!;
        int callsBefore = rig.Gateway.Calls.Count;

        rig.Runner.RequestCancel();
        rig.Runner.Tick(10);

        Assert.Same(before, rig.Runner.Report);
        Assert.Equal(StopReason.Completed, rig.Runner.Report!.Reason);
        Assert.Equal(callsBefore, rig.Gateway.Calls.Count);
    }

    // Attack: Tick after Finished
    [Fact]
    public void Tick_AfterFinished_IsNoOp()
    {
        Rig rig = Build(source: 3);
        rig.Runner.Tick(10);
        int callsBefore = rig.Gateway.Calls.Count;
        int logsBefore = rig.Log.Entries.Count;

        rig.Runner.Tick(10);

        Assert.Equal(callsBefore, rig.Gateway.Calls.Count);
        Assert.Equal(logsBefore, rig.Log.Entries.Count);
        Assert.Equal(TransferState.Finished, rig.Runner.State);
    }

    // Attack: initial state
    [Fact]
    public void NewRunner_BeforeTick_IsPlanningWithNoReportAndNoGatewayCalls()
    {
        Rig rig = Build(source: 3);

        Assert.Equal(TransferState.Planning, rig.Runner.State);
        Assert.Equal(0, rig.Runner.Requested);
        Assert.Equal(0, rig.Runner.Moved);
        Assert.Null(rig.Runner.Report);
        Assert.Empty(rig.Gateway.Calls);
    }

    // Attack: int.MaxValue request / All sentinel
    [Fact]
    public void Tick_IntMaxValueRequest_ClampsToSourceAndDoesNotOverflow()
    {
        Rig rig = Build(source: 5, amount: int.MaxValue);

        rig.Runner.Tick(100);

        Assert.Equal(5, rig.Runner.Report!.Requested);
        Assert.Equal(5, rig.Runner.Report.Moved);
        rig.AssertConserved();
    }

    // Attack: target exactly full after the run (boundary), then item amount 1
    [Theory]
    [InlineData(6, 0, 6, 6)]
    [InlineData(1, 0, 1, 1)]
    [InlineData(1, 0, 100, 1)]
    public void Tick_TargetFreeEqualsOrExceedsSource_MovesEverything(int source, int target, int capacity, int expectedMoved)
    {
        Rig rig = Build(source, target, amount: 1 == source ? 1 : AmountClamp.All, targetCapacity: capacity);

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.Completed, rig.Runner.Report!.Reason);
        Assert.Equal(expectedMoved, rig.Runner.Report.Moved);
        Assert.Equal(expectedMoved, rig.Target);
        rig.AssertConserved();
    }

    // Attack: target with prior stock is not counted as moved
    [Fact]
    public void Tick_TargetAlreadyHoldsItem_MovedCountsOnlyThisRun()
    {
        Rig rig = Build(source: 4, target: 10);

        rig.Runner.Tick(100);

        Assert.Equal(4, rig.Runner.Report!.Moved);
        Assert.Equal(14, rig.Target);
        Assert.Equal("10", Assert.Single(rig.Log.Named("transfer.start")).Text("t0"));
    }

    // Attack: inventory already holding the item must not read as stranded
    [Fact]
    public void Tick_InventoryAlreadyHoldsItem_ReportsZeroStranded()
    {
        Rig rig = Build(source: 3);
        rig.Gateway.Inventory = 5;

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.Completed, rig.Runner.Report!.Reason);
        Assert.Equal(0, rig.Runner.Report.StrandedInInventory);
        Assert.Equal(5, rig.Gateway.Inventory);
        Assert.Equal("5", Assert.Single(rig.Log.Named("transfer.start")).Text("i0"));
    }

    // Invariants that must survive card 7: a fault stops all further mutation and the report is read fresh.
    [Fact]
    public void Tick_TakeRefusedMidRun_StopsWithNothingStranded()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.TakeBehavior = n => n == 3 ? GatewayOutcome.Refused("carry cap") : null;

        rig.Runner.Tick(100);

        TransferReport report = Assert.IsType<TransferReport>(rig.Runner.Report);
        Assert.Equal(TransferState.Finished, rig.Runner.State);
        Assert.NotEqual(StopReason.Completed, report.Reason);
        Assert.Equal(2, report.Moved);
        Assert.Equal(4, report.Remaining);
        Assert.Equal(0, report.StrandedInInventory);
        Assert.Equal(3, rig.Gateway.TakeCalls);
        Assert.Equal(2, rig.Gateway.StoreCalls);
        rig.AssertReportAddsUp();
    }

    [Fact]
    public void Tick_StoreRefusedAfterTake_ReportsOneStrandedAndStopsMutating()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.StoreBehavior = n => n == 3 ? GatewayOutcome.Refused("target full") : null;

        rig.Runner.Tick(100);

        TransferReport report = Assert.IsType<TransferReport>(rig.Runner.Report);
        Assert.NotEqual(StopReason.Completed, report.Reason);
        Assert.Equal(2, report.Moved);
        Assert.Equal(3, report.Remaining);
        Assert.Equal(1, report.StrandedInInventory);
        Assert.Equal(3, rig.Gateway.TakeCalls);
        Assert.Equal(3, rig.Gateway.StoreCalls);
        rig.AssertReportAddsUp();
        rig.Runner.Tick(10);
        Assert.Equal(3, rig.Gateway.TakeCalls);
        Assert.Equal(3, rig.Gateway.StoreCalls);
    }

    [Fact]
    public void Tick_StoreReportsOkButNothingMoved_StopsAndReportsStrandedFromFreshRead()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.StoreBehavior = n => n == 2 ? GatewayOutcome.Ok : null;

        rig.Runner.Tick(100);

        TransferReport report = Assert.IsType<TransferReport>(rig.Runner.Report);
        Assert.NotEqual(StopReason.Completed, report.Reason);
        Assert.Equal(1, report.StrandedInInventory);
        Assert.Equal(2, rig.Gateway.TakeCalls);
        Assert.Equal(2, rig.Gateway.StoreCalls);
        rig.AssertReportAddsUp();
    }

    [Fact]
    public void Tick_TakeReportsOkButSourceUnchanged_StopsBeforeStore()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.TakeBehavior = n => n == 2 ? GatewayOutcome.Ok : null;

        rig.Runner.Tick(100);

        TransferReport report = Assert.IsType<TransferReport>(rig.Runner.Report);
        Assert.NotEqual(StopReason.Completed, report.Reason);
        Assert.Equal(1, report.Moved);
        Assert.Equal(0, report.StrandedInInventory);
        Assert.Equal(2, rig.Gateway.TakeCalls);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        rig.AssertReportAddsUp();
    }

    [Fact]
    public void Tick_TakeRemovesTwoFromSource_StopsWithoutStoringAndNeverReportsMoreThanOneStranded()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.TakeBehavior = n =>
        {
            if (n != 2)
            {
                return null;
            }

            rig.Gateway.ApplyTake(_src);
            return rig.Gateway.ApplyTake(_src);
        };

        rig.Runner.Tick(100);

        TransferReport report = Assert.IsType<TransferReport>(rig.Runner.Report);
        Assert.NotEqual(StopReason.Completed, report.Reason);
        Assert.Equal(StopReason.SourceMismatch, report.Reason);
        Assert.Equal(1, report.StrandedInInventory);
        Assert.Equal(2, rig.Gateway.TakeCalls);
        Assert.Equal(1, rig.Gateway.StoreCalls);
    }
}
