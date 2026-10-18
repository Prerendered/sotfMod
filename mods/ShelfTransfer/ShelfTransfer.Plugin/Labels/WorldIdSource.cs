// purpose: polls the game once per second for the loaded world's GameId and reports world entry and exit to LabelsFeature
// owns:    WorldIdSource
// deps:    ShelfTransfer.Core.Logging (IModLog), interop GameState, GameStateData, Endnight SingletonBehaviour
// caution: read-only (I2): one static field, one instance field and GameStateData.GameId (a field) per poll, no getter
//          or method of the game. An observation must repeat on two consecutive polls before it is reported, so a world
//          whose GameId arrives a moment after its data does not open, then drop, an unknown.json session.
//          Update is the P2 boundary: the first throw logs labels.tick and reports exit, and polling then stays off until
//          the game restarts, so labels are unavailable for the rest of that run

using System;
using System.Runtime.CompilerServices;

using Endnight.Types;
using Il2CppInterop.Runtime.Attributes;
using Sons.Save;

using UnityEngine;

using ShelfTransfer.Core.Logging;

namespace ShelfTransfer.Plugin.Labels;

public sealed class WorldIdSource : MonoBehaviour
{
    private const long _pollIntervalMs = 1000;

    private IModLog? _log;
    private Action<string?>? _onEntered;
    private Action? _onLeft;
    private Observation _current;
    private Observation? _pending;
    private long _nextPollAt;
    private bool _isHalted;

    public WorldIdSource(IntPtr ptr) : base(ptr) { }

    // Managed parameters cannot cross into Il2Cpp; hidden so ClassInjector does not try to expose it.
    [HideFromIl2Cpp]
    public void Init(IModLog log, Action<string?> onEntered, Action onLeft)
    {
        _log = log;
        _onEntered = onEntered;
        _onLeft = onLeft;
    }

    public void Update()
    {
        if (_log is null || _onEntered is null || _onLeft is null || _isHalted)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (now < _nextPollAt)
        {
            return;
        }

        _nextPollAt = now + _pollIntervalMs;
        try
        {
            Poll(_onEntered, _onLeft);
        }
        catch (Exception ex)
        {
            // Without polling a world change goes unseen, so the store is dropped rather than left on the old world.
            // Called even when no world was current: the throw may have come from inside onEntered after a store opened.
            _isHalted = true;
            _log.WriteException("labels.tick", ex);
            _onLeft();
        }
    }

    // Plain fields only; a game type is touched nowhere else, so a member renamed by a game update throws here, inside
    // Update's catch. NoInlining keeps that resolution failure at this call rather than in Update's own JIT.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Observation ReadObservation()
    {
        GameState? state = SingletonBehaviour<GameState>._instance;
        if (state == null)
        {
            return default;
        }

        GameStateData? data = state._storedGameStateData;
        return data is null ? default : new Observation(true, data.GameId);
    }

    private void Poll(Action<string?> onEntered, Action onLeft)
    {
        Observation seen = ReadObservation();
        if (seen == _current)
        {
            _pending = null;
            return;
        }

        if (_pending != seen)
        {
            _pending = seen;
            return;
        }

        _pending = null;
        Observation previous = _current;
        _current = seen;
        if (previous.IsLoaded)
        {
            onLeft();
        }

        if (seen.IsLoaded)
        {
            onEntered(seen.GameId);
        }
    }

    // default = no world loaded. GameId stays raw here; LabelStore sanitises it before it reaches a path.
    private readonly record struct Observation(bool IsLoaded, string? GameId);
}
