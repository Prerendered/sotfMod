// purpose: scriptable in-memory IShelfGateway for runner tests: real take/store arithmetic, ordered call log, per-call hooks
// owns:    FakeShelfGateway, FakeShelf
// deps:    IShelfGateway
// caution: calls are logged as "Name(key)" or "Name" (inventory); OnCall fires after logging and before the call acts, so it can flip IsAlive, request cancel or throw

using System;
using System.Collections.Generic;

using ShelfTransfer.Core.Gateway;

namespace ShelfTransfer.Core.Tests.Fakes;

public sealed class FakeShelf
{
    public int Count { get; set; }

    /// <summary>Total items the shelf accepts of the item; free space is Capacity - Count.</summary>
    public int Capacity { get; set; } = int.MaxValue;

    public bool CanHold { get; set; } = true;

    public bool IsAlive { get; set; } = true;

    public int Free => Math.Max(0, Capacity - Count);
}

public sealed class FakeShelfGateway : IShelfGateway
{
    private readonly Dictionary<string, FakeShelf> _shelves = new();

    public List<string> Calls { get; } = new();

    public int Inventory { get; set; }

    public int TakeCalls { get; private set; }

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
        if (fake.Count <= 0)
        {
            return GatewayOutcome.Refused("empty");
        }

        fake.Count--;
        Inventory++;
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
        return _shelves[shelf.Key].Count;
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

    public GatewayOutcome TakeOne(ShelfRef shelf, SlotRef slot, ItemId item)
    {
        TakeCalls++;
        Record($"TakeOne({shelf.Key})");
        return TakeBehavior?.Invoke(TakeCalls) ?? ApplyTake(shelf);
    }

    public GatewayOutcome StoreOne(ShelfRef shelf, ItemId item)
    {
        StoreCalls++;
        Record($"StoreOne({shelf.Key})");
        return StoreBehavior?.Invoke(StoreCalls) ?? ApplyStore(shelf);
    }

    private void Record(string entry)
    {
        Calls.Add(entry);
        OnCall?.Invoke(entry);
    }
}
