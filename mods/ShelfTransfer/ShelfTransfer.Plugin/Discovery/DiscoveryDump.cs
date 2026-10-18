// purpose: D6 debug dump; on the Discovery key, logs the components, fields and methods of the aimed object and its parents
// owns:    DiscoveryDump
// deps:    ModConfig, ShelfTransfer.Core.Logging (IModLog, LogLevel), UnityEngine (Camera, Physics, Input), Il2CppSystem reflection
// caution: read-only (I2): no game method invoked, no field written. Static field values are never read (that can run
//          a class constructor). Il2Cpp objects live in locals of one Update only. Only managed values reach the log,
//          never an Il2Cpp object (its ToString could be a game override)

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;

using UnityEngine;

using ShelfTransfer.Core.Logging;
using ShelfTransfer.Plugin.Config;

using Il2CppBindingFlags = Il2CppSystem.Reflection.BindingFlags;
using Il2CppFieldInfo = Il2CppSystem.Reflection.FieldInfo;
using Il2CppMethodInfo = Il2CppSystem.Reflection.MethodInfo;
using Il2CppObject = Il2CppSystem.Object;
using Il2CppParameterInfo = Il2CppSystem.Reflection.ParameterInfo;
using Il2CppSystemType = Il2CppSystem.Type;
using LogLevel = ShelfTransfer.Core.Logging.LogLevel;

namespace ShelfTransfer.Plugin.Discovery;

public sealed class DiscoveryDump : MonoBehaviour
{
    // Interaction range is a few metres; 10 m reaches any shelf the player can use without hitting distant scenery.
    private const float _maxAimDistance = 10f;

    // Aimed object plus up to 8 parents; the scene root is usually reached first. Bounded so a deep hierarchy cannot flood the log.
    private const int _maxParentDepth = 8;

    // Budget against LogSizeCap.MaxSessionBytes (10 MiB): a full dump is about 2000 lines x ~200 B = ~0.4 MiB.
    private const int _maxComponentsPerDump = 64;
    private const int _maxMembersPerComponent = 200;
    private const int _maxMembersPerDump = 2000;

    private const string _terrainColliderType = "UnityEngine.TerrainCollider";

    private const Il2CppBindingFlags _declaredMembers = Il2CppBindingFlags.DeclaredOnly | Il2CppBindingFlags.Instance
        | Il2CppBindingFlags.Static | Il2CppBindingFlags.Public | Il2CppBindingFlags.NonPublic;

    private IModLog? _log;
    private ModConfig? _config;
    private bool _isHalted;

    public DiscoveryDump(IntPtr ptr) : base(ptr) { }

    // Managed parameters cannot cross into Il2Cpp; hidden so ClassInjector does not try to expose it.
    [HideFromIl2Cpp]
    public void Init(IModLog log, ModConfig config)
    {
        _log = log;
        _config = config;
    }

    public void Update()
    {
        if (_log is null || _config is null || _isHalted || !_config.DiscoveryEnabled.Value)
        {
            return;
        }

        try
        {
            if (Input.GetKeyDown(_config.DiscoveryKey.Value))
            {
                WriteDump(_log);
            }
        }
        catch (Exception ex)
        {
            // Per-frame boundary (P2): nothing reaches the game. Halting stops a broken input backend from logging every frame.
            _isHalted = true;
            _log.WriteException("discovery.tick", ex);
        }
    }

    private static void WriteDump(IModLog log)
    {
        Collider? collider = FindAimedCollider(log);
        if (collider == null)
        {
            return;
        }

        int componentsLeft = _maxComponentsPerDump;
        int membersLeft = _maxMembersPerDump;
        Transform current = collider.transform;
        for (int depth = 0; current != null && depth <= _maxParentDepth; depth++)
        {
            if (!WriteGameObject(log, current.gameObject, depth, ref componentsLeft, ref membersLeft))
            {
                return;
            }

            current = current.parent;
        }

        if (current != null)
        {
            WriteMissing(log, "depth_cap", new("limit", _maxParentDepth), new("next", current.gameObject.name));
        }
    }

