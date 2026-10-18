// purpose: resolves the aimed shelf slot (shelf, hook index, item, count) every frame and logs aim.change on change
// owns:    AimWatcher
// deps:    ShelfRegistry, AimTarget, IModLog, UnityEngine (Camera, Physics), Sons.Gameplay / Sons.Inventory (interop)
// caution: read-only (I2): field reads plus the getters GetCurrentCount, GetItemCount, StorageSlotIndex; no Harmony patch.
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
    private bool _isHalted;
    private bool _hasListedShelves;
    private AimState? _lastState;

    // Key cache: GetShelfKey formats a string, so it is rebuilt only when the aimed controller or its position changes.
    private int _cachedControllerId;
    private float _cachedX;
    private float _cachedY;
    private float _cachedZ;
    private ShelfRef? _cachedShelf;

    private AimTarget? _current;

    public AimWatcher(IntPtr ptr) : base(ptr) { }

    // Managed parameters cannot cross into Il2Cpp; hidden so ClassInjector does not try to expose it.
    [HideFromIl2Cpp]
    public void Init(IModLog log, ShelfRegistry registry)
    {
        _log = log;
        _registry = registry;
    }

    /// <summary>The slot aimed at this frame, or null when there is none (or the watcher halted).</summary>
    [HideFromIl2Cpp]
    public AimTarget? GetCurrent() => _current;

    public void Update()
    {
        if (_log is null || _registry is null || _isHalted)
        {
            return;
        }

        try
        {
            Tick(_log, _registry);
        }
        catch (Exception ex)
        {
            // Per-frame boundary (P2): log once and stop for the session rather than throw every frame.
            _isHalted = true;
            _current = null;
            _log.WriteException("aim.tick", ex);
        }
    }

    [HideFromIl2Cpp]
    private void Tick(IModLog log, ShelfRegistry registry)
    {
        Camera camera = Camera.main;
        if (camera == null)
        {
            // Loading screens have no main camera; keep the last logged state so a reload logs only real changes.
            _current = null;
            return;
        }

        Collider? hit = FindAimedCollider(camera);
        if (hit == null)
        {
            SetNoTarget(log, _reasonNoHit);
            return;
        }

        ItemStorageController? controller = FindInParent<ItemStorageController>(hit);
        if (controller == null)
        {
            SetNoTarget(log, _reasonNotShelf);
            return;
        }

        InWorldLayoutItemGroup? group = FindInParent<InWorldLayoutItemGroup>(hit);
        if (group == null)
        {
            ItemStorageHookPoint? hook = FindInParent<ItemStorageHookPoint>(hit);
            if (hook == null)
            {
                SetNoTarget(log, _reasonNoSlot);
                return;
            }

            group = FindActiveGroup(hook);
            if (group == null)
            {
                SetNoTarget(log, _reasonEmptySlot);
                return;
            }
        }

        StructureStorage? storage = group._storage;
        if (storage == null)
        {
            SetNoTarget(log, _reasonNoSlot);
            return;
        }

        int count = group.GetCurrentCount();
        if (count <= 0)
        {
            SetNoTarget(log, _reasonEmptySlot);
            return;
        }

        int item = group._itemId;
        int slot = storage.StorageSlotIndex;
        ShelfRef shelf = GetShelf(controller);
        _current = new AimTarget(shelf, new SlotRef(slot), new ItemId(item), count);

        AimState state = new(shelf.Key, slot, item, count, null);
        if (state == _lastState)
        {
            return;
        }

        _lastState = state;
        WriteTargetChange(log, shelf, slot, item, count, group._maxAmount, controller, storage);
        if (!_hasListedShelves)
        {
            // First shelf aimed this session: one scan so the key-to-object map exists for card 9.
            _hasListedShelves = true;
            registry.List();
        }
    }

    [HideFromIl2Cpp]
    private void SetNoTarget(IModLog log, string reason)
    {
        _current = null;
        AimState state = new(null, 0, 0, 0, reason);
        if (state == _lastState)
        {
            return;
        }

        _lastState = state;
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

    // Diagnostics (cap, save_id, hook_count) are read only here, on a change, not every frame.
    private static void WriteTargetChange(IModLog log, ShelfRef shelf, int slot, int item, int count, int cap,
        ItemStorageController controller, StructureStorage storage)
    {
        (int? saveId, int? hookCount, string? error) = ReadDiagnostics(controller, storage, item);
        List<KeyValuePair<string, object?>> line = new()
        {
            new("shelf", shelf.Key),
            new("slot", slot),
            new("item", item),
            new("count", count),
            new("cap", cap),
            new("save_id", saveId),
            new("hook_count", hookCount),
        };
        if (error is not null)
        {
            line.Add(new("diag_error", error));
        }

        log.Write(LogLevel.Info, "aim.change", line);
    }

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

    // Debounce key: a line is written only when one of these changes (a count change while aiming counts).
    private readonly record struct AimState(string? ShelfKey, int Slot, int Item, int Count, string? Reason);
}
