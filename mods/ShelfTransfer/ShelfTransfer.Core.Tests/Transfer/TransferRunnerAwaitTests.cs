// purpose: pins the host-confirm wait of the TransferRunner (card 46, F2): re-read-only waiting after an Ok take or store, the bound, drift, cancel, faults, log lines
// owns:    TransferRunnerAwaitTests
// deps:    TransferRunner, FakeShelfGateway, FakeModLog
// caution: the fake host applies a take or store at the end of a chosen tick, between Tick calls; delay d applies at the end of issue-tick + d - 1, so a wait tick j sees it when j >= d

using System;
using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Logging;
using ShelfTransfer.Core.Tests.Fakes;
using ShelfTransfer.Core.Transfer;

using Xunit;

namespace ShelfTransfer.Core.Tests.Transfer;

public sealed class TransferRunnerAwaitTests
{
    private static readonly string _start = "Info transfer.start source=src target=dst item=312 amount=1 s0=3 t0=0 i0=0";

    private static readonly ShelfRef _src = new("src", "StorageShelf", default);
    private static readonly ShelfRef _dst = new("dst", "StorageShelf", default);
    private static readonly ItemId _item = new(312);

    private static readonly string[] _planCalls =
    {
        "IsAlive(src)", "IsAlive(dst)", "GetCount(src)", "GetCount(dst)", "GetInventoryCount", "CanHold(dst)", "GetFreeCapacity(dst)",
    };

    private static Rig Build(
        int confirmTicks,
        int takeDelay = 0,
        int storeDelay = 0,
        int source = 3,
        int target = 0,
        int amount = 1)
    {
        FakeShelfGateway gateway = new();
        gateway.Add(_src, source);
        gateway.Add(_dst, target);
        FakeModLog log = new();
        TransferRunner runner = new(gateway, log, new TransferRequest(_src, new SlotRef(null), _dst, _item, amount), confirmTicks);
        Rig rig = new(gateway, log, runner, source);
        if (takeDelay > 0)
        {
            gateway.TakeBehavior = _ =>
            {
                rig.At(rig.TickNo + takeDelay - 1, () => gateway.ApplyTake(_src));
                return GatewayOutcome.Ok;
            };
        }

        if (storeDelay > 0)
        {
            gateway.StoreBehavior = _ =>
            {
                rig.At(rig.TickNo + storeDelay - 1, () => gateway.ApplyStore(_dst));
                return GatewayOutcome.Ok;
            };
        }

        return rig;
    }

    private static string Render(LogEntry entry) =>
        $"{entry.Level} {entry.Event} " + string.Join(" ", entry.Fields.Select(f => $"{f.Key}={f.Value?.ToString() ?? "null"}"));

    private sealed class Rig
    {
        private readonly List<(int Tick, Action Action)> _scheduled = new();

        public Rig(FakeShelfGateway gateway, FakeModLog log, TransferRunner runner, int sourceStart)
        {
            Gateway = gateway;
            Log = log;
            Runner = runner;
            SourceStart = sourceStart;
        }

        public FakeShelfGateway Gateway { get; }

        public FakeModLog Log { get; }

        public TransferRunner Runner { get; }

        public int SourceStart { get; }

        public int TickNo { get; private set; }

        public int Target => Gateway.Shelf(_dst).Count;

        public TransferReport Report => Assert.IsType<TransferReport>(Runner.Report);

        public void At(int tick, Action action) => _scheduled.Add((tick, action));

        /// <summary>Runs one tick, then fires the host actions due by its end.</summary>
        public void Run(int maxCycles = 1)
        {
            TickNo++;
            Runner.Tick(maxCycles);
            foreach ((int Tick, Action Action) entry in _scheduled.Where(s => s.Tick <= TickNo).ToList())
            {
                _scheduled.Remove(entry);
                entry.Action();
            }
        }

        /// <summary>The gateway calls made while the action runs.</summary>
        public List<string> CallsDuring(Action action)
        {
            int mark = Gateway.Calls.Count;
            action();
            return Gateway.Calls.Skip(mark).ToList();
        }

        public List<string> Lines() => Log.Entries.Select(Render).ToList();

        public List<string> Lines(params string[] events) =>
            Log.Entries.Where(e => events.Contains(e.Event)).Select(Render).ToList();

        public List<string> Events() => Log.Entries.Select(e => e.Event).ToList();

        public void RunUntilFinished(int maxTicks = 200, int maxCycles = 1)
        {
            for (int i = 0; i < maxTicks && Runner.State != TransferState.Finished; i++)
            {
                Run(maxCycles);
            }

            Assert.Equal(TransferState.Finished, Runner.State);
        }

        // I1: only TakeOne/StoreOne mutate, they strictly alternate, and no step is ever retried.
        public void AssertI1()
        {
            Assert.True(Gateway.TakeCalls <= Runner.Moved + 1, $"TakeCalls {Gateway.TakeCalls} > Moved {Runner.Moved} + 1");
            Assert.True(Gateway.StoreCalls <= Gateway.TakeCalls, "a store without a take");
            Assert.True(Gateway.StoreCalls <= Runner.Moved + 1, "more stores than items attempted");
            List<string> mutations = Gateway.Calls
                .Where(c => c.StartsWith("TakeOne", StringComparison.Ordinal) || c.StartsWith("StoreOne", StringComparison.Ordinal))
                .ToList();
            for (int i = 0; i < mutations.Count; i++)
            {
                Assert.Equal(i % 2 == 0 ? "TakeOne(src)" : "StoreOne(dst)", mutations[i]);
            }
        }

        public void AssertConservedOrWarned()
        {
            TransferReport report = Report;
            bool isBalanced = SourceStart == report.Remaining + report.Moved + report.StrandedInInventory;
            Assert.True(isBalanced || Log.Named("conservation.mismatch").Any(), "unbalanced report without a conservation.mismatch WARN");
        }
    }

    // ---------------------------------------------------------------- criterion 1: wait after TakeOne

    // Criterion 1
    [Fact]
    public void Take_HostAppliesAfterOneTick_WaitsRereadsOnlyThenContinues()
    {
        Rig rig = Build(confirmTicks: 3, takeDelay: 1);

        List<string> tick1 = rig.CallsDuring(() => rig.Run());
        Assert.Equal(TransferState.TakenAwaitingVerify, rig.Runner.State);
        Assert.Equal(
            _planCalls.Concat(new[]
            {
                "IsAlive(src)", "IsAlive(dst)",
                "GetCount(src)", "GetCount(dst)", "GetInventoryCount",
                "TakeOne(src)", "GetCount(src)", "GetInventoryCount",
            }).ToList(),
            tick1);

        List<string> tick2 = rig.CallsDuring(() => rig.Run());

        Assert.Equal(
            new[]
            {
                "IsAlive(src)", "IsAlive(dst)", "GetCount(src)", "GetInventoryCount", "GetCount(dst)",
                "StoreOne(dst)", "GetCount(dst)", "GetInventoryCount",
                "IsAlive(src)", "GetCount(src)", "GetInventoryCount",
            },
            tick2);
        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        Assert.Equal(
            new[]
            {
                _start,
                "Info transfer.wait n=1 step=take ticks=1",
                "Info transfer.take n=1 src_before=3 src_after=2 inv_before=0 inv_after=1 verify=ok",
                "Info transfer.store n=1 dst_before=0 dst_after=1 inv_before=1 inv_after=0 verify=ok",
                "Info transfer.stop reason=Completed s0=3 moved=1 remaining=2 stranded=0 detail=null",
            },
            rig.Lines());
    }

