// purpose: attacks the Send all queue (card 75) beyond its criteria: nested aborts, skip reasons, count drift, listing faults,
//          cancel timing, constructor guards, hand-offs, split slots and the client (confirm wait) shape
// owns:    SendAllRunnerAttackTests
// deps:    SendAllRunner, SendAllRunnerTests.Rig, FakeShelfGateway, FakeModLog
// caution: the rig's source is MRE 6 (hook 8), rifle ammo 20 at unit 10 (hook 9), item 504 x4 (hook 10); stores 1..6 are type 1,
//          7..26 type 2; faults fire from OnCall, which runs before the call acts, so only read calls carry an Abort

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Tests.Fakes;
using ShelfTransfer.Core.Transfer;

using Xunit;

using Rig = ShelfTransfer.Core.Tests.Transfer.SendAllRunnerTests.Rig;

namespace ShelfTransfer.Core.Tests.Transfer;

public sealed class SendAllRunnerAttackTests
{
    private const string _outerDetail = "GatewayError: outer";

    // Abort arrives from inside a tick (a gateway callback), then a nested call lands during the run's abort re-reads
    [Theory]
    [InlineData("abort")]
    [InlineData("abort-shelf-gone")]
    [InlineData("tick")]
    [InlineData("cancel")]
    public void Abort_OuterFromInsideTickWithNestedCallDuringReReads_ReportIsFinalAndStopIsLastLine(string nested)
    {
        Rig rig = new();
        bool isFired = false;
        bool isNestedDone = false;
        rig.Gateway.OnCall = call =>
        {
            if (!isFired && call == "GetCount(dst)" && rig.Gateway.StoreCalls == 16)
            {
                isFired = true;
                rig.Queue.Abort(StopReason.GatewayError, "outer");
            }
            else if (isFired && !isNestedDone && call == "IsAlive(src)")
            {
                isNestedDone = true;
                switch (nested)
                {
                    case "abort":
                        rig.Queue.Abort(StopReason.GatewayError, "nested");
                        break;
                    case "abort-shelf-gone":
                        rig.Queue.Abort(StopReason.ShelfGone, "nested gone");
                        break;
                    case "tick":
                        rig.Queue.Tick(1);
                        break;
                    default:
                        rig.Queue.RequestCancel();
                        break;
                }
            }
        };

        rig.RunToEnd();

        Assert.True(isNestedDone);
        SendAllReport report = rig.Report;
        Assert.Equal(SendAllStopReason.SafetyStop, report.Reason);
        Assert.Equal(_outerDetail, report.Detail);
        Assert.Equal(SendAllTypeOutcome.Stopped, report.Types[1].Outcome);
        Assert.Equal(10, report.Types[1].Moved);
        Assert.Equal(10, report.Types[1].Remaining);
        Assert.Equal(20, report.Types[1].SourceStart);
        Assert.Equal(SendAllTypeOutcome.NotStarted, report.Types[2].Outcome);
        Assert.Equal(16, report.Moved);
        Assert.Equal(16, rig.Queue.Moved);
        Assert.Single(rig.Log.Named("queue.stop"));
        Assert.Equal("queue.stop", rig.Log.Entries[^1].Event);
        Assert.Equal(2, rig.Log.Named("queue.type").Count());
        Assert.Equal(16, rig.Gateway.StoreCalls);
    }

    [Fact]
    public void Tick_TransferUnitUnknownForAStockedType_SkippedWithUnitUnknownAndQueueContinues()
    {
        Rig rig = new();
        rig.Gateway.ItemUnits[SendAllRunnerTests.Ammo] = 0;

        rig.RunToEnd();

        SendAllTypeResult ammo = rig.Report.Types[1];
        Assert.Equal(SendAllTypeOutcome.Skipped, ammo.Outcome);
        Assert.Equal(SendAllSkipReason.UnitUnknown, ammo.SkipReason);
        Assert.Equal(20, ammo.SourceStart);
        Assert.Equal(20, ammo.Remaining);
        Assert.Equal(SendAllStopReason.Completed, rig.Report.Reason);
        Assert.Equal(4, rig.Report.Types[2].Moved);
        Assert.Contains("queue.type k=2 n=3 item=362 outcome=Skipped moved=0 remaining=20 stranded=0 s0=20 reason=UnitUnknown",
            rig.Lines("queue."));
    }

