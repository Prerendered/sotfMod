// purpose: the `transfer` command (debug console and the transfer prompt): list target shelves by index, start a transfer
//          of the aimed item, cancel it
// owns:    TransferCommand
// deps:    TransferDriver, AimWatcher, ShelfRegistry, IShelfGateway, ShelfListBuilder, StopReasonText, IModLog,
//          TheForest.DebugConsole (interop: RegisterCommand, TryGetDynamicCommands, LogCommandInfo), UnityEngine
// caution: indexes refer to the last list snapshot, never a fresh scan, so the number typed is the row printed. I2 - never
//          calls TakeOne/StoreOne; only the runner does, through TransferDriver.
//          The game keeps the sender behind a WeakReference; this component is the sender and lives on BepInEx's manager object

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Il2CppInterop.Runtime.Attributes;

using Sons.Gameplay;
using TheForest;

using UnityEngine;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Labels;
using ShelfTransfer.Core.Logging;
using ShelfTransfer.Core.Shelves;
using ShelfTransfer.Core.Transfer;
using ShelfTransfer.Core.Ui;
using ShelfTransfer.Plugin.Game;
using ShelfTransfer.Plugin.Runtime;

using Il2CppCommands = Il2CppSystem.Collections.Generic.Dictionary<Il2CppSystem.Object, TheForest.DynamicCommand>;
using LogLevel = ShelfTransfer.Core.Logging.LogLevel;

namespace ShelfTransfer.Plugin.Commands;

public sealed class TransferCommand : MonoBehaviour
{
    private const string _commandName = "transfer";
    private const string _listArgument = "list";
    private const string _cancelArgument = "cancel";
    private const string _usage = "Usage: transfer list | transfer <count> <index> | transfer cancel";
    private const string _failedText = "transfer failed, see the mod log";

    // The console can drop dynamic commands (ClearDynamicCommands); a cheap lookup once a second re-adds ours.
    private const double _registerCheckSeconds = 1.0;
    private const int _maxPrintedRows = 20;

    private static readonly IReadOnlyDictionary<string, ShelfLabel> _noLabels = new Dictionary<string, ShelfLabel>();

    private IModLog? _log;
    private ShelfRegistry? _registry;
    private IShelfGateway? _gateway;
    private AimWatcher? _aimWatcher;
    private TransferDriver? _driver;
    private bool _isHalted;
    private double _nextRegisterCheckAt;

    // Held so the converted delegate lives as long as the registration that points at it.
    private Il2CppSystem.Func<string, bool>? _handler;
    private IReadOnlyList<ShelfRow> _indexedTargets = Array.Empty<ShelfRow>();

    // The prompt's aim, taken when it opens: the camera may move while the player types. The controller is the looked-at
    // one, re-tracked before a start because a list rescan in between can map a duplicate key to its twin.
    private AimTarget? _capturedAim;
    private ItemStorageController? _capturedShelf;
    private bool _hasCapturedShelf;
    private string _runningText = string.Empty;

    public TransferCommand(IntPtr ptr) : base(ptr) { }

    public bool IsRunning => _driver is not null && _driver.IsRunning;

    /// <summary>True when the shelf captured by CaptureAim has been destroyed since.</summary>
    public bool IsCapturedShelfGone => _hasCapturedShelf && _capturedShelf == null;

    // Managed parameters cannot cross into Il2Cpp; hidden so ClassInjector does not try to expose it.
    [HideFromIl2Cpp]
    public void Init(IModLog log, ShelfRegistry registry, IShelfGateway gateway, AimWatcher aimWatcher, TransferDriver driver)
    {
        _log = log;
        _registry = registry;
        _gateway = gateway;
        _aimWatcher = aimWatcher;
        _driver = driver;
    }

