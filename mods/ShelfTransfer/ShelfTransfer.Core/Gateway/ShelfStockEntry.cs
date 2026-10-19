// purpose: one stocked item type on a shelf, as Send all queues it; BuildList folds per-slot reads into these entries
// owns:    ShelfStockEntry
// deps:    ItemId, SlotRef
// caution: TransferUnit and FirstSlot come from the same slot, so the unit is what GetTransferUnit(shelf, FirstSlot, item) reads

using System.Collections.Generic;

namespace ShelfTransfer.Core.Gateway;

public sealed record ShelfStockEntry(ItemId Item, int Count, int TransferUnit, SlotRef FirstSlot)
{
    /// <summary>One entry per item type with stock, counts summed over its slots, ordered by first stocked slot then item id.
    /// Slots with no stock are ignored; on equal slot indexes the earlier slot in <paramref name="slots"/> wins.</summary>
    public static IReadOnlyList<ShelfStockEntry> BuildList(
        IEnumerable<(SlotRef Slot, ItemId Item, int Count, int TransferUnit)> slots)
    {
        Dictionary<ItemId, ShelfStockEntry> byItem = new();
        foreach ((SlotRef slot, ItemId item, int count, int unit) in slots)
        {
            if (count <= 0)
            {
                continue;
            }

            if (!byItem.TryGetValue(item, out ShelfStockEntry? entry))
            {
                byItem[item] = new ShelfStockEntry(item, count, unit, slot);
            }
            else if (CompareSlots(slot, entry.FirstSlot) < 0)
            {
                byItem[item] = new ShelfStockEntry(item, entry.Count + count, unit, slot);
            }
            else
            {
                byItem[item] = entry with { Count = entry.Count + count };
            }
        }

        List<ShelfStockEntry> entries = new(byItem.Values);
        entries.Sort((a, b) =>
        {
            int bySlot = CompareSlots(a.FirstSlot, b.FirstSlot);
            return bySlot != 0 ? bySlot : a.Item.Value.CompareTo(b.Item.Value);
        });
        return entries;
    }

    // An unknown hook index has no position on the shelf, so it sorts after every known one.
    private static int CompareSlots(SlotRef a, SlotRef b) => (a.Index, b.Index) switch
    {
        (int x, int y) => x.CompareTo(y),
        (int, null) => -1,
        (null, int) => 1,
        _ => 0,
    };
}