    // Count listed as 20 but 0 at the type's turn: skipped NothingToMove, S0 0, nothing remaining
    [Fact]
    public void Tick_CountDriftsToZeroBeforeTypesTurn_SkippedNothingToMoveWithS0Zero()
    {
        Rig rig = new();
        rig.Gateway.OnCall = call =>
        {
            if (call == "StoreOne(dst)" && rig.Gateway.StoreCalls == 6)
            {
                List<(SlotRef Slot, ItemId Item, int Count)> slots = rig.Gateway.Shelf(SendAllRunnerTests.Src).Slots;
                int at = slots.FindIndex(s => s.Item == SendAllRunnerTests.Ammo);
                slots[at] = (slots[at].Slot, slots[at].Item, 0);
            }
        };

        rig.RunToEnd();

        SendAllTypeResult ammo = rig.Report.Types[1];
        Assert.Equal(SendAllTypeOutcome.Skipped, ammo.Outcome);
        Assert.Equal(SendAllSkipReason.NothingToMove, ammo.SkipReason);
        Assert.Equal(0, ammo.SourceStart);
        Assert.Equal(0, ammo.Remaining);
        Assert.Equal(SendAllStopReason.Completed, rig.Report.Reason);
        Assert.Equal(4, rig.Report.Types[2].Moved);
        Assert.Contains("queue.type k=2 n=3 item=362 outcome=Skipped moved=0 remaining=0 stranded=0 s0=0 reason=NothingToMove",
            rig.Lines("queue."));
    }

    // The source shelf is gone at type 2's turn: the run never reads the source, so there is no S0
    [Fact]
    public void Tick_SourceGoneBeforeTypesTurn_StoppedWithNoSourceStartAndS0NullInLog()
    {
        Rig rig = new();
        while (!rig.Log.Named("queue.type").Any())
        {
            rig.Queue.Tick(1);
        }

        rig.Gateway.Shelf(SendAllRunnerTests.Src).IsAlive = false;
        rig.RunToEnd();

        SendAllTypeResult ammo = rig.Report.Types[1];
        Assert.Equal(SendAllTypeOutcome.Stopped, ammo.Outcome);
        Assert.Equal(StopReason.ShelfGone, SendAllRunnerTests.RunOf(ammo).Reason);
        Assert.Null(ammo.SourceStart);
        Assert.Equal(0, ammo.Moved);
        Assert.Equal(SendAllStopReason.SafetyStop, rig.Report.Reason);
        Assert.Equal(SendAllTypeOutcome.NotStarted, rig.Report.Types[2].Outcome);
        Assert.Contains(rig.Lines("queue."), l => l.StartsWith("queue.type k=2", StringComparison.Ordinal) && l.Contains("s0=null", StringComparison.Ordinal));
        Assert.Equal(6, rig.Gateway.TakeCalls);
    }

    // Abort (a return, not a throw) while ListStock is on the stack
    [Fact]
    public void Abort_DuringListStock_FinishesWithListingFailedAndNoTypes()
    {
        Rig rig = new();
        rig.Gateway.OnCall = call =>
        {
            if (call == "ListStock(src)")
            {
                rig.Queue.Abort(StopReason.GatewayError, "during listing");
            }
        };

        rig.Queue.Tick(1);

        Assert.Equal(SendAllState.Finished, rig.Queue.State);
        Assert.Equal(SendAllStopReason.SafetyStop, rig.Report.Reason);
        Assert.Empty(rig.Report.Types);
        Assert.Equal(new[]
        {
            "queue.start source=src target=dst types=0 listing_failed=true",
            "queue.stop reason=SafetyStop types_done=0 moved=0 stranded=0 detail=\"GatewayError: during listing\"",
        }, rig.Lines("queue."));
        Assert.Equal(new[] { "ListStock(src)" }, rig.Gateway.Calls);
        Assert.Equal(0, rig.Gateway.Mutations);
    }