    /// <summary>Takes the slot aimed at right now for RunCaptured; null when none is aimed.</summary>
    [HideFromIl2Cpp]
    public AimTarget? CaptureAim()
    {
        _capturedAim = _aimWatcher?.GetCurrent();
        _capturedShelf = null;
        _hasCapturedShelf = false;
        if (_capturedAim is not null && _aimWatcher is not null && _registry is not null && _aimWatcher.TrackCurrent()
            && _registry.TryGetLive(_capturedAim.Shelf.Key, out ItemStorageController? shelf))
        {
            _capturedShelf = shelf;
            _hasCapturedShelf = true;
        }

        return _capturedAim;
    }

    /// <summary>Drops the CaptureAim slot so no Il2Cpp controller is held once the prompt is closed.</summary>
    [HideFromIl2Cpp]
    public void ReleaseAim()
    {
        _capturedAim = null;
        _capturedShelf = null;
        _hasCapturedShelf = false;
    }

    /// <summary>
    /// Runs one command line against the CaptureAim slot. Lines go to <paramref name="output"/>, the finished run's result
    /// line to <paramref name="onFinished"/>. True when a transfer started.
    /// </summary>
    [HideFromIl2Cpp]
    public bool RunCaptured(string arguments, List<string> output, Action<string> onFinished) =>
        Run(arguments, _capturedAim, TrackCaptured, output, onFinished);

    /// <summary>The running transfer's start line, or null when none runs.</summary>
    [HideFromIl2Cpp]
    public string? GetRunningText() => IsRunning ? _runningText : null;

    public void Update()
    {
        if (_log is null || _isHalted || Time.unscaledTimeAsDouble < _nextRegisterCheckAt)
        {
            return;
        }

        _nextRegisterCheckAt = Time.unscaledTimeAsDouble + _registerCheckSeconds;
        try
        {
            EnsureRegistered(_log);
        }
        catch (Exception ex)
        {
            // Per-frame boundary (P2): an interop failure here would repeat every second, so log once and stop.
            _isHalted = true;
            _log.WriteException("console.register", ex);
        }
    }

    [HideFromIl2Cpp]
    private void EnsureRegistered(IModLog log)
    {
        if (DebugConsole.TryGetDynamicCommands(_commandName, out Il2CppCommands? commands) && commands != null
            && commands.ContainsKey(this))
        {
            return;
        }

        _handler ??= new Func<string, bool>(HandleCommand);
        DebugConsole.RegisterCommand(_commandName, _handler, this);
        DebugConsole? console = DebugConsole.Instance;
        log.Write(LogLevel.Info, "console.register", new KeyValuePair<string, object?>[]
        {
            new("command", _commandName),
            new("console", console != null),
            new("allowed", console == null ? null : console.IsConsoleAllowed()),
        });
    }

    // Called by the game's console with the live aim; Run is the boundary, so no throw crosses back into Il2Cpp.
    [HideFromIl2Cpp]
    private bool HandleCommand(string? arguments)
    {
        if (_log is null || _registry is null || _gateway is null || _aimWatcher is null || _driver is null)
        {
            return false;
        }

        List<string> output = new();
        Run(arguments ?? "", _aimWatcher.GetCurrent(), _aimWatcher.TrackCurrent, output, Print);
        foreach (string line in output)
        {
            Print(line);
        }

        // The command always answers with its own console line, so the game's failure text would only add noise.
        return true;
    }

    // The one boundary for both callers (console callback, prompt frame); a throw becomes the failed line.
    [HideFromIl2Cpp]
    private bool Run(string arguments, AimTarget? aim, Func<bool> trackSource, List<string> output, Action<string> onFinished)
    {
        if (_log is null || _registry is null || _gateway is null || _driver is null)
        {
            output.Add(_failedText);
            return false;
        }

        bool hasStarted = false;
        bool hasFailed = false;
        try
        {
            hasStarted = Dispatch(arguments, aim, trackSource, output, onFinished, _log, _registry, _gateway, _driver);
        }
        catch (Exception ex)
        {
            _log.WriteException("transfer.command", ex);
            hasFailed = true;
        }

        if (hasFailed)
        {
            output.Add(_failedText);
        }

        return hasStarted;
    }

