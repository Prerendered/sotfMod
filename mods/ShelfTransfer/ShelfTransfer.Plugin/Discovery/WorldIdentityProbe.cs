// purpose: card 47 probe; logs what GameSetup and GameState expose about the current world as discovery.world lines
// owns:    WorldIdentityProbe
// deps:    DiscoveryDump (TryRead, WriteMissing), ShelfTransfer.Core.Logging (IModLog, LogLevel), interop GameSetup, GameState, GameStateData
// caution: read-only (I2): static auto-property backing fields and instance fields only, except the IsMpClient and
//          IsSavedGame getters, which have no backing field and so run game code. Nothing but the GameState instance
//          check runs until a world is loaded, so no class constructor or getter runs first at the menu or during load.
//          Game types appear only inside the read lambdas, so a member missing after a game update fails that one read.
//          No user id or hash of one is ever logged (personal data)

using System;
using System.Collections.Generic;

using Endnight.Types;
using Sons.Save;
using TheForest.Utils;

using ShelfTransfer.Core.Logging;

namespace ShelfTransfer.Plugin.Discovery;

public static class WorldIdentityProbe
{
    private const string _gameSetupSource = "TheForest.Utils.GameSetup";
    private const string _gameStateSource = "Sons.Save.GameState";

    private static readonly (string Key, Func<object, object?> Read)[] _gameSetupReads =
    {
        ("slot", _ => GameSetup._Slot_k__BackingField),
        // SaveUserId is the player's platform id; even a short hash of it is reversible, so only its presence is logged.
        ("save_user_present", _ => !string.IsNullOrEmpty(GameSetup._SaveUserId_k__BackingField)),
        ("mp_type", _ => GameSetup._MpType_k__BackingField),
        ("game", _ => GameSetup._Game_k__BackingField),
        ("init", _ => GameSetup._Init_k__BackingField),
        // MpTypes has only Server and Client, so mp_type alone cannot tell single-player from client; the getter can.
        ("is_mp_client", _ => GameSetup.IsMpClient),
        ("is_saved_game", _ => GameSetup.IsSavedGame),
    };

    // Read gets the per-press GameStateData snapshot, typed object so no game type reaches Write (see caution).
    private static readonly (string Key, Func<object, object?> Read)[] _gameStateReads =
    {
        ("game_name", data => ((GameStateData)data).GameName),
        ("game_id", data => ((GameStateData)data).GameId),
        ("game_type", data => ((GameStateData)data).GameType),
        ("seed", data => ((GameStateData)data).ObjectRandomizerSeed),
        ("crash_site", data => ((GameStateData)data).CrashSite),
    };

    public static void Write(IModLog log)
    {
        // Loaded-world gate: one snapshot of the stored data per press, read before anything else game-side.
        if (!DiscoveryDump.TryRead<object?>(() => ReadGameStateData(), out object? data, out string error))
        {
            DiscoveryDump.WriteMissing(log, "unreadable", new("source", _gameStateSource), new("name", "instance"), new("error", error));
            return;
        }

        if (data is null)
        {
            DiscoveryDump.WriteMissing(log, "no_instance", new KeyValuePair<string, object?>("source", _gameStateSource));
            return;
        }

        WriteSource(log, _gameSetupSource, _gameSetupReads, data);
        WriteSource(log, _gameStateSource, _gameStateReads, data);
    }

    private static void WriteSource(IModLog log, string source, (string Key, Func<object, object?> Read)[] reads, object data)
    {
        List<KeyValuePair<string, object?>> line = new() { new("source", source) };
        List<(string Key, string Error)> failures = new();
        foreach ((string key, Func<object, object?> read) in reads)
        {
            if (DiscoveryDump.TryRead(() => read(data), out object? value, out string error))
            {
                line.Add(new(key, value));
            }
            else
            {
                failures.Add((key, error));
            }
        }

        log.Write(LogLevel.Info, "discovery.world", line);
        foreach ((string key, string error) in failures)
        {
            DiscoveryDump.WriteMissing(log, "unreadable", new("source", source), new("name", key), new("error", error));
        }
    }

    // Plain fields; GetGameName() and ObjectRandomizerSeed() are methods and would run game code.
    private static GameStateData? ReadGameStateData()
    {
        GameState? state = SingletonBehaviour<GameState>._instance;
        return state == null ? null : state._storedGameStateData;
    }

}