    // ListStock throws, nobody aborts: the next Tick ends the queue and does not list again
    [Fact]
    public void Tick_AfterListStockThrowWithoutAbort_NextTickEndsQueueAndNeverRelists()
    {
        Rig rig = new();
        rig.Gateway.OnCall = call =>
        {
            if (call == "ListStock(src)")
            {
                throw new InvalidOperationException("list boom");
            }
        };
        Assert.Throws<InvalidOperationException>(() => rig.Queue.Tick(1));

        rig.Queue.Tick(1);

        Assert.Equal(SendAllState.Finished, rig.Queue.State);
        Assert.Equal(SendAllStopReason.SafetyStop, rig.Report.Reason);
        Assert.Empty(rig.Report.Types);
        Assert.Equal("GatewayError: previous queue tick threw", rig.Report.Detail);
        Assert.Equal(1, rig.Count("ListStock(src)"));
        Assert.Equal(0, rig.Gateway.Mutations);
        Assert.Equal("queue.start", rig.Log.Entries[0].Event);
        Assert.Equal("True", rig.Log.Entries[0].Text("listing_failed"));
    }

    [Fact]
    public void RequestCancel_BeforeFirstTick_CancelsAfterListingWithNoRunStarted()
    {
        Rig rig = new();
        rig.Queue.RequestCancel();

        rig.Queue.Tick(1);

        Assert.Equal(SendAllStopReason.Cancelled, rig.Report.Reason);
        Assert.Equal(3, rig.Report.Types.Count);
        Assert.All(rig.Report.Types, t => Assert.Equal(SendAllTypeOutcome.NotStarted, t.Outcome));
        Assert.Equal(0, rig.Report.TypesDone);
        Assert.Equal(new[] { "ListStock(src)" }, rig.Gateway.Calls);
        Assert.Empty(rig.Log.Named("transfer.start"));
        Assert.Empty(rig.Log.Named("queue.type"));
        Assert.Equal(new[]
        {
            "queue.start source=src target=dst types=3",
            "queue.stop reason=Cancelled types_done=0 moved=0 stranded=0 detail=null",
        }, rig.Lines("queue."));
    }

    // Abort with no active run: between two types, and before the first tick
    [Fact]
    public void Abort_BetweenTypes_EndsQueueWithRemainingTypesNotStartedAndNoCall()
    {
        Rig rig = new();
        while (!rig.Log.Named("queue.type").Any())
        {
            rig.Queue.Tick(1);
        }

        int calls = rig.Gateway.Calls.Count;
        rig.Queue.Abort(StopReason.GatewayError, "between");
        rig.Queue.Tick(1);

        Assert.Equal(SendAllStopReason.SafetyStop, rig.Report.Reason);
        Assert.Equal("GatewayError: between", rig.Report.Detail);
        Assert.Equal(new[] { SendAllTypeOutcome.Moved, SendAllTypeOutcome.NotStarted, SendAllTypeOutcome.NotStarted },
            rig.Report.Types.Select(t => t.Outcome));
        Assert.Equal(calls, rig.Gateway.Calls.Count);
        Assert.Equal(6, rig.Report.Moved);
    }

