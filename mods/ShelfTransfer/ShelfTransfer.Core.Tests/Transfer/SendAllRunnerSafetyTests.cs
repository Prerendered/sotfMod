// purpose: pins the Send all safety rule (card 75, I1/I3): any run that does not end Completed, BlockedBeforeStart or Cancelled
//          with nothing stranded ends the whole queue at once, later types not started, no further gateway call
// owns:    SendAllRunnerSafetyTests
// deps:    SendAllRunner, SendAllRunnerTests.Rig, FakeShelfGateway
// caution: faults land in type 2 (ammo, unit 10): take 7 is its first chunk, stores 7..16 store that chunk

using System;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Tests.Fakes;
using ShelfTransfer.Core.Transfer;

using Xunit;

using Rig = ShelfTransfer.Core.Tests.Transfer.SendAllRunnerTests.Rig;

namespace ShelfTransfer.Core.Tests.Transfer;

public sealed class SendAllRunnerSafetyTests
{
    private const string _throwMessage = "interop threw";

    // Criterion 4: one case per unsafe reason
    [Theory]
    [InlineData("source mismatch", StopReason.SourceMismatch, SendAllTypeOutcome.Stopped)]
    [InlineData("target mismatch", StopReason.TargetMismatch, SendAllTypeOutcome.Stopped)]
    [InlineData("inventory mismatch", StopReason.InventoryMismatch, SendAllTypeOutcome.Stopped)]
    [InlineData("take refused", StopReason.TakeRefused, SendAllTypeOutcome.Stopped)]
    [InlineData("shelf gone", StopReason.ShelfGone, SendAllTypeOutcome.Stopped)]
    [InlineData("gateway failed", StopReason.GatewayError, SendAllTypeOutcome.Stopped)]
    [InlineData("tick throws", StopReason.GatewayError, SendAllTypeOutcome.Stopped)]
    [InlineData("confirm timeout", StopReason.SourceMismatch, SendAllTypeOutcome.Stopped)]
    [InlineData("store refused", StopReason.StoreRefused, SendAllTypeOutcome.Stranded)]
    public void Tick_UnsafeStopInType2_EndsQueueAndLeavesType3NotStarted(string fault, StopReason runReason, SendAllTypeOutcome outcome)
    {
        Rig rig = new(confirmTicks: fault == "confirm timeout" ? 2 : 0);
        Arm(rig, fault);

        DriveToEnd(rig);

        SendAllReport report = rig.Report;
        Assert.Equal(SendAllStopReason.SafetyStop, report.Reason);
        Assert.Equal(SendAllTypeOutcome.Moved, report.Types[0].Outcome);
        Assert.Equal(outcome, report.Types[1].Outcome);
        Assert.Equal(runReason, SendAllRunnerTests.RunOf(report.Types[1]).Reason);
        Assert.Equal(SendAllTypeOutcome.NotStarted, report.Types[2].Outcome);
        Assert.Equal(new[] { 6, 20, 4 }, report.Types.Select(t => t.Moved + t.Remaining + t.Stranded));
        Assert.StartsWith(runReason.ToString(), report.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Log.Named("transfer.start"), e => e.Text("item") == SendAllRunnerTests.Third.Value.ToString());
        Assert.Equal(report.Detail, rig.Log.Named("queue.stop").Single().Text("detail"));
        AssertNoFurtherCall(rig);
    }

    // Criterion 4: the confirm wait's bound is what ends the run
    [Fact]
    public void Tick_HostNeverConfirmsTake_RunTimesOutAndQueueStops()
    {
        Rig rig = new(confirmTicks: 2);
        Arm(rig, "confirm timeout");

        DriveToEnd(rig);

        Assert.Equal(SendAllStopReason.SafetyStop, rig.Report.Reason);
        Assert.Contains("no host confirm after 2 ticks", rig.Report.Detail, StringComparison.Ordinal);
    }

