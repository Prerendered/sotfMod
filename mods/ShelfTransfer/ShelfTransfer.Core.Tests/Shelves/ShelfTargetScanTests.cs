// purpose: pins ShelfTargetScan (card 80): the shelf-only filter, candidate order, gateway call order, and the no-aim case
// owns:    ShelfTargetScanTests
// deps:    ShelfTargetScan, ShelfCandidate, ShelfRef, FakeShelfGateway
// caution: the fake throws for a shelf it was never given, so an excluded storage proves no gateway read happened for it

using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Shelves;
using ShelfTransfer.Core.Tests.Fakes;

using Xunit;

namespace ShelfTransfer.Core.Tests.Shelves;

public sealed class ShelfTargetScanTests
{
    private static readonly ItemId _item = new(7);

    private static ShelfRef Ref(string key, string type = "ShelfStructure") => new(key, type, new WorldPosition(1f, 0f, 0f));

    [Theory]
    [InlineData("ShelfStructure", true)]
    [InlineData("shelfstructure", true)]
    [InlineData("SHELF", true)]
    [InlineData("StorageShelf(Clone)", true)]
    [InlineData("Mannequin", false)]
    [InlineData("HeadTrophyMount", false)]
    [InlineData("StorageArrows", false)]
    [InlineData("", false)]
    public void IsShelf_MatchesPrefabNameIgnoringCase(string typeName, bool expected) =>
        Assert.Equal(expected, ShelfTargetScan.IsShelf(Ref("k", typeName)));

    [Fact]
    public void IsShelf_NullTypeName_Throws()
    {
        // A null TypeName cannot come from the plugin (IL2CPP strings are never null); pins current behaviour.
        ShelfRef broken = Ref("k") with { TypeName = null! };

        Assert.ThrowsAny<System.Exception>(() => ShelfTargetScan.IsShelf(broken));
    }

    [Fact]
    public void ReadCandidates_DropsNonShelvesAndReadsNothingForThem()
    {
        FakeShelfGateway gateway = new();
        ShelfRef a = Ref("a");
        ShelfRef b = Ref("b");
        gateway.Add(a, 0).Capacity = 10;
        gateway.Add(b, 0).Capacity = 10;
        ShelfRef[] registry = { Ref("m", "Mannequin"), a, Ref("t", "HeadTrophyMount"), b, Ref("r", "StorageArrows") };

        IReadOnlyList<ShelfCandidate> result = ShelfTargetScan.ReadCandidates(gateway, registry, _item);

        Assert.Equal(new[] { "a", "b" }, result.Select(c => c.Shelf.Key));
        Assert.DoesNotContain(gateway.Calls, call => call.Contains("(m)") || call.Contains("(t)") || call.Contains("(r)"));
    }

    [Fact]
    public void ReadCandidates_CallsCanHoldThenFreeCapacityPerShelf_InRegistryOrder()
    {
        FakeShelfGateway gateway = new();
        ShelfRef a = Ref("a");
        ShelfRef b = Ref("b");
        gateway.Add(a, 0);
        gateway.Add(b, 0);

        ShelfTargetScan.ReadCandidates(gateway, new[] { b, a }, _item);

        Assert.Equal(new[] { "CanHold(b)", "GetFreeCapacity(b)", "CanHold(a)", "GetFreeCapacity(a)" }, gateway.Calls);
        Assert.Equal(0, gateway.Mutations);
    }

    [Fact]
    public void ReadCandidates_CarriesCanHoldAndFreeSpace()
    {
        FakeShelfGateway gateway = new();
        ShelfRef a = Ref("a");
        ShelfRef b = Ref("b");
        FakeShelf fa = gateway.Add(a, 3);
        fa.Capacity = 10;
        gateway.Add(b, 0).CanHold = false;

        IReadOnlyList<ShelfCandidate> result = ShelfTargetScan.ReadCandidates(gateway, new[] { a, b }, _item);

        Assert.Equal(new ShelfCandidate(a, 7, true), result[0]);
        Assert.False(result[1].CanHold);
    }

    [Fact]
    public void ReadCandidates_NoItem_ReadsNothingAndFitsZero()
    {
        FakeShelfGateway gateway = new();

        IReadOnlyList<ShelfCandidate> result = ShelfTargetScan.ReadCandidates(gateway, new[] { Ref("a"), Ref("m", "Mannequin") }, null);

        Assert.Empty(gateway.Calls);
        ShelfCandidate only = Assert.Single(result);
        Assert.Equal(new ShelfCandidate(Ref("a"), 0, false), only);
    }

    [Fact]
    public void ReadCandidates_EmptyRegistry_ReturnsEmpty()
    {
        FakeShelfGateway gateway = new();

        Assert.Empty(ShelfTargetScan.ReadCandidates(gateway, new List<ShelfRef>(), _item));
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public void ReadCandidates_DuplicateKeys_AreKeptAsListed()
    {
        FakeShelfGateway gateway = new();
        ShelfRef a = Ref("a");
        gateway.Add(a, 0);

        IReadOnlyList<ShelfCandidate> result = ShelfTargetScan.ReadCandidates(gateway, new[] { a, a }, _item);

        Assert.Equal(2, result.Count);
    }
}
