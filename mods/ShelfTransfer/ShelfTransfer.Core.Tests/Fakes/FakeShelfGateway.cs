// purpose: scriptable in-memory IShelfGateway for runner tests: real take/store arithmetic, ordered call log, per-call hooks
// owns:    FakeShelfGateway, FakeShelf
// deps:    IShelfGateway
// caution: calls are logged as "Name(key)" or "Name" (inventory); OnCall fires after logging and before the call acts, so it can flip IsAlive, request cancel or throw.
//          A shelf given Slots is a mixed shelf: ListStock, GetCount, GetTransferUnit, GetMovableCount and TakeOne use the slots, StoreOne
//          still uses Count

using System;
using System.Collections.Generic;
using System.Linq;

using ShelfTransfer.Core.Gateway;

namespace ShelfTransfer.Core.Tests.Fakes;

public sealed class FakeShelf
{
    public int Count { get; set; }

    /// <summary>Total items the shelf accepts of the item; free space is Capacity - Count.</summary>
    public int Capacity { get; set; } = int.MaxValue;

    public bool CanHold { get; set; } = true;

    public bool IsAlive { get; set; } = true;

    /// <summary>What GetReceivingHookFree answers; null mirrors the plugin on a one-hook shelf: Free while the shelf holds the
    /// item, 0 (an empty hook) while it holds none.</summary>
    public int? ReceivingHookFree { get; set; }

    /// <summary>Per-slot stock of a mixed shelf (card 74), in hook order.</summary>
    public List<(SlotRef Slot, ItemId Item, int Count)> Slots { get; } = new();

    public int Free => Math.Max(0, Capacity - Count);
}

public sealed class FakeShelfGateway : IShelfGateway
{
    private readonly Dictionary<string, FakeShelf> _shelves = new();

    public List<string> Calls { get; } = new();

    public int Inventory { get; set; }

    /// <summary>What GetTransferUnit returns for a stocked shelf (0 for an empty one, as the plugin), and how many items the
    /// real take moves (whole chunks only, as the plugin). GetTransferUnit is not logged in <see cref="Calls"/>.</summary>
    public int TransferUnit { get; set; } = 1;

    /// <summary>Per-item chunk on a mixed shelf (e.g. 10 for rifle ammo); an item missing here uses <see cref="TransferUnit"/>.</summary>
    public Dictionary<ItemId, int> ItemUnits { get; } = new();

    /// <summary>GetMovableCount calls; the runner reads it only for a chunk (unit > 1).</summary>
    public int MovableCountReads { get; private set; }

    /// <summary>Runs inside GetMovableCount before it answers; not in <see cref="Calls"/>, so it can throw, abort or flip IsAlive on the read.</summary>
    public Action? OnMovableRead { get; set; }

    public int TakeCalls { get; private set; }

    /// <summary>The slot argument of every TakeOne call, in order.</summary>
    public List<SlotRef> TakeSlots { get; } = new();

    public int StoreCalls { get; private set; }

    /// <summary>Every TakeOne plus StoreOne call: the only mutations (invariant 2).</summary>
    public int Mutations => TakeCalls + StoreCalls;

    /// <summary>Runs on every call with its log entry, before the call acts.</summary>
    public Action<string>? OnCall { get; set; }

    /// <summary>Given the 1-based TakeOne call number, returns an outcome to use instead of the real take, or null for the real take.</summary>
    public Func<int, GatewayOutcome?>? TakeBehavior { get; set; }

    /// <summary>Same as <see cref="TakeBehavior"/> for StoreOne.</summary>
    public Func<int, GatewayOutcome?>? StoreBehavior { get; set; }

    public FakeShelf Add(ShelfRef shelf, int count)
    {
        FakeShelf fake = new() { Count = count };
        _shelves[shelf.Key] = fake;
        return fake;
    }

    public FakeShelf Shelf(ShelfRef shelf) => _shelves[shelf.Key];

    /// <summary>Inventory plus the given shelves, for conservation checks (invariant 1).</summary>
    public int Total(params ShelfRef[] shelves)
    {
        int total = Inventory;
        foreach (ShelfRef shelf in shelves)
        {
            total += _shelves[shelf.Key].Count;
        }

        return total;
    }

    /// <summary>The real vanilla-like take, usable from a behavior that wants extra or partial effects.</summary>
    public GatewayOutcome ApplyTake(ShelfRef shelf)
    {
        FakeShelf fake = _shelves[shelf.Key];
        if (fake.Count <= 0 || fake.Count < TransferUnit)
        {
            return GatewayOutcome.Refused("empty");
        }

        fake.Count -= TransferUnit;
        Inventory += TransferUnit;
        return GatewayOutcome.Ok;
    }