    [HideFromIl2Cpp]
    private bool Dispatch(string arguments, AimTarget? aim, Func<bool> trackSource, List<string> output,
        Action<string> onFinished, IModLog log, ShelfRegistry registry, IShelfGateway gateway, TransferDriver driver)
    {
        string[] parts = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // Whether the console passes the whole line or only what follows the command is unverified; accept both.
        if (parts.Length > 0 && IsWord(parts[0], _commandName))
        {
            parts = parts[1..];
        }

        if (parts.Length == 0)
        {
            output.Add(_usage);
        }
        else if (parts.Length == 1 && IsWord(parts[0], _listArgument))
        {
            ListTargets(log, registry, gateway, aim, output);
        }
        else if (parts.Length == 1 && IsWord(parts[0], _cancelArgument))
        {
            output.Add(driver.RequestCancel() ? "Cancel requested; stops after the current item." : "No transfer is running.");
        }
        else
        {
            return StartTransfer(log, gateway, driver, aim, trackSource, parts, arguments, output, onFinished);
        }

        return false;
    }

    [HideFromIl2Cpp]
    private void ListTargets(IModLog log, ShelfRegistry registry, IShelfGateway gateway, AimTarget? aim, List<string> output)
    {
        Camera camera = Camera.main;
        if (camera == null)
        {
            output.Add("No world loaded.");
            return;
        }

        Vector3 player = camera.transform.position;
        List<ShelfCandidate> candidates = new();
        foreach (ShelfRef shelf in registry.List())
        {
            // CanHold then GetFreeCapacity back to back share one hook walk in the gateway.
            bool canHold = aim is not null && gateway.CanHold(shelf, aim.Item);
            int free = aim is null ? 0 : gateway.GetFreeCapacity(shelf, aim.Item);
            candidates.Add(new ShelfCandidate(shelf, free, canHold));
        }

        int sourceCount = aim is null ? 0 : gateway.GetCount(aim.Shelf, aim.Item);
        ShelfList list = ShelfListBuilder.Build(candidates, aim?.Shelf.Key ?? "", sourceCount,
            new WorldPosition(player.x, player.y, player.z), _noLabels);
        _indexedTargets = list.Groups.SelectMany(group => group.Rows).ToList();

        output.Add(aim is null
            ? $"{_indexedTargets.Count} shelves by distance (aim at a stocked slot to see how many fit):"
            : $"{_indexedTargets.Count} targets for item {aim.Item.Value} by distance (fits = how many of the {sourceCount} go there):");
        for (int i = 0; i < _indexedTargets.Count; i++)
        {
            ShelfRow row = _indexedTargets[i];
            int index = i + 1;
            log.Write(LogLevel.Info, "shelves.index", new KeyValuePair<string, object?>[]
            {
                new("index", index),
                new("shelf", row.Shelf.Key),
                new("distance", Math.Round(row.Distance, 1)),
                new("bearing", row.Bearing),
                new("item", aim?.Item.Value),
                new("fits", row.Fits),
            });
            if (i < _maxPrintedRows)
            {
                output.Add(string.Format(CultureInfo.InvariantCulture, "{0}. {1}  {2:0} m {3}  fits {4}",
                    index, row.Shelf.TypeName, row.Distance, row.Bearing, row.Fits));
            }
        }

        if (_indexedTargets.Count > _maxPrintedRows)
        {
            output.Add($"... {_indexedTargets.Count - _maxPrintedRows} more in the mod log (shelves.index lines).");
        }
    }

