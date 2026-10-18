// purpose: pins the runner as the plugin's TransferDriver drives it (card 10): one Tick(ItemsPerFrame) per frame inside a catch that calls Abort
// owns:    TransferDriverScenarioTests
// deps:    TransferRunner, FakeShelfGateway, FakeModLog
// caution: Frame mirrors TransferDriver.Update (try Tick, catch -> Abort(GatewayError)); if the driver's loop changes, change Frame with it

using System;
using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Tests.Fakes;
using ShelfTransfer.Core.Transfer;

using Xunit;

namespace ShelfTransfer.Core.Tests.Transfer;

public sealed class TransferDriverScenarioTests
{
    private static readonly ShelfRef _src = new("src", "StorageShelf", default);
    private static readonly ShelfRef _dst = new("dst", "StorageShelf", default);
    private static readonly ItemId _item = new(312);

    private sealed class Rig
    {
        public Rig(int source, int amount, int confirmTicks, int target = 0, int? targetCapacity = null)
        {
            Gateway = new FakeShelfGateway();
            Gateway.Add(_src, source);
            FakeShelf dst = Gateway.Add(_dst, target);
            if (targetCapacity is int capacity)
            {
                dst.Capacity = capacity;
            }

            Log = new FakeModLog();
            Runner = new TransferRunner(Gateway, Log, new TransferRequest(_src, new SlotRef(null), _dst, _item, amount), confirmTicks);
            SourceStart = source;
        }

        public FakeShelfGateway Gateway { get; }

        public FakeModLog Log { get; }

        public TransferRunner Runner { get; }

        public int SourceStart { get; }

        public List<string> Events => Log.Entries.Select(e => e.Event).ToList();

        // One driver frame: Tick(maxCycles) in the P2 try/catch; true when the runner is finished afterwards.
        public bool Frame(int maxCycles)
        {
            try
            {
                Runner.Tick(maxCycles);
            }
            catch (Exception ex)
            {
                Runner.Abort(StopReason.GatewayError, ex.GetType().Name + ": " + ex.Message);
            }

            return Runner.State == TransferState.Finished;
        }

        public int RunToEnd(int maxCycles, int maxFrames = 50)
        {
            for (int frames = 1; frames <= maxFrames; frames++)
            {
                if (Frame(maxCycles))
                {
                    return frames;
                }
            }

            throw new Xunit.Sdk.XunitException($"runner not finished after {maxFrames} frames");
        }
    }

    // Criterion 2 + 6: the exact log sequence for 3 of 6 at ItemsPerFrame=1, with no host delay at any confirm bound.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(300)]
    public void Frames_ThreeOfSixItemsPerFrameOne_LogsStartThenThreeTakeStorePairsThenStop(int confirmTicks)
    {
        Rig rig = new(source: 6, amount: 3, confirmTicks);

        int frames = rig.RunToEnd(maxCycles: 1);

        Assert.Equal(
            new[]
            {
                "transfer.start",
                "transfer.take", "transfer.store",
                "transfer.take", "transfer.store",
                "transfer.take", "transfer.store",
                "transfer.stop",
            },
            rig.Events);
        Assert.Equal(3, frames);

        LogEntry start = Assert.Single(rig.Log.Named("transfer.start"));
        Assert.Equal("src", start.Text("source"));
        Assert.Equal("dst", start.Text("target"));
        Assert.Equal("312", start.Text("item"));
        Assert.Equal("3", start.Text("amount"));
        Assert.Equal("6", start.Text("s0"));
        Assert.Equal("0", start.Text("t0"));
        Assert.Equal("0", start.Text("i0"));

        List<LogEntry> steps = rig.Log.Entries.Where(e => e.Event is "transfer.take" or "transfer.store").ToList();
        Assert.All(steps, step => Assert.Equal("ok", step.Text("verify")));
        Assert.Equal(new[] { "1", "1", "2", "2", "3", "3" }, steps.Select(s => s.Text("n")));

        LogEntry stop = Assert.Single(rig.Log.Named("transfer.stop"));
        Assert.Equal("Completed", stop.Text("reason"));
        Assert.Equal("3", stop.Text("moved"));
        Assert.Equal("3", stop.Text("remaining"));
        Assert.Equal("0", stop.Text("stranded"));
        Assert.Equal(3, rig.Gateway.Shelf(_src).Count);
        Assert.Equal(3, rig.Gateway.Shelf(_dst).Count);
    }

