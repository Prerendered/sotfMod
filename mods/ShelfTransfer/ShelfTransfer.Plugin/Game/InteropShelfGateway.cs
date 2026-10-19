// purpose: IShelfGateway over the game's own calls; the only code that changes shelf state (I2)
// owns:    InteropShelfGateway
// deps:    ShelfRegistry, IShelfGateway, GatewayOutcome, ShelfStockEntry, IModLog, Sons.Gameplay / Sons.Inventory / TheForest (interop)
// caution: side effects only in TakeOne (InWorldLayoutItemGroup.RemoveItem, one whole chunk of _transferAmount) and StoreOne
//          (ItemStorageController.TryAddItem then PlayerInventory.RemoveItem, the vanilla pair, one item); all else is field reads
//          and count getters. On a client the host applies shelf changes later (card 46). Never ItemInstanceManager writes,
//          Host* or *FromClient calls.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

using Sons.Gameplay;
using Sons.Inventory;

using TheForest.Items.Inventory;
using TheForest.Utils;

using UnityEngine;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Logging;

using LogLevel = ShelfTransfer.Core.Logging.LogLevel;

namespace ShelfTransfer.Plugin.Game;

public sealed class InteropShelfGateway : IShelfGateway
{
    private const string _shelfGone = "shelf gone";
    private const string _noInventory = "no player inventory";

    private readonly ShelfRegistry _registry;
    private readonly IModLog _log;

    // One gateway.fail per disappearance, not one per runner tick; a key leaves the set once it resolves again.
    private readonly HashSet<string> _reportedGone = new(StringComparer.Ordinal);
    private bool _hasReportedNoInventory;
    private CapacityRead? _lastCapacity;

    public InteropShelfGateway(ShelfRegistry registry, IModLog log)
    {
        _registry = registry;
        _log = log;
    }

    public bool IsAlive(ShelfRef shelf) =>
        Guard("is_alive", _ => false, () => TryGetShelf(shelf, out _));

    public int GetCount(ShelfRef shelf, ItemId item) =>
        Guard("get_count", _ => 0, () =>
        {
            if (!TryGetShelf(shelf, out ItemStorageController? controller))
            {
                return 0;
            }

            int total = 0;
            foreach (ItemStorageHookPoint hook in CopyHooks(controller))
            {
                InWorldLayoutItemGroup? group = FindActiveGroup(hook);
                if (group != null && group._itemId == item.Value)
                {
                    total += Math.Max(0, group.GetCurrentCount());
                }
            }

            return total;
        });

    public int GetFreeCapacity(ShelfRef shelf, ItemId item) =>
        Guard("get_free_capacity", _ => 0, () => ReadCapacityShared(shelf, item).Free, isCapacityRead: true);

    public bool CanHold(ShelfRef shelf, ItemId item) =>
        Guard("can_hold", _ => false, () => ReadCapacityShared(shelf, item).CanHold, isCapacityRead: true);

    public int GetInventoryCount(ItemId item) =>
        Guard("get_inventory_count", _ => 0, () =>
            TryGetInventory(out PlayerInventory? inventory) ? inventory.AmountOf(item.Value, true, false) : 0);

    // The same hook TakeOne would take from, so the runner plans with the chunk the take really moves.
    public int GetTransferUnit(ShelfRef shelf, SlotRef slot, ItemId item) =>
        Guard("get_transfer_unit", _ => 0, () =>
        {
            if (!TryGetShelf(shelf, out ItemStorageController? controller))
            {
                return 0;
            }

            foreach (ItemStorageHookPoint hook in CopyHooks(controller))
            {
                InWorldLayoutItemGroup? group = FindActiveGroup(hook);
                int unit = group != null && group._itemId == item.Value && group.GetCurrentCount() > 0
                    ? Math.Max(0, group._transferAmount)
                    : 0;
                if (slot.Index is int index)
                {
                    if (IsSlot(hook, group, index))
                    {
                        return unit;
                    }
                }
                else if (unit > 0)
                {
                    return unit;
                }
            }

            return 0;
        });

    // Reads only (I2): TakeOne's hook walk and TakeFrom's whole-chunk rule, so a slot it would refuse adds nothing (card 85).
    public int GetMovableCount(ShelfRef shelf, SlotRef slot, ItemId item) =>
        Guard("get_movable_count", _ => 0, () =>
        {
            if (!TryGetShelf(shelf, out ItemStorageController? controller))
            {
                return 0;
            }

            int total = 0;
            foreach (ItemStorageHookPoint hook in CopyHooks(controller))
            {
                InWorldLayoutItemGroup? group = FindActiveGroup(hook);
                if (slot.Index is int index)
                {
                    if (IsSlot(hook, group, index))
                    {
                        return ReadMovable(group, item);
                    }
                }
                else
                {
                    total += ReadMovable(group, item);
                }
            }

            return total;
        });

