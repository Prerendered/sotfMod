// purpose: pins the Send all queue (card 75): one TransferRunner run per listed type, fresh plans, skips, cancel, report and log
// owns:    SendAllRunnerTests
// deps:    SendAllRunner, FakeShelfGateway, FakeModLog, LogLine
// caution: the rig's source is MRE 6 (hook 8), rifle ammo 20 at unit 10 (hook 9), item 504 x4 (hook 10); ticks run one cycle each,
//          as the driver's default ItemsPerFrame

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Logging;
using ShelfTransfer.Core.Tests.Fakes;
using ShelfTransfer.Core.Transfer;

using Xunit;

namespace ShelfTransfer.Core.Tests.Transfer;

public sealed class SendAllRunnerTests
{
    internal static readonly ShelfRef Src = new("src", "StorageShelf", default);
    internal static readonly ShelfRef Dst = new("dst", "StorageShelf", default);
    internal static readonly ItemId Mre = new(438);
    internal static readonly ItemId Ammo = new(362);
    internal static readonly ItemId Third = new(504);

    internal sealed class Rig
    {
        public Rig(int mre = 6, int ammo = 20, int third = 4, int? capacity = null, int confirmTicks = 0)
        {
            Gateway = new FakeShelfGateway();
            Gateway.ItemUnits[Ammo] = 10;
            FakeShelf source = Gateway.Add(Src, 0);
            source.Slots.Add((new SlotRef(8), Mre, mre));
            source.Slots.Add((new SlotRef(9), Ammo, ammo));
            source.Slots.Add((new SlotRef(10), Third, third));
            Target = Gateway.Add(Dst, 0);
            if (capacity is int cap)
            {
                Target.Capacity = cap;
            }

            Log = new FakeModLog();
            Queue = new SendAllRunner(Gateway, Log, Src, Dst, confirmTicks);
        }

        public FakeShelfGateway Gateway { get; }

        public FakeShelf Target { get; }

        public FakeModLog Log { get; }

        public SendAllRunner Queue { get; }

        public SendAllReport Report => Assert.IsType<SendAllReport>(Queue.Report);

        public void RunToEnd()
        {
            for (int tick = 0; tick < 500 && Queue.State != SendAllState.Finished; tick++)
            {
                Queue.Tick(1);
            }

            Assert.Equal(SendAllState.Finished, Queue.State);
        }

        public int Count(string call) => Gateway.Calls.Count(c => c == call);

        // The log text as LogLine writes it, minus timestamp and level.
        public List<string> Lines(string prefix) =>
            Log.Entries
                .Where(e => e.Event.StartsWith(prefix, StringComparison.Ordinal))
                .Select(e => LogLine.Format(DateTimeOffset.UnixEpoch, e.Level, e.Event, e.Fields.ToList()))
                .Select(line => line[(line.IndexOf(' ', line.IndexOf(' ') + 1) + 1)..].TrimStart())
                .ToList();

        public List<int> StartedItems() => Log.Named("transfer.start").Select(e => int.Parse(e.Text("item"), CultureInfo.InvariantCulture)).ToList();
    }

    // Criterion 1
    [Fact]
    public void Tick_ThreeTypesEmptyTarget_OneRunPerTypeInListOrderMovingAll()
    {
        Rig rig = new();

        rig.RunToEnd();

        Assert.Equal(SendAllStopReason.Completed, rig.Report.Reason);
        Assert.Equal(new[] { Mre, Ammo, Third }, rig.Report.Types.Select(t => t.Item));
        Assert.Equal(new[] { 6, 20, 4 }, rig.Report.Types.Select(t => t.Moved));
        Assert.Equal(new[] { 6, 20, 4 }, rig.Report.Types.Select(t => RunOf(t).Requested));
        Assert.All(rig.Report.Types, t => Assert.Equal(0, t.Stranded));
        Assert.All(rig.Report.Types, t => Assert.Equal(StopReason.Completed, RunOf(t).Reason));
        Assert.Equal(new[] { Mre.Value, Ammo.Value, Third.Value }, rig.StartedItems());
        Assert.Equal(30, rig.Target.Count);
        Assert.Equal(0, rig.Gateway.Inventory);
    }

