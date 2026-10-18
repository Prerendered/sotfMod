// purpose: owns the current world's LabelStore over BepInEx/config/ShelfTransfer/<worldId>.json, opened on world entry (F3, I5)
// owns:    LabelsFeature (Install, Store, SetLabel, IsValidName, SaveNow, MaxNameLength, MaxNewWorldFilesPerSession)
// deps:    WorldIdSource, LabelEditorUi, ModConfig, ShelfTransfer.Core.Labels (LabelStore, JsonLabelFileSystem), ShelfTransfer.Core.Logging, BepInEx (Paths, IL2CPPChainloader)
// caution: main thread only. Store is null outside a world and is replaced on every world change, so callers read it
//          fresh each time and never keep it. A change counts only once SaveNow is called after Set; nothing else saves it.
//          UI writes go through SetLabel, the one place the name rule is enforced before anything reaches the file

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using BepInEx;
using BepInEx.Unity.IL2CPP;

using ShelfTransfer.Core.Labels;
using ShelfTransfer.Core.Logging;
using ShelfTransfer.Plugin.Config;
using ShelfTransfer.Plugin.Ui;

namespace ShelfTransfer.Plugin.Labels;

public static class LabelsFeature
{
    /// <summary>Empty files written on a first visit per game run; later new worlds load but get no file until SaveNow.</summary>
    public const int MaxNewWorldFilesPerSession = 5;

    /// <summary>Longest label or group name, after trimming; the editor's text fields stop at this length.</summary>
    public const int MaxNameLength = 40;

    private const string _folderName = "ShelfTransfer";
    private const string _sourceGameId = "game_id";
    private const string _sourceUnknown = "unknown";

    private static IModLog? _log;
    private static ILabelFileSystem? _fileSystem;
    private static int _newWorldFiles;

    /// <summary>The loaded world's labels; null when no world is loaded or the feature halted.</summary>
    public static LabelStore? Store { get; private set; }

    public static void Install(IModLog log, ModConfig config)
    {
        log.Write(LogLevel.Info, "plugin.load", new KeyValuePair<string, object?>[] { new("feature", "labels") });
        _log = log;
        _fileSystem = new JsonLabelFileSystem(Path.Combine(Paths.ConfigPath, _folderName), log);

        // What BasePlugin.AddComponent calls; registers the injected type and adds it to BepInEx's persistent manager object.
        IL2CPPChainloader.AddUnityComponent<WorldIdSource>().Init(log, EnterWorld, LeaveWorld);
        IL2CPPChainloader.AddUnityComponent<LabelEditorUi>().Init(log, config);
    }

    /// <summary>Writes Store to its file; null when there is no Store. Blocked (logged) after a Version or Failed load.</summary>
    public static LabelSaveResult? SaveNow()
    {
        LabelStore? store = Store;
        return store is null || _log is null ? null : Save(store, _log);
    }

    /// <summary>Null or blank is valid (it clears); otherwise at most MaxNameLength chars once trimmed, no control chars.</summary>
    public static bool IsValidName(string? name) =>
        name is null || (name.Trim().Length <= MaxNameLength && !name.Any(char.IsControl));

    /// <summary>
    /// Sets the shelf's label and group in Store and saves it; null when there is no Store. Blank label and group
    /// clear the entry. Throws on a name IsValidName rejects: callers check first, so invalid text never reaches the file.
    /// </summary>
    public static LabelSaveResult? SetLabel(string key, string? label, string? group)
    {
        if (!IsValidName(label) || !IsValidName(group))
        {
            throw new ArgumentException("label or group fails IsValidName", nameof(label));
        }

        LabelStore? store = Store;
        if (store is null || _log is null)
        {
            return null;
        }

        store.Set(key, label, group);
        return Save(store, _log);
    }

    private static void EnterWorld(string? gameId)
    {
        if (_log is null || _fileSystem is null)
        {
            return;
        }

        bool hasGameId = !string.IsNullOrWhiteSpace(gameId);
        LabelStore store = new(_fileSystem, gameId);
        string world = store.SanitisedWorldId;
        _log.Write(LogLevel.Info, "labels.world", new KeyValuePair<string, object?>[]
        {
            new("world", world),
            new("source", hasGameId ? _sourceGameId : _sourceUnknown),
        });
        if (!hasGameId)
        {
            _log.Write(LogLevel.Warn, "labels.read", new KeyValuePair<string, object?>[]
            {
                new("world", world),
                new("outcome", "no_world_id"),
            });
        }

        // Corrupt: the .bad copy already exists, and without a valid file every later launch would read the same
        // corrupt bytes again. NoFile: write the empty file so it exists after the first visit (see WriteNewWorldFile).
        LabelLoadResult loaded = store.Load();
        if (loaded.Status == LabelLoadStatus.Corrupt)
        {
            Save(store, _log);
        }
        else if (loaded.Status == LabelLoadStatus.NoFile && hasGameId)
        {
            WriteNewWorldFile(store, _log);
        }

        Store = store;
    }

    // Not for unknown: a menu state or a GameId that arrives late would leave a stray unknown.json; SaveNow still
    // writes it. Capped because the GameId comes from the host, and a modified host could send a new one every join.
    private static void WriteNewWorldFile(LabelStore store, IModLog log)
    {
        if (_newWorldFiles >= MaxNewWorldFilesPerSession)
        {
            log.Write(LogLevel.Warn, "labels.write", new KeyValuePair<string, object?>[]
            {
                new("world", store.SanitisedWorldId),
                new("count", store.Count),
                new("reason", "new_world_cap"),
                new("limit", MaxNewWorldFilesPerSession),
            });
            return;
        }

        _newWorldFiles++;
        Save(store, log);
    }

    // Nothing to flush: SaveNow writes every change when it is made.
    private static void LeaveWorld()
    {
        LabelStore? store = Store;
        if (store is null || _log is null)
        {
            return;
        }

        Store = null;
        _log.Write(LogLevel.Info, "labels.world", new KeyValuePair<string, object?>[]
        {
            new("world", store.SanitisedWorldId),
            new("outcome", "left"),
        });
    }

    // JsonLabelFileSystem logs Saved and Failed; Blocked never reaches it, so that one is logged here.
    private static LabelSaveResult Save(LabelStore store, IModLog log)
    {
        LabelSaveResult saved = store.Save();
        if (saved.Status == LabelSaveStatus.Blocked)
        {
            log.Write(LogLevel.Warn, "labels.write", new KeyValuePair<string, object?>[]
            {
                new("world", store.SanitisedWorldId),
                new("count", saved.Count),
                new("reason", "blocked"),
                new("detail", saved.Detail),
            });
        }

        return saved;
    }
}
