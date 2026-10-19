// purpose: pins card 85: a chunked item split over slots is planned per slot (whole chunks each), never from its rounded total
// owns:    TransferRunnerSplitSlotTests
// deps:    TransferRunner, FakeShelfGateway, FakeModLog
// caution: slots sit at hooks 9, 10, 11 in that order; the any-hook take (SlotRef(null)) takes from the first slot holding a whole chunk

using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Tests.Fakes;
using ShelfTransfer.Core.Transfer;

using Xunit;

namespace ShelfTransfer.Core.Tests.Transfer;

public sealed class TransferRunnerSplitSlotTests
{
    private static readonly ShelfRef _src = new("src", "StorageShelf", default);
    private static readonly ShelfRef _dst = new("dst", "StorageShelf", default);
    private static readonly ItemId _item = new(362);
    private static readonly ItemId _other = new(438);

    private sealed class Rig
    {
        public Rig(int unit, int[] slots, int? slot = null, int amount = AmountClamp.All, int? capacity = null)
        {
            Gateway = new FakeShelfGateway { TransferUnit = unit };
            FakeShelf source = Gateway.Add(_src, 0);
            for (int i = 0; i < slots.Length; i++)
            {
                source.Slots.Add((new SlotRef(9 + i), _item, slots[i]));
            }

            Dst = Gateway.Add(_dst, 0);
            if (capacity is int cap)
            {
                Dst.Capacity = cap;
            }

            Log = new FakeModLog();
            Runner = new TransferRunner(Gateway, Log, new TransferRequest(_src, new SlotRef(slot), _dst, _item, amount));
        }

        public FakeShelfGateway Gateway { get; }

        public FakeShelf Dst { get; }

        public FakeModLog Log { get; }

        public TransferRunner Runner { get; }

        public TransferReport Report => Assert.IsType<TransferReport>(Runner.Report);

        public int SourceNow => Gateway.Shelf(_src).Slots.Sum(s => s.Count);

        public void RunToEnd()
        {
            for (int tick = 0; tick < 200 && Runner.State != TransferState.Finished; tick++)
            {
                Runner.Tick(1);
            }

            Assert.Equal(TransferState.Finished, Runner.State);
        }

        public string StartAmount() => Log.Named("transfer.start").Single().Text("amount");
    }

    // Criterion 1 (15 + 15) and the split shapes the Overseer named: every whole chunk moves, Completed, nothing refused
    [Theory]
    [InlineData(10, new[] { 15, 15 }, 20)]
    [InlineData(10, new[] { 10, 15 }, 20)]
    [InlineData(10, new[] { 10, 10, 7 }, 20)]
    [InlineData(10, new[] { 15, 15, 15 }, 30)]
    [InlineData(10, new[] { 9, 30 }, 30)]
    [InlineData(50, new[] { 60, 60 }, 100)]
    [InlineData(50, new[] { 50, 49, 100 }, 150)]
    public void Tick_ChunkSplitOverSlots_PlansAndMovesEveryWholeChunkThenCompletes(int unit, int[] slots, int expected)
    {
        Rig rig = new(unit, slots);

        rig.RunToEnd();

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(expected, rig.Runner.Requested);
        Assert.Equal(expected.ToString(CultureInfo.InvariantCulture), rig.StartAmount());
        Assert.Equal(expected, rig.Report.Moved);
        Assert.Equal(slots.Sum() - expected, rig.Report.Remaining);
        Assert.Equal(0, rig.Report.StrandedInInventory);
        Assert.Equal(expected / unit, rig.Gateway.TakeCalls);
        Assert.Equal(expected, rig.Dst.Count);
        Assert.Equal(slots.Sum() - expected, rig.SourceNow);
        Assert.Equal(0, rig.Gateway.Inventory);
    }

    // Criterion 3 at the runner: at least one chunk in total, none in any slot -> blocked before any take, with the split detail
    [Theory]
    [InlineData(10, new[] { 5, 5, 5 })]
    [InlineData(10, new[] { 9, 9 })]
    [InlineData(50, new[] { 40, 40, 40 })]
    public void Tick_TotalAtLeastOneChunkButNoWholeChunkInAnySlot_BlockedBeforeAnyTake(int unit, int[] slots)
    {
        Rig rig = new(unit, slots);

        rig.RunToEnd();

        Assert.Equal(StopReason.BlockedBeforeStart, rig.Report.Reason);
        Assert.Equal($"no slot holds a whole chunk of {unit}", rig.Report.Detail);
        Assert.Equal(0, rig.Runner.Requested);
        Assert.Equal("0", rig.StartAmount());
        Assert.Equal(0, rig.Gateway.Mutations);
        Assert.Equal(slots.Sum(), rig.Report.Remaining);
    }