    // Reads only (I2): each stocked hook's group fields and count, folded in Core. The unit is the group's _transferAmount, as
    // GetTransferUnit reads it for the same slot.
    public IReadOnlyList<ShelfStockEntry> ListStock(ShelfRef shelf) =>
        Guard<IReadOnlyList<ShelfStockEntry>>("list_stock", _ => Array.Empty<ShelfStockEntry>(), () =>
        {
            if (!TryGetShelf(shelf, out ItemStorageController? controller))
            {
                return Array.Empty<ShelfStockEntry>();
            }

            List<(SlotRef Slot, ItemId Item, int Count, int TransferUnit)> slots = new();
            foreach (ItemStorageHookPoint hook in CopyHooks(controller))
            {
                InWorldLayoutItemGroup? group = FindActiveGroup(hook);
                int count = group == null ? 0 : group.GetCurrentCount();
                if (group != null && count > 0)
                {
                    slots.Add((new SlotRef(ReadSlotIndex(hook, group)), new ItemId(group._itemId), count,
                        Math.Max(0, group._transferAmount)));
                }
            }

            return ShelfStockEntry.BuildList(slots);
        });

    // Reads only (I2), mirroring ItemStorageController.TryAddItem (0x333D580): an empty _selectedHookPoint wins over any
    // hook; else TryGetAvailableHook (0x333ECB0) takes the first hook in order whose _layoutItemGroup holds the item with
    // GetAvailableSpace() > 0, then an empty hook with a template. Arrow mode takes another path (unresolved here).
    public int GetReceivingHookFree(ShelfRef shelf, ItemId item) =>
        Guard("get_receiving_hook_free", _ => 0, () =>
        {
            if (!TryGetShelf(shelf, out ItemStorageController? controller) || controller._arrowMode)
            {
                return 0;
            }

            ItemStorageHookPoint? selected = controller._selectedHookPoint;
            if (selected != null && selected._layoutItemGroup == null)
            {
                return 0;
            }

            foreach (ItemStorageHookPoint hook in CopyHooks(controller))
            {
                InWorldLayoutItemGroup? group = hook._layoutItemGroup;
                if (group != null && group._itemId == item.Value)
                {
                    int free = group.GetAvailableSpace();
                    if (free > 0)
                    {
                        return free;
                    }
                }
            }

            return 0;
        });

    public GatewayOutcome TakeOne(ShelfRef shelf, SlotRef slot, ItemId item) =>
        Guard("take_one", GatewayOutcome.Failed, () =>
        {
            if (!TryGetShelf(shelf, out ItemStorageController? controller))
            {
                return GatewayOutcome.Failed(_shelfGone);
            }

            string? lastRefusal = null;
            foreach (ItemStorageHookPoint hook in CopyHooks(controller))
            {
                InWorldLayoutItemGroup? group = FindActiveGroup(hook);
                if (slot.Index is int index)
                {
                    if (!IsSlot(hook, group, index))
                    {
                        continue;
                    }

                    string? refusal = TakeFrom(group, item);
                    return refusal is null ? GatewayOutcome.Ok : GatewayOutcome.Refused(refusal);
                }

                if (group != null && group._itemId == item.Value && group.GetCurrentCount() > 0)
                {
                    // Any matching hook will do; one the take must refuse (e.g. a partial chunk) falls through to the next.
                    lastRefusal = TakeFrom(group, item);
                    if (lastRefusal is null)
                    {
                        return GatewayOutcome.Ok;
                    }
                }
            }

            return slot.Index is null
                ? GatewayOutcome.Refused(lastRefusal ?? "no slot holds the item")
                : GatewayOutcome.Failed("slot gone");
        });