    /// <summary>The plugin's take on a mixed shelf: one chunk of the item's unit from the given slot, or from the first slot
    /// holding a whole chunk of it when Index is null (a short slot falls through, as the plugin's any-hook take).</summary>
    public GatewayOutcome ApplySlotTake(ShelfRef shelf, SlotRef slot, ItemId item)
    {
        List<(SlotRef Slot, ItemId Item, int Count)> slots = _shelves[shelf.Key].Slots;
        int unit = UnitOf(item);
        int index = slot.Index is null
            ? slots.FindIndex(s => s.Item == item && s.Count >= unit)
            : slots.FindIndex(s => s.Slot == slot);
        if (index < 0)
        {
            return slot.Index is null ? GatewayOutcome.Refused("no slot holds the item") : GatewayOutcome.Failed("slot gone");
        }

        (SlotRef at, ItemId held, int count) = slots[index];
        if (count <= 0)
        {
            return GatewayOutcome.Refused("slot empty");
        }

        if (held != item)
        {
            return GatewayOutcome.Refused("slot holds item " + held);
        }

        if (count < unit)
        {
            return GatewayOutcome.Refused($"partial chunk {count} of {unit}");
        }

        slots[index] = (at, held, count - unit);
        Inventory += unit;
        return GatewayOutcome.Ok;
    }

    public GatewayOutcome ApplyStore(ShelfRef shelf)
    {
        FakeShelf fake = _shelves[shelf.Key];
        if (fake.Free <= 0 || Inventory <= 0)
        {
            return GatewayOutcome.Refused("full");
        }

        fake.Count++;
        Inventory--;
        return GatewayOutcome.Ok;
    }

    public bool IsAlive(ShelfRef shelf)
    {
        Record($"IsAlive({shelf.Key})");
        return _shelves[shelf.Key].IsAlive;
    }

    public int GetCount(ShelfRef shelf, ItemId item)
    {
        Record($"GetCount({shelf.Key})");
        FakeShelf fake = _shelves[shelf.Key];
        return fake.Slots.Count > 0 ? fake.Slots.Where(s => s.Item == item).Sum(s => s.Count) : fake.Count;
    }

    public int GetFreeCapacity(ShelfRef shelf, ItemId item)
    {
        Record($"GetFreeCapacity({shelf.Key})");
        return _shelves[shelf.Key].Free;
    }

    public bool CanHold(ShelfRef shelf, ItemId item)
    {
        Record($"CanHold({shelf.Key})");
        return _shelves[shelf.Key].CanHold;
    }

    public int GetInventoryCount(ItemId item)
    {
        Record("GetInventoryCount");
        return Inventory;
    }

    // A mixed shelf answers as the plugin: the unit of the given slot (any slot when Index is null) stocked with the item.
    public int GetTransferUnit(ShelfRef shelf, SlotRef slot, ItemId item)
    {
        FakeShelf fake = _shelves[shelf.Key];
        if (fake.Slots.Count == 0)
        {
            return fake.Count > 0 ? TransferUnit : 0;
        }

        bool isStocked = fake.Slots.Any(s => s.Item == item && s.Count > 0 && (slot.Index is null || s.Slot == slot));
        return isStocked ? UnitOf(item) : 0;
    }

    // As the plugin, whole chunks per slot that TakeOne with this slot could reach. Counted, not logged, like GetTransferUnit.
    public int GetMovableCount(ShelfRef shelf, SlotRef slot, ItemId item)
    {
        MovableCountReads++;
        OnMovableRead?.Invoke();
        FakeShelf fake = _shelves[shelf.Key];
        if (!fake.IsAlive)
        {
            return 0; // the plugin's Guard answers 0 for a gone shelf
        }

        if (fake.Slots.Count == 0)
        {
            return TransferUnit < 1 ? 0 : fake.Count / TransferUnit * TransferUnit;
        }

        int unit = UnitOf(item);
        return unit < 1
            ? 0
            : fake.Slots
                .Where(s => s.Item == item && s.Count > 0 && (slot.Index is null || s.Slot == slot))
                .Sum(s => s.Count / unit * unit);
    }

    public IReadOnlyList<ShelfStockEntry> ListStock(ShelfRef shelf)
    {
        Record($"ListStock({shelf.Key})");
        if (!_shelves.TryGetValue(shelf.Key, out FakeShelf? fake) || !fake.IsAlive)
        {
            return Array.Empty<ShelfStockEntry>();
        }

        return ShelfStockEntry.BuildList(fake.Slots.Select(s => (s.Slot, s.Item, s.Count, UnitOf(s.Item))));
    }

    // A gone shelf answers 0, as the plugin's Guard does.
    public int GetReceivingHookFree(ShelfRef shelf, ItemId item)
    {
        Record($"GetReceivingHookFree({shelf.Key})");
        FakeShelf fake = _shelves[shelf.Key];
        if (!fake.IsAlive)
        {
            return 0;
        }

        return fake.ReceivingHookFree ?? (fake.Count > 0 ? fake.Free : 0);
    }

    public GatewayOutcome TakeOne(ShelfRef shelf, SlotRef slot, ItemId item)
    {
        TakeCalls++;
        TakeSlots.Add(slot);
        Record($"TakeOne({shelf.Key})");
        return TakeBehavior?.Invoke(TakeCalls)
            ?? (_shelves[shelf.Key].Slots.Count > 0 ? ApplySlotTake(shelf, slot, item) : ApplyTake(shelf));
    }

    public GatewayOutcome StoreOne(ShelfRef shelf, ItemId item)
    {
        StoreCalls++;
        Record($"StoreOne({shelf.Key})");
        return StoreBehavior?.Invoke(StoreCalls) ?? ApplyStore(shelf);
    }

    private int UnitOf(ItemId item) => ItemUnits.TryGetValue(item, out int unit) ? unit : TransferUnit;

    private void Record(string entry)
    {
        Calls.Add(entry);
        OnCall?.Invoke(entry);
    }
}
