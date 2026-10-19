// purpose: runs one TransferRunner at a time, ticking it once per frame inside the P2 try/catch boundary
// owns:    TransferDriver
// deps:    TransferRunner, TransferRequest, TransferReport, IShelfGateway, IModLog, ModConfig (ItemsPerFrame, ConfirmTicks, BurstStores,
//          BurstSize), UnityEngine
// caution: the only catch is around Tick; a throw aborts the runner (GatewayError, counts re-read), nothing is retried (I3)

using System;
using System.Collections.Generic;

using Il2CppInterop.Runtime.Attributes;

using UnityEngine;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Logging;
using ShelfTransfer.Core.Transfer;
using ShelfTransfer.Plugin.Config;

using LogLevel = ShelfTransfer.Core.Logging.LogLevel;

namespace ShelfTransfer.Plugin.Runtime;

public sealed class TransferDriver : MonoBehaviour
{
    private IModLog? _log;
    private ModConfig? _config;
    private IShelfGateway? _gateway;
    private TransferRunner? _runner;
    private Action<TransferReport>? _onFinished;

    public TransferDriver(IntPtr ptr) : base(ptr) { }

    public bool IsRunning => _runner is not null;

    public int Moved => _runner?.Moved ?? 0;

    /// <summary>The runner's clamped amount; 0 until it has planned (its first tick) or when nothing runs.</summary>
    public int Planned => _runner?.Requested ?? 0;

    // Managed parameters cannot cross into Il2Cpp; hidden so ClassInjector does not try to expose it.
    [HideFromIl2Cpp]
    public void Init(IModLog log, ModConfig config, IShelfGateway gateway)
    {
        _log = log;
        _config = config;
        _gateway = gateway;
    }

    /// <summary>False while another transfer runs (one runner at a time); the caller tells the player.</summary>
    [HideFromIl2Cpp]
    public bool TryStart(TransferRequest request, Action<TransferReport> onFinished)
    {
        if (_runner is not null || _log is null || _config is null || _gateway is null)
        {
            return false;
        }

        int confirmTicks = _config.ConfirmTicks.Value;
        bool isBursting = _config.BurstStores.Value;
        int burstSize = isBursting ? _config.BurstSize.Value : 1;
        List<KeyValuePair<string, object?>> line = new()
        {
            new("source", request.Source.Key),
            new("target", request.Target.Key),
            new("item", request.Item.Value),
            new("amount", request.Amount),
            new("slot", request.SourceSlot.Index),
            new("items_per_frame", _config.ItemsPerFrame.Value),
            new("confirm_ticks", confirmTicks),
        };

        // Only when on, so a run with BurstStores off logs exactly as before card 91.
        if (isBursting)
        {
            line.Add(new("burst_size", burstSize));
        }

        _log.Write(LogLevel.Info, "transfer.request", line);
        _runner = new TransferRunner(_gateway, _log, request, confirmTicks, burstSize);
        _onFinished = onFinished;
        return true;
    }

    /// <summary>Honoured after the in-flight item is stored (D7); false when nothing runs.</summary>
    public bool RequestCancel()
    {
        if (_runner is null || _log is null)
        {
            return false;
        }

        _runner.RequestCancel();
        _log.Write(LogLevel.Info, "transfer.cancel", new KeyValuePair<string, object?>[]
        {
            new("moved", _runner.Moved),
            new("state", _runner.State),
        });
        return true;
    }

    public void Update()
    {
        if (_runner is null || _log is null || _config is null)
        {
            return;
        }

        TransferRunner runner = _runner;
        int maxCycles = _config.ItemsPerFrame.Value;
        int movedBefore = runner.Moved;
        try
        {
            runner.Tick(maxCycles);
        }
        catch (Exception ex)
        {
            // P2: the runner stays pure; this boundary turns a throw into a report and the game carries on.
            _log.WriteException("transfer.tick", ex);
            runner.Abort(StopReason.GatewayError, ex.GetType().Name + ": " + ex.Message);
        }

        bool isFinished = runner.State == TransferState.Finished;
        if (runner.Moved != movedBefore || isFinished)
        {
            WriteFrame(_log, runner, maxCycles);
        }

        if (!isFinished)
        {
            return;
        }

        // Runner cleared before the callback, so a throw there cannot leave a finished runner blocking the next transfer;
        // the command's callback guards its own console call.
        Action<TransferReport>? onFinished = _onFinished;
        _runner = null;
        _onFinished = null;
        if (runner.Report is not null)
        {
            onFinished?.Invoke(runner.Report);
        }
    }

    // ItemsPerFrame evidence: the take/store lines logged since the previous frame line all ran in this frame's one Tick.
    // Wait-only ticks write nothing here; transfer.wait covers them.
    private static void WriteFrame(IModLog log, TransferRunner runner, int maxCycles) =>
        log.Write(LogLevel.Info, "transfer.frame", new KeyValuePair<string, object?>[]
        {
            new("frame", Time.frameCount),
            new("max_cycles", maxCycles),
            new("moved", runner.Moved),
            new("state", runner.State),
        });
}