    // Criterion 4: a run that ends Completed but leaves an item in the inventory is unsafe too
    [Fact]
    public void Tick_CompletedRunWithStrandedItem_EndsQueue()
    {
        Rig rig = new();

        // Type 1's stop re-reads begin with IsAlive(src), so the extra item is seen by its report only.
        AfterVerifiedStore(rig, 6, () => rig.Gateway.Inventory++);

        DriveToEnd(rig);

        Assert.Equal(SendAllStopReason.SafetyStop, rig.Report.Reason);
        Assert.Equal(StopReason.Completed, SendAllRunnerTests.RunOf(rig.Report.Types[0]).Reason);
        Assert.Equal(SendAllTypeOutcome.Stranded, rig.Report.Types[0].Outcome);
        Assert.Equal("1 stranded after Completed", rig.Report.Detail);
        Assert.Equal(new[] { SendAllTypeOutcome.NotStarted, SendAllTypeOutcome.NotStarted }, rig.Report.Types.Skip(1).Select(t => t.Outcome));
        AssertNoFurtherCall(rig);
    }

    // Criterion 4 (P2): with no Abort after a throw, the next tick ends the queue instead of resuming the run
    [Fact]
    public void Tick_AfterAThrowWithoutAbort_NextTickEndsQueue()
    {
        Rig rig = new();
        Arm(rig, "tick throws");
        Assert.Throws<InvalidOperationException>(() =>
        {
            for (int tick = 0; tick < 100; tick++)
            {
                rig.Queue.Tick(1);
            }
        });

        rig.Queue.Tick(1);

        Assert.Equal(SendAllStopReason.SafetyStop, rig.Report.Reason);
        Assert.Equal(StopReason.GatewayError, SendAllRunnerTests.RunOf(rig.Report.Types[1]).Reason);
        Assert.Equal(SendAllTypeOutcome.NotStarted, rig.Report.Types[2].Outcome);
        AssertNoFurtherCall(rig);
    }

    // Criterion 5: another player fills the target between chunks; nothing stranded, still the end of the queue
    [Fact]
    public void Tick_TargetFilledMidRun_EndsQueueEvenWithNothingStranded()
    {
        Rig rig = new(capacity: 40);
        AfterVerifiedStore(rig, 16, () => rig.Target.Count += rig.Target.Free);

        DriveToEnd(rig);

        Assert.Equal(SendAllStopReason.SafetyStop, rig.Report.Reason);
        Assert.Equal(StopReason.TargetMismatch, SendAllRunnerTests.RunOf(rig.Report.Types[1]).Reason);
        Assert.Equal(10, rig.Report.Types[1].Moved);
        Assert.Equal(0, rig.Report.Stranded);
        Assert.Equal(SendAllTypeOutcome.NotStarted, rig.Report.Types[2].Outcome);
        AssertNoFurtherCall(rig);
    }

    // Criterion 5: the target's space shrinks mid-run, so the next store is refused with a chunk in the inventory
    [Fact]
    public void Tick_TargetSpaceGoneMidRun_StoreRefusedEndsQueue()
    {
        Rig rig = new();
        AfterVerifiedStore(rig, 16, () => rig.Target.Capacity = rig.Target.Count);

        DriveToEnd(rig);

        Assert.Equal(SendAllStopReason.SafetyStop, rig.Report.Reason);
        Assert.Equal(StopReason.StoreRefused, SendAllRunnerTests.RunOf(rig.Report.Types[1]).Reason);
        Assert.Equal(10, rig.Report.Stranded);
        Assert.Equal(SendAllTypeOutcome.NotStarted, rig.Report.Types[2].Outcome);
        AssertNoFurtherCall(rig);
    }

    // Review fix (PR #33): a gateway callback during an outer Abort's stop re-reads must not settle the run before its report exists
    [Theory]
    [InlineData("abort")]
    [InlineData("tick")]
    public void Abort_NestedCallDuringRunStopReReads_ReportsTheRunsFinalCountsAndStopIsTheLastLine(string nested)
    {
        Rig rig = new();
        while (rig.Gateway.StoreCalls < 16)
        {
            rig.Queue.Tick(1);
        }

        rig.Gateway.OnCall = call =>
        {
            if (call != "IsAlive(src)")
            {
                return;
            }

            if (nested == "abort")
            {
                rig.Queue.Abort(StopReason.GatewayError, "nested");
            }
            else
            {
                rig.Queue.Tick(1);
            }
        };

        rig.Queue.Abort(StopReason.GatewayError, "outer");

        SendAllReport report = rig.Report;
        Assert.Equal(SendAllStopReason.SafetyStop, report.Reason);
        Assert.Equal(SendAllTypeOutcome.Stopped, report.Types[1].Outcome);
        Assert.Equal(10, report.Types[1].Moved);
        Assert.Equal(20, report.Types[1].SourceStart);
        Assert.Equal(SendAllTypeOutcome.NotStarted, report.Types[2].Outcome);
        Assert.Equal(16, report.Moved);
        Assert.Equal(new[]
        {
            "queue.start source=src target=dst types=3",
            "queue.type k=1 n=3 item=438 outcome=Moved moved=6 remaining=0 stranded=0 s0=6",
            "queue.type k=2 n=3 item=362 outcome=Stopped moved=10 remaining=10 stranded=0 s0=20",
            "queue.stop reason=SafetyStop types_done=2 moved=16 stranded=0 detail=\"GatewayError: outer\"",
        }, rig.Lines("queue."));
    }

