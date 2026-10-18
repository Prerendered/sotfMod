// purpose: pins card-7 failure semantics of TransferRunner (F2): precise stop reasons, no retry, abort from any state, guarded final re-reads, conservation
// owns:    TransferRunnerFailureTests
// deps:    TransferRunner, FakeShelfGateway, FakeModLog
// caution: faults are injected through FakeShelfGateway.OnCall/TakeBehavior/StoreBehavior; "frozen" means zero gateway calls after a stop, except Abort's own three re-reads

using System;
using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Logging;
using ShelfTransfer.Core.Tests.Fakes;
using ShelfTransfer.Core.Transfer;

using Xunit;

namespace ShelfTransfer.Core.Tests.Transfer;

public sealed class TransferRunnerFailureTests
{
    private static readonly ShelfRef _src = new("src", "StorageShelf", default);
    private static readonly ShelfRef _dst = new("dst", "StorageShelf", default);
    private static readonly ItemId _item = new(312);

    private static Rig Build(int source, int inventory = 0, int target = 0)
    {
        FakeShelfGateway gateway = new() { Inventory = inventory };
        gateway.Add(_src, source);
        gateway.Add(_dst, target);
        FakeModLog log = new();
        TransferRunner runner = new(gateway, log, new TransferRequest(_src, new SlotRef(null, null), _dst, _item, AmountClamp.All));
        return new Rig(gateway, log, runner, source);
    }

    private sealed record Rig(FakeShelfGateway Gateway, FakeModLog Log, TransferRunner Runner, int S0)
    {
        public TransferReport Report => Assert.IsType<TransferReport>(Runner.Report);

        public bool HasReportMismatchWarn => Log.Named("conservation.mismatch").Any(e => e.Level == LogLevel.Warn && e.Text("check") == "report");

        /// <summary>Invariant 1: the numbers add up, or the runner said so at WARN.</summary>
        public void AssertI1OrWarned()
        {
            TransferReport r = Report;
            bool isConserved = S0 == r.Remaining + r.Moved + r.StrandedInInventory;
            Assert.True(isConserved || HasReportMismatchWarn, $"I1 broken silently: S0={S0} report={r}");
            Assert.Equal(!isConserved, HasReportMismatchWarn);
            Assert.InRange(r.StrandedInInventory, 0, 1);
        }

        public void AssertAddsUp()
        {
            TransferReport r = Report;
            Assert.Equal(S0, r.Remaining + r.Moved + r.StrandedInInventory);
            Assert.False(HasReportMismatchWarn);
        }

        /// <summary>Nothing moves and nothing is called after a stop, and the first report stands.</summary>
        public void AssertFrozen()
        {
            Assert.Equal(TransferState.Finished, Runner.State);
            TransferReport first = Report;
            int calls = Gateway.Calls.Count;
            int mutations = Gateway.Mutations;

            Runner.Tick(100);
            Runner.RequestCancel();
            Runner.Tick(1);

            Assert.Equal(calls, Gateway.Calls.Count);
            Assert.Equal(mutations, Gateway.Mutations);
            Assert.Same(first, Runner.Report);
        }
    }

    private static InvalidOperationException Boom() => new("boom");

    // Throws once on the first call matching the predicate.
    private static void ThrowOnce(Rig rig, Func<string, bool> when)
    {
        bool isFired = false;
        rig.Gateway.OnCall = entry =>
        {
            if (!isFired && when(entry))
            {
                isFired = true;
                throw Boom();
            }
        };
    }

    // Throws on the matching call, then on every read afterwards (mutations are never reached once stopped).
    private static void ThrowThenBreak(Rig rig, Func<string, bool> when)
    {
        bool isBroken = false;
        rig.Gateway.OnCall = entry =>
        {
            if (!isBroken && when(entry))
            {
                isBroken = true;
                throw Boom();
            }

            if (isBroken && !entry.StartsWith("TakeOne", StringComparison.Ordinal) && !entry.StartsWith("StoreOne", StringComparison.Ordinal))
            {
                throw Boom();
            }
        };
    }

