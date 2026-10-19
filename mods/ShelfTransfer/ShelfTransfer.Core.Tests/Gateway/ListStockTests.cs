// purpose: pins ListStock (card 74): one entry per stocked item type, summed over slots, stable order, read-only, unit per type
// owns:    ListStockTests
// deps:    ShelfStockEntry, FakeShelfGateway

using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Tests.Fakes;

using Xunit;

namespace ShelfTransfer.Core.Tests.Gateway;

public sealed class ListStockTests
{
    private static readonly ShelfRef _shelf = new("shelf", "StorageShelf", default);
    private static readonly ItemId _mre = new(438);
    private static readonly ItemId _ammo = new(362);
    private static readonly ItemId _tarp = new(504);

    private static FakeShelfGateway BuildMixed(params (int? Slot, ItemId Item, int Count)[] slots)
    {
        FakeShelfGateway gateway = new();
        gateway.ItemUnits[_ammo] = 10;
        FakeShelf fake = gateway.Add(_shelf, 0);
        foreach ((int? slot, ItemId item, int count) in slots)
        {
            fake.Slots.Add((new SlotRef(slot), item, count));
        }

        return gateway;
    }

    // Criterion 1
    [Fact]
    public void ListStock_MixedShelf_OneEntryPerTypeSummedOverSlots()
    {
        FakeShelfGateway gateway = BuildMixed((8, _mre, 6), (9, _ammo, 30), (10, _mre, 4), (11, _tarp, 2));

        IReadOnlyList<ShelfStockEntry> stock = gateway.ListStock(_shelf);

        Assert.Equal(new[]
        {
            new ShelfStockEntry(_mre, 10, 1, new SlotRef(8)),
            new ShelfStockEntry(_ammo, 30, 10, new SlotRef(9)),
            new ShelfStockEntry(_tarp, 2, 1, new SlotRef(11)),
        }, stock);
    }

    [Fact]
    public void ListStock_EmptyShelf_EmptyListNotNull()
    {
        FakeShelfGateway gateway = BuildMixed();

        IReadOnlyList<ShelfStockEntry> stock = gateway.ListStock(_shelf);

        Assert.NotNull(stock);
        Assert.Empty(stock);
    }

    [Fact]
    public void ListStock_OnlyZeroCountSlots_EmptyList()
    {
        FakeShelfGateway gateway = BuildMixed((8, _mre, 0), (9, _ammo, 0));

        Assert.Empty(gateway.ListStock(_shelf));
    }

    [Fact]
    public void ListStock_HookOrderDiffersFromIndex_OrderedByFirstStockedSlotThenItemId()
    {
        FakeShelfGateway gateway = BuildMixed((11, _mre, 1), (10, _tarp, 1), (9, _mre, 0), (10, _ammo, 10));

        IReadOnlyList<ShelfStockEntry> stock = gateway.ListStock(_shelf);

        Assert.Equal(new[] { _ammo, _tarp, _mre }, Items(stock));
        Assert.Equal(new SlotRef(11), stock[2].FirstSlot);
    }

    [Fact]
    public void ListStock_UnknownSlotIndex_SortsAfterKnownSlots()
    {
        FakeShelfGateway gateway = BuildMixed((null, _ammo, 10), (11, _tarp, 1));

        Assert.Equal(new[] { _tarp, _ammo }, Items(gateway.ListStock(_shelf)));
    }

    [Fact]
    public void ListStock_CalledTwice_SameOrder()
    {
        FakeShelfGateway gateway = BuildMixed((9, _tarp, 1), (8, _mre, 1), (10, _ammo, 10));

        Assert.Equal(gateway.ListStock(_shelf), gateway.ListStock(_shelf));
    }

    // Criterion 2
    [Fact]
    public void ListStock_ShelfGone_EmptyListNoThrow()
    {
        FakeShelfGateway gateway = BuildMixed((8, _mre, 6));
        gateway.Shelf(_shelf).IsAlive = false;

        Assert.Empty(gateway.ListStock(_shelf));
    }

    [Fact]
    public void ListStock_UnknownShelf_EmptyListNoThrow()
    {
        FakeShelfGateway gateway = new();

        Assert.Empty(gateway.ListStock(new ShelfRef("missing", "StorageShelf", default)));
    }

    [Fact]
    public void ListStock_AnyShelf_NoMutatingCall()
    {
        FakeShelfGateway gateway = BuildMixed((8, _mre, 6), (9, _ammo, 30));

        gateway.ListStock(_shelf);
        gateway.Shelf(_shelf).IsAlive = false;
        gateway.ListStock(_shelf);

        Assert.Equal(0, gateway.Mutations);
        Assert.Equal(6, gateway.GetCount(_shelf, _mre));
        Assert.Equal(30, gateway.GetCount(_shelf, _ammo));
    }

    // Criterion 3
    [Fact]
    public void ListStock_EachEntry_TransferUnitEqualsGetTransferUnit()
    {
        FakeShelfGateway gateway = BuildMixed((8, _mre, 6), (9, _ammo, 30));

        IReadOnlyList<ShelfStockEntry> stock = gateway.ListStock(_shelf);

        Assert.Equal(new[] { 1, 10 }, new[] { stock[0].TransferUnit, stock[1].TransferUnit });
        Assert.All(stock, e => Assert.Equal(gateway.GetTransferUnit(_shelf, e.FirstSlot, e.Item), e.TransferUnit));
    }

    [Fact]
    public void ListStock_CountBelowOneChunk_StillListed()
    {
        FakeShelfGateway gateway = BuildMixed((9, _ammo, 5));

        ShelfStockEntry entry = Assert.Single(gateway.ListStock(_shelf));

        Assert.Equal(new ShelfStockEntry(_ammo, 5, 10, new SlotRef(9)), entry);
    }

    private static ItemId[] Items(IReadOnlyList<ShelfStockEntry> stock) => stock.Select(e => e.Item).ToArray();
}