    // Criterion 1
    [Fact]
    public void Take_UnappliedWaitTick_IssuesNoMutationAndOnlyRereads()
    {
        Rig rig = Build(confirmTicks: 5, takeDelay: 4);
        rig.Run();

        for (int i = 0; i < 3; i++)
        {
            List<string> calls = rig.CallsDuring(() => rig.Run());
            Assert.Equal(new[] { "IsAlive(src)", "IsAlive(dst)", "GetCount(src)", "GetInventoryCount" }, calls);
            Assert.Equal(TransferState.TakenAwaitingVerify, rig.Runner.State);
        }

        Assert.Equal(1, rig.Gateway.Mutations);
        Assert.Empty(rig.Log.Named("transfer.wait"));
        Assert.Empty(rig.Log.Named("transfer.take"));
    }

    // Criterion 1
    [Theory]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    public void Take_HostAppliesWithinBound_ContinuesAndLogsTicksEqualToDelay(int bound, int delay)
    {
        Rig rig = Build(confirmTicks: bound, takeDelay: delay);

        rig.RunUntilFinished();

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(1, rig.Report.Moved);
        Assert.Equal(new[] { $"Info transfer.wait n=1 step=take ticks={delay}" }, rig.Lines("transfer.wait"));
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        rig.AssertI1();
    }

