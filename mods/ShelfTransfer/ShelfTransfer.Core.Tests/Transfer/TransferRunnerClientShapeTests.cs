// purpose: pins the runner against the StoreOne shape after the I1 fix (card 9): inventory drops inside StoreOne, the target +1 arrives later on a friend-hosted client
// owns:    TransferRunnerClientShapeTests
// deps:    TransferRunner, FakeShelfGateway, FakeModLog
// caution: the host add is scheduled at the end of tick (issue-tick + k - 1), so a wait tick j sees it when j >= k; detail texts are pinned as-is (card 67 will change them)

using System;
using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Tests.Fakes;
using ShelfTransfer.Core.Transfer;

using Xunit;

namespace ShelfTransfer.Core.Tests.Transfer;

public sealed class TransferRunnerClientShapeTests
{
    private static readonly ShelfRef _src = new("src", "StorageShelf", default);
    private static readonly ShelfRef _dst = new("dst", "StorageShelf", default);
    private static readonly ItemId _item = new(312);

    private sealed class Rig
    {
        private readonly List<(int Tick, Action Action)> _scheduled = new();
        private int _tickNo;

        public Rig(int amount, int confirmTicks, int source = 5)
        {
            Gateway = new FakeShelfGateway();
            Gateway.Add(_src, source);
            Gateway.Add(_dst, 0);
            Log = new FakeModLog();
            Runner = new TransferRunner(Gateway, Log, new TransferRequest(_src, new SlotRef(null), _dst, _item, amount), confirmTicks);
            SourceStart = source;
        }

        public FakeShelfGateway Gateway { get; }

        public FakeModLog Log { get; }

        public TransferRunner Runner { get; }

        public int SourceStart { get; }

        public TransferReport Report => Assert.IsType<TransferReport>(Runner.Report);

        /// <summary>Client shape: inventory -1 now, target +1 after k ticks.</summary>
        public void UseClientStore(int k, bool hostApplies = true)
        {
            Gateway.StoreBehavior = _ =>
            {
                if (Gateway.Inventory <= 0)
                {
                    return GatewayOutcome.Refused("no item");
                }

                Gateway.Inventory--;
                if (hostApplies)
                {
                    _scheduled.Add((_tickNo + k - 1, () => Gateway.Shelf(_dst).Count++));
                }

                return GatewayOutcome.Ok;
            };
        }

        public void RunUntilFinished(int maxTicks = 200)
        {
            for (int i = 0; i < maxTicks && Runner.State != TransferState.Finished; i++)
            {
                _tickNo++;
                Runner.Tick(1);
                foreach ((int Tick, Action Action) entry in _scheduled.Where(s => s.Tick <= _tickNo).ToList())
                {
                    _scheduled.Remove(entry);
                    entry.Action();
                }
            }

            Assert.Equal(TransferState.Finished, Runner.State);
        }

        public List<string> Lines(params string[] events) =>
            Log.Entries
                .Where(e => events.Length == 0 || events.Contains(e.Event))
                .Select(e => $"{e.Level} {e.Event} " + string.Join(" ", e.Fields.Select(f => $"{f.Key}={f.Value?.ToString() ?? "null"}")))
                .ToList();

        public List<string> Mutations() =>
            Gateway.Calls.Where(c => c.StartsWith("TakeOne", StringComparison.Ordinal) || c.StartsWith("StoreOne", StringComparison.Ordinal)).ToList();
    }

    // Criterion: one store through the game's own calls (client shape, host applies within the bound)
    [Fact]
    public void ClientShape_HostAppliesWithinBound_WaitsThenVerifiesOk()
    {
        Rig rig = new(amount: 1, confirmTicks: 5);
        rig.UseClientStore(k: 3);

        rig.RunUntilFinished();

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(1, rig.Report.Moved);
        Assert.Equal(0, rig.Report.StrandedInInventory);
        Assert.Equal(
            new[]
            {
                "Info transfer.wait n=1 step=store ticks=3",
                "Info transfer.store n=1 dst_before=0 dst_after=1 inv_before=1 inv_after=0 verify=ok",
            },
            rig.Lines("transfer.wait", "transfer.store"));
        Assert.Empty(rig.Log.Named("conservation.mismatch"));
        Assert.Equal(rig.SourceStart, rig.Gateway.Total(_src, _dst));
        Assert.Equal(new[] { "TakeOne(src)", "StoreOne(dst)" }, rig.Mutations());
    }

    [Fact]
    public void ClientShape_HostAppliesOnFirstWaitTick_StillWaitsOnce()
    {
        Rig rig = new(amount: 1, confirmTicks: 5);
        rig.UseClientStore(k: 1);

        rig.RunUntilFinished();

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Single(rig.Log.Named("transfer.wait"));
    }