    // Under one chunk in total keeps its card 68 reason and text (the split read never runs)
    [Fact]
    public void Tick_TotalUnderOneChunk_KeepsNothingToMoveDetailWithoutTheSplitRead()
    {
        Rig rig = new(10, new[] { 4, 5 });

        rig.RunToEnd();

        Assert.Equal(StopReason.BlockedBeforeStart, rig.Report.Reason);
        Assert.Equal("NothingToMove, less than one chunk of 10", rig.Report.Detail);
        Assert.Equal(0, rig.Gateway.MovableCountReads);
    }

    // A target that fits under one chunk still reports TargetFull first, as before card 85
    [Fact]
    public void Tick_SplitSlotsAndTargetUnderOneChunk_KeepsTargetFullDetail()
    {
        Rig rig = new(10, new[] { 5, 5, 5 }, capacity: 5);

        rig.RunToEnd();

        Assert.Equal("TargetFull, less than one chunk of 10", rig.Report.Detail);
        Assert.Equal(0, rig.Gateway.Mutations);
    }

    // The clamp still bounds by request and target free after the per-slot cap
    [Theory]
    [InlineData(AmountClamp.All, 10, 10)]
    [InlineData(10, null, 10)]
    [InlineData(25, null, 20)]
    [InlineData(AmountClamp.All, 25, 20)]
    public void Tick_SplitSlotsWithRequestOrTargetBelowMovable_MovesTheSmallerInWholeChunks(int amount, int? capacity, int expected)
    {
        Rig rig = new(10, new[] { 15, 15 }, amount: amount, capacity: capacity);

        rig.RunToEnd();

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(expected, rig.Report.Moved);
    }

    // An aimed slot (overlay, typed prompt) takes only from that slot, so only its whole chunks are planned
    [Theory]
    [InlineData(9, 10)]
    [InlineData(10, 20)]
    public void Tick_AimedSlotOfASplitType_PlansOnlyThatSlotsWholeChunks(int slot, int expected)
    {
        Rig rig = new(10, new[] { 15, 25 }, slot: slot);

        rig.RunToEnd();

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(expected, rig.Report.Moved);
        Assert.All(rig.Gateway.TakeSlots, s => Assert.Equal(slot, s.Index));
    }

    // Criterion 2: unit 1 never reads the per-slot count, and its calls and log match a single-slot shelf of the same total
    [Theory]
    [InlineData(new[] { 15, 15 })]
    [InlineData(new[] { 5, 5, 5 })]
    [InlineData(new[] { 10, 10, 7 })]
    public void Tick_UnitOneSplitOverSlots_UnchangedFromOneSlotOfTheSameTotal(int[] slots)
    {
        Rig split = new(1, slots);
        Rig single = new(1, new[] { slots.Sum() });

        split.RunToEnd();
        single.RunToEnd();

        Assert.Equal(StopReason.Completed, split.Report.Reason);
        Assert.Equal(slots.Sum(), split.Report.Moved);
        Assert.Equal(0, split.Gateway.MovableCountReads);
        Assert.Equal(single.Gateway.Calls, split.Gateway.Calls);
        Assert.Equal(FormatLog(single.Log), FormatLog(split.Log));
    }

    // A slot of another item never counts towards the planned amount
    [Fact]
    public void Tick_OtherItemSlotBetweenSplitSlots_IsNotCounted()
    {
        Rig rig = new(10, new[] { 15 });
        rig.Gateway.Shelf(_src).Slots.Add((new SlotRef(10), _other, 40));
        rig.Gateway.Shelf(_src).Slots.Add((new SlotRef(11), _item, 12));

        rig.RunToEnd();

        Assert.Equal(StopReason.Completed, rig.Report.Reason);
        Assert.Equal(20, rig.Report.Moved);
        Assert.Equal(40, rig.Gateway.Shelf(_src).Slots.Single(s => s.Item == _other).Count);
    }

    private static List<string> FormatLog(FakeModLog log) =>
        log.Entries.Select(e => $"{e.Level} {e.Event} " + string.Join(" ", e.Fields.Select(f => $"{f.Key}={f.Value}"))).ToList();
}