    private static void AssertLastCallsAreFinishRereads(FakeShelfGateway gateway)
    {
        List<string> tail = gateway.Calls.TakeLast(3).ToList();
        Assert.Equal(new[] { "IsAlive(src)", "GetCount(src)", "GetInventoryCount" }, tail);
    }

    // Criterion 1
    [Fact]
    public void Tick_TargetFillsMidRun_StoreRefusedWithOneStrandedAndNoFurtherTake()
    {
        Rig rig = Build(source: 5);
        rig.Gateway.OnCall = entry =>
        {
            if (entry == "TakeOne(src)" && rig.Gateway.TakeCalls == 2)
            {
                rig.Gateway.Shelf(_dst).Capacity = 1;
            }
        };

        rig.Runner.Tick(100);

        TransferReport r = rig.Report;
        Assert.Equal(StopReason.StoreRefused, r.Reason);
        Assert.Equal(1, r.Moved);
        Assert.Equal(1, r.StrandedInInventory);
        Assert.Equal(3, r.Remaining);
        Assert.Equal(2, rig.Gateway.TakeCalls);
        Assert.Equal(2, rig.Gateway.StoreCalls);
        Assert.Equal(1, rig.Gateway.Inventory);
        rig.AssertAddsUp();
        rig.AssertFrozen();
    }

    // Criterion 1, 7
    [Fact]
    public void Tick_StoreRefusedOnFirstItem_StopsAtOnceWithStrandedOneAndNoRetry()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.StoreBehavior = _ => GatewayOutcome.Refused("full");

        rig.Runner.Tick(100);