    [HideFromIl2Cpp]
    private bool StartTransfer(IModLog log, IShelfGateway gateway, TransferDriver driver, AimTarget? aim,
        Func<bool> trackSource, string[] parts, string arguments, List<string> output, Action<string> onFinished)
    {
        if (driver.IsRunning)
        {
            log.Write(LogLevel.Warn, "transfer.request", new KeyValuePair<string, object?>[]
            {
                new("outcome", "refused"),
                new("reason", "busy"),
                new("args", arguments),
            });
            output.Add("A transfer is already running; wait for it or type: transfer cancel");
            return false;
        }

        if (parts.Length != 2 || !TryParsePositive(parts[0], out int amount))
        {
            output.Add(Block(log, gateway, aim, "usage", arguments, _usage));
            return false;
        }

        if (aim is null)
        {
            output.Add(Block(log, gateway, null, "no aimed slot", arguments,
                "Aim at a shelf slot that holds items, then run the command."));
            return false;
        }

        if (!TryParsePositive(parts[1], out int index) || index > _indexedTargets.Count)
        {
            string detail = _indexedTargets.Count == 0 ? "no shelf list" : $"index outside 1..{_indexedTargets.Count}";
            string message = _indexedTargets.Count == 0
                ? "No shelf list yet; run: transfer list"
                : $"Index must be 1 to {_indexedTargets.Count}; run transfer list to refresh.";
            output.Add(Block(log, gateway, aim, detail, arguments, message));
            return false;
        }

        ShelfRef target = _indexedTargets[index - 1].Shelf;
        if (target.Key == aim.Shelf.Key)
        {
            output.Add(Block(log, gateway, aim, "target is the source", arguments, "That index is the shelf you are aiming at."));
            return false;
        }

        // A list rescans and can map a duplicate key to the other twin; the source must be the looked-at shelf.
        if (!trackSource())
        {
            output.Add(Block(log, gateway, aim, "aim lost", arguments,
                "Could not confirm the aimed shelf; aim at the slot and try again."));
            return false;
        }

        TransferRequest request = new(aim.Shelf, aim.Slot, target, aim.Item, amount);
        if (!driver.TryStart(request, report => onFinished(StopReasonText.Format(report).Text)))
        {
            output.Add(Block(log, gateway, aim, "driver not ready", arguments, "Transfer could not start, see the mod log."));
            return false;
        }

        _runningText = $"Moving up to {amount} of item {aim.Item.Value} to #{index} {target.TypeName}...";
        output.Add(_runningText);
        return true;
    }

    // Mirrors the runner's own transfer.stop fields, so a refused command reads the same in the log as a refused plan.
    // Returns the player-facing message.
    [HideFromIl2Cpp]
    private static string Block(IModLog log, IShelfGateway gateway, AimTarget? aim, string detail, string arguments,
        string message)
    {
        int? sourceCount = aim is null ? null : gateway.GetCount(aim.Shelf, aim.Item);
        log.Write(LogLevel.Info, "transfer.stop", new KeyValuePair<string, object?>[]
        {
            new("reason", StopReason.BlockedBeforeStart),
            new("s0", sourceCount),
            new("moved", 0),
            new("remaining", sourceCount),
            new("stranded", 0),
            new("detail", detail),
            new("args", arguments),
        });
        return message;
    }

    // The prompt's counterpart of AimWatcher.TrackCurrent: the controller captured at open instead of a fresh ray.
    [HideFromIl2Cpp]
    private bool TrackCaptured()
    {
        if (_capturedAim is null || _registry is null || !_hasCapturedShelf || _capturedShelf == null)
        {
            return false;
        }

        _registry.Track(_capturedAim.Shelf.Key, _capturedShelf);
        return true;
    }

    private static bool TryParsePositive(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    private static bool IsWord(string text, string word) => string.Equals(text, word, StringComparison.OrdinalIgnoreCase);

    // Guarded here because it also runs from the driver's finish callback, outside any catch, and the console may be
    // gone by then (world unload). Best effort: the mod log already has the full report.
    [HideFromIl2Cpp]
    private void Print(string message)
    {
        try
        {
            DebugConsole? console = DebugConsole.Instance;
            if (console != null)
            {
                console.LogCommandInfo(message);
            }
        }
        catch (Exception ex)
        {
            _log?.WriteException("console.print", ex);
        }
    }
}
