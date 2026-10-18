// purpose: resolves the aimed shelf slot (shelf, hook index, item, count) every frame; logs aim.change and aim.probe once settled
// owns:    AimWatcher
// deps:    ShelfRegistry, IShelfGateway, AimTarget, AimChangeDebouncer, IModLog, UnityEngine (Camera, Physics), Sons.Gameplay / Sons.Inventory (interop)
// caution: read-only (I2): field reads, the getters GetCurrentCount, GetItemCount, StorageSlotIndex and gateway reads (never
//          TakeOne/StoreOne); no Harmony patch.
//          Il2Cpp objects live in locals of one Update; only managed values (ints, the key string) survive a frame

using System;
using System.Collections.Generic;

using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes;

using Sons.Crafting.Structures;
using Sons.Gameplay;
using Sons.Inventory;

using UnityEngine;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Logging;

using LogLevel = ShelfTransfer.Core.Logging.LogLevel;

namespace ShelfTransfer.Plugin.Game;

public sealed class AimWatcher : MonoBehaviour
{
    // Same reach as the discovery dump (card 3): every usable shelf slot, without distant scenery.
    private const float _maxAimDistance = 10f;

    private const string _noShelf = "none";
    private const string _reasonNoHit = "no_hit";
    private const string _reasonNotShelf = "not_shelf";
    private const string _reasonNoSlot = "no_slot";
    private const string _reasonEmptySlot = "empty_slot";

    private IModLog? _log;
    private ShelfRegistry? _registry;
    private IShelfGateway? _gateway;
    private bool _isHalted;
    private readonly AimChangeDebouncer _debouncer = new();
    private readonly AimChangeDebouncer _probeDebouncer = new();
    private static readonly SlotRef _anySlot = new(null);

    // Key cache: GetShelfKey formats a string, so it is rebuilt only when the aimed controller or its position changes.
    private int _cachedControllerId;
    private float _cachedX;
    private float _cachedY;
    private float _cachedZ;
    private ShelfRef? _cachedShelf;

    private AimTarget? _current;

    // Last logged target, for aim.probe (would that item fit on the newly aimed shelf).
    private string? _lastLoggedShelfKey;
    private int _lastLoggedItem;

    public AimWatcher(IntPtr ptr) : base(ptr) { }

    // Managed parameters cannot cross into Il2Cpp; hidden so ClassInjector does not try to expose it.
    [HideFromIl2Cpp]
    public void Init(IModLog log, ShelfRegistry registry, IShelfGateway gateway)
    {
        _log = log;
        _registry = registry;
        _gateway = gateway;
    }

    /// <summary>The slot aimed at this frame, or null when there is none (or the watcher halted).</summary>
    [HideFromIl2Cpp]
    public AimTarget? GetCurrent() => _current;

    public void Update()
    {
        if (_log is null || _registry is null || _gateway is null || _isHalted)
        {
            return;
        }

        try
        {
            ItemStorageController? unstocked = Tick(_log, _registry, _gateway);
            UpdateEmptyProbe(_log, _registry, _gateway, unstocked);
        }
        catch (Exception ex)
        {
            // Per-frame boundary (P2): log once and stop for the session rather than throw every frame.
            _isHalted = true;
            _current = null;
            _log.WriteException("aim.tick", ex);
        }
    }