    // Criterion 6: ItemsPerFrame=1 means each frame ends with exactly one more take and one more store, never a take without its store.
    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void Frames_ItemsPerFrameOne_EachFrameAddsExactlyOneTakeAndOneStore(int confirmTicks)
    {
        Rig rig = new(source: 6, amount: 3, confirmTicks);

        for (int frame = 1; frame <= 3; frame++)
        {
            rig.Frame(maxCycles: 1);

            Assert.Equal(frame, rig.Gateway.TakeCalls);
            Assert.Equal(frame, rig.Gateway.StoreCalls);
            Assert.Equal(frame, rig.Runner.Moved);
        }

        Assert.Equal(TransferState.Finished, rig.Runner.State);
    }

    // Criterion 6: the budget scales; the last frame may use less than the budget.
    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 1)]
    [InlineData(5, 1)]
    public void Frames_ItemsPerFrameN_ThreeOfSixFinishesInCeilingFrames(int itemsPerFrame, int expectedFrames)
    {
        Rig rig = new(source: 6, amount: 3, confirmTicks: 30);

        int frames = rig.RunToEnd(itemsPerFrame);

        Assert.Equal(expectedFrames, frames);
        Assert.Equal(3, rig.Gateway.TakeCalls);
        Assert.Equal(3, rig.Gateway.StoreCalls);
    }

    // Criterion 3: the log's transfer.start carries the clamped amount, before the first take.
    [Theory]
    [InlineData(6, 10, 4, 4, 2)]
    [InlineData(6, 4, 4, 4, 2)]
    [InlineData(6, 3, 4, 3, 3)]
    [InlineData(3, 10, 4, 3, 0)]
    [InlineData(6, int.MaxValue, 4, 4, 2)]
    public void Frames_RequestAboveTargetFree_StartLineCarriesClampedAmount(int source, int request, int free, int expectedAmount, int expectedRemaining)
    {
        Rig rig = new(source, request, confirmTicks: 30, targetCapacity: free);

        rig.RunToEnd(maxCycles: 1);

        LogEntry start = Assert.Single(rig.Log.Named("transfer.start"));
        Assert.Equal(expectedAmount.ToString(), start.Text("amount"));
        Assert.Equal(0, rig.Events.IndexOf("transfer.start"));
        LogEntry stop = Assert.Single(rig.Log.Named("transfer.stop"));
        Assert.Equal("Completed", stop.Text("reason"));
        Assert.Equal(expectedAmount.ToString(), stop.Text("moved"));
        Assert.Equal(expectedRemaining.ToString(), stop.Text("remaining"));
        Assert.Equal(expectedAmount, rig.Gateway.TakeCalls);
    }

    // Criterion 3: free space is what is left, so a target that already holds some of the item clamps to the remainder.
    [Fact]
    public void Frames_TargetPartlyFull_ClampsToRemainingFreeSpace()
    {
        Rig rig = new(source: 6, amount: 10, confirmTicks: 30, target: 3, targetCapacity: 7);

        rig.RunToEnd(maxCycles: 1);

        LogEntry start = Assert.Single(rig.Log.Named("transfer.start"));
        Assert.Equal("4", start.Text("amount"));
        Assert.Equal("3", start.Text("t0"));
        Assert.Equal(7, rig.Gateway.Shelf(_dst).Count);
    }

    // Criterion 5 at the runner level: a throw mid-run, the driver catches and aborts; one stop line, nothing moves afterwards.
    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void Frames_GatewayThrowsOnSecondStore_AbortGivesOneGatewayErrorStopAndNoFurtherCalls(int confirmTicks)
    {
        Rig rig = new(source: 6, amount: 3, confirmTicks);
        rig.Gateway.OnCall = call =>
        {
            if (call == "StoreOne(dst)" && rig.Gateway.StoreCalls == 2)
            {
                throw new InvalidOperationException("boom");
            }
        };

        Assert.False(rig.Frame(maxCycles: 1));
        Assert.True(rig.Frame(maxCycles: 1));
        rig.Gateway.OnCall = null;

        TransferReport report = Assert.IsType<TransferReport>(rig.Runner.Report);
        Assert.Equal(StopReason.GatewayError, report.Reason);
        Assert.Equal(1, report.Moved);
        Assert.Equal(1, report.StrandedInInventory);
        Assert.Equal(rig.SourceStart, report.Moved + report.Remaining + report.StrandedInInventory);
        Assert.Contains("InvalidOperationException", report.Detail);

        int callsAtStop = rig.Gateway.Calls.Count;
        for (int i = 0; i < 5; i++)
        {
            rig.Frame(maxCycles: 1);
        }

        Assert.Equal(callsAtStop, rig.Gateway.Calls.Count);
        Assert.Equal(2, rig.Gateway.TakeCalls);
        Assert.Equal(2, rig.Gateway.StoreCalls);
        LogEntry stop = Assert.Single(rig.Log.Named("transfer.stop"));
        Assert.Equal("GatewayError", stop.Text("reason"));
    }

    // Criterion 5: a throw while planning (before any baseline) still yields exactly one report and no mutation.
    [Fact]
    public void Frames_GatewayThrowsWhilePlanning_OneStopLineZeroMutations()
    {
        Rig rig = new(source: 6, amount: 3, confirmTicks: 30);
        rig.Gateway.OnCall = call =>
        {
            if (call == "GetInventoryCount")
            {
                throw new InvalidOperationException("boom");
            }
        };

        bool isFinished = rig.Frame(maxCycles: 1);
        rig.Gateway.OnCall = null;

        Assert.True(isFinished);
        Assert.Equal(0, rig.Gateway.Mutations);
        Assert.Empty(rig.Log.Named("transfer.start"));
        LogEntry stop = Assert.Single(rig.Log.Named("transfer.stop"));
        Assert.Equal("GatewayError", stop.Text("reason"));
        Assert.Equal(6, rig.Gateway.Shelf(_src).Count);
    }

    // D7: cancel typed between frames is honoured at the next boundary, after whole cycles only.
    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void Frames_CancelBetweenFramesAfterTwoItems_StopsCancelledWithTwoMovedAndNoThirdTake(int confirmTicks)
    {
        Rig rig = new(source: 6, amount: 5, confirmTicks);

        rig.Frame(maxCycles: 1);
        rig.Frame(maxCycles: 1);
        rig.Runner.RequestCancel();
        bool isFinished = rig.Frame(maxCycles: 1);

        Assert.True(isFinished);
        Assert.Equal(2, rig.Gateway.TakeCalls);
        Assert.Equal(2, rig.Gateway.StoreCalls);
        LogEntry stop = Assert.Single(rig.Log.Named("transfer.stop"));
        Assert.Equal("Cancelled", stop.Text("reason"));
        Assert.Equal("2", stop.Text("moved"));
        Assert.Equal("4", stop.Text("remaining"));
        Assert.Equal("0", stop.Text("stranded"));
    }

    // D7: a cancel that lands during a cycle (here: while the second take is issued) never strands the taken item.
    [Fact]
    public void Frames_CancelDuringSecondTake_CycleCompletesThenCancelled()
    {
        Rig rig = new(source: 6, amount: 5, confirmTicks: 30);
        rig.Gateway.OnCall = call =>
        {
            if (call == "TakeOne(src)" && rig.Gateway.TakeCalls == 2)
            {
                rig.Runner.RequestCancel();
            }
        };

        rig.RunToEnd(maxCycles: 1);

        Assert.Equal(2, rig.Gateway.TakeCalls);
        Assert.Equal(2, rig.Gateway.StoreCalls);
        LogEntry stop = Assert.Single(rig.Log.Named("transfer.stop"));
        Assert.Equal("Cancelled", stop.Text("reason"));
        Assert.Equal("2", stop.Text("moved"));
        Assert.Equal("0", stop.Text("stranded"));
        Assert.Equal(0, rig.Gateway.Inventory);
    }
}