    // The game's store pair in its own order (I2 amended): TryAddItem never touches the inventory (session 6 duplicated),
    // so every vanilla caller follows it with RemoveItem; the args are InWorldLayoutItemGroup.AddItem's.
    public GatewayOutcome StoreOne(ShelfRef shelf, ItemId item)
    {
        // Once the add went through, any throw has stored without consuming: Failed with its own detail so the runner stops.
        bool isAdded = false;
        return Guard("store_one",
            detail => GatewayOutcome.Failed(isAdded ? "stored, consume threw: " + detail : detail),
            () =>
            {
                if (!TryGetShelf(shelf, out ItemStorageController? controller))
                {
                    return GatewayOutcome.Failed(_shelfGone);
                }

                if (!TryGetInventory(out PlayerInventory? inventory))
                {
                    return GatewayOutcome.Failed(_noInventory);
                }

                // Backpack only: an item held in hand alone is not found and refuses here.
                ItemInstance? instance = inventory.GetItemInstanceOfType(item.Value);
                if (instance == null)
                {
                    return GatewayOutcome.Refused("none in inventory");
                }

                // On a client the host applies the add later and may still reject it, after the item is consumed;
                // a fresh capacity read narrows that window to what changed since.
                if (ReadCapacity(controller, item).Free <= 0)
                {
                    return GatewayOutcome.Refused("target full");
                }

                if (!controller.TryAddItem(instance))
                {
                    return GatewayOutcome.Refused("store declined");
                }

                isAdded = true;
                bool isConsumed = inventory.RemoveItem(item.Value, amount: 1, allowAmountOverflow: false,
                    shouldEquipPrevious: true, removeFromQuickSelect: true, itemInstance: instance, instantDestroy: false);
                return isConsumed ? GatewayOutcome.Ok : GatewayOutcome.Failed("stored but not consumed");
            });
    }

    // Card 4 candidate for the vanilla take (E); bypassPlayerInventoryUpdate=false so the item goes to the player.
    // Null when taken, else the refusal detail (nothing called).
    private static string? TakeFrom(InWorldLayoutItemGroup? group, ItemId item)
    {
        if (group == null || group.GetCurrentCount() <= 0)
        {
            return "slot empty";
        }

        if (group._itemId != item.Value)
        {
            return "slot holds item " + group._itemId;
        }

        // RemoveItem moves min(_transferAmount, count) (Sons.dll disassembly); a short slot would hand over a partial chunk
        // the runner cannot verify as one (I1/I3 amended), so only a whole chunk is taken.
        int unit = group._transferAmount;
        if (unit < 1)
        {
            return "transfer unit " + unit;
        }

        int count = group.GetCurrentCount();
        if (count < unit)
        {
            return $"partial chunk {count} of {unit}";
        }

        group.RemoveItem(false);
        return null;
    }

    // What repeated TakeFrom calls on the group would move: whole chunks only, nothing for another item or a bad unit.
    private static int ReadMovable(InWorldLayoutItemGroup? group, ItemId item)
    {
        if (group == null || group._itemId != item.Value)
        {
            return 0;
        }

        int unit = group._transferAmount;
        int count = group.GetCurrentCount();
        return unit < 1 || count <= 0 ? 0 : count / unit * unit;
    }

    // AimWatcher's resolution (the stocked group's _storage); the hook's own component only for an empty slot.
    private static bool IsSlot(ItemStorageHookPoint hook, InWorldLayoutItemGroup? group, int index) =>
        ReadSlotIndex(hook, group) == index;

    private static int? ReadSlotIndex(ItemStorageHookPoint hook, InWorldLayoutItemGroup? group)
    {
        StructureStorage? storage = group != null && group._storage != null
            ? group._storage
            : FindOnObject<StructureStorage>(hook);
        return storage == null ? null : storage.StorageSlotIndex;
    }

    // A stocked hook takes more only of its own item; an empty hook takes any item it has a layout template for,
    // up to that template's cap. A hook stocked with another item still proves the shelf accepts the type.
    // CanHold and GetFreeCapacity asked back to back (aim line, runner Planning) share one hook walk. Any other gateway
    // call in between, or a new frame, drops the shared read, so it never outlives a take/store.
    private Capacity ReadCapacityShared(ShelfRef shelf, ItemId item)
    {
        int frame = Time.frameCount;
        if (_lastCapacity is { } last && last.Frame == frame && last.Item == item
            && string.Equals(last.ShelfKey, shelf.Key, StringComparison.Ordinal))
        {
            return last.Value;
        }

        Capacity value = TryGetShelf(shelf, out ItemStorageController? controller) ? ReadCapacity(controller, item) : default;
        _lastCapacity = new CapacityRead(shelf.Key, item, frame, value);
        return value;
    }