    // Returns the aimed storage when the ray is on it but on no stocked slot (the empty-storage probe's input), else null.
    [HideFromIl2Cpp]
    private ItemStorageController? Tick(IModLog log, ShelfRegistry registry, IShelfGateway gateway)
    {
        Camera camera = Camera.main;
        if (camera == null)
        {
            // Loading screens have no main camera; keep the last logged state so a reload logs only real changes.
            _current = null;
            return null;
        }

        Collider? hit = FindAimedCollider(camera);
        if (hit == null)
        {
            SetNoTarget(log, _reasonNoHit);
            return null;
        }

        ItemStorageController? controller = FindInParent<ItemStorageController>(hit);
        if (controller == null)
        {
            SetNoTarget(log, _reasonNotShelf);
            return null;
        }

        InWorldLayoutItemGroup? group = FindInParent<InWorldLayoutItemGroup>(hit);
        if (group == null)
        {
            ItemStorageHookPoint? hook = FindInParent<ItemStorageHookPoint>(hit);
            if (hook == null)
            {
                SetNoTarget(log, _reasonNoSlot);
                return controller;
            }

            group = FindActiveGroup(hook);
            if (group == null)
            {
                SetNoTarget(log, _reasonEmptySlot);
                return controller;
            }
        }

        StructureStorage? storage = group._storage;
        if (storage == null)
        {
            SetNoTarget(log, _reasonNoSlot);
            return controller;
        }

        int count = group.GetCurrentCount();
        if (count <= 0)
        {
            SetNoTarget(log, _reasonEmptySlot);
            return controller;
        }

        int item = group._itemId;
        int slot = storage.StorageSlotIndex;
        ShelfRef shelf = GetShelf(controller);
        _current = new AimTarget(shelf, new SlotRef(slot), new ItemId(item), count);
        if (!_debouncer.ShouldLog(_current, Time.unscaledTimeAsDouble))
        {
            return null;
        }

        // The live controller in hand: gateway reads hit this shelf without a rescan, never a duplicate-key twin.
        registry.Track(shelf.Key, controller);
        WriteTargetChange(log, gateway, _current, group._maxAmount, controller, storage);
        if (_lastLoggedShelfKey is not null && _lastLoggedShelfKey != shelf.Key && _lastLoggedItem != item)
        {
            WriteProbe(log, gateway, shelf, new ItemId(_lastLoggedItem));
        }

        _lastLoggedShelfKey = shelf.Key;
        _lastLoggedItem = item;
        return null;
    }

    // An empty storage never writes a target line, so it is probed here, settled by its own debouncer like aim.change.
    [HideFromIl2Cpp]
    private void UpdateEmptyProbe(IModLog log, ShelfRegistry registry, IShelfGateway gateway,
        ItemStorageController? unstocked)
    {
        AimTarget? candidate = null;
        if (unstocked != null && _lastLoggedShelfKey is not null)
        {
            ShelfRef shelf = GetShelf(unstocked);
            if (shelf.Key != _lastLoggedShelfKey)
            {
                candidate = new AimTarget(shelf, _anySlot, new ItemId(_lastLoggedItem), 0);
            }
        }

        if (_probeDebouncer.ShouldLog(candidate, Time.unscaledTimeAsDouble) && candidate is not null && unstocked != null)
        {
            registry.Track(candidate.Shelf.Key, unstocked);
            WriteProbe(log, gateway, candidate.Shelf, candidate.Item);
        }
    }

    [HideFromIl2Cpp]
    private void SetNoTarget(IModLog log, string reason)
    {
        _current = null;
        // All reasons are one no-target state; the reason logged is the one current when that state settles.
        if (!_debouncer.ShouldLog(null, Time.unscaledTimeAsDouble))
        {
            return;
        }

        log.Write(LogLevel.Info, "aim.change", new KeyValuePair<string, object?>[]
        {
            new("shelf", _noShelf),
            new("reason", reason),
        });
    }

    [HideFromIl2Cpp]
    private ShelfRef GetShelf(ItemStorageController controller)
    {
        int controllerId = controller.GetInstanceID();
        Vector3 position = controller.transform.position;
        if (_cachedShelf is null || controllerId != _cachedControllerId
            || position.x != _cachedX || position.y != _cachedY || position.z != _cachedZ)
        {
            _cachedShelf = ShelfRegistry.BuildShelfRef(controller);
            _cachedControllerId = controllerId;
            _cachedX = position.x;
            _cachedY = position.y;
            _cachedZ = position.z;
        }

        return _cachedShelf;
    }