    // Criterion 1 / 3
    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 6)]
    [InlineData(2, 9)]
    public void Take_HostAppliesOneTickPastBound_TimesOutSourceMismatchWithoutRetry(int bound, int delay)
    {
        Rig rig = Build(confirmTicks: bound, takeDelay: delay);

        rig.RunUntilFinished();
        rig.Run();
        rig.Run();

        TransferReport report = rig.Report;
        Assert.Equal(StopReason.SourceMismatch, report.Reason);
        Assert.Equal($"take: source expected 2 read 3; no host confirm after {bound} ticks", report.Detail);
        Assert.Equal(new TransferReport(StopReason.SourceMismatch, 1, 0, 3, 0, report.Detail), report);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(0, rig.Gateway.StoreCalls);
        Assert.Equal(new[] { $"Warn transfer.wait n=1 step=take ticks={bound}" }, rig.Lines("transfer.wait"));
        rig.AssertI1();
        rig.AssertConservedOrWarned();
    }

    // Criterion 1
    [Fact]
    public void Take_CountsMoveSourceFirstThenInventory_WaitsThenContinues()
    {
        Rig rig = Build(confirmTicks: 3);
        rig.Gateway.TakeBehavior = _ => GatewayOutcome.Ok;
        rig.At(1, () => rig.Gateway.Shelf(_src).Count--);
        rig.At(2, () => rig.Gateway.Inventory++);

        rig.RunUntilFinished();

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(new[] { "Info transfer.wait n=1 step=take ticks=2" }, rig.Lines("transfer.wait"));
        Assert.Equal(1, rig.Gateway.TakeCalls);
    }

    // Criterion 1
    [Fact]
    public void Take_CountsMoveInventoryFirstThenSource_WaitsThenContinues()
    {
        Rig rig = Build(confirmTicks: 3);
        rig.Gateway.TakeBehavior = _ => GatewayOutcome.Ok;
        rig.At(1, () => rig.Gateway.Inventory++);
        rig.At(2, () => rig.Gateway.Shelf(_src).Count--);

        rig.RunUntilFinished();

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(new[] { "Info transfer.wait n=1 step=take ticks=2" }, rig.Lines("transfer.wait"));
        Assert.Equal(1, rig.Gateway.TakeCalls);
    }

    // Criterion 1 (pins: a count regressing expected -> before keeps waiting)
    [Fact]
    public void Take_SourceMovesThenRegressesThenBothApply_KeepsWaiting()
    {
        Rig rig = Build(confirmTicks: 3);
        rig.Gateway.TakeBehavior = _ => GatewayOutcome.Ok;
        rig.At(1, () => rig.Gateway.Shelf(_src).Count = 2);
        rig.At(2, () => rig.Gateway.Shelf(_src).Count = 3);
        rig.At(3, () =>
        {
            rig.Gateway.Shelf(_src).Count = 2;
            rig.Gateway.Inventory = 1;
        });

        rig.RunUntilFinished();

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(new[] { "Info transfer.wait n=1 step=take ticks=3" }, rig.Lines("transfer.wait"));
        Assert.Equal(1, rig.Gateway.TakeCalls);
    }

    // Criterion 1 / 3: partial apply that never completes
    [Fact]
    public void Take_OnlySourceMovesThenTimeout_InventoryMismatchWithoutNoConfirmNote()
    {
        Rig rig = Build(confirmTicks: 2);
        rig.Gateway.TakeBehavior = _ => GatewayOutcome.Ok;
        rig.At(1, () => rig.Gateway.Shelf(_src).Count = 2);

        rig.RunUntilFinished();

        TransferReport report = rig.Report;
        Assert.Equal(StopReason.InventoryMismatch, report.Reason);
        Assert.Equal("take: inventory expected 1 read 0", report.Detail);
        Assert.Equal(2, report.Remaining);
        Assert.Equal(0, report.StrandedInInventory);
        Assert.Equal(0, rig.Gateway.StoreCalls);
        Assert.Equal(new[] { "Warn transfer.wait n=1 step=take ticks=2" }, rig.Lines("transfer.wait"));
        rig.AssertConservedOrWarned();
    }

    // Criterion 1 / 3: partial apply that never completes
    [Fact]
    public void Take_OnlyInventoryMovesThenTimeout_SourceMismatchWithoutNoConfirmNote()
    {
        Rig rig = Build(confirmTicks: 2);
        rig.Gateway.TakeBehavior = _ => GatewayOutcome.Ok;
        rig.At(1, () => rig.Gateway.Inventory = 1);

        rig.RunUntilFinished();

        TransferReport report = rig.Report;
        Assert.Equal(StopReason.SourceMismatch, report.Reason);
        Assert.Equal("take: source expected 2 read 3", report.Detail);
        Assert.Equal(1, report.StrandedInInventory);
        Assert.Equal(0, rig.Gateway.StoreCalls);
        rig.AssertI1();
        rig.AssertConservedOrWarned();
    }

    // ---------------------------------------------------------------- criterion 2: wait after StoreOne

    // Criterion 2
    [Fact]
    public void Store_HostAppliesAfterTwoTicks_WaitsRereadsOnlyThenContinues()
    {
        Rig rig = Build(confirmTicks: 3, storeDelay: 2);

        rig.Run();
        Assert.Equal(TransferState.StoredAwaitingVerify, rig.Runner.State);
        List<string> tick2 = rig.CallsDuring(() => rig.Run());
        Assert.Equal(new[] { "IsAlive(src)", "IsAlive(dst)", "GetCount(dst)", "GetInventoryCount" }, tick2);
        List<string> tick3 = rig.CallsDuring(() => rig.Run());

        Assert.Equal(
            new[]
            {
                "IsAlive(src)", "IsAlive(dst)", "GetCount(dst)", "GetInventoryCount",
                "IsAlive(src)", "GetCount(src)", "GetInventoryCount",
            },
            tick3);
        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        Assert.Equal(
            new[]
            {
                _start,
                "Info transfer.take n=1 src_before=3 src_after=2 inv_before=0 inv_after=1 verify=ok",
                "Info transfer.wait n=1 step=store ticks=2",
                "Info transfer.store n=1 dst_before=0 dst_after=1 inv_before=1 inv_after=0 verify=ok",
                "Info transfer.stop reason=Completed s0=3 moved=1 remaining=2 stranded=0 detail=null",
            },
            rig.Lines());
    }

    // Criterion 2
    [Theory]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    public void Store_HostAppliesWithinBound_ContinuesAndLogsTicksEqualToDelay(int bound, int delay)
    {
        Rig rig = Build(confirmTicks: bound, storeDelay: delay);

        rig.RunUntilFinished();

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(new[] { $"Info transfer.wait n=1 step=store ticks={delay}" }, rig.Lines("transfer.wait"));
        Assert.Equal(1, rig.Gateway.StoreCalls);
        Assert.Equal(1, rig.Target);
        rig.AssertI1();
    }

    // Criterion 2 / 3
    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 6)]
    public void Store_HostAppliesOneTickPastBound_TimesOutTargetMismatchWithoutRetry(int bound, int delay)
    {
        Rig rig = Build(confirmTicks: bound, storeDelay: delay);

        rig.RunUntilFinished();
        rig.Run();

        TransferReport report = rig.Report;
        Assert.Equal(StopReason.TargetMismatch, report.Reason);
        Assert.Equal($"store: target expected 1 read 0; no host confirm after {bound} ticks", report.Detail);
        Assert.Equal(new TransferReport(StopReason.TargetMismatch, 1, 0, 2, 1, report.Detail), report);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        rig.AssertI1();
        rig.AssertConservedOrWarned();
    }

    // Criterion 2 / 3 (exact log)
    [Fact]
    public void Store_TimeoutBoundOne_LogsWaitWarnThenStoreFailThenMismatchThenStop()
    {
        Rig rig = Build(confirmTicks: 1, storeDelay: 3);

        rig.RunUntilFinished();

        Assert.Equal(
            new[]
            {
                _start,
                "Info transfer.take n=1 src_before=3 src_after=2 inv_before=0 inv_after=1 verify=ok",
                "Warn transfer.wait n=1 step=store ticks=1",
                "Warn transfer.store n=1 dst_before=0 dst_after=0 inv_before=1 inv_after=1 verify=fail",
                "Warn conservation.mismatch check=store n=1 dst_before=0 dst_after=0 inv_before=1 inv_after=1",
                "Warn transfer.stop reason=TargetMismatch s0=3 moved=0 remaining=2 stranded=1 detail=store: target expected 1 read 0; no host confirm after 1 ticks",
            },
            rig.Lines());
    }

    // Criterion 2 / 3
    [Fact]
    public void Store_OnlyTargetMovesThenTimeout_InventoryMismatchWithoutNote()
    {
        Rig rig = Build(confirmTicks: 2);
        rig.Gateway.StoreBehavior = _ => GatewayOutcome.Ok;
        rig.At(1, () => rig.Gateway.Shelf(_dst).Count = 1);

        rig.RunUntilFinished();

        Assert.Equal(StopReason.InventoryMismatch, rig.Report.Reason);
        Assert.Equal("store: inventory expected 0 read 1", rig.Report.Detail);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        rig.AssertI1();
        rig.AssertConservedOrWarned();
    }

    // Criterion 2 / 3
    [Fact]
    public void Store_OnlyInventoryMovesThenTimeout_TargetMismatchWithoutNote()
    {
        Rig rig = Build(confirmTicks: 2);
        rig.Gateway.StoreBehavior = _ => GatewayOutcome.Ok;
        rig.At(1, () => rig.Gateway.Inventory = 0);

        rig.RunUntilFinished();

        Assert.Equal(StopReason.TargetMismatch, rig.Report.Reason);
        Assert.Equal("store: target expected 1 read 0", rig.Report.Detail);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        rig.AssertI1();
    }

    // Criterion 2 (regression expected -> before inside a store wait)
    [Fact]
    public void Store_TargetMovesThenRegressesThenBothApply_KeepsWaiting()
    {
        Rig rig = Build(confirmTicks: 3);
        rig.Gateway.StoreBehavior = _ => GatewayOutcome.Ok;
        rig.At(1, () => rig.Gateway.Shelf(_dst).Count = 1);
        rig.At(2, () => rig.Gateway.Shelf(_dst).Count = 0);
        rig.At(3, () =>
        {
            rig.Gateway.Shelf(_dst).Count = 1;
            rig.Gateway.Inventory = 0;
        });

        rig.RunUntilFinished();

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(new[] { "Info transfer.wait n=1 step=store ticks=3" }, rig.Lines("transfer.wait"));
        Assert.Equal(1, rig.Gateway.StoreCalls);
    }

    // Criterion 1 + 2: both steps delayed in one cycle
    [Fact]
    public void Cycle_BothStepsDelayed_TwoWaitLinesEachBeforeItsStepLine()
    {
        Rig rig = Build(confirmTicks: 3, takeDelay: 2, storeDelay: 3);

        rig.RunUntilFinished();

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(
            new[]
            {
                _start,
                "Info transfer.wait n=1 step=take ticks=2",
                "Info transfer.take n=1 src_before=3 src_after=2 inv_before=0 inv_after=1 verify=ok",
                "Info transfer.wait n=1 step=store ticks=3",
                "Info transfer.store n=1 dst_before=0 dst_after=1 inv_before=1 inv_after=0 verify=ok",
                "Info transfer.stop reason=Completed s0=3 moved=1 remaining=2 stranded=0 detail=null",
            },
            rig.Lines());
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(1, rig.Gateway.StoreCalls);
    }

    // Criterion 1 + 2 + 3: the full grid; success iff every delay is 0 (immediate) or within the bound
    [Fact]
    public void Grid_BoundAndDelays_OutcomeFollowsTheBoundExactlyAndI1HoldsEverywhere()
    {
        foreach (int bound in Enumerable.Range(0, 5))
        {
            foreach (int takeDelay in Enumerable.Range(0, 6))
            {
                foreach (int storeDelay in Enumerable.Range(0, 6))
                {
                    foreach (int budget in new[] { 1, 2, 3 })
                    {
                        string label = $"k={bound} takeDelay={takeDelay} storeDelay={storeDelay} budget={budget}";
                        Rig rig = Build(confirmTicks: bound, takeDelay: takeDelay, storeDelay: storeDelay, amount: 2);

                        rig.RunUntilFinished(maxTicks: 100, maxCycles: budget);

                        bool isTakeOk = takeDelay == 0 || takeDelay <= bound;
                        bool isStoreOk = storeDelay == 0 || storeDelay <= bound;
                        TransferReport report = rig.Report;
                        if (isTakeOk && isStoreOk)
                        {
                            Assert.True(report.Reason == StopReason.Completed, label + " " + report.Reason);
                            Assert.True(report.Moved == 2 && rig.Gateway.TakeCalls == 2 && rig.Gateway.StoreCalls == 2, label);
                        }
                        else if (!isTakeOk)
                        {
                            Assert.True(report.Reason == StopReason.SourceMismatch, label + " " + report.Reason);
                            Assert.True(report.Moved == 0 && rig.Gateway.TakeCalls == 1 && rig.Gateway.StoreCalls == 0, label);
                        }
                        else
                        {
                            Assert.True(report.Reason == StopReason.TargetMismatch, label + " " + report.Reason);
                            Assert.True(report.Moved == 0 && rig.Gateway.TakeCalls == 1 && rig.Gateway.StoreCalls == 1, label);
                        }

                        rig.AssertI1();
                        rig.AssertConservedOrWarned();
                    }
                }
            }
        }
    }

    // Criterion 1 + 2: multi-item run, budget accounting
    [Fact]
    public void Tick_BudgetOneWithDelayedTake_ResumedCycleCountsAsTheOneCycle()
    {
        Rig rig = Build(confirmTicks: 3, takeDelay: 2, amount: 3);
        List<int> takesAfterTick = new();
        List<int> storesAfterTick = new();

        while (rig.Runner.State != TransferState.Finished && rig.TickNo < 50)
        {
            rig.Run(maxCycles: 1);
            takesAfterTick.Add(rig.Gateway.TakeCalls);
            storesAfterTick.Add(rig.Gateway.StoreCalls);
        }

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(3, rig.Report.Moved);
        Assert.Equal(new[] { 1, 1, 1, 2, 2, 2, 3, 3, 3 }, takesAfterTick);
        Assert.Equal(new[] { 0, 0, 1, 1, 1, 2, 2, 2, 3 }, storesAfterTick);
        Assert.Equal(
            new[]
            {
                "Info transfer.wait n=1 step=take ticks=2",
                "Info transfer.wait n=2 step=take ticks=2",
                "Info transfer.wait n=3 step=take ticks=2",
            },
            rig.Lines("transfer.wait"));
        rig.AssertI1();
    }

    // Criterion 1 + 2: a waiting cycle ends the tick even with budget to spare
    [Fact]
    public void Tick_BudgetTenWithWaitingCycle_EndsTheTickAtTheWait()
    {
        Rig rig = Build(confirmTicks: 3, takeDelay: 2, amount: 3);

        rig.Run(maxCycles: 10);

        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(0, rig.Gateway.StoreCalls);
        Assert.Equal(TransferState.TakenAwaitingVerify, rig.Runner.State);
    }

    // Criterion 1 + 2: budget 2 never exceeds two takes and two stores a tick
    [Fact]
    public void Tick_BudgetTwoWithDelayedTakes_NeverExceedsTwoTakesNorTwoStoresPerTick()
    {
        Rig rig = Build(confirmTicks: 3, takeDelay: 2, source: 5, amount: 5);
        int takesBefore = 0;
        int storesBefore = 0;

        while (rig.Runner.State != TransferState.Finished && rig.TickNo < 80)
        {
            rig.Run(maxCycles: 2);
            Assert.True(rig.Gateway.TakeCalls - takesBefore <= 2);
            Assert.True(rig.Gateway.StoreCalls - storesBefore <= 2);
            takesBefore = rig.Gateway.TakeCalls;
            storesBefore = rig.Gateway.StoreCalls;
            rig.AssertI1();
        }

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(5, rig.Report.Moved);
    }

    // Criterion 1 + 3: waits between items do not leak ticks into the next step
    [Fact]
    public void Run_TwoItemsEachDelayed_WaitTicksResetPerStep()
    {
        Rig rig = Build(confirmTicks: 2, takeDelay: 2, storeDelay: 1, amount: 2);

        rig.RunUntilFinished();

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(
            new[]
            {
                "Info transfer.wait n=1 step=take ticks=2",
                "Info transfer.wait n=1 step=store ticks=1",
                "Info transfer.wait n=2 step=take ticks=2",
                "Info transfer.wait n=2 step=store ticks=1",
            },
            rig.Lines("transfer.wait"));
    }

    // ---------------------------------------------------------------- criterion 3: timeout

    // Criterion 3 (exact report, log, calls; I1)
    [Fact]
    public void Timeout_TakeNeverApplied_FinishesSourceMismatchFromFreshReadsAndNeverRetries()
    {
        Rig rig = Build(confirmTicks: 2, takeDelay: 99);

        rig.Run();
        rig.Run();
        List<string> final = rig.CallsDuring(() => rig.Run());

        Assert.Equal(
            new[]
            {
                "IsAlive(src)", "IsAlive(dst)", "GetCount(src)", "GetInventoryCount",
                "IsAlive(src)", "GetCount(src)", "GetInventoryCount",
            },
            final);
        Assert.Equal(
            new[]
            {
                _start,
                "Warn transfer.wait n=1 step=take ticks=2",
                "Warn transfer.take n=1 src_before=3 src_after=3 inv_before=0 inv_after=0 verify=fail",
                "Warn transfer.stop reason=SourceMismatch s0=3 moved=0 remaining=3 stranded=0 detail=take: source expected 2 read 3; no host confirm after 2 ticks",
            },
            rig.Lines());
        int callCount = rig.Gateway.Calls.Count;
        rig.Run();
        rig.Run();
        Assert.Equal(callCount, rig.Gateway.Calls.Count);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(0, rig.Gateway.StoreCalls);
    }

    // Criterion 3: Remaining and Stranded are fresh reads of whatever the host did by the stop
    [Fact]
    public void Timeout_InventoryAppliedLateOnly_ReportUsesFreshReads()
    {
        Rig rig = Build(confirmTicks: 1, takeDelay: 99);
        rig.Run();
        rig.Gateway.Inventory = 1;

        rig.Run();

        TransferReport report = rig.Report;
        Assert.Equal(StopReason.SourceMismatch, report.Reason);
        Assert.Equal(3, report.Remaining);
        Assert.Equal(1, report.StrandedInInventory);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        rig.AssertConservedOrWarned();
    }

    // Criterion 3: bound 0 with a count that has not moved is the existing immediate mismatch, no wait line, no note
    [Fact]
    public void BoundZero_TakeNotApplied_ImmediateSourceMismatchNoWaitLineNoNote()
    {
        Rig rig = Build(confirmTicks: 0, takeDelay: 3);

        rig.Run();

        Assert.Equal(StopReason.SourceMismatch, rig.Report.Reason);
        Assert.Equal("take: source expected 2 read 3", rig.Report.Detail);
        Assert.Empty(rig.Log.Named("transfer.wait"));
        Assert.Equal(1, rig.Gateway.Mutations);
    }

    // Criterion 3: a huge bound never overflows and still never retries
    [Fact]
    public void Wait_IntMaxBoundFiftyTicksUnapplied_StillOneTakeAndWaiting()
    {
        Rig rig = Build(confirmTicks: int.MaxValue, takeDelay: 1000);

        for (int i = 0; i < 50; i++)
        {
            rig.Run();
        }

        Assert.Equal(TransferState.TakenAwaitingVerify, rig.Runner.State);
        Assert.Equal(1, rig.Gateway.Mutations);
        Assert.Empty(rig.Log.Named("transfer.wait"));
    }

    // ---------------------------------------------------------------- criterion 4: unexpected values

    // Criterion 4
    [Fact]
    public void Take_SourceDropsByTwoDuringWait_StopsAtOnceSourceMismatch()
    {
        Rig rig = Build(confirmTicks: 5);
        rig.Gateway.TakeBehavior = _ => GatewayOutcome.Ok;
        rig.At(1, () => rig.Gateway.Shelf(_src).Count = 1);

        rig.Run();
        rig.Run();

        Assert.Equal(StopReason.SourceMismatch, rig.Report.Reason);
        Assert.Equal("take: source expected 2 read 1", rig.Report.Detail);
        Assert.Equal(1, rig.Gateway.Mutations);
        Assert.Equal(
            new[]
            {
                "Warn transfer.wait n=1 step=take ticks=1",
                "Warn transfer.take n=1 src_before=3 src_after=1 inv_before=0 inv_after=0 verify=fail",
            },
            rig.Lines("transfer.wait", "transfer.take"));
        rig.AssertConservedOrWarned();
    }

    // Criterion 4
    [Fact]
    public void Take_InventoryJumpsByTwoWithSourceUnmoved_StopsAtOnceSourceMismatchNoNote()
    {
        Rig rig = Build(confirmTicks: 5);
        rig.Gateway.TakeBehavior = _ => GatewayOutcome.Ok;
        rig.At(1, () => rig.Gateway.Inventory = 2);

        rig.Run();
        rig.Run();

        Assert.Equal(StopReason.SourceMismatch, rig.Report.Reason);
        Assert.Equal("take: source expected 2 read 3", rig.Report.Detail);
        Assert.Equal(1, rig.Gateway.Mutations);
        Assert.Equal(TransferState.Finished, rig.Runner.State);
    }

    // Criterion 4
    [Fact]
    public void Take_InventoryJumpsByTwoWithSourceVerified_StopsAtOnceInventoryMismatch()
    {
        Rig rig = Build(confirmTicks: 5);
        rig.Gateway.TakeBehavior = _ => GatewayOutcome.Ok;
        rig.At(1, () =>
        {
            rig.Gateway.Shelf(_src).Count = 2;
            rig.Gateway.Inventory = 2;
        });

        rig.Run();
        rig.Run();

        Assert.Equal(StopReason.InventoryMismatch, rig.Report.Reason);
        Assert.Equal("take: inventory expected 1 read 2", rig.Report.Detail);
        Assert.Equal(1, rig.Gateway.Mutations);
        Assert.Equal(1, rig.Report.StrandedInInventory);
    }

    // Criterion 4
    [Fact]
    public void Take_InventoryDropsBelowBeforeDuringWait_StopsAtOnceInventoryMismatch()
    {
        Rig rig = Build(confirmTicks: 5);
        rig.Gateway.Inventory = 4;
        rig.Gateway.TakeBehavior = _ => GatewayOutcome.Ok;
        rig.At(1, () =>
        {
            rig.Gateway.Shelf(_src).Count = 2;
            rig.Gateway.Inventory = 3;
        });

        rig.Run();
        rig.Run();

        Assert.Equal(StopReason.InventoryMismatch, rig.Report.Reason);
        Assert.Equal("take: inventory expected 5 read 3", rig.Report.Detail);
        Assert.Equal(1, rig.Gateway.Mutations);
    }

    // Criterion 4
    [Fact]
    public void Store_TargetJumpsByTwoDuringWait_StopsAtOnceTargetMismatch()
    {
        Rig rig = Build(confirmTicks: 5);
        rig.Gateway.StoreBehavior = _ => GatewayOutcome.Ok;
        rig.At(1, () => rig.Gateway.Shelf(_dst).Count = 2);

        rig.Run();
        rig.Run();

        Assert.Equal(StopReason.TargetMismatch, rig.Report.Reason);
        Assert.Equal("store: target expected 1 read 2", rig.Report.Detail);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(
            new[]
            {
                "Warn transfer.wait n=1 step=store ticks=1",
                "Warn transfer.store n=1 dst_before=0 dst_after=2 inv_before=1 inv_after=1 verify=fail",
                "Warn conservation.mismatch check=store n=1 dst_before=0 dst_after=2 inv_before=1 inv_after=1",
            },
            rig.Lines("transfer.wait", "transfer.store", "conservation.mismatch"));
    }

    // Criterion 4
    [Fact]
    public void Store_InventoryJumpsDuringWaitWithTargetVerified_StopsAtOnceInventoryMismatch()
    {
        Rig rig = Build(confirmTicks: 5);
        rig.Gateway.StoreBehavior = _ => GatewayOutcome.Ok;
        rig.At(1, () =>
        {
            rig.Gateway.Shelf(_dst).Count = 1;
            rig.Gateway.Inventory = 5;
        });

        rig.Run();
        rig.Run();

        Assert.Equal(StopReason.InventoryMismatch, rig.Report.Reason);
        Assert.Equal("store: inventory expected 0 read 5", rig.Report.Detail);
        Assert.Equal(1, rig.Gateway.StoreCalls);
    }

    // Criterion 4
    [Fact]
    public void Store_TargetDropsBelowBeforeDuringWait_StopsAtOnceTargetMismatch()
    {
        Rig rig = Build(confirmTicks: 5, target: 4);
        rig.Gateway.StoreBehavior = _ => GatewayOutcome.Ok;
        rig.At(1, () => rig.Gateway.Shelf(_dst).Count = 2);

        rig.Run();
        rig.Run();

        Assert.Equal(StopReason.TargetMismatch, rig.Report.Reason);
        Assert.Equal("store: target expected 5 read 2", rig.Report.Detail);
        Assert.Equal(1, rig.Gateway.StoreCalls);
    }

    // Criterion 4: target drift while the take is still unconfirmed is caught once the take verifies, before any store
    [Fact]
    public void Take_TargetPlusTwoWhileTakeStillUnconfirmed_KeepsWaitingThenStopsBeforeStore()
    {
        Rig rig = Build(confirmTicks: 5, takeDelay: 3);
        rig.At(1, () => rig.Gateway.Shelf(_dst).Count = 2);

        rig.RunUntilFinished();

        TransferReport report = rig.Report;
        Assert.Equal(StopReason.TargetMismatch, report.Reason);
        Assert.Equal("target changed during take wait: expected 0 read 2", report.Detail);
        Assert.Equal(0, rig.Gateway.StoreCalls);
        Assert.Equal(1, report.StrandedInInventory);
        Assert.Equal(new[] { "Info transfer.wait n=1 step=take ticks=3" }, rig.Lines("transfer.wait"));
        rig.AssertConservedOrWarned();
    }

    // ---------------------------------------------------------------- target drift during the take wait

    // Criterion 4 (blocker fix): target +1 or -1 while the take was in flight
    [Theory]
    [InlineData(6)]
    [InlineData(4)]
    public void Take_TargetDriftsDuringTakeWait_TargetMismatchZeroStoreStrandedOne(int driftedTarget)
    {
        Rig rig = Build(confirmTicks: 3, takeDelay: 1, target: 5);
        rig.At(1, () => rig.Gateway.Shelf(_dst).Count = driftedTarget);

        List<string> calls = rig.CallsDuring(() =>
        {
            rig.Run();
            rig.Run();
        });

        TransferReport report = rig.Report;
        string detail = $"target changed during take wait: expected 5 read {driftedTarget}";
        Assert.Equal(StopReason.TargetMismatch, report.Reason);
        Assert.Equal(detail, report.Detail);
        Assert.Equal(0, rig.Gateway.StoreCalls);
        Assert.DoesNotContain("StoreOne(dst)", calls);
        Assert.Equal(new TransferReport(StopReason.TargetMismatch, 1, 0, 2, 1, detail), report);
        Assert.Empty(rig.Log.Named("conservation.mismatch"));
        Assert.Equal(
            new[]
            {
                "Info transfer.wait n=1 step=take ticks=1",
                "Info transfer.take n=1 src_before=3 src_after=2 inv_before=0 inv_after=1 verify=ok",
                $"Warn transfer.stop reason=TargetMismatch s0=3 moved=0 remaining=2 stranded=1 detail={detail}",
            },
            rig.Lines("transfer.wait", "transfer.take", "transfer.stop"));
        rig.AssertConservedOrWarned();
    }

    // Criterion 4: no drift, several wait ticks, target unchanged -> the store goes ahead in the verifying tick
    [Fact]
    public void Take_TargetUnchangedAcrossTakeWait_StoresInTheVerifyingTick()
    {
        Rig rig = Build(confirmTicks: 3, takeDelay: 3, target: 5);

        rig.RunUntilFinished();

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(6, rig.Target);
        Assert.Equal(1, rig.Gateway.StoreCalls);
    }

    // Blocker scenario pinned: an outside +1 on the target while a store is pending looks like the store; the host then applies it.
    [Fact]
    public void Store_OutsidePlusOneOnTargetWhileStorePendingThenHostApplies_TargetMismatchNotFalseSuccess()
    {
        Rig rig = Build(confirmTicks: 5);
        rig.Gateway.StoreBehavior = _ =>
        {
            rig.At(rig.TickNo + 2, () => rig.Gateway.ApplyStore(_dst));
            return GatewayOutcome.Ok;
        };
        rig.At(1, () => rig.Gateway.Shelf(_dst).Count = 1);

        rig.RunUntilFinished();

        TransferReport report = rig.Report;
        Assert.Equal(StopReason.TargetMismatch, report.Reason);
        Assert.Equal("store: target expected 1 read 2", report.Detail);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        Assert.Equal(0, report.Moved);
        Assert.Equal(1, rig.Gateway.TakeCalls);
    }

    // Blocker scenario pinned: an outside +1 mimics the store and the host never applies it -> InventoryMismatch at the bound, never a false success
    [Fact]
    public void Store_OutsidePlusOneOnTargetAndStoreNeverApplied_InventoryMismatchAtBoundNeverFalseSuccess()
    {
        Rig rig = Build(confirmTicks: 2);
        rig.Gateway.StoreBehavior = _ => GatewayOutcome.Ok;
        rig.At(1, () => rig.Gateway.Shelf(_dst).Count = 1);

        rig.RunUntilFinished();

        TransferReport report = rig.Report;
        Assert.Equal(StopReason.InventoryMismatch, report.Reason);
        Assert.Equal("store: inventory expected 0 read 1", report.Detail);
        Assert.Equal(0, report.Moved);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        rig.AssertI1();
    }

    // ---------------------------------------------------------------- criterion 5: cancel, faults

    // Criterion 5
    [Fact]
    public void Cancel_DuringTakeWait_CycleCompletesThenCancelled()
    {
        Rig rig = Build(confirmTicks: 3, takeDelay: 2, amount: 2);
        rig.Run();
        rig.Runner.RequestCancel();
        rig.Run();
        Assert.Equal(TransferState.TakenAwaitingVerify, rig.Runner.State);
        Assert.Equal(1, rig.Gateway.Mutations);

        rig.Run();

        TransferReport report = rig.Report;
        Assert.Equal(StopReason.Cancelled, report.Reason);
        Assert.Equal(1, report.Moved);
        Assert.Equal(2, report.Requested);
        Assert.Equal(2, report.Remaining);
        Assert.Equal(0, report.StrandedInInventory);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        Assert.Equal(1, rig.Target);
        rig.AssertConservedOrWarned();
    }

    // Criterion 5
    [Fact]
    public void Cancel_DuringStoreWait_CycleCompletesThenCancelled()
    {
        Rig rig = Build(confirmTicks: 3, storeDelay: 2, amount: 2);
        rig.Run();
        rig.Runner.RequestCancel();
        rig.Run();
        Assert.Equal(TransferState.StoredAwaitingVerify, rig.Runner.State);

        rig.Run();

        TransferReport report = rig.Report;
        Assert.Equal(StopReason.Cancelled, report.Reason);
        Assert.Equal(1, report.Moved);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        Assert.Equal(0, report.StrandedInInventory);
        Assert.Equal(new[] { "Info transfer.wait n=1 step=store ticks=2" }, rig.Lines("transfer.wait"));
    }

    // Criterion 5: cancel set while a wait times out -> the timeout mismatch wins, not Cancelled
    [Fact]
    public void Cancel_DuringTakeWaitThenTimeout_MismatchReasonWins()
    {
        Rig rig = Build(confirmTicks: 1, takeDelay: 99, amount: 2);
        rig.Run();
        rig.Runner.RequestCancel();

        rig.Run();

        Assert.Equal(StopReason.SourceMismatch, rig.Report.Reason);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(0, rig.Gateway.StoreCalls);
    }

    // Criterion 5: cancel before the first tick of a multi-item run: no wait begins
    [Fact]
    public void Cancel_RequestedBeforeFirstTick_NoTakeAndNoWait()
    {
        Rig rig = Build(confirmTicks: 3, takeDelay: 2, amount: 2);
        rig.Runner.RequestCancel();

        rig.Run();

        Assert.Equal(StopReason.Cancelled, rig.Report.Reason);
        Assert.Equal(0, rig.Gateway.Mutations);
        Assert.Empty(rig.Log.Named("transfer.wait"));
    }

    // Criterion 5: a gateway throw during a wait re-read ends GatewayError via the next Tick (card-7 path); one wait line
    [Fact]
    public void Throw_DuringTakeWaitReread_NextTickFinishesGatewayErrorWithOneWaitLine()
    {
        Rig rig = Build(confirmTicks: 5, takeDelay: 99);
        rig.Run();
        bool hasThrown = false;
        rig.Gateway.OnCall = entry =>
        {
            if (!hasThrown && entry == "GetCount(src)")
            {
                hasThrown = true;
                throw new InvalidOperationException("boom");
            }
        };

        Assert.Throws<InvalidOperationException>(() => rig.Run());
        Assert.Empty(rig.Log.Named("transfer.wait"));
        rig.Run();

        TransferReport report = rig.Report;
        Assert.Equal(StopReason.GatewayError, report.Reason);
        Assert.Equal("previous tick threw during TakenAwaitingVerify", report.Detail);
        Assert.Equal(1, rig.Gateway.Mutations);
        Assert.Equal(new[] { "Warn transfer.wait n=1 step=take ticks=1" }, rig.Lines("transfer.wait"));
        List<string> order = rig.Events();
        Assert.True(order.IndexOf("transfer.wait") < order.IndexOf("transfer.stop"));
        Assert.Equal(3, report.Remaining);
        rig.AssertConservedOrWarned();
        rig.AssertI1();
    }

    // Criterion 5: the driver aborts after the throw
    [Fact]
    public void Throw_DuringStoreWaitReread_AbortFinishesGatewayErrorWithOneWaitLine()
    {
        Rig rig = Build(confirmTicks: 5, storeDelay: 99);
        rig.Run();
        bool hasThrown = false;
        rig.Gateway.OnCall = entry =>
        {
            if (!hasThrown && entry == "GetCount(dst)")
            {
                hasThrown = true;
                throw new InvalidOperationException("boom");
            }
        };
        Assert.Throws<InvalidOperationException>(() => rig.Run());

        rig.Runner.Abort(StopReason.GatewayError, "tick threw");

        TransferReport report = rig.Report;
        Assert.Equal(StopReason.GatewayError, report.Reason);
        Assert.StartsWith("tick threw", report.Detail);
        Assert.Equal(new[] { "Warn transfer.wait n=1 step=store ticks=1" }, rig.Lines("transfer.wait"));
        Assert.Equal(1, rig.Gateway.StoreCalls);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        rig.Run();
        Assert.Equal(2, rig.Gateway.Mutations);
        rig.AssertConservedOrWarned();
    }

    // Criterion 5: Abort between ticks, before any re-read
    [Fact]
    public void Abort_BetweenTicksBeforeFirstReread_WaitLineTicksZeroWarnBeforeStop()
    {
        Rig rig = Build(confirmTicks: 5, takeDelay: 99);
        rig.Run();

        rig.Runner.Abort(StopReason.GatewayError, "driver gave up");

        Assert.Equal(StopReason.GatewayError, rig.Report.Reason);
        List<string> lines = rig.Lines("transfer.wait", "transfer.stop");
        Assert.Equal(2, lines.Count);
        Assert.Equal("Warn transfer.wait n=1 step=take ticks=0", lines[0]);
        Assert.StartsWith("Warn transfer.stop reason=GatewayError", lines[1]);
        Assert.Equal(1, rig.Gateway.Mutations);
        Assert.Equal(3, rig.Report.Remaining);
        rig.Run();
        Assert.Equal(1, rig.Gateway.Mutations);
    }

    // Criterion 5: Abort after some re-reads records the ticks reached
    [Fact]
    public void Abort_AfterTwoWaitTicks_WaitLineCarriesTicksTwo()
    {
        Rig rig = Build(confirmTicks: 5, storeDelay: 99);
        rig.Run();
        rig.Run();
        rig.Run();

        rig.Runner.Abort(StopReason.GatewayError, "driver gave up");

        Assert.Equal(new[] { "Warn transfer.wait n=1 step=store ticks=2" }, rig.Lines("transfer.wait"));
        Assert.Equal(StopReason.GatewayError, rig.Report.Reason);
        Assert.Equal(1, rig.Report.StrandedInInventory);
    }

    // Criterion 5: re-entrant Abort from a gateway callback during a wait
    [Fact]
    public void Reentrant_AbortFromCallbackDuringWait_FirstStopWinsNoMutationAfter()
    {
        Rig rig = Build(confirmTicks: 5, takeDelay: 1);
        rig.Run();
        bool hasFired = false;
        rig.Gateway.OnCall = entry =>
        {
            if (!hasFired && entry == "GetCount(src)")
            {
                hasFired = true;
                rig.Runner.Abort(StopReason.TargetMismatch, "first");
                rig.Runner.Abort(StopReason.ShelfGone, "second");
            }
        };

        rig.Run();

        Assert.Equal(StopReason.TargetMismatch, rig.Report.Reason);
        Assert.StartsWith("first", rig.Report.Detail);
        Assert.Equal(1, rig.Gateway.Mutations);
        Assert.Single(rig.Lines("transfer.wait"));
        Assert.Single(rig.Log.Named("transfer.stop"));
        Assert.Empty(rig.Log.Named("transfer.take"));
        rig.AssertI1();
    }

    // Criterion 5: re-entrant Tick from a gateway callback during a wait
    [Fact]
    public void Reentrant_TickFromCallbackDuringWait_StopsGatewayErrorNoMutationAfter()
    {
        Rig rig = Build(confirmTicks: 5, storeDelay: 3);
        rig.Run();
        bool hasFired = false;
        rig.Gateway.OnCall = entry =>
        {
            if (!hasFired && entry == "GetCount(dst)")
            {
                hasFired = true;
                rig.Runner.Tick(1);
            }
        };

        rig.Run();

        Assert.Equal(StopReason.GatewayError, rig.Report.Reason);
        Assert.StartsWith("re-entrant tick during StoredAwaitingVerify", rig.Report.Detail);
        Assert.Equal(2, rig.Gateway.Mutations);
        Assert.Single(rig.Log.Named("transfer.stop"));
        Assert.Single(rig.Lines("transfer.wait"));
    }

    // ---------------------------------------------------------------- shelf destroyed during a wait

    // Criterion 5 (F2 shelf-gone): src destroyed during a take wait
    [Fact]
    public void ShelfGone_SourceDestroyedDuringTakeWait_ShelfGoneShortCircuitsDstCheck()
    {
        Rig rig = Build(confirmTicks: 5, takeDelay: 99);
        rig.Run();
        rig.Gateway.Shelf(_src).IsAlive = false;

        List<string> calls = rig.CallsDuring(() => rig.Run());

        Assert.Equal("IsAlive(src)", calls[0]);
        Assert.NotEqual("IsAlive(dst)", calls[1]);
        TransferReport report = rig.Report;
        Assert.Equal(StopReason.ShelfGone, report.Reason);
        Assert.StartsWith("shelf gone during take wait", report.Detail);
        Assert.Equal(1, rig.Gateway.Mutations);
        Assert.Equal(new[] { "Warn transfer.wait n=1 step=take ticks=1" }, rig.Lines("transfer.wait"));
        rig.AssertConservedOrWarned();
    }

    // Criterion 5 (F2 shelf-gone): dst destroyed during a take wait
    [Fact]
    public void ShelfGone_TargetDestroyedDuringTakeWait_ShelfGoneNoStore()
    {
        Rig rig = Build(confirmTicks: 5, takeDelay: 1);
        rig.Run();
        rig.Gateway.Shelf(_dst).IsAlive = false;

        rig.Run();

        TransferReport report = rig.Report;
        Assert.Equal(StopReason.ShelfGone, report.Reason);
        Assert.StartsWith("shelf gone during take wait", report.Detail);
        Assert.Equal(0, rig.Gateway.StoreCalls);
        Assert.Equal(new[] { "Warn transfer.wait n=1 step=take ticks=1" }, rig.Lines("transfer.wait"));
        rig.AssertConservedOrWarned();
    }

    // Criterion 5 (F2 shelf-gone): src destroyed during a store wait
    [Fact]
    public void ShelfGone_SourceDestroyedDuringStoreWait_ShelfGoneNoFurtherMutation()
    {
        Rig rig = Build(confirmTicks: 5, storeDelay: 99);
        rig.Run();
        rig.Gateway.Shelf(_src).IsAlive = false;

        rig.Run();

        TransferReport report = rig.Report;
        Assert.Equal(StopReason.ShelfGone, report.Reason);
        Assert.StartsWith("shelf gone during store wait", report.Detail);
        Assert.Equal(2, rig.Gateway.Mutations);
        Assert.Equal(new[] { "Warn transfer.wait n=1 step=store ticks=1" }, rig.Lines("transfer.wait"));
        rig.AssertConservedOrWarned();
    }

    // Criterion 5 (F2 shelf-gone): dst destroyed during a store wait
    [Fact]
    public void ShelfGone_TargetDestroyedDuringStoreWait_ShelfGoneNoFurtherMutation()
    {
        Rig rig = Build(confirmTicks: 5, storeDelay: 99);
        rig.Run();
        rig.Gateway.Shelf(_dst).IsAlive = false;

        rig.Run();

        Assert.Equal(StopReason.ShelfGone, rig.Report.Reason);
        Assert.StartsWith("shelf gone during store wait", rig.Report.Detail);
        Assert.Equal(2, rig.Gateway.Mutations);
        rig.AssertConservedOrWarned();
    }

    // ---------------------------------------------------------------- criterion 6: immediate counts identical to today

    // Criterion 6
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Immediate_BoundZeroVersusLargeBound_IdenticalCallLogLinesAndReport(int scenario)
    {
        Rig zero = BuildScenario(0, scenario);
        Rig large = BuildScenario(1000, scenario);

        zero.Runner.Tick(100);
        large.Runner.Tick(100);

        Assert.Equal(zero.Gateway.Calls, large.Gateway.Calls);
        Assert.Equal(zero.Lines(), large.Lines());
        Assert.Equal(zero.Report, large.Report);
        Assert.Empty(large.Log.Named("transfer.wait"));
        Assert.Equal(zero.Gateway.TakeCalls, large.Gateway.TakeCalls);
        Assert.Equal(zero.Gateway.StoreCalls, large.Gateway.StoreCalls);
    }

    private static Rig BuildScenario(int confirmTicks, int scenario)
    {
        Rig rig = Build(confirmTicks, source: 6, amount: AmountClamp.All);
        if (scenario == 1)
        {
            rig.Gateway.OnCall = entry =>
            {
                if (entry == "StoreOne(dst)" && rig.Gateway.StoreCalls == 3)
                {
                    rig.Runner.RequestCancel();
                }
            };
        }
        else if (scenario == 2)
        {
            rig.Gateway.StoreBehavior = n => n == 2 ? GatewayOutcome.Refused("full") : null;
        }

        return rig;
    }

    // Criterion 6: same, driven one cycle at a time
    [Fact]
    public void Immediate_BudgetOneTicks_BoundZeroVersusLargeBoundIdentical()
    {
        Rig zero = Build(0, source: 4, amount: 4);
        Rig large = Build(50, source: 4, amount: 4);

        while (zero.Runner.State != TransferState.Finished)
        {
            zero.Run();
            large.Run();
            Assert.Equal(zero.Runner.State, large.Runner.State);
            Assert.Equal(zero.Gateway.Calls, large.Gateway.Calls);
        }

        Assert.Equal(zero.Lines(), large.Lines());
        Assert.Equal(zero.Report, large.Report);
    }

    // Criterion 6: the default constructor argument is 0
    [Fact]
    public void Constructor_NoBoundGiven_BehavesLikeBoundZero()
    {
        FakeShelfGateway gateway = new();
        gateway.Add(_src, 3);
        gateway.Add(_dst, 0);
        gateway.TakeBehavior = _ => GatewayOutcome.Ok;
        FakeModLog log = new();
        TransferRunner runner = new(gateway, log, new TransferRequest(_src, new SlotRef(null), _dst, _item, 1));

        runner.Tick(5);

        Assert.Equal(StopReason.SourceMismatch, runner.Report!.Reason);
        Assert.Empty(log.Named("transfer.wait"));
        Assert.Equal(1, gateway.Mutations);
    }

    // Criterion 6
    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Constructor_NegativeBound_Throws(int bound)
    {
        FakeShelfGateway gateway = new();
        gateway.Add(_src, 3);
        gateway.Add(_dst, 0);

        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new TransferRunner(gateway, new FakeModLog(), new TransferRequest(_src, new SlotRef(null), _dst, _item, 1), bound));

        Assert.Equal("confirmTicks", ex.ParamName);
    }

    // ---------------------------------------------------------------- criterion 7: log

    // Criterion 7
    [Fact]
    public void Log_VerifiedWaitIsInfoTimeoutIsWarnBothPrecedeTheStepLineNoWaitLineWhenNoWait()
    {
        Rig verified = Build(confirmTicks: 2, takeDelay: 1);
        verified.RunUntilFinished();
        Rig timedOut = Build(confirmTicks: 2, takeDelay: 9);
        timedOut.RunUntilFinished();
        Rig immediate = Build(confirmTicks: 2);
        immediate.RunUntilFinished();

        List<string> vEvents = verified.Events();
        Assert.Equal(LogLevel.Info, verified.Log.Named("transfer.wait").Single().Level);
        Assert.Equal(vEvents.IndexOf("transfer.take"), vEvents.IndexOf("transfer.wait") + 1);
        List<string> tEvents = timedOut.Events();
        Assert.Equal(LogLevel.Warn, timedOut.Log.Named("transfer.wait").Single().Level);
        Assert.Equal(tEvents.IndexOf("transfer.take"), tEvents.IndexOf("transfer.wait") + 1);
        Assert.Empty(immediate.Log.Named("transfer.wait"));
        Assert.Equal(new[] { "n", "step", "ticks" }, verified.Log.Named("transfer.wait").Single().Fields.Keys.ToArray());
    }

    // Criterion 7: drift is a WARN wait line
    [Fact]
    public void Log_DriftEndsWaitWithWarnWaitLineBeforeTheStepLine()
    {
        Rig rig = Build(confirmTicks: 5);
        rig.Gateway.TakeBehavior = _ => GatewayOutcome.Ok;
        rig.At(1, () => rig.Gateway.Shelf(_src).Count = 1);

        rig.RunUntilFinished();

        List<string> events = rig.Events();
        Assert.Equal(LogLevel.Warn, rig.Log.Named("transfer.wait").Single().Level);
        Assert.Equal(events.IndexOf("transfer.take"), events.IndexOf("transfer.wait") + 1);
    }

    // Criterion 7: a mid-wait stop logs exactly one wait line before the stop, and a second Abort adds none
    [Fact]
    public void Log_MidWaitStopWritesExactlyOneWaitLineBeforeStop()
    {
        Rig rig = Build(confirmTicks: 5, takeDelay: 99);
        rig.Run();
        rig.Run();

        rig.Runner.Abort(StopReason.GatewayError, "x");
        rig.Runner.Abort(StopReason.GatewayError, "y");

        List<string> events = rig.Events();
        Assert.Single(rig.Log.Named("transfer.wait"));
        Assert.Single(rig.Log.Named("transfer.stop"));
        Assert.Equal(events.IndexOf("transfer.stop"), events.IndexOf("transfer.wait") + 1);
    }
}
