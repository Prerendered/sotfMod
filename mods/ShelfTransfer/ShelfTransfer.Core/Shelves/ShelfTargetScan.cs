// purpose: the one target scan behind the overlay and the typed prompt: keeps only shelves and reads each one's CanHold
//          and free space for the moved item, so both list the same targets with the same fits (card 80)
// owns:    ShelfTargetScan (IsShelf, ReadCandidates, ShelfTypeMarker)
// deps:    IShelfGateway (reads only), ShelfCandidate, ShelfRef, ItemId
// caution: targets only - the prune listing stays unfiltered, or a label on a mannequin would look stale. I2: reads only

using System;
using System.Collections.Generic;

using ShelfTransfer.Core.Gateway;

namespace ShelfTransfer.Core.Shelves;

public static class ShelfTargetScan
{
    /// <summary>
    /// A storage is a shelf when its prefab name contains this, ignoring case (ShelfStructure). Head trophy mounts,
    /// mannequins and arrow racks share ItemStorageController but are not shelves (card 82 brief).
    /// </summary>
    public const string ShelfTypeMarker = "Shelf";

    public static bool IsShelf(ShelfRef shelf) =>
        shelf.TypeName.Contains(ShelfTypeMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every shelf in <paramref name="shelves"/>, in order, with CanHold and free space for <paramref name="item"/>; with no
    /// item nothing is read and each shelf holds nothing (fits 0).
    /// </summary>
    public static IReadOnlyList<ShelfCandidate> ReadCandidates(IShelfGateway gateway, IReadOnlyList<ShelfRef> shelves,
        ItemId? item)
    {
        List<ShelfCandidate> candidates = new(shelves.Count);
        foreach (ShelfRef shelf in shelves)
        {
            if (!IsShelf(shelf))
            {
                continue;
            }

            if (item is not ItemId held)
            {
                candidates.Add(new ShelfCandidate(shelf, 0, false));
                continue;
            }

            // CanHold then GetFreeCapacity back to back share one hook walk in the plugin's gateway.
            bool canHold = gateway.CanHold(shelf, held);
            int free = gateway.GetFreeCapacity(shelf, held);
            candidates.Add(new ShelfCandidate(shelf, free, canHold));
        }

        return candidates;
    }
}