    // Diagnostics and gateway reads (inv, free, can_hold) run only here, on a settled change, never per frame. They stay out
    // of the debounce key; a take/store on this slot still re-logs because the count is in it.
    private static void WriteTargetChange(IModLog log, IShelfGateway gateway, AimTarget target, int cap,
        ItemStorageController controller, StructureStorage storage)
    {
        int item = target.Item.Value;
        (int? saveId, int? hookCount, string? error) = ReadDiagnostics(controller, storage, item);
        List<KeyValuePair<string, object?>> line = new()
        {
            new("shelf", target.Shelf.Key),
            new("slot", target.Slot.Index),
            new("item", item),
            new("count", target.Count),
            new("cap", cap),
            new("inv", gateway.GetInventoryCount(target.Item)),
            new("free", gateway.GetFreeCapacity(target.Shelf, target.Item)),
            new("can_hold", gateway.CanHold(target.Shelf, target.Item)),
            new("save_id", saveId),
            new("hook_count", hookCount),
        };
        if (error is not null)
        {
            line.Add(new("diag_error", error));
        }

        log.Write(LogLevel.Info, "aim.change", line);
    }

    // Card 9 criterion 2: the previous target's item checked against this shelf, so a shelf that rejects the type shows.
    private static void WriteProbe(IModLog log, IShelfGateway gateway, ShelfRef shelf, ItemId item) =>
        log.Write(LogLevel.Info, "aim.probe", new KeyValuePair<string, object?>[]
        {
            new("shelf", shelf.Key),
            new("item", item.Value),
            new("can_hold", gateway.CanHold(shelf, item)),
            new("free", gateway.GetFreeCapacity(shelf, item)),
        });

    // Guarded on its own so an unreadable diagnostic (a generic-base field, a missing component) never halts the watcher.
    // Runs only on a target change, so it is not a per-frame catch.
    private static (int? SaveId, int? HookCount, string? Error) ReadDiagnostics(ItemStorageController controller,
        StructureStorage storage, int item)
    {
        try
        {
            // Sibling component, not controller._screwStructure: that field read null on a client (card 4 dump).
            ScrewStructureWithStorage? structure = FindOnObject<ScrewStructureWithStorage>(controller);
            ItemInstanceManager? items = FindOnObject<ItemInstanceManager>(storage);
            return (structure == null ? null : structure._Sons_Crafting_Structures_IScrewStructureSaveID_SaveId_k__BackingField,
                items == null ? null : items.GetItemCount(item), null);
        }
        catch (Exception ex)
        {
            return (null, null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    // Nearest hit that is not part of the player rig (card 3's approach). Triggers stay in: the slot's
    // InteractionTrigger is one. One pass for the minimum, since RaycastAll returns hits unordered.
    private static Collider? FindAimedCollider(Camera camera)
    {
        Transform view = camera.transform;
        Transform playerRoot = view.root;
        List<RaycastHit> hits = new(Physics.RaycastAll(view.position, view.forward, _maxAimDistance,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide));
        Collider? nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (RaycastHit hit in hits)
        {
            float distance = hit.distance;
            if (distance >= nearestDistance)
            {
                continue;
            }

            Collider hitCollider = hit.collider;
            if (hitCollider == null || hitCollider.transform.IsChildOf(playerRoot))
            {
                continue;
            }

            nearest = hitCollider;
            nearestDistance = distance;
        }

        return nearest;
    }

    // A hook's own collider can be the nearest hit; its stocked group is then the active child.
    private static InWorldLayoutItemGroup? FindActiveGroup(ItemStorageHookPoint hook)
    {
        Component? child = hook.GetComponentInChildren(Il2CppType.Of<InWorldLayoutItemGroup>());
        return child == null ? null : child.TryCast<InWorldLayoutItemGroup>();
    }

    // Non-generic lookups plus TryCast: the generic Unity overloads depend on an inflated method existing in the game.
    private static T? FindInParent<T>(Component component) where T : Il2CppObjectBase
    {
        Component? found = component.GetComponentInParent(Il2CppType.Of<T>());
        return found == null ? null : found.TryCast<T>();
    }

    private static T? FindOnObject<T>(Component component) where T : Il2CppObjectBase
    {
        Component? found = component.GetComponent(Il2CppType.Of<T>());
        return found == null ? null : found.TryCast<T>();
    }
}