    // Criterion 1: the queue's one gateway call of its own is ListStock, first; every mutation is a per-type run's take or store
    [Fact]
    public void Tick_ThreeTypes_ListStockReadOnceFirstAndMutationsAreTheRunsChunks()
    {
        Rig rig = new();

        rig.RunToEnd();

        Assert.Equal("ListStock(src)", rig.Gateway.Calls[0]);
        Assert.Equal(1, rig.Count("ListStock(src)"));
        Assert.Equal(6 + 2 + 4, rig.Gateway.TakeCalls);
        Assert.Equal(30, rig.Gateway.StoreCalls);
        Assert.All(rig.Gateway.TakeSlots, slot => Assert.Equal(new SlotRef(null), slot));
    }

    // Criterion 2 (I4): type 2 is planned from a free-capacity read made after type 1 stored its last item
    [Fact]
    public void Tick_SharedTargetSpace_EachTypePlannedFromFreshFreeCapacity()
    {
        Rig rig = new(capacity: 25);

        rig.RunToEnd();

        List<string> calls = rig.Gateway.Calls;
        int lastMreStore = IndexOfNth(calls, "StoreOne(dst)", 6);
        int firstAmmoTake = IndexOfNth(calls, "TakeOne(src)", 7);
        int freeReads = calls.Skip(lastMreStore).Take(firstAmmoTake - lastMreStore).Count(c => c == "GetFreeCapacity(dst)");
        Assert.Equal(1, freeReads);
        Assert.Equal(3, rig.Count("GetFreeCapacity(dst)"));

        // 25 - 6 = 19 free rounds down to one chunk of 10; then 9 free takes the 4.
        Assert.Equal(new[] { 6, 10, 4 }, rig.Report.Types.Select(t => RunOf(t).Requested));
        Assert.Equal(new[] { 6, 10, 4 }, rig.Report.Types.Select(t => t.Moved));
        Assert.Equal(SendAllStopReason.Completed, rig.Report.Reason);
    }

    // Criterion 3
    [Theory]
    [InlineData("free below one chunk")]
    [InlineData("cannot hold")]
    [InlineData("source below one chunk")]
    public void Tick_TypeDoesNotFit_SkippedBeforeAnyTakeAndQueueContinues(string why)
    {
        Rig rig = why switch
        {
            "free below one chunk" => new Rig(capacity: 11),
            "source below one chunk" => new Rig(ammo: 5),
            _ => new Rig(),
        };
        if (why == "cannot hold")
        {
            int canHoldReads = 0;
            rig.Gateway.OnCall = call =>
            {
                if (call == "CanHold(dst)")
                {
                    canHoldReads++;
                    rig.Target.CanHold = canHoldReads != 2;
                }
            };
        }

        rig.RunToEnd();

        SendAllTypeResult ammo = rig.Report.Types[1];
        Assert.Equal(SendAllTypeOutcome.Skipped, ammo.Outcome);
        Assert.Equal(StopReason.BlockedBeforeStart, RunOf(ammo).Reason);
        Assert.Equal(0, ammo.Moved);
        Assert.Equal(6 + 4, rig.Gateway.TakeCalls);
        Assert.Equal(6 + 4, rig.Gateway.StoreCalls);
        Assert.Equal(SendAllTypeOutcome.Moved, rig.Report.Types[2].Outcome);
        Assert.Equal(4, rig.Report.Types[2].Moved);
        Assert.Equal(SendAllStopReason.Completed, rig.Report.Reason);
    }