    // Nearest hit that is not part of the player rig, or null after logging why there is none.
    private static Collider? FindAimedCollider(IModLog log)
    {
        Camera camera = Camera.main;
        if (camera == null)
        {
            WriteMissing(log, "no_camera");
            return null;
        }

        Transform view = camera.transform;
        Transform playerRoot = view.root;
        // Triggers stay in (shelf slots may be triggers); the player's own trigger volumes around the camera are skipped below.
        List<RaycastHit> rawHits = new(Physics.RaycastAll(view.position, view.forward, _maxAimDistance,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide));
        List<(float Distance, Collider Collider)> hits = new();
        foreach (RaycastHit hit in rawHits)
        {
            Collider hitCollider = hit.collider;
            if (hitCollider != null)
            {
                hits.Add((hit.distance, hitCollider));
            }
        }

        // RaycastAll returns hits in no particular order.
        hits.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        int skippedPlayer = 0;
        foreach ((float distance, Collider hitCollider) in hits)
        {
            if (hitCollider.transform.IsChildOf(playerRoot))
            {
                skippedPlayer++;
                continue;
            }

            log.Write(LogLevel.Info, "discovery.aim", new KeyValuePair<string, object?>[]
            {
                new("hit", hitCollider.gameObject.name),
                new("distance", MathF.Round(distance, 2)),
                new("skipped_player", skippedPlayer),
            });
            if (FormatTypeName(hitCollider.GetIl2CppType()) == _terrainColliderType)
            {
                WriteMissing(log, "no_target", new("detail", "terrain"), new("skipped_player", skippedPlayer));
                return null;
            }

            return hitCollider;
        }

        WriteMissing(log, "no_target", new("detail", "no_hit"), new("skipped_player", skippedPlayer));
        return null;
    }

    // False once the component cap is hit, which ends the walk.
    private static bool WriteGameObject(IModLog log, GameObject gameObject, int depth, ref int componentsLeft, ref int membersLeft)
    {
        string objectName = gameObject.name;
        List<Component> components = new(gameObject.GetComponents(Il2CppType.Of<Component>()));
        foreach (Component component in components)
        {
            if (componentsLeft == 0)
            {
                WriteMissing(log, "component_cap", new("limit", _maxComponentsPerDump), new("object", objectName));
                return false;
            }

            componentsLeft--;
            WriteComponent(log, component, depth, objectName, ref membersLeft);
        }

        return true;
    }

    private static void WriteComponent(IModLog log, Component component, int depth, string objectName, ref int membersLeft)
    {
        if (component == null)
        {
            // A missing script shows up as a null entry in GetComponents.
            WriteMissing(log, "null_component", new("depth", depth), new("object", objectName));
            return;
        }

        if (!TryRead(() => ReadTypeInfo(component.GetIl2CppType()), out (Il2CppSystemType Type, string Name, bool IsEngine, Il2CppSystemType? Base) type, out string error))
        {
            WriteMissing(log, "unreadable", new("depth", depth), new("object", objectName), new("error", error));
            return;
        }

        bool isDumped = !type.IsEngine && membersLeft > 0;
        log.Write(LogLevel.Info, "discovery.component", new KeyValuePair<string, object?>[]
        {
            new("depth", depth),
            new("object", objectName),
            new("component", type.Name),
            new("members", type.IsEngine ? "engine" : isDumped ? "dumped" : "budget"),
        });

        if (isDumped)
        {
            WriteMembers(log, component, type.Type, type.Name, ref membersLeft);
        }
    }

