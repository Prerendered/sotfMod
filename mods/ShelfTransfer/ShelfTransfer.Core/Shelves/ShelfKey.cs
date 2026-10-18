// purpose: the single place a shelf's identity is derived (D2) so a persistent save ID can replace it later
// owns:    ShelfKey.GetShelfKey
// deps:    WorldPosition
// caution: changing the format invalidates every saved label file; bump LabelFile.CurrentVersion with it

using System;
using System.Globalization;

using ShelfTransfer.Core.Gateway;

namespace ShelfTransfer.Core.Shelves;

public static class ShelfKey
{
    /// <summary>Type name plus world position rounded to 0.1 m, e.g. "StorageShelf@12.3,4.5,-6.7".</summary>
    public static string GetShelfKey(string typeName, WorldPosition position)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw new ArgumentException("type name is required", nameof(typeName));
        }

        return $"{typeName}@{Round(position.X)},{Round(position.Y)},{Round(position.Z)}";
    }

    // Fixed "0.0" format: decimal keeps the input's scale, so 0f would otherwise print as "0".
    private static string Round(float value) =>
        Math.Round((decimal)value, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture);
}