    // Criterion 6 (D7)
    [Fact]
    public void RequestCancel_DuringType2_StopsAfterTheChunkIsStoredAndStartsNoFurtherType()
    {
        Rig rig = new();
        int stores = 0;
        rig.Gateway.OnCall = call =>
        {
            if (call == "StoreOne(dst)" && ++stores == 8)
            {
                rig.Queue.RequestCancel();
            }
        };

        rig.RunToEnd();

        Assert.Equal(SendAllStopReason.Cancelled, rig.Report.Reason);
        Assert.Equal(0, rig.Report.Stranded);
        Assert.Equal(SendAllTypeOutcome.Cancelled, rig.Report.Types[1].Outcome);
        Assert.Equal(10, rig.Report.Types[1].Moved);
        Assert.Equal(SendAllTypeOutcome.NotStarted, rig.Report.Types[2].Outcome);
        Assert.Equal(7, rig.Gateway.TakeCalls);
        Assert.Equal(16, rig.Gateway.StoreCalls);
        Assert.Equal(new[] { Mre.Value, Ammo.Value }, rig.StartedItems());
    }

    // Criterion 6 (D7)
    [Fact]
    public void RequestCancel_BetweenTypes_StartsNoFurtherTypeAndCallsNothing()
    {
        Rig rig = new();
        while (!rig.Log.Named("queue.type").Any())
        {
            rig.Queue.Tick(1);
        }

        int callsBefore = rig.Gateway.Calls.Count;
        rig.Queue.RequestCancel();
        rig.Queue.Tick(1);

        Assert.Equal(SendAllStopReason.Cancelled, rig.Report.Reason);
        Assert.Equal(callsBefore, rig.Gateway.Calls.Count);
        Assert.Equal(new[] { SendAllTypeOutcome.Moved, SendAllTypeOutcome.NotStarted, SendAllTypeOutcome.NotStarted },
            rig.Report.Types.Select(t => t.Outcome));
        Assert.Equal(new[] { Mre.Value }, rig.StartedItems());
    }

    // Criterion 7
    [Fact]
    public void Tick_MixedOutcomes_ReportAddsUpPerTypeAndLogsStartTypeStop()
    {
        Rig rig = new(capacity: 11);

        rig.RunToEnd();

        SendAllReport report = rig.Report;
        Assert.Equal(new[] { SendAllTypeOutcome.Moved, SendAllTypeOutcome.Skipped, SendAllTypeOutcome.Moved },
            report.Types.Select(t => t.Outcome));
        Assert.Equal(new[] { 6, 20, 4 }, report.Types.Select(t => t.Moved + t.Remaining + t.Stranded));
        Assert.Equal(10, report.Moved);
        Assert.Equal(0, report.Stranded);
        Assert.Equal(3, report.TypesDone);
        Assert.Equal(new[]
        {
            "queue.start source=src target=dst types=3",
            "queue.type k=1 n=3 item=438 outcome=Moved moved=6 remaining=0 stranded=0",
            "queue.type k=2 n=3 item=362 outcome=Skipped moved=0 remaining=20 stranded=0",
            "queue.type k=3 n=3 item=504 outcome=Moved moved=4 remaining=0 stranded=0",
            "queue.stop reason=Completed types_done=3 moved=10 stranded=0 detail=null",
        }, rig.Lines("queue."));
    }

    // Criterion 7
    [Fact]
    public void Tick_EmptySource_CompletesWithZeroTypesAndNoTypeLine()
    {
        Rig rig = new(mre: 0, ammo: 0, third: 0);

        rig.Queue.Tick(1);

        Assert.Equal(SendAllStopReason.Completed, rig.Report.Reason);
        Assert.Empty(rig.Report.Types);
        Assert.Equal(new[]
        {
            "queue.start source=src target=dst types=0",
            "queue.stop reason=Completed types_done=0 moved=0 stranded=0 detail=null",
        }, rig.Lines("queue."));
        Assert.Equal(new[] { "ListStock(src)" }, rig.Gateway.Calls);
    }

    internal static TransferReport RunOf(SendAllTypeResult type) => Assert.IsType<TransferReport>(type.Run);

    private static int IndexOfNth(List<string> calls, string call, int nth)
    {
        int seen = 0;
        for (int index = 0; index < calls.Count; index++)
        {
            if (calls[index] == call && ++seen == nth)
            {
                return index;
            }
        }

        throw new InvalidOperationException($"{call} #{nth} not found");
    }
}