    // Walks the game-side base types too, so private fields of a game base class are listed.
    private static void WriteMembers(IModLog log, Component component, Il2CppSystemType type, string typeName, ref int membersLeft)
    {
        int componentLeft = _maxMembersPerComponent;
        Il2CppSystemType? owner = type;
        while (owner != null)
        {
            Il2CppSystemType declaring = owner;
            if (!TryRead(() => ReadTypeInfo(declaring), out (Il2CppSystemType Type, string Name, bool IsEngine, Il2CppSystemType? Base) ownerInfo, out string ownerError))
            {
                WriteMissing(log, "unreadable", new("component", typeName), new("kind", "base_type"), new("error", ownerError));
                return;
            }

            if (ownerInfo.IsEngine)
            {
                return;
            }

            string ownerName = ownerInfo.Name;
            owner = ownerInfo.Base;
            if (TryRead(() => new List<Il2CppFieldInfo>(declaring.GetFields(_declaredMembers)), out var fields, out string fieldsError))
            {
                foreach (Il2CppFieldInfo field in fields)
                {
                    if (!TakeMemberBudget(log, typeName, ref componentLeft, ref membersLeft))
                    {
                        return;
                    }

                    WriteField(log, component, field, typeName, ownerName);
                }
            }
            else
            {
                WriteMissing(log, "unreadable", new("component", typeName), new("owner", ownerName), new("kind", "field"), new("error", fieldsError));
            }

            if (TryRead(() => new List<Il2CppMethodInfo>(declaring.GetMethods(_declaredMembers)), out var methods, out string methodsError))
            {
                foreach (Il2CppMethodInfo method in methods)
                {
                    if (!TakeMemberBudget(log, typeName, ref componentLeft, ref membersLeft))
                    {
                        return;
                    }

                    WriteMethod(log, method, typeName, ownerName);
                }
            }
            else
            {
                WriteMissing(log, "unreadable", new("component", typeName), new("owner", ownerName), new("kind", "method"), new("error", methodsError));
            }
        }
    }

    private static bool TakeMemberBudget(IModLog log, string typeName, ref int componentLeft, ref int membersLeft)
    {
        if (membersLeft == 0)
        {
            WriteMissing(log, "member_cap", new("component", typeName), new("scope", "dump"), new("limit", _maxMembersPerDump));
            return false;
        }

        if (componentLeft == 0)
        {
            WriteMissing(log, "member_cap", new("component", typeName), new("scope", "component"), new("limit", _maxMembersPerComponent));
            return false;
        }

        componentLeft--;
        membersLeft--;
        return true;
    }

    private static void WriteField(IModLog log, Component component, Il2CppFieldInfo field, string typeName, string ownerName)
    {
        if (!TryRead(() => (field.Name, field.FieldType, field.IsStatic), out (string Name, Il2CppSystemType Type, bool IsStatic) info, out string error))
        {
            WriteMissing(log, "unreadable", new("component", typeName), new("owner", ownerName), new("kind", "field"), new("error", error));
            return;
        }

        List<KeyValuePair<string, object?>> line = BuildMemberLine("field", typeName, ownerName, info.Name, FormatTypeName(info.Type), info.IsStatic);
        string? valueError = null;
        if (!info.IsStatic)
        {
            if (TryRead(() => ReadValue(field.GetValue(component), info.Type), out KeyValuePair<string, object?>? value, out string readError))
            {
                if (value is not null)
                {
                    line.Add(value.Value);
                }
            }
            else
            {
                valueError = readError;
            }
        }

        log.Write(LogLevel.Info, "discovery.member", line);
        if (valueError is not null)
        {
            WriteMissing(log, "unreadable", new("component", typeName), new("name", info.Name), new("kind", "value"), new("error", valueError));
        }
    }

    private static void WriteMethod(IModLog log, Il2CppMethodInfo method, string typeName, string ownerName)
    {
        if (!TryRead(() => (method.Name, FormatTypeName(method.ReturnType), method.IsStatic, FormatParameters(method)),
                out (string Name, string ReturnType, bool IsStatic, string Parameters) info, out string error))
        {
            WriteMissing(log, "unreadable", new("component", typeName), new("owner", ownerName), new("kind", "method"), new("error", error));
            return;
        }

        List<KeyValuePair<string, object?>> line = BuildMemberLine("method", typeName, ownerName, info.Name, info.ReturnType, info.IsStatic);
        line.Add(new("params", info.Parameters));
        log.Write(LogLevel.Info, "discovery.member", line);
    }

