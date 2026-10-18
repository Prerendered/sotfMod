// purpose: IShelfGateway over the game's own calls; the only code that changes shelf state (I2)
// owns:    InteropShelfGateway
// deps:    ShelfRegistry, IShelfGateway, GatewayOutcome, IModLog, Sons.Gameplay / Sons.Inventory / TheForest (interop)
// caution: side effects only in TakeOne (InWorldLayoutItemGroup.RemoveItem) and StoreOne (ItemStorageController.TryAddItem);
//          all else is field reads and count getters. Both are discovery candidates until card 12 runs them; on a client
//          the host applies them later (card 46). Never ItemInstanceManager writes, Host* or *FromClient calls.

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
        Guard("is_alive", _ => false, () => _registry.TryResolve(shelf.Key, out _));

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

    public GatewayOutcome TakeOne(ShelfRef shelf, SlotRef slot, ItemId item) =>
        Guard("take_one", GatewayOutcome.Failed, () =>
        {
            if (!TryGetShelf(shelf, out ItemStorageController? controller))
            {
                return GatewayOutcome.Failed(_shelfGone);
            }

            foreach (ItemStorageHookPoint hook in CopyHooks(controller))
            {
                if (slot.Index is int index)
                {
                    StructureStorage? storage = FindOnObject<StructureStorage>(hook);
                    if (storage != null && storage.StorageSlotIndex == index)
                    {
                        return TakeFrom(FindActiveGroup(hook), item);
                    }

                    continue;
                }

                InWorldLayoutItemGroup? group = FindActiveGroup(hook);
                if (group != null && group._itemId == item.Value && group.GetCurrentCount() > 0)
                {
                    return TakeFrom(group, item);
                }
            }

            return slot.Index is null ? GatewayOutcome.Refused("no slot holds the item") : GatewayOutcome.Failed("slot gone");
        });

    public GatewayOutcome StoreOne(ShelfRef shelf, ItemId item) =>
        Guard("store_one", GatewayOutcome.Failed, () =>
        {
            if (!TryGetShelf(shelf, out ItemStorageController? controller))
            {
                return GatewayOutcome.Failed(_shelfGone);
            }

            if (!TryGetInventory(out PlayerInventory? inventory))
            {
                return GatewayOutcome.Failed(_noInventory);
            }

            ItemInstance? instance = inventory.GetItemInstanceOfType(item.Value);
            if (instance == null)
            {
                return GatewayOutcome.Refused("none in inventory");
            }

            // Card 4 candidate for the vanilla store (C); the controller picks the hook, so no SlotRef.
            return controller.TryAddItem(instance) ? GatewayOutcome.Ok : GatewayOutcome.Refused("store declined");
        });

    // Card 4 candidate for the vanilla take (E); bypassPlayerInventoryUpdate=false so the item goes to the player.
    private static GatewayOutcome TakeFrom(InWorldLayoutItemGroup? group, ItemId item)
    {
        if (group == null || group.GetCurrentCount() <= 0)
        {
            return GatewayOutcome.Refused("slot empty");
        }

        if (group._itemId != item.Value)
        {
            return GatewayOutcome.Refused("slot holds item " + group._itemId);
        }

        // RemoveItem moves the group's whole transfer unit; anything but 1 would put more than one item in hand (I1).
        int unit = group._transferAmount;
        if (unit != 1)
        {
            return GatewayOutcome.Refused("transfer unit " + unit);
        }

        group.RemoveItem(false);
        return GatewayOutcome.Ok;
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

        Capacity value = ReadCapacity(shelf, item);
        _lastCapacity = new CapacityRead(shelf.Key, item, frame, value);
        return value;
    }

    private Capacity ReadCapacity(ShelfRef shelf, ItemId item)
    {
        if (!TryGetShelf(shelf, out ItemStorageController? controller))
        {
            return default;
        }

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