    [Fact]
    public void Abort_BeforeFirstTick_EndsQueueWithoutAnyGatewayCall()
    {
        Rig rig = new();

        rig.Queue.Abort(StopReason.GatewayError, "early");

        Assert.Equal(SendAllState.Finished, rig.Queue.State);
        Assert.Equal(SendAllStopReason.SafetyStop, rig.Report.Reason);
        Assert.Empty(rig.Gateway.Calls);
        rig.Queue.Tick(1);
        Assert.Empty(rig.Gateway.Calls);
    }

    [Fact]
    public void Constructor_SourceEqualsTarget_Throws()
    {
        FakeShelfGateway gateway = new();
        ShelfRef sameKey = new(SendAllRunnerTests.Src.Key, "OtherKind", default);

        Assert.Throws<ArgumentException>(() => new SendAllRunner(gateway, new FakeModLog(), SendAllRunnerTests.Src, SendAllRunnerTests.Src));
        Assert.Throws<ArgumentException>(() => new SendAllRunner(gateway, new FakeModLog(), SendAllRunnerTests.Src, sameKey));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Constructor_NegativeConfirmTicks_Throws(int confirmTicks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SendAllRunner(new FakeShelfGateway(), new FakeModLog(), SendAllRunnerTests.Src, SendAllRunnerTests.Dst, confirmTicks));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Tick_FewerThanOneCycle_ThrowsAndTouchesNothing(int maxCycles)
    {
        Rig rig = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Queue.Tick(maxCycles));

        Assert.Empty(rig.Gateway.Calls);
        rig.Queue.Tick(1);
        Assert.Equal(SendAllState.Running, rig.Queue.State);
    }

    // The cancel lands during the final store of type 2: the type is whole, so it is Moved, the queue is Cancelled
    [Fact]
    public void RequestCancel_DuringLastChunkOfType_TypeMovedQueueCancelledNextTypeNotStarted()
    {
        Rig rig = new();
        rig.Gateway.OnCall = call =>
        {
            if (call == "StoreOne(dst)" && rig.Gateway.StoreCalls == 26)
            {
                rig.Queue.RequestCancel();
            }
        };

        rig.RunToEnd();

        Assert.Equal(SendAllStopReason.Cancelled, rig.Report.Reason);
        Assert.Equal(SendAllTypeOutcome.Moved, rig.Report.Types[1].Outcome);
        Assert.Equal(20, rig.Report.Types[1].Moved);
        Assert.Equal(SendAllTypeOutcome.NotStarted, rig.Report.Types[2].Outcome);
        Assert.Equal(26, rig.Report.Moved);
        Assert.Equal(0, rig.Report.Stranded);
        Assert.Equal(26, rig.Gateway.StoreCalls);
        Assert.Equal(2, rig.Log.Named("transfer.start").Count());
    }

    // Moved never drops and always equals what the target holds at the end of every tick (inventory empty between ticks)
    [Fact]
    public void Moved_AcrossTypeHandOffs_IsMonotonicAndMatchesTheTargetEveryTick()
    {
        Rig rig = new();
        int last = 0;

        for (int tick = 0; tick < 200 && rig.Queue.State != SendAllState.Finished; tick++)
        {
            rig.Queue.Tick(1);
            Assert.True(rig.Queue.Moved >= last);
            Assert.Equal(rig.Target.Count, rig.Queue.Moved);
            Assert.Equal(0, rig.Gateway.Inventory);
            last = rig.Queue.Moved;
        }

        Assert.Equal(30, last);
        Assert.Equal(30, rig.Report.Moved);
    }