    private static List<KeyValuePair<string, object?>> BuildMemberLine(string kind, string typeName, string ownerName, string name, string memberType, bool isStatic)
    {
        List<KeyValuePair<string, object?>> line = new()
        {
            new("kind", kind),
            new("component", typeName),
            new("name", name),
            new("type", memberType),
        };
        if (ownerName != typeName)
        {
            line.Add(new("owner", ownerName));
        }

        if (isStatic)
        {
            line.Add(new("static", true));
        }

        return line;
    }

    private static string FormatParameters(Il2CppMethodInfo method)
    {
        List<Il2CppParameterInfo> parameters = new(method.GetParameters());
        StringBuilder text = new();
        foreach (Il2CppParameterInfo parameter in parameters)
        {
            if (text.Length > 0)
            {
                text.Append(", ");
            }

            text.Append(FormatTypeName(parameter.ParameterType)).Append(' ').Append(parameter.Name);
        }

        return text.ToString();
    }

    // Plain data (primitives, strings, enums) is unboxed to a managed value; for other references only null-ness
    // and a runtime type that differs from the declared one are logged, since their text would need a method call.
    private static KeyValuePair<string, object?>? ReadValue(Il2CppObject? value, Il2CppSystemType declaredType)
    {
        if (value is null)
        {
            return new("value", null);
        }

        Il2CppSystemType plainType = declaredType.IsEnum ? declaredType.GetEnumUnderlyingType() : declaredType;
        object? plain = UnboxPlain(value, FormatTypeName(plainType));
        if (plain is not null)
        {
            return new("value", plain);
        }

        string runtimeType = FormatTypeName(value.GetIl2CppType());
        return runtimeType == FormatTypeName(declaredType) ? null : new("value_type", runtimeType);
    }

    private static object? UnboxPlain(Il2CppObject value, string typeName) => typeName switch
    {
        "System.Boolean" => value.Unbox<bool>(),
        "System.Byte" => value.Unbox<byte>(),
        "System.SByte" => value.Unbox<sbyte>(),
        "System.Int16" => value.Unbox<short>(),
        "System.UInt16" => value.Unbox<ushort>(),
        "System.Int32" => value.Unbox<int>(),
        "System.UInt32" => value.Unbox<uint>(),
        "System.Int64" => value.Unbox<long>(),
        "System.UInt64" => value.Unbox<ulong>(),
        "System.Single" => value.Unbox<float>(),
        "System.Double" => value.Unbox<double>(),
        "System.Char" => value.Unbox<char>(),
        "System.String" => IL2CPP.Il2CppStringToManaged(value.Pointer),
        _ => null,
    };

    // RuntimeType.ToString gives "List`1[Item]"; FullName would carry assembly-qualified generic args past the 256-char cap.
    private static string FormatTypeName(Il2CppSystemType type) => type.ToString();

    private static (Il2CppSystemType Type, string Name, bool IsEngine, Il2CppSystemType? Base) ReadTypeInfo(Il2CppSystemType type) =>
        (type, FormatTypeName(type), IsEngineNamespace(type.Namespace), type.BaseType);

    private static bool IsEngineNamespace(string? ns) =>
        ns is not null && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)
            || ns == "UnityEngine" || ns.StartsWith("UnityEngine.", StringComparison.Ordinal));

    // The one guarded read: interop throws on unreadable metadata or values, and the dump must log that as
    // discovery.missing (criterion 3) instead of losing the rest of the dump. Debug path, not per-frame.
    private static bool TryRead<T>(Func<T> read, [MaybeNullWhen(false)] out T value, out string error)
    {
        try
        {
            value = read();
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            value = default;
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static void WriteMissing(IModLog log, string reason, params KeyValuePair<string, object?>[] details)
    {
        List<KeyValuePair<string, object?>> line = new() { new("reason", reason) };
        line.AddRange(details);
        log.Write(LogLevel.Info, "discovery.missing", line);
    }
}
