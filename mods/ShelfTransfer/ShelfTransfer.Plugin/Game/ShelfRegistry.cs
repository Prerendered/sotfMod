// purpose: lists every storage controller in the scene as a ShelfRef and maps each key back to its live component
// owns:    ShelfRegistry
// deps:    ShelfKey, ShelfRef, WorldPosition, IModLog, Sons.Gameplay.ItemStorageController (interop)
// caution: read-only (I2). List() is a full scene scan; call it on demand, never per frame. The map holds live Il2Cpp
//          components across frames, so callers go through TryResolve/TryGetLive (destroyed check) before touching one

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using Il2CppInterop.Runtime;

using Sons.Gameplay;

using UnityEngine;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Logging;
using ShelfTransfer.Core.Shelves;

using LogLevel = ShelfTransfer.Core.Logging.LogLevel;
using UnityObject = UnityEngine.Object;

namespace ShelfTransfer.Plugin.Game;

public sealed class ShelfRegistry
{
    private const string _cloneSuffix = "(Clone)";

    // A gone shelf is asked about every runner tick; this bounds the scene scans that a miss triggers.
    private const double _minRescanSeconds = 5.0;

    private readonly IModLog _log;
    private readonly Dictionary<string, ItemStorageController> _byKey = new(StringComparer.Ordinal);
    private bool _hasListed;
    private double _lastListAt;

    public ShelfRegistry(IModLog log) => _log = log;

    public IReadOnlyList<ShelfRef> List()
    {
        _hasListed = true;
        _lastListAt = Time.unscaledTimeAsDouble;
        _byKey.Clear();
        List<ShelfRef> shelves = new();
        int duplicateKeys = 0;
        List<UnityObject> found = new(UnityObject.FindObjectsOfType(Il2CppType.Of<ItemStorageController>()));
        foreach (UnityObject candidate in found)
        {
            ItemStorageController? controller = candidate == null ? null : candidate.TryCast<ItemStorageController>();
            if (controller == null)
            {
                continue;
            }

            ShelfRef shelf = BuildShelfRef(controller);
            if (!_byKey.TryAdd(shelf.Key, controller))
            {
                duplicateKeys++;
                continue;
            }

            shelves.Add(shelf);
        }

        _log.Write(LogLevel.Info, "shelves.list", new KeyValuePair<string, object?>[]
        {
            new("count", shelves.Count),
            new("duplicate_keys", duplicateKeys),
        });
        return shelves;
    }

    /// <summary>
    /// <see cref="TryGetLive"/>, and on a miss one rescan (rate-limited) so shelves built or streamed in after the
    /// last <see cref="List"/> resolve too.
    /// </summary>
    public bool TryResolve(string key, [NotNullWhen(true)] out ItemStorageController? controller)
    {
        if (TryGetLive(key, out controller))
        {
            return true;
        }

        double now = Time.unscaledTimeAsDouble;
        if (_hasListed && now - _lastListAt < _minRescanSeconds)
        {
            return false;
        }

        List();
        return TryGetLive(key, out controller);
    }

    public bool TryGetLive(string key, [NotNullWhen(true)] out ItemStorageController? controller)
    {
        if (_byKey.TryGetValue(key, out ItemStorageController? found) && found != null)
        {
            controller = found;
            return true;
        }

        controller = null;
        return false;
    }

    // The one place the aim watcher and the registry derive a key, so aim.change and shelves.list always agree (D2).
    public static ShelfRef BuildShelfRef(ItemStorageController controller)
    {
        string typeName = GetTypeName(controller.gameObject.name);
        Vector3 position = controller.transform.position;
        WorldPosition worldPosition = new(position.x, position.y, position.z);
        return new ShelfRef(ShelfKey.GetShelfKey(typeName, worldPosition), typeName, worldPosition);
    }

    // Prefab name, not the component type: ItemStorageController is shared by every storage structure.
    private static string GetTypeName(string objectName)
    {
        if (!objectName.EndsWith(_cloneSuffix, StringComparison.Ordinal))
        {
            return objectName;
        }

        string prefabName = objectName[..^_cloneSuffix.Length].TrimEnd();
        return prefabName.Length == 0 ? objectName : prefabName;
    }
}
