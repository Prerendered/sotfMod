// purpose: pins how the runner consumes the interop gateway's card-9 outcomes (refusal details, shelf gone, swallowed reads)
// owns:    TransferRunnerGatewayDetailTests
// deps:    TransferRunner, FakeShelfGateway, FakeModLog
// caution: the details are the strings InteropShelfGateway returns; a gateway that throws is swallowed to 0/false/Failed before Core sees it

using System;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Tests.Fakes;
using ShelfTransfer.Core.Transfer;

using Xunit;

namespace ShelfTransfer.Core.Tests.Transfer;

public sealed class TransferRunnerGatewayDetailTests
{
    private static readonly ShelfRef _src = new("src", "StorageShelf", default);
    private static readonly ShelfRef _dst = new("dst", "StorageShelf", default);
    private static readonly ItemId _item = new(312);

    private static (FakeShelfGateway Gateway, FakeModLog Log, TransferRunner Runner) Build(int source, int target = 0)
    {
        FakeShelfGateway gateway = new();
        gateway.Add(_src, source);
        gateway.Add(_dst, target);
        FakeModLog log = new();
        TransferRunner runner = new(gateway, log, new TransferRequest(_src, new SlotRef(8), _dst, _item, AmountClamp.All));
        return (gateway, log, runner);
    }

    // Card 9 criterion 5: the take refusals the interop adapter can return on a first live use (card 12)
    [Theory]
    [InlineData("transfer unit 5")]
    [InlineData("transfer unit 0")]
    [InlineData("slot empty")]
    [InlineData("slot holds item 99")]
    [InlineData("no slot holds the item")]
    public void Tick_FirstTakeRefusedWithAdapterDetail_TakeRefusedNothingMovedNoStoreDetailCarried(string detail)
    {
        (FakeShelfGateway gateway, _, TransferRunner runner) = Build(source: 4);
        gateway.TakeBehavior = _ => GatewayOutcome.Refused(detail);

        runner.Tick(10);

        TransferReport report = Assert.IsType<TransferReport>(runner.Report);
        Assert.Equal(StopReason.TakeRefused, report.Reason);
        Assert.Equal(0, report.Moved);
        Assert.Equal(4, report.Remaining);
        Assert.Equal(0, report.StrandedInInventory);
        Assert.Equal($"take Refused: {detail}", report.Detail);
        Assert.Equal(1, gateway.TakeCalls);
        Assert.Equal(0, gateway.StoreCalls);
    }

    // Card 9 criterion 3: a take or store on a gone shelf is Failed, which is a GatewayError (not a refusal)
    [Fact]
    public void Tick_TakeFailedShelfGone_GatewayErrorWithDetailAndNoStore()
    {
        (FakeShelfGateway gateway, _, TransferRunner runner) = Build(source: 4);
        gateway.TakeBehavior = _ => GatewayOutcome.Failed("shelf gone");

        runner.Tick(10);

        TransferReport report = Assert.IsType<TransferReport>(runner.Report);
        Assert.Equal(StopReason.GatewayError, report.Reason);
        Assert.Equal(0, report.Moved);
        Assert.Equal(4, report.Remaining);
        Assert.Equal("take Failed: shelf gone", report.Detail);
        Assert.Equal(0, gateway.StoreCalls);
    }

    [Theory]
    [InlineData("shelf gone")]
    [InlineData("no player inventory")]
    public void Tick_StoreFailedWithAdapterDetail_GatewayErrorOneStrandedDetailCarried(string detail)
    {
        (FakeShelfGateway gateway, _, TransferRunner runner) = Build(source: 4);
        gateway.StoreBehavior = _ => GatewayOutcome.Failed(detail);

        runner.Tick(10);

        TransferReport report = Assert.IsType<TransferReport>(runner.Report);
        Assert.Equal(StopReason.GatewayError, report.Reason);
        Assert.Equal(0, report.Moved);
        Assert.Equal(3, report.Remaining);
        Assert.Equal(1, report.StrandedInInventory);
        Assert.Equal($"store Failed: {detail}", report.Detail);
        Assert.Equal(1, gateway.TakeCalls);
    }

    // Card 9 criterion 2: the adapter reports can_hold=false free=0 for a rejecting shelf; Planning never reads free then
    [Fact]
    public void Tick_TargetRejectsTypeWithFreeZero_BlockedBeforeStartWithoutReadingFreeCapacity()
    {
        (FakeShelfGateway gateway, _, TransferRunner runner) = Build(source: 4);
        FakeShelf target = gateway.Shelf(_dst);
        target.CanHold = false;
        target.Capacity = 0;

        runner.Tick(10);

        TransferReport report = Assert.IsType<TransferReport>(runner.Report);
        Assert.Equal(StopReason.BlockedBeforeStart, report.Reason);
        Assert.Equal("target cannot hold item", report.Detail);
        Assert.Equal(0, gateway.Mutations);
        Assert.DoesNotContain(gateway.Calls, call => call.StartsWith("GetFreeCapacity", StringComparison.Ordinal));
    }

    // CanHold true but nothing free: the clamp, not the can-hold branch, stops it
    [Fact]
    public void Tick_TargetCanHoldButFreeZero_BlockedBeforeStartTargetFull()
    {
        (FakeShelfGateway gateway, _, TransferRunner runner) = Build(source: 4, target: 6);
        gateway.Shelf(_dst).Capacity = 6;

        runner.Tick(10);

        TransferReport report = Assert.IsType<TransferReport>(runner.Report);
        Assert.Equal(StopReason.BlockedBeforeStart, report.Reason);
        Assert.Equal("TargetFull", report.Detail);
        Assert.Equal(0, gateway.Mutations);
    }

    // The adapter turns a swallowed throw into 0; a source count read as 0 mid-run must stop before the next take
    [Fact]
    public void Tick_SourceCountReadsZeroAfterSwallowedThrowMidRun_SourceMismatchBeforeNextTake()
    {
        (FakeShelfGateway gateway, _, TransferRunner runner) = Build(source: 4);
        int sourceReads = 0;
        gateway.OnCall = call =>
        {
            if (call == "GetCount(src)" && ++sourceReads == 4)
            {
                gateway.Shelf(_src).Count = 0;
            }
        };

        runner.Tick(10);

        TransferReport report = Assert.IsType<TransferReport>(runner.Report);
        Assert.Equal(StopReason.SourceMismatch, report.Reason);
        Assert.Equal(1, gateway.TakeCalls);
        Assert.Equal(1, gateway.StoreCalls);
    }

    // A planning read swallowed to 0 on a live source means nothing to move; it must not take
    [Fact]
    public void Tick_SourceCountReadsZeroAtPlanning_BlockedBeforeStartZeroMutations()
    {
        (FakeShelfGateway gateway, _, TransferRunner runner) = Build(source: 0);

        runner.Tick(10);

        TransferReport report = Assert.IsType<TransferReport>(runner.Report);
        Assert.Equal(StopReason.BlockedBeforeStart, report.Reason);
        Assert.Equal(0, gateway.Mutations);
    }
}