    private static Capacity ReadCapacity(ItemStorageController controller, ItemId item)
    {
        List<InWorldLayoutItemGroup> shelfTemplates = CopyList(controller._itemLayoutGroups);
        bool canHold = false;
        int free = 0;
        foreach (ItemStorageHookPoint hook in CopyHooks(controller))
        {
            InWorldLayoutItemGroup? group = FindActiveGroup(hook);
            bool isStocked = group != null && group.GetCurrentCount() > 0;
            if (group != null && isStocked && group._itemId == item.Value)
            {
                canHold = true;
                free += Math.Max(0, group.GetAvailableSpace());
                continue;
            }

            if (isStocked && canHold)
            {
                continue;
            }

            InWorldLayoutItemGroup? template = FindTemplate(hook, shelfTemplates, item);
            if (template == null)
            {
                continue;
            }

            canHold = true;
            if (!isStocked)
            {
                free += Math.Max(0, template._maxAmount);
            }
        }

        return new Capacity(canHold, free);
    }

    // The hook's own template list; the shelf's list stands in when a hook carries none.
    private static InWorldLayoutItemGroup? FindTemplate(ItemStorageHookPoint hook,
        List<InWorldLayoutItemGroup> shelfTemplates, ItemId item)
    {
        List<InWorldLayoutItemGroup> templates = CopyList(hook._itemLayoutGroups);
        foreach (InWorldLayoutItemGroup template in templates.Count > 0 ? templates : shelfTemplates)
        {
            if (template._itemId == item.Value)
            {
                return template;
            }
        }

        return null;
    }

    // The IsAlive check every shelf call starts with; a miss rescans once (registry) before reporting the shelf gone.
    private bool TryGetShelf(ShelfRef shelf, [NotNullWhen(true)] out ItemStorageController? controller)
    {
        if (_registry.TryResolve(shelf.Key, out controller))
        {
            _reportedGone.Remove(shelf.Key);
            return true;
        }

        if (_reportedGone.Add(shelf.Key))
        {
            WriteFail(shelf.Key, _shelfGone);
        }

        return false;
    }

    private bool TryGetInventory([NotNullWhen(true)] out PlayerInventory? inventory)
    {
        PlayerInventory? found = LocalPlayer.Inventory;
        if (found != null)
        {
            _hasReportedNoInventory = false;
            inventory = found;
            return true;
        }

        if (!_hasReportedNoInventory)
        {
            _hasReportedNoInventory = true;
            WriteFail(null, _noInventory);
        }

        inventory = null;
        return false;
    }

    private void WriteFail(string? shelfKey, string detail)
    {
        List<KeyValuePair<string, object?>> line = new();
        if (shelfKey is not null)
        {
            line.Add(new("shelf", shelfKey));
        }

        line.Add(new("detail", detail));
        _log.Write(LogLevel.Warn, "gateway.fail", line);
    }

    // The Core/game boundary (P1): interop can throw on a collected object, and Core must only ever see a value.
    private T Guard<T>(string operation, Func<string, T> onThrow, Func<T> call, bool isCapacityRead = false)
    {
        if (!isCapacityRead)
        {
            _lastCapacity = null;
        }

        try
        {
            return call();
        }
        catch (Exception ex)
        {
            _log.WriteException("gateway." + operation, ex);
            return onThrow(ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static List<ItemStorageHookPoint> CopyHooks(ItemStorageController controller) =>
        CopyList(controller._hookPoints);

    // Il2Cpp collections are copied into managed lists before iteration (P1); destroyed entries are dropped.
    private static List<T> CopyList<T>(Il2CppSystem.Collections.Generic.List<T>? source) where T : UnityEngine.Object
    {
        List<T> copy = new();
        if (source == null)
        {
            return copy;
        }

        int count = source.Count;
        for (int i = 0; i < count; i++)
        {
            T? entry = source[i];
            if (entry != null)
            {
                copy.Add(entry);
            }
        }

        return copy;
    }

    // Same lookup as AimWatcher: the hook's stocked group is its active InWorldLayoutItemGroup child.
    private static InWorldLayoutItemGroup? FindActiveGroup(ItemStorageHookPoint hook)
    {
        Component? child = hook.GetComponentInChildren(Il2CppType.Of<InWorldLayoutItemGroup>());
        return child == null ? null : child.TryCast<InWorldLayoutItemGroup>();
    }

    private static T? FindOnObject<T>(Component component) where T : Il2CppObjectBase
    {
        Component? found = component.GetComponent(Il2CppType.Of<T>());
        return found == null ? null : found.TryCast<T>();
    }

    private readonly record struct Capacity(bool CanHold, int Free);

    private readonly record struct CapacityRead(string ShelfKey, ItemId Item, int Frame, Capacity Value);
}
