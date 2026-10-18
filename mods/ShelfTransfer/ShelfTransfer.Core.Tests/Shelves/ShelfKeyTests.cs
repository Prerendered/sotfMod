// purpose: pins the D2 key format; also the scaffold's proof that the test project resolves Core
// owns:    ShelfKeyTests
// deps:    ShelfKey, WorldPosition

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Shelves;

using Xunit;

namespace ShelfTransfer.Core.Tests.Shelves;

public sealed class ShelfKeyTests
{
    [Fact]
    public void GetShelfKey_RoundsPositionToTenthOfMetre()
    {
        string key = ShelfKey.GetShelfKey("StorageShelf", new WorldPosition(12.34f, 4.55f, -6.76f));

        Assert.Equal("StorageShelf@12.3,4.6,-6.8", key);
    }

    [Fact]
    public void GetShelfKey_IsStableForPositionsWithinSameTenth()
    {
        string first = ShelfKey.GetShelfKey("StorageShelf", new WorldPosition(1.01f, 2.02f, 3.03f));
        string second = ShelfKey.GetShelfKey("StorageShelf", new WorldPosition(1.04f, 2.04f, 3.04f));

        Assert.Equal(first, second);
    }

    [Fact]
    public void GetShelfKey_UsesInvariantDecimalSeparator()
    {
        string key = ShelfKey.GetShelfKey("StorageShelf", new WorldPosition(0.5f, 0f, 0f));

        Assert.Equal("StorageShelf@0.5,0.0,0.0", key);
    }
}