        TransferReport r = rig.Report;
        Assert.Equal(StopReason.StoreRefused, r.Reason);
        Assert.Equal(0, r.Moved);
        Assert.Equal(1, r.StrandedInInventory);
        Assert.Equal(5, r.Remaining);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        rig.AssertAddsUp();
        rig.AssertFrozen();
    }

    // Criterion 2
    [Fact]
    public void Tick_TakeOkButSourceDoesNotDrop_SourceMismatchWithNoStoreAndFreshCounts()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.TakeBehavior = _ => GatewayOutcome.Ok;

        rig.Runner.Tick(100);

        TransferReport r = rig.Report;
        Assert.Equal(StopReason.SourceMismatch, r.Reason);
        Assert.Equal(0, rig.Gateway.StoreCalls);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(0, r.Moved);
        Assert.Equal(6, r.Remaining);
        Assert.Equal(0, r.StrandedInInventory);
        rig.AssertAddsUp();
        rig.AssertFrozen();
    }

    // Criterion 2 (source is checked before inventory)
    [Fact]
    public void Tick_TakeOkSourceAndInventoryBothWrong_ReportsSourceMismatchNotInventory()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.TakeBehavior = _ =>
        {
            rig.Gateway.Inventory += 3;
            return GatewayOutcome.Ok;
        };

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.SourceMismatch, rig.Report.Reason);
        Assert.Equal(0, rig.Gateway.StoreCalls);
        rig.AssertI1OrWarned();
    }

    public static TheoryData<string, Action<FakeShelfGateway>, StopReason, string> BetweenCycleDrift() => new()
    {
        { "source loses one", g => g.Shelf(_src).Count -= 1, StopReason.SourceMismatch, "expected 5 read 4" },
        { "source gains two", g => g.Shelf(_src).Count += 2, StopReason.SourceMismatch, "expected 5 read 7" },
        { "target gains one", g => g.Shelf(_dst).Count += 1, StopReason.TargetMismatch, "expected 1 read 2" },
        { "target loses one", g => g.Shelf(_dst).Count -= 1, StopReason.TargetMismatch, "expected 1 read 0" },
        { "inventory gains one", g => g.Inventory += 1, StopReason.InventoryMismatch, "expected 0 read 1" },
        { "inventory loses one", g => g.Inventory -= 1, StopReason.InventoryMismatch, "expected 0 read -1" },
    };

    // Criterion 3, 5
    [Theory]
    [MemberData(nameof(BetweenCycleDrift))]
    public void Tick_AnotherPlayerChangesCountsBetweenCycles_StopsBeforeNextTakeWithRereadCounts(
        string label,
        Action<FakeShelfGateway> interfere,
        StopReason expected,
        string detailPart)
    {
        Assert.NotEmpty(label);
        Rig rig = Build(source: 6);
        rig.Runner.Tick(1);
        Assert.Equal(1, rig.Runner.Moved);
        interfere(rig.Gateway);

        rig.Runner.Tick(100);

        TransferReport r = rig.Report;
        Assert.Equal(expected, r.Reason);
        Assert.Contains(detailPart, r.Detail);
        Assert.Equal(1, r.Moved);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        Assert.Equal(rig.Gateway.Shelf(_src).Count, r.Remaining);
        rig.AssertI1OrWarned();
        rig.AssertFrozen();
    }

    // Criterion 3, 7: an outside change breaks conservation and the runner says so
    [Fact]
    public void Tick_AnotherPlayerRemovesFromSource_ReportStatesRereadRemainingAndWarnsConservation()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.StoreBehavior = _ =>
        {
            GatewayOutcome outcome = rig.Gateway.ApplyStore(_dst);
            rig.Gateway.Shelf(_src).Count -= 1;
            return outcome;
        };

        rig.Runner.Tick(100);

        Assert.Equal(4, rig.Report.Remaining);
        Assert.True(rig.HasReportMismatchWarn);
    }

    // Criterion 4
    [Fact]
    public void Abort_AfterGatewayThrowInTick_YieldsReportAndMakesNoFurtherGatewayCall()
    {
        Rig rig = Build(source: 6);
        ThrowOnce(rig, e => e == "TakeOne(src)" && rig.Gateway.TakeCalls == 2);

        Assert.Throws<InvalidOperationException>(() => rig.Runner.Tick(100));
        int callsAtThrow = rig.Gateway.Calls.Count;
        rig.Runner.Abort(StopReason.GatewayError, "boom");

        TransferReport r = rig.Report;
        Assert.Equal(StopReason.GatewayError, r.Reason);
        Assert.Equal("boom", r.Detail);
        Assert.Equal(1, r.Moved);
        Assert.Equal(5, r.Remaining);
        Assert.Equal(0, r.StrandedInInventory);
        Assert.Equal(callsAtThrow + 3, rig.Gateway.Calls.Count);
        AssertLastCallsAreFinishRereads(rig.Gateway);
        Assert.Equal(2, rig.Gateway.TakeCalls);
        rig.AssertAddsUp();
        rig.AssertFrozen();
    }

    // Criterion 4, 9: no Abort from the driver, next Tick must not resume
    [Fact]
    public void Tick_AfterUnabortedThrow_FinishesGatewayErrorWithoutMutatingAgain()
    {
        Rig rig = Build(source: 6);
        ThrowOnce(rig, e => e == "TakeOne(src)" && rig.Gateway.TakeCalls == 2);
        Assert.Throws<InvalidOperationException>(() => rig.Runner.Tick(100));
        int mutations = rig.Gateway.Mutations;

        rig.Runner.Tick(100);

        Assert.Equal(TransferState.Finished, rig.Runner.State);
        Assert.Equal(StopReason.GatewayError, rig.Report.Reason);
        Assert.Contains("previous tick threw during ReadyForNext", rig.Report.Detail);
        Assert.Equal(mutations, rig.Gateway.Mutations);
        rig.AssertAddsUp();
        rig.AssertFrozen();
    }

    // Criterion 4, 9: a throw during Planning, Abort before a baseline exists
    [Fact]
    public void Abort_AfterThrowDuringPlanning_ReportsFreshRemainingAndNoConservationWarn()
    {
        Rig rig = Build(source: 6);
        ThrowOnce(rig, e => e == "IsAlive(src)");
        Assert.Throws<InvalidOperationException>(() => rig.Runner.Tick(100));

        rig.Runner.Abort(StopReason.GatewayError, "plan threw");

        TransferReport r = rig.Report;
        Assert.Equal(6, r.Remaining);
        Assert.Equal(0, r.Moved);
        Assert.Equal(0, r.StrandedInInventory);
        Assert.False(rig.HasReportMismatchWarn);
        Assert.Equal(0, rig.Gateway.Mutations);
        rig.AssertFrozen();
    }

    // Criterion 5
    [Fact]
    public void Tick_TakeLowersSourceButInventoryDoesNotRise_InventoryMismatchWithNoStore()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.TakeBehavior = n =>
        {
            if (n != 2)
            {
                return null;
            }

            rig.Gateway.Shelf(_src).Count--;
            return GatewayOutcome.Ok;
        };

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.InventoryMismatch, rig.Report.Reason);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        Assert.Equal(1, rig.Report.Moved);
        Assert.Equal(0, rig.Report.StrandedInInventory);
        Assert.True(rig.HasReportMismatchWarn);
        rig.AssertI1OrWarned();
        rig.AssertFrozen();
    }

    // Criterion 5, 6: adversarial jump, stranded clamps to 1
    [Fact]
    public void Tick_InventoryJumpsByThreeOnTake_InventoryMismatchAndStrandedClampedToOne()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.TakeBehavior = n =>
        {
            if (n != 2)
            {
                return null;
            }

            GatewayOutcome outcome = rig.Gateway.ApplyTake(_src);
            rig.Gateway.Inventory += 2;
            return outcome;
        };

        rig.Runner.Tick(100);

        TransferReport r = rig.Report;
        Assert.Equal(StopReason.InventoryMismatch, r.Reason);
        Assert.Equal(1, r.StrandedInInventory);
        Assert.Equal(3, rig.Gateway.Inventory);
        Assert.Equal(2, rig.Gateway.TakeCalls);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        rig.AssertI1OrWarned();
        rig.AssertFrozen();
    }

    // Criterion 6: negative drift clamps to 0
    [Fact]
    public void Tick_InventoryFallsBelowBaselineOnTake_StrandedClampedToZero()
    {
        Rig rig = Build(source: 6, inventory: 4);
        rig.Gateway.TakeBehavior = _ =>
        {
            GatewayOutcome outcome = rig.Gateway.ApplyTake(_src);
            rig.Gateway.Inventory = 0;
            return outcome;
        };

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.InventoryMismatch, rig.Report.Reason);
        Assert.Equal(0, rig.Report.StrandedInInventory);
        rig.AssertI1OrWarned();
        rig.AssertFrozen();
    }

    // Criterion 5, 9: store Ok but target did not rise
    [Fact]
    public void Tick_StoreOkButTargetDoesNotRise_TargetMismatchWithStoreConservationWarn()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.StoreBehavior = _ => GatewayOutcome.Ok;

        rig.Runner.Tick(100);

        TransferReport r = rig.Report;
        Assert.Equal(StopReason.TargetMismatch, r.Reason);
        Assert.Equal(1, r.StrandedInInventory);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        LogEntry warn = Assert.Single(rig.Log.Named("conservation.mismatch"), e => e.Text("check") == "store");
        Assert.Equal(LogLevel.Warn, warn.Level);
        rig.AssertI1OrWarned();
        rig.AssertFrozen();
    }

    // Criterion 5, 9: store Ok, target rose, inventory did not fall
    [Fact]
    public void Tick_StoreOkTargetRisesButInventoryDoesNotFall_InventoryMismatchWithStoreConservationWarn()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.StoreBehavior = _ =>
        {
            GatewayOutcome outcome = rig.Gateway.ApplyStore(_dst);
            rig.Gateway.Inventory++;
            return outcome;
        };

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.InventoryMismatch, rig.Report.Reason);
        LogEntry warn = Assert.Single(rig.Log.Named("conservation.mismatch"), e => e.Text("check") == "store");
        Assert.Equal(LogLevel.Warn, warn.Level);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        rig.AssertI1OrWarned();
        rig.AssertFrozen();
    }

    // Criterion 6, 7
    [Fact]
    public void Abort_ReportComesFromFreshReadsNotLocalCounters_AndForcedViolationWarnsAfterStop()
    {
        Rig rig = Build(source: 6);
        ThrowOnce(rig, e => e == "TakeOne(src)" && rig.Gateway.TakeCalls == 2);
        Assert.Throws<InvalidOperationException>(() => rig.Runner.Tick(100));
        rig.Gateway.Shelf(_src).Count = 2;
        rig.Gateway.Inventory = 1;

        rig.Runner.Abort(StopReason.GatewayError, "boom");

        TransferReport r = rig.Report;
        Assert.Equal(2, r.Remaining);
        Assert.Equal(1, r.StrandedInInventory);
        Assert.Equal(1, r.Moved);
        Assert.True(rig.HasReportMismatchWarn);
        int stop = rig.Log.Entries.FindIndex(e => e.Event == "transfer.stop");
        int mismatch = rig.Log.Entries.FindIndex(e => e.Event == "conservation.mismatch");
        Assert.True(stop >= 0 && mismatch > stop);
        LogEntry entry = rig.Log.Entries[mismatch];
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Equal("report", entry.Text("check"));
        Assert.Equal("2", entry.Text("remaining"));
    }

    // Criterion 6: Finish reads exactly the three live values, last
    [Fact]
    public void Tick_SourceMismatchFinish_RereadsSourceAliveCountAndInventoryAsLastThreeCalls()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.TakeBehavior = _ => GatewayOutcome.Ok;

        rig.Runner.Tick(100);

        AssertLastCallsAreFinishRereads(rig.Gateway);
    }

    // Criterion 7: conservation holds on a clean stop without any warning
    [Fact]
    public void Tick_ConservedFailure_EmitsNoConservationMismatch()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.StoreBehavior = _ => GatewayOutcome.Refused("full");

        rig.Runner.Tick(100);

        Assert.Empty(rig.Log.Named("conservation.mismatch"));
    }

    // Criterion 8
    [Fact]
    public void Tick_TakeRefusedOnThirdItem_TakeRefusedWithNothingMovedThatCycle()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.TakeBehavior = n => n == 3 ? GatewayOutcome.Refused("carry cap") : null;

        rig.Runner.Tick(100);

        TransferReport r = rig.Report;
        Assert.Equal(StopReason.TakeRefused, r.Reason);
        Assert.Equal(2, r.Moved);
        Assert.Equal(4, r.Remaining);
        Assert.Equal(0, r.StrandedInInventory);
        Assert.Equal(3, rig.Gateway.TakeCalls);
        Assert.Equal(2, rig.Gateway.StoreCalls);
        Assert.Contains("carry cap", r.Detail);
        rig.AssertAddsUp();
        rig.AssertFrozen();
    }

    // Criterion 8
    [Fact]
    public void Tick_TakeFailed_GatewayErrorWithNothingMoved()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.TakeBehavior = n => n == 2 ? GatewayOutcome.Failed("object destroyed") : null;

        rig.Runner.Tick(100);

        TransferReport r = rig.Report;
        Assert.Equal(StopReason.GatewayError, r.Reason);
        Assert.Equal(1, r.Moved);
        Assert.Equal(5, r.Remaining);
        Assert.Equal(0, r.StrandedInInventory);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        rig.AssertAddsUp();
        rig.AssertFrozen();
    }

    // Criterion 8
    [Fact]
    public void Tick_StoreFailed_GatewayErrorWithOneStranded()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.StoreBehavior = n => n == 2 ? GatewayOutcome.Failed("object destroyed") : null;

        rig.Runner.Tick(100);

        TransferReport r = rig.Report;
        Assert.Equal(StopReason.GatewayError, r.Reason);
        Assert.Equal(1, r.Moved);
        Assert.Equal(4, r.Remaining);
        Assert.Equal(1, r.StrandedInInventory);
        Assert.Equal(2, rig.Gateway.TakeCalls);
        rig.AssertAddsUp();
        rig.AssertFrozen();
    }

    // Criterion 8: refusals are precise, not the interim GatewayError
    [Fact]
    public void Tick_RefusedTakeAndRefusedStore_AreNotCollapsedIntoGatewayError()
    {
        Rig take = Build(source: 3);
        take.Gateway.TakeBehavior = _ => GatewayOutcome.Refused("x");
        take.Runner.Tick(10);
        Rig store = Build(source: 3);
        store.Gateway.StoreBehavior = _ => GatewayOutcome.Refused("x");
        store.Runner.Tick(10);

        Assert.Equal(StopReason.TakeRefused, take.Report.Reason);
        Assert.Equal(StopReason.StoreRefused, store.Report.Reason);
    }

    // Criterion 9
    [Fact]
    public void Abort_FromTakenAwaitingVerify_ReportsFreshCountsWithOneStranded()
    {
        Rig rig = Build(source: 6);
        ThrowOnce(rig, e => e == "GetCount(src)" && rig.Gateway.TakeCalls == 1 && rig.Gateway.StoreCalls == 0);
        Assert.Throws<InvalidOperationException>(() => rig.Runner.Tick(100));
        Assert.Equal(TransferState.TakenAwaitingVerify, rig.Runner.State);

        rig.Runner.Abort(StopReason.GatewayError, "boom");

        TransferReport r = rig.Report;
        Assert.Equal(5, r.Remaining);
        Assert.Equal(1, r.StrandedInInventory);
        Assert.Equal(0, r.Moved);
        Assert.Equal(0, rig.Gateway.StoreCalls);
        rig.AssertAddsUp();
        rig.AssertFrozen();
    }

    // Criterion 9, 7: store done but unverified, Moved not yet credited; I1 may break but must be warned
    [Fact]
    public void Abort_FromStoredAwaitingVerify_FinishesAndWarnsIfConservationBroken()
    {
        Rig rig = Build(source: 6);
        ThrowOnce(rig, e => e == "GetCount(dst)" && rig.Gateway.StoreCalls == 1);
        Assert.Throws<InvalidOperationException>(() => rig.Runner.Tick(100));
        Assert.Equal(TransferState.StoredAwaitingVerify, rig.Runner.State);

        rig.Runner.Abort(StopReason.GatewayError, "boom");

        Assert.Equal(StopReason.GatewayError, rig.Report.Reason);
        Assert.Equal(5, rig.Report.Remaining);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        rig.AssertI1OrWarned();
        rig.AssertFrozen();
    }

    // Criterion 9
    [Fact]
    public void Abort_FromReadyForNext_ReportsMovedAndRemaining()
    {
        Rig rig = Build(source: 6);
        ThrowOnce(rig, e => e == "IsAlive(src)" && rig.Gateway.StoreCalls == 1);
        Assert.Throws<InvalidOperationException>(() => rig.Runner.Tick(100));
        Assert.Equal(TransferState.ReadyForNext, rig.Runner.State);

        rig.Runner.Abort(StopReason.GatewayError, "boom");

        Assert.Equal(1, rig.Report.Moved);
        Assert.Equal(5, rig.Report.Remaining);
        rig.AssertAddsUp();
        rig.AssertFrozen();
    }

    // Criterion 9: re-reads that throw fall back to tracked counts, per state
    [Theory]
    [InlineData("taken", 0, 5, 1, true)]
    [InlineData("stored", 0, 5, 0, false)]
    [InlineData("ready", 1, 5, 0, true)]
    public void Abort_WhenFinalRereadsThrowToo_FallsBackToTrackedCountsAndSaysSo(string state, int moved, int remaining, int stranded, bool isConserved)
    {
        Rig rig = Build(source: 6);
        Func<string, bool> when = state switch
        {
            "taken" => e => e == "GetCount(src)" && rig.Gateway.TakeCalls == 1 && rig.Gateway.StoreCalls == 0,
            "stored" => e => e == "GetCount(dst)" && rig.Gateway.StoreCalls == 1,
            _ => e => e == "IsAlive(src)" && rig.Gateway.StoreCalls == 1,
        };
        ThrowThenBreak(rig, when);
        Assert.Throws<InvalidOperationException>(() => rig.Runner.Tick(100));

        rig.Runner.Abort(StopReason.GatewayError, "x");

        TransferReport r = rig.Report;
        Assert.Equal(TransferState.Finished, rig.Runner.State);
        Assert.Equal(moved, r.Moved);
        Assert.Equal(remaining, r.Remaining);
        Assert.Equal(stranded, r.StrandedInInventory);
        Assert.EndsWith("; counts tracked, re-read threw InvalidOperationException", r.Detail);
        Assert.StartsWith("x; ", r.Detail);
        Assert.NotEmpty(rig.Log.Exceptions);
        Assert.All(rig.Log.Exceptions, x => Assert.Equal("transfer.stop", x.Event));
        Assert.Equal(isConserved, !rig.HasReportMismatchWarn);
        rig.AssertI1OrWarned();
        rig.AssertFrozen();
    }

    // Criterion 9: a Tick that throws followed by another Tick is not a stuck no-op
    [Fact]
    public void Tick_AfterThrowInVerifyRead_FinishesGatewayErrorAndStaysSilent()
    {
        Rig rig = Build(source: 6);
        ThrowOnce(rig, e => e == "GetInventoryCount" && rig.Gateway.TakeCalls == 1 && rig.Gateway.StoreCalls == 0);
        Assert.Throws<InvalidOperationException>(() => rig.Runner.Tick(100));

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.GatewayError, rig.Report.Reason);
        Assert.Contains("previous tick threw during TakenAwaitingVerify", rig.Report.Detail);
        Assert.Equal(0, rig.Gateway.StoreCalls);
        rig.AssertFrozen();
    }

    // Reviewer ask: ShelfGone with a tracked Remaining
    [Fact]
    public void Tick_SourceGoneMidRun_ReportsTrackedRemainingWithoutReadingSourceCount()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.OnCall = e =>
        {
            if (e == "IsAlive(src)" && rig.Gateway.StoreCalls == 1)
            {
                rig.Gateway.Shelf(_src).IsAlive = false;
            }
        };

        rig.Runner.Tick(100);

        TransferReport r = rig.Report;
        Assert.Equal(StopReason.ShelfGone, r.Reason);
        Assert.Equal(1, r.Moved);
        Assert.Equal(5, r.Remaining);
        Assert.Equal("shelf gone mid-run; source gone, remaining tracked", r.Detail);
        Assert.Equal(new[] { "IsAlive(src)", "GetInventoryCount" }, rig.Gateway.Calls.TakeLast(2));
        rig.AssertAddsUp();
        rig.AssertFrozen();
    }

    // Reviewer ask: target gone, source readable
    [Fact]
    public void Tick_TargetGoneMidRun_ReportsFreshRemaining()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.OnCall = e =>
        {
            if (e == "IsAlive(dst)" && rig.Gateway.StoreCalls == 1)
            {
                rig.Gateway.Shelf(_dst).IsAlive = false;
            }
        };

        rig.Runner.Tick(100);

        TransferReport r = rig.Report;
        Assert.Equal(StopReason.ShelfGone, r.Reason);
        Assert.Equal("shelf gone mid-run", r.Detail);
        Assert.Equal(5, r.Remaining);
        Assert.Equal(1, r.Moved);
        rig.AssertAddsUp();
        rig.AssertFrozen();
    }

    // Reviewer ask: re-entrant Tick from a gateway callback
    [Fact]
    public void Tick_ReentrantFromGatewayCallback_FinishesGatewayErrorAndNoFurtherMutation()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.OnCall = e =>
        {
            if (e == "TakeOne(src)" && rig.Gateway.TakeCalls == 2)
            {
                rig.Runner.Tick(100);
            }
        };

        rig.Runner.Tick(100);

        Assert.Equal(TransferState.Finished, rig.Runner.State);
        Assert.Equal(StopReason.GatewayError, rig.Report.Reason);
        Assert.Equal(2, rig.Gateway.TakeCalls);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        rig.AssertFrozen();
    }

    // Reviewer ask / attack: re-entrant Abort, first stop wins, zero further mutations
    [Fact]
    public void Abort_ReentrantFromGatewayCallback_FirstStopWinsAndNoFurtherMutation()
    {
        Rig rig = Build(source: 6);
        rig.Gateway.OnCall = e =>
        {
            if (e == "TakeOne(src)" && rig.Gateway.TakeCalls == 2)
            {
                rig.Runner.Abort(StopReason.Cancelled, "first");
                rig.Runner.Abort(StopReason.GatewayError, "second");
            }
        };

        rig.Runner.Tick(100);

        Assert.Equal(StopReason.Cancelled, rig.Report.Reason);
        Assert.Equal("first", rig.Report.Detail);
        Assert.Equal(2, rig.Gateway.TakeCalls);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        rig.AssertFrozen();
    }

    // Attack: Abort twice
    [Fact]
    public void Abort_CalledTwice_SecondIsNoOpAndMakesNoGatewayCall()
    {
        Rig rig = Build(source: 6);
        rig.Runner.Tick(1);
        rig.Runner.Abort(StopReason.GatewayError, "one");
        TransferReport first = rig.Report;
        int calls = rig.Gateway.Calls.Count;

        rig.Runner.Abort(StopReason.SourceMismatch, "two");

        Assert.Same(first, rig.Report);
        Assert.Equal(calls, rig.Gateway.Calls.Count);
        Assert.Single(rig.Log.Named("transfer.stop"));
    }

    // Attack: Abort after Completed
    [Fact]
    public void Abort_AfterCompleted_LeavesReportUntouchedAndLogsNothingMore()
    {
        Rig rig = Build(source: 3);
        rig.Runner.Tick(100);
        TransferReport done = rig.Report;
        int calls = rig.Gateway.Calls.Count;
        int logs = rig.Log.Entries.Count;

        rig.Runner.Abort(StopReason.GatewayError, "late");

        Assert.Same(done, rig.Report);
        Assert.Equal(StopReason.Completed, done.Reason);
        Assert.Equal(calls, rig.Gateway.Calls.Count);
        Assert.Equal(logs, rig.Log.Entries.Count);
    }

    // Attack: Abort before the first Tick
    [Fact]
    public void Abort_BeforeFirstTick_ReportsFreshSourceCountAndNothingMutates()
    {
        Rig rig = Build(source: 6);

        rig.Runner.Abort(StopReason.GatewayError, "early");

        TransferReport r = rig.Report;
        Assert.Equal(StopReason.GatewayError, r.Reason);
        Assert.Equal(6, r.Remaining);
        Assert.Equal(0, r.Moved);
        Assert.Equal(0, r.StrandedInInventory);
        Assert.Equal(0, rig.Gateway.Mutations);
        Assert.False(rig.HasReportMismatchWarn);
        rig.AssertFrozen();
    }
}