    // A type spread over several slots under the any-hook take
    [Theory]
    [InlineData(10, 15, 0, 20, 5)]
    [InlineData(10, 10, 10, 30, 0)]
    [InlineData(10, 10, 7, 20, 7)]
    [InlineData(15, 15, 0, 20, 10)]
    [InlineData(15, 15, 15, 30, 15)]
    [InlineData(5, 25, 9, 20, 19)]
    public void Tick_TypeSplitOverSlots_MovesWholeChunksAndLeavesTheRemainder(int a, int b, int c, int moved, int left)
    {
        Rig rig = new(mre: 0, ammo: a, third: 0);
        List<(SlotRef Slot, ItemId Item, int Count)> slots = rig.Gateway.Shelf(SendAllRunnerTests.Src).Slots;
        slots.Add((new SlotRef(11), SendAllRunnerTests.Ammo, b));
        slots.Add((new SlotRef(12), SendAllRunnerTests.Ammo, c));

        rig.RunToEnd();

        SendAllTypeResult ammo = Assert.Single(rig.Report.Types);
        Assert.Equal(moved, ammo.Moved);
        Assert.Equal(left, ammo.Remaining);
        Assert.Equal(0, ammo.Stranded);
        Assert.Equal(a + b + c, ammo.SourceStart);
        Assert.Equal(moved == 0 ? SendAllTypeOutcome.Skipped : SendAllTypeOutcome.Moved, ammo.Outcome);
        Assert.Equal(SendAllStopReason.Completed, rig.Report.Reason);
        Assert.Equal(left, rig.Gateway.Shelf(SendAllRunnerTests.Src).Slots.Sum(s => s.Count));
        Assert.Equal(0, rig.Gateway.Inventory);
    }

    // Card 85 crit 1: ammo 15 + 15 plans 20, moves 20, Completed, and the queue goes on to the next type
    [Fact]
    public void Tick_AmmoFifteenPlusFifteen_MovesTwentyCompletedAndQueueContinues()
    {
        Rig rig = new(mre: 0, ammo: 15, third: 4);
        rig.Gateway.Shelf(SendAllRunnerTests.Src).Slots.Add((new SlotRef(11), SendAllRunnerTests.Ammo, 15));

        rig.RunToEnd();

        Assert.Equal(SendAllStopReason.Completed, rig.Report.Reason);
        SendAllTypeResult ammo = rig.Report.Types[0];
        Assert.Equal(SendAllTypeOutcome.Moved, ammo.Outcome);
        Assert.Equal(StopReason.Completed, SendAllRunnerTests.RunOf(ammo).Reason);
        Assert.Equal(20, SendAllRunnerTests.RunOf(ammo).Requested);
        Assert.Equal(20, ammo.Moved);
        Assert.Equal(10, ammo.Remaining);
        Assert.Equal(SendAllTypeOutcome.Moved, rig.Report.Types[1].Outcome);
        Assert.Equal(24, rig.Report.Moved);
        Assert.Equal(0, rig.Report.Stranded);
        Assert.Equal(6, rig.Gateway.TakeCalls);
    }

    // Card 85 crit 3: total 15 at unit 10 but no slot holds a whole chunk -> skipped before any take, the queue goes on
    [Fact]
    public void Tick_TypeOnlyInPartialChunkSlots_SkippedDoesntFitBeforeAnyTakeAndQueueContinues()
    {
        Rig rig = new(mre: 0, ammo: 5, third: 4);
        rig.Gateway.Shelf(SendAllRunnerTests.Src).Slots.Add((new SlotRef(11), SendAllRunnerTests.Ammo, 5));
        rig.Gateway.Shelf(SendAllRunnerTests.Src).Slots.Add((new SlotRef(12), SendAllRunnerTests.Ammo, 5));

        rig.RunToEnd();

        Assert.Equal(SendAllStopReason.Completed, rig.Report.Reason);
        SendAllTypeResult ammo = rig.Report.Types[0];
        Assert.Equal(SendAllTypeOutcome.Skipped, ammo.Outcome);
        Assert.Equal(SendAllSkipReason.DoesntFit, ammo.SkipReason);
        Assert.Equal(StopReason.BlockedBeforeStart, SendAllRunnerTests.RunOf(ammo).Reason);
        Assert.Equal(15, ammo.Remaining);
        Assert.Equal(SendAllTypeOutcome.Moved, rig.Report.Types[1].Outcome);
        Assert.Equal(4, rig.Report.Moved);
        Assert.Equal(0, rig.Report.Stranded);
        Assert.Equal(4, rig.Gateway.TakeCalls);
        Assert.Contains("reason=DoesntFit", rig.Lines("queue.type")[0], StringComparison.Ordinal);
    }

