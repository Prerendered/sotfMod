// purpose: pins SlotRef (card 45): identified by the game's hook index only, value equality, literal ToString, pass-through to the gateway
// owns:    SlotRefTests
// deps:    SlotRef, AimTarget, TransferRequest, TransferRunner, FakeShelfGateway, FakeModLog

using System.Linq;
using System.Reflection;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Tests.Fakes;
using ShelfTransfer.Core.Transfer;

using Xunit;

namespace ShelfTransfer.Core.Tests.Gateway;

public sealed class SlotRefTests
{
    private static readonly ShelfRef _src = new("src", "StorageShelf", default);
    private static readonly ShelfRef _dst = new("dst", "StorageShelf", default);
    private static readonly ItemId _item = new(312);

    // Criterion 1
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(int.MaxValue)]
    public void Index_RoundTrips(int? index)
    {
        Assert.Equal(index, new SlotRef(index).Index);
    }

    [Fact]
    public void Shape_HasExactlyOneIndexPropertyOfNullableInt_NoRowOrColumn()
    {
        PropertyInfo[] props = typeof(SlotRef).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract").ToArray();

        PropertyInfo only = Assert.Single(props);
        Assert.Equal("Index", only.Name);
        Assert.Equal(typeof(int?), only.PropertyType);
        Assert.Null(typeof(SlotRef).GetProperty("Row"));
        Assert.Null(typeof(SlotRef).GetProperty("Column"));
    }

    // Criterion 2
    [Fact]
    public void Equality_SameIndex_EqualWithSameHash()
    {
        Assert.Equal(new SlotRef(9), new SlotRef(9));
        Assert.Equal(new SlotRef(9).GetHashCode(), new SlotRef(9).GetHashCode());
        Assert.Equal(new SlotRef(null), new SlotRef(null));
        Assert.Equal(new SlotRef(null).GetHashCode(), new SlotRef(null).GetHashCode());
    }

    [Fact]
    public void Equality_DifferentIndex_NotEqual()
    {
        Assert.NotEqual(new SlotRef(9), new SlotRef(10));
    }

    [Fact]
    public void Equality_NullVersusZero_NotEqual()
    {
        Assert.NotEqual(new SlotRef(null), new SlotRef(0));
        Assert.False(new SlotRef(null) == new SlotRef(0));
    }

    [Fact]
    public void ToString_WithIndex_IsPinnedLiteral()
    {
        Assert.Equal("SlotRef { Index = 9 }", new SlotRef(9).ToString());
    }

    [Fact]
    public void ToString_NullIndex_IsPinnedLiteral()
    {
        Assert.Equal("SlotRef { Index =  }", new SlotRef(null).ToString());
    }

    // Criterion 1 (flow): the aimed slot reaches the gateway unchanged
    [Fact]
    public void Runner_RequestCarryingSlot10_GatewayTakeOneReceivesSlot10()
    {
        SlotRef aimed = new AimTarget(_src, new SlotRef(10), _item, 3).Slot;
        FakeShelfGateway gateway = new();
        gateway.Add(_src, 3);
        gateway.Add(_dst, 0);
        TransferRunner runner = new(gateway, new FakeModLog(), new TransferRequest(_src, aimed, _dst, _item, AmountClamp.All));

        runner.Tick(100);

        Assert.Equal(3, gateway.TakeSlots.Count);
        Assert.All(gateway.TakeSlots, s => Assert.Equal(new SlotRef(10), s));
    }

    [Fact]
    public void Runner_NullSlot_GatewayTakeOneReceivesNullIndex()
    {
        FakeShelfGateway gateway = new();
        gateway.Add(_src, 1);
        gateway.Add(_dst, 0);
        TransferRunner runner = new(gateway, new FakeModLog(), new TransferRequest(_src, new SlotRef(null), _dst, _item, AmountClamp.All));

        runner.Tick(100);

        Assert.Null(Assert.Single(gateway.TakeSlots).Index);
    }
}
