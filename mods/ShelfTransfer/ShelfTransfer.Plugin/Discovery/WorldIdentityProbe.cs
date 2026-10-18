// purpose: card 47 probe; logs what GameSetup and GameState expose about the current world as discovery.world lines
// owns:    WorldIdentityProbe
// deps:    DiscoveryDump (TryRead, WriteMissing), ShelfTransfer.Core.Logging (IModLog, LogLevel), interop GameSetup, GameState, GameStateData
// caution: read-only (I2): static auto-property backing fields and instance fields only, except the IsMpClient and
//          IsSavedGame getters, which have no backing field and so run game code. A type's first touch runs its class
//          constructor; on a key press in a loaded world the game has already run both. Game types appear only inside
//          the read lambdas, so a member missing after a game update fails that one read, not the whole probe

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

using Endnight.Types;
using Sons.Save;
using TheForest.Utils;

using ShelfTransfer.Core.Logging;

namespace ShelfTransfer.Plugin.Discovery;

public static class WorldIdentityProbe
{
    private const string _gameSetupSource = "TheForest.Utils.GameSetup";
    private const string _gameStateSource = "Sons.Save.GameState";

    // 32 bits: enough to tell two ids apart in a debug log without printing the id.
    private const int _userIdHashLength = 8;

    private static readonly (string Key, Func<object?> Read)[] _gameSetupReads =
    {
        ("slot", () => GameSetup._Slot_k__BackingField),
        ("save_user_hash", () => HashUserId(GameSetup._SaveUserId_k__BackingField)),
        ("mp_type", () => GameSetup._MpType_k__BackingField),
        ("game", () => GameSetup._Game_k__BackingField),
        ("init", () => GameSetup._Init_k__BackingField),
        // MpTypes has only Server and Client, so mp_type alone cannot tell single-player from client; the getter can.
        ("is_mp_client", () => GameSetup.IsMpClient),
        ("is_saved_game", () => GameSetup.IsSavedGame),
    };

    private static readonly (string Key, Func<object?> Read)[] _gameStateReads =
    {
        ("game_name", () => ReadGameStateData()?.GameName),
        ("game_id", () => ReadGameStateData()?.GameId),
        ("game_type", () => ReadGameStateData()?.GameType),
        ("seed", () => ReadGameStateData()?.ObjectRandomizerSeed),
        ("crash_site", () => ReadGameStateData()?.CrashSite),
    };

    public static void Write(IModLog log)
    {
        WriteSource(log, _gameSetupSource, _gameSetupReads);

        if (!DiscoveryDump.TryRead(() => ReadGameStateData() is not null, out bool hasState, out string error))
        {
            DiscoveryDump.WriteMissing(log, "unreadable", new("source", _gameStateSource), new("name", "instance"), new("error", error));
            return;
        }

        if (!hasState)
        {
            DiscoveryDump.WriteMissing(log, "no_instance", new KeyValuePair<string, object?>("source", _gameStateSource));
            return;
        }

        WriteSource(log, _gameStateSource, _gameStateReads);
    }

    private static void WriteSource(IModLog log, string source, (string Key, Func<object?> Read)[] reads)
    {
        List<KeyValuePair<string, object?>> line = new() { new("source", source) };
        List<(string Key, string Error)> failures = new();
        foreach ((string key, Func<object?> read) in reads)
        {
            if (DiscoveryDump.TryRead(read, out object? value, out string error))
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

    // SaveUserId is the player's platform id (personal data); equal ids still give equal hashes, which is all card 39 needs.
    private static string? HashUserId(string? userId) =>
        string.IsNullOrEmpty(userId)
            ? userId
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[.._userIdHashLength].ToLowerInvariant();
}