    // Client shape: the host applies a take or store only after the call returns, so the run waits (confirmTicks > 0)
    [Fact]
    public void Tick_ClientShapeWithDelayedHostConfirm_MovesEveryTypeInOrderWithoutAMismatch()
    {
        Rig rig = new(confirmTicks: 4);
        DelayedHost host = new(rig);

        host.Drive();

        Assert.Equal(SendAllState.Finished, rig.Queue.State);
        Assert.Equal(SendAllStopReason.Completed, rig.Report.Reason);
        Assert.Equal(new[] { 6, 20, 4 }, rig.Report.Types.Select(t => t.Moved));
        Assert.Equal(new int?[] { 6, 20, 4 }, rig.Report.Types.Select(t => t.SourceStart));
        Assert.Equal(0, rig.Report.Stranded);
        Assert.Equal(new[] { 438, 362, 504 }, rig.StartedItems());
        Assert.Equal(30, rig.Target.Count);
        Assert.Equal(0, rig.Gateway.Inventory);
        Assert.True(host.MaxInventory <= 10);
    }

    // Client shape: the host stops applying takes after the 6th -> the run times out, the queue stops, the last type never starts
    [Fact]
    public void Tick_ClientShapeHostStopsConfirming_QueueStopsAfterEarlierTypeWasMoved()
    {
        Rig rig = new(confirmTicks: 2);
        DelayedHost host = new(rig) { LastAppliedTake = 6 };

        host.Drive();

        Assert.Equal(SendAllStopReason.SafetyStop, rig.Report.Reason);
        Assert.Equal(SendAllTypeOutcome.Moved, rig.Report.Types[0].Outcome);
        Assert.Equal(SendAllTypeOutcome.Stopped, rig.Report.Types[1].Outcome);
        Assert.Equal(SendAllTypeOutcome.NotStarted, rig.Report.Types[2].Outcome);
        Assert.Equal(0, rig.Report.Stranded);
    }

    // A host that applies each take and store one tick after the call returned.
    private sealed class DelayedHost
    {
        private readonly Rig _rig;
        private bool _isTakePending;
        private bool _isStorePending;
        private int _takes;

        public DelayedHost(Rig rig)
        {
            _rig = rig;
            rig.Gateway.TakeBehavior = _ =>
            {
                _isTakePending = ++_takes <= LastAppliedTake;
                return GatewayOutcome.Ok;
            };
            rig.Gateway.StoreBehavior = _ =>
            {
                _isStorePending = true;
                return GatewayOutcome.Ok;
            };
        }

        public int LastAppliedTake { get; init; } = int.MaxValue;

        public int MaxInventory { get; private set; }

        public void Drive()
        {
            for (int tick = 0; tick < 2000 && _rig.Queue.State != SendAllState.Finished; tick++)
            {
                _rig.Queue.Tick(1);
                MaxInventory = Math.Max(MaxInventory, _rig.Gateway.Inventory);
                int? item = _rig.Log.Named("transfer.start").Select(e => (int?)int.Parse(e.Text("item"), CultureInfo.InvariantCulture)).LastOrDefault();
                if (_isTakePending && item is int id)
                {
                    _isTakePending = false;
                    Assert.True(_rig.Gateway.ApplySlotTake(SendAllRunnerTests.Src, new SlotRef(null), new ItemId(id)).IsOk);
                }

                if (_isStorePending)
                {
                    _isStorePending = false;
                    Assert.True(_rig.Gateway.ApplyStore(SendAllRunnerTests.Dst).IsOk);
                }
            }
        }
    }
}
