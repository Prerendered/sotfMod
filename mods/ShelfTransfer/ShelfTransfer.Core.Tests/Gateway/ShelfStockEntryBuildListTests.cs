// purpose: pins ShelfStockEntry.BuildList (card 74): folding, per-slot unit pairing, null-index ordering, junk counts, determinism
// owns:    ShelfStockEntryBuildListTests
// deps:    ShelfStockEntry, FakeShelfGateway

using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Tests.Fakes;

using Xunit;

namespace ShelfTransfer.Core.Tests.Gateway;

public sealed class ShelfStockEntryBuildListTests
{
    private static readonly ItemId _a = new(100);
    private static readonly ItemId _b = new(200);
    private static readonly ItemId _c = new(300);

    private static (SlotRef Slot, ItemId Item, int Count, int TransferUnit) S(int? slot, ItemId item, int count, int unit) =>
        (new SlotRef(slot), item, count, unit);

    private static ItemId[] Items(IReadOnlyList<ShelfStockEntry> list) => list.Select(e => e.Item).ToArray();

    [Fact]
    public void BuildList_NoSlots_EmptyNotNull()
    {
        IReadOnlyList<ShelfStockEntry> list = ShelfStockEntry.BuildList(new List<(SlotRef, ItemId, int, int)>());

        Assert.NotNull(list);
        Assert.Empty(list);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void BuildList_ZeroOrNegativeCount_Ignored(int count)
    {
        Assert.Empty(ShelfStockEntry.BuildList(new[] { S(1, _a, count, 1) }));
    }

    [Fact]
    public void BuildList_NegativeCountDoesNotReduceSum()
    {
        ShelfStockEntry entry = Assert.Single(ShelfStockEntry.BuildList(new[] { S(1, _a, 5, 1), S(2, _a, -3, 1), S(3, _a, 0, 1) }));

        Assert.Equal(5, entry.Count);
    }

    [Fact]
    public void BuildList_SameItemDifferingUnits_LowerSlotLaterInInput_UnitAndFirstSlotFromSameSlot()
    {
        ShelfStockEntry entry = Assert.Single(ShelfStockEntry.BuildList(new[] { S(5, _a, 3, 1), S(2, _a, 7, 10) }));

        Assert.Equal(new ShelfStockEntry(_a, 10, 10, new SlotRef(2)), entry);
    }

    [Fact]
    public void BuildList_SameItemDifferingUnits_LowerSlotFirstInInput_KeepsItsUnit()
    {
        ShelfStockEntry entry = Assert.Single(ShelfStockEntry.BuildList(new[] { S(2, _a, 7, 10), S(5, _a, 3, 1) }));

        Assert.Equal(new ShelfStockEntry(_a, 10, 10, new SlotRef(2)), entry);
    }

    [Fact]
    public void BuildList_EqualSlotIndexSameItem_EarlierInInputWins()
    {
        ShelfStockEntry entry = Assert.Single(ShelfStockEntry.BuildList(new[] { S(4, _a, 1, 10), S(4, _a, 2, 1) }));

        Assert.Equal(new ShelfStockEntry(_a, 3, 10, new SlotRef(4)), entry);
    }

    [Fact]
    public void BuildList_KnownSlotAfterNullSlot_KnownBecomesFirstWithItsUnit()
    {
        ShelfStockEntry entry = Assert.Single(ShelfStockEntry.BuildList(new[] { S(null, _a, 1, 1), S(9, _a, 2, 10) }));

        Assert.Equal(new ShelfStockEntry(_a, 3, 10, new SlotRef(9)), entry);
    }

    [Fact]
    public void BuildList_NullSlotAfterKnownSlot_KnownStaysFirst()
    {
        ShelfStockEntry entry = Assert.Single(ShelfStockEntry.BuildList(new[] { S(9, _a, 2, 10), S(null, _a, 1, 1) }));

        Assert.Equal(new ShelfStockEntry(_a, 3, 10, new SlotRef(9)), entry);
    }

    [Fact]
    public void BuildList_AllNullSlots_OrderedByItemId()
    {
        IReadOnlyList<ShelfStockEntry> list = ShelfStockEntry.BuildList(new[] { S(null, _c, 1, 1), S(null, _a, 1, 1), S(null, _b, 1, 1) });

        Assert.Equal(new[] { _a, _b, _c }, Items(list));
    }

    [Fact]
    public void BuildList_NullSlotSortsLast_EvenAfterHighKnownIndex()
    {
        IReadOnlyList<ShelfStockEntry> list = ShelfStockEntry.BuildList(new[] { S(null, _a, 1, 1), S(int.MaxValue, _b, 1, 1), S(0, _c, 1, 1) });

        Assert.Equal(new[] { _c, _b, _a }, Items(list));
    }

    [Fact]
    public void BuildList_SameFirstSlotIndexDifferentItems_ItemIdBreaksTie()
    {
        IReadOnlyList<ShelfStockEntry> list = ShelfStockEntry.BuildList(new[] { S(3, _c, 1, 1), S(3, _a, 1, 1) });

        Assert.Equal(new[] { _a, _c }, Items(list));
    }

    [Fact]
    public void BuildList_NegativeSlotIndex_SortsBeforeZero()
    {
        IReadOnlyList<ShelfStockEntry> list = ShelfStockEntry.BuildList(new[] { S(0, _a, 1, 1), S(-1, _b, 1, 1) });

        Assert.Equal(new[] { _b, _a }, Items(list));
    }

    [Fact]
    public void BuildList_ManyTypes_OneEntryEachAscendingBySlot()
    {
        List<(SlotRef Slot, ItemId Item, int Count, int TransferUnit)> input = Enumerable.Range(0, 200).Reverse()
            .Select(i => S(i, new ItemId(1000 + i), i + 1, 1)).ToList();

        IReadOnlyList<ShelfStockEntry> list = ShelfStockEntry.BuildList(input);

        Assert.Equal(200, list.Count);
        Assert.Equal(Enumerable.Range(0, 200), list.Select(e => e.FirstSlot.Index!.Value));
        Assert.Equal(200, list.Select(e => e.Item).Distinct().Count());
    }

    [Fact]
    public void BuildList_InputPermutations_SameResult()
    {
        (SlotRef Slot, ItemId Item, int Count, int TransferUnit)[] input =
            { S(3, _a, 1, 1), S(1, _b, 2, 10), S(2, _a, 4, 1), S(null, _c, 1, 1) };
        IReadOnlyList<ShelfStockEntry> expected = ShelfStockEntry.BuildList(input);

        Assert.Equal(expected, ShelfStockEntry.BuildList(input.Reverse()));
        Assert.Equal(expected, ShelfStockEntry.BuildList(input.OrderBy(x => x.Count)));
        Assert.Equal(expected, ShelfStockEntry.BuildList(input));
    }

    [Fact]
    public void BuildList_ZeroUnit_PassedThrough()
    {
        ShelfStockEntry entry = Assert.Single(ShelfStockEntry.BuildList(new[] { S(1, _a, 2, 0) }));

        Assert.Equal(0, entry.TransferUnit);
    }

    [Fact]
    public void BuildList_LargeCountsSummed()
    {
        ShelfStockEntry entry = Assert.Single(ShelfStockEntry.BuildList(new[] { S(1, _a, 1_000_000, 1), S(2, _a, 1_000_000, 1) }));

        Assert.Equal(2_000_000, entry.Count);
    }

    [Fact]
    public void FakeListStock_SameItemUnitsPerSlot_PairsFirstSlotWithItsUnit()
    {
        ShelfRef shelf = new("k", "StorageShelf", default);
        FakeShelfGateway gateway = new();
        FakeShelf fake = gateway.Add(shelf, 0);
        fake.Slots.Add((new SlotRef(5), _a, 3));
        fake.Slots.Add((new SlotRef(2), _a, 7));
        gateway.ItemUnits[_a] = 10;

        ShelfStockEntry entry = Assert.Single(gateway.ListStock(shelf));

        Assert.Equal(new SlotRef(2), entry.FirstSlot);
        Assert.Equal(gateway.GetTransferUnit(shelf, entry.FirstSlot, entry.Item), entry.TransferUnit);
        Assert.Equal(entry.Count, gateway.GetCount(shelf, entry.Item));
    }

    [Fact]
    public void FakeListStock_CountsAgreeWithGetCountPerItem()
    {
        ShelfRef shelf = new("k", "StorageShelf", default);
        FakeShelfGateway gateway = new();
        FakeShelf fake = gateway.Add(shelf, 0);
        fake.Slots.Add((new SlotRef(1), _a, 3));
        fake.Slots.Add((new SlotRef(2), _b, 5));
        fake.Slots.Add((new SlotRef(3), _a, 4));
        fake.Slots.Add((new SlotRef(4), _c, 0));

        IReadOnlyList<ShelfStockEntry> stock = gateway.ListStock(shelf);

        Assert.Equal(2, stock.Count);
        Assert.All(stock, e => Assert.Equal(gateway.GetCount(shelf, e.Item), e.Count));
        Assert.Equal(0, gateway.Mutations);
    }
}