    [Fact]
    public void ClientShape_HostAppliesExactlyAtBound_Completes()
    {
        Rig rig = new(amount: 1, confirmTicks: 4);
        rig.UseClientStore(k: 4);

        rig.RunUntilFinished();

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Single(rig.Log.Named("transfer.wait"));
    }

    [Fact]
    public void ClientShape_ThreeItems_CompletesWithMovedThreeAndNoRetry()
    {
        Rig rig = new(amount: 3, confirmTicks: 5);
        rig.UseClientStore(k: 2);

        rig.RunUntilFinished();

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(3, rig.Report.Moved);
        Assert.Equal(0, rig.Report.StrandedInInventory);
        Assert.Equal(2, rig.Report.Remaining);
        Assert.Equal(3, rig.Lines("transfer.store").Count(l => l.EndsWith("verify=ok", StringComparison.Ordinal)));
        Assert.Equal(3, rig.Lines("transfer.wait").Count(l => l.Contains("step=store", StringComparison.Ordinal)));
        Assert.Equal(3, rig.Gateway.StoreCalls);
        Assert.Equal(3, rig.Gateway.TakeCalls);
        Assert.Equal(3, rig.Gateway.Shelf(_dst).Count);
        Assert.Equal(0, rig.Gateway.Inventory);
        Assert.Empty(rig.Log.Named("conservation.mismatch"));
    }

    // Host never applies: current detail text pinned (card 67 will improve it)
    [Fact]
    public void ClientShape_HostNeverApplies_TargetMismatchAfterBound()
    {
        Rig rig = new(amount: 1, confirmTicks: 3);
        rig.UseClientStore(k: 1, hostApplies: false);

        rig.RunUntilFinished();

        Assert.Equal(StopReason.TargetMismatch, rig.Report.Reason);
        Assert.Equal("store: target expected 1 read 0", rig.Report.Detail);
        Assert.Equal(
            new[] { "Warn transfer.stop reason=TargetMismatch s0=5 moved=0 remaining=4 stranded=0 detail=store: target expected 1 read 0" },
            rig.Lines("transfer.stop"));
        Assert.Equal(0, rig.Report.Moved);
        Assert.Equal(0, rig.Report.StrandedInInventory);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(new[] { "TakeOne(src)", "StoreOne(dst)" }, rig.Mutations());
        List<string> mismatches = rig.Lines("conservation.mismatch");
        Assert.Equal(2, mismatches.Count);
        Assert.Equal("Warn conservation.mismatch check=store n=1 dst_before=0 dst_after=0 inv_before=1 inv_after=0", mismatches[0]);
        Assert.StartsWith("Warn conservation.mismatch check=report reason=TargetMismatch", mismatches[1], StringComparison.Ordinal);
    }

    // Host/offline shape: both counts move inside StoreOne
    [Fact]
    public void HostShape_BothMoveInsideStoreOne_NoWait()
    {
        Rig rig = new(amount: 2, confirmTicks: 5);

        rig.RunUntilFinished();

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(2, rig.Report.Moved);
        Assert.Equal(0, rig.Report.StrandedInInventory);
        Assert.Empty(rig.Log.Named("transfer.wait"));
        Assert.Equal(2, rig.Lines("transfer.store").Count(l => l.EndsWith("verify=ok", StringComparison.Ordinal)));
    }

    // Failed after the target +1 and no inventory change: pin current report behaviour
    [Fact]
    public void StoreFailedAfterTargetAdd_InventoryUnchanged_GatewayErrorStopNoFurtherTake()
    {
        Rig rig = new(amount: 3, confirmTicks: 5);
        rig.Gateway.StoreBehavior = _ =>
        {
            rig.Gateway.Shelf(_dst).Count++;
            return GatewayOutcome.Failed("stored but not consumed");
        };

        rig.RunUntilFinished();

        Assert.Equal(StopReason.GatewayError, rig.Report.Reason);
        Assert.Equal("store Failed: stored but not consumed", rig.Report.Detail);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        Assert.Equal(0, rig.Report.Moved);
        Assert.Equal(1, rig.Report.StrandedInInventory);
    }

    // Refused: nothing lost
    [Fact]
    public void StoreRefused_TargetFull_StopsWithOneStrandedNothingLost()
    {
        Rig rig = new(amount: 3, confirmTicks: 5);
        rig.Gateway.StoreBehavior = _ => GatewayOutcome.Refused("target full");

        rig.RunUntilFinished();

        Assert.Equal(StopReason.StoreRefused, rig.Report.Reason);
        Assert.Equal("store Refused: target full", rig.Report.Detail);
        Assert.Equal(1, rig.Report.StrandedInInventory);
        Assert.Equal(0, rig.Report.Moved);
        Assert.Equal(1, rig.Gateway.TakeCalls);
        Assert.Equal(1, rig.Gateway.StoreCalls);
        Assert.Equal(rig.SourceStart, rig.Gateway.Total(_src, _dst));
        Assert.Empty(rig.Log.Named("conservation.mismatch"));
    }
}