    // Review fix (PR #33): queue.start opens the log even when ListStock throws
    [Fact]
    public void Abort_AfterListStockThrows_LogsStartThenStop()
    {
        Rig rig = new();
        rig.Gateway.OnCall = call =>
        {
            if (call == "ListStock(src)")
            {
                throw new InvalidOperationException(_throwMessage);
            }
        };

        DriveToEnd(rig);

        Assert.Equal(new[]
        {
            "queue.start source=src target=dst types=0 listing_failed=true",
            $"queue.stop reason=SafetyStop types_done=0 moved=0 stranded=0 detail=\"GatewayError: {_throwMessage}\"",
        }, rig.Lines("queue."));
    }

    private static void Arm(Rig rig, string fault)
    {
        FakeShelfGateway gateway = rig.Gateway;
        switch (fault)
        {
            case "source mismatch":
            case "confirm timeout":
                gateway.TakeBehavior = n => n == 7 ? GatewayOutcome.Ok : null;
                break;
            case "take refused":
                gateway.TakeBehavior = n => n == 7 ? GatewayOutcome.Refused("slot empty") : null;
                break;
            case "gateway failed":
                gateway.TakeBehavior = n => n == 7 ? GatewayOutcome.Failed("interop failed") : null;
                break;
            case "store refused":
                gateway.StoreBehavior = n => n == 7 ? GatewayOutcome.Refused("target full") : null;
                break;
            case "tick throws":
                gateway.OnCall = call =>
                {
                    if (call == "TakeOne(src)" && gateway.TakeCalls == 7)
                    {
                        throw new InvalidOperationException(_throwMessage);
                    }
                };
                break;
            case "target mismatch":
                AfterVerifiedStore(rig, 16, () => rig.Target.Count++);
                break;
            case "inventory mismatch":
                // The player drops one carried item; a rise would read as stranded instead.
                gateway.Inventory = 3;
                AfterVerifiedStore(rig, 16, () => gateway.Inventory--);
                break;
            case "shelf gone":
                AfterVerifiedStore(rig, 16, () => gateway.Shelf(SendAllRunnerTests.Src).IsAlive = false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault), fault, "unknown fault");
        }
    }

    // Drift once the given store is verified: the next cycle's first read is IsAlive(src), and OnCall runs before it answers.
    private static void AfterVerifiedStore(Rig rig, int storeNumber, Action drift)
    {
        bool isDone = false;
        rig.Gateway.OnCall = call =>
        {
            if (!isDone && call == "IsAlive(src)" && rig.Gateway.StoreCalls == storeNumber)
            {
                isDone = true;
                drift();
            }
        };
    }

    // What the driver does (P2): a throw out of Tick is turned into an Abort, never retried.
    private static void DriveToEnd(Rig rig)
    {
        for (int tick = 0; tick < 500 && rig.Queue.State != SendAllState.Finished; tick++)
        {
            try
            {
                rig.Queue.Tick(1);
            }
            catch (InvalidOperationException ex)
            {
                rig.Queue.Abort(StopReason.GatewayError, ex.Message);
            }
        }

        Assert.Equal(SendAllState.Finished, rig.Queue.State);
    }

    private static void AssertNoFurtherCall(Rig rig)
    {
        int calls = rig.Gateway.Calls.Count;
        int mutations = rig.Gateway.Mutations;
        string stopLines = string.Join("|", rig.Lines("queue.stop"));

        rig.Queue.Tick(1);
        rig.Queue.RequestCancel();
        rig.Queue.Abort(StopReason.GatewayError, "late");
        rig.Queue.Tick(1);

        Assert.Equal(calls, rig.Gateway.Calls.Count);
        Assert.Equal(mutations, rig.Gateway.Mutations);
        Assert.Equal(stopLines, string.Join("|", rig.Lines("queue.stop")));
    }
}
