// purpose: the `transfer` debug-console command: list target shelves by index, start a transfer of the aimed item, cancel it
// owns:    TransferCommand
// deps:    TransferDriver, AimWatcher, ShelfRegistry, IShelfGateway, ShelfListBuilder, StopReasonText, IModLog,
//          TheForest.DebugConsole (interop: RegisterCommand, TryGetDynamicCommands, LogCommandInfo), UnityEngine
// caution: indexes refer to the last `transfer list` snapshot, never a fresh scan, so the number typed is the row printed.
//          The game keeps the sender behind a WeakReference; this component is the sender and lives on BepInEx's manager object

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Il2CppInterop.Runtime.Attributes;

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

    public TransferCommand(IntPtr ptr) : base(ptr) { }

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

    // Called by the game's console; a throw must not cross back into Il2Cpp, so this is the command's one boundary.
    [HideFromIl2Cpp]
    private bool HandleCommand(string? arguments)
    {
        if (_log is null || _registry is null || _gateway is null || _aimWatcher is null || _driver is null)
        {
            return false;
        }

        bool hasFailed = false;
        try
        {
            Dispatch(arguments ?? "", _log, _registry, _gateway, _aimWatcher, _driver);
        }
        catch (Exception ex)
        {
            _log.WriteException("transfer.command", ex);
            hasFailed = true;
        }

        if (hasFailed)
        {
            Print("transfer failed, see the mod log");
        }

        // The command always answers with its own console line, so the game's failure text would only add noise.
        return true;
    }

    [HideFromIl2Cpp]
    private void Dispatch(string arguments, IModLog log, ShelfRegistry registry, IShelfGateway gateway, AimWatcher aimWatcher,
        TransferDriver driver)
    {
        string[] parts = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // Whether the console passes the whole line or only what follows the command is unverified; accept both.
        if (parts.Length > 0 && IsWord(parts[0], _commandName))
        {
            parts = parts[1..];
        }

        if (parts.Length == 0)
        {
            Print(_usage);
        }
        else if (parts.Length == 1 && IsWord(parts[0], _listArgument))
        {
            ListTargets(log, registry, gateway, aimWatcher.GetCurrent());
        }
        else if (parts.Length == 1 && IsWord(parts[0], _cancelArgument))
        {
            Print(driver.RequestCancel() ? "Cancel requested; stops after the current item." : "No transfer is running.");
        }
        else
        {
            StartTransfer(log, gateway, aimWatcher, driver, parts, arguments);
        }
    }

    [HideFromIl2Cpp]
    private void ListTargets(IModLog log, ShelfRegistry registry, IShelfGateway gateway, AimTarget? aim)
    {
        Camera camera = Camera.main;
        if (camera == null)
        {
            Print("No world loaded.");
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

        Print(aim is null
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
                Print(string.Format(CultureInfo.InvariantCulture, "{0}. {1}  {2:0} m {3}  fits {4}",
                    index, row.Shelf.TypeName, row.Distance, row.Bearing, row.Fits));
            }
        }

        if (_indexedTargets.Count > _maxPrintedRows)
        {
            Print($"... {_indexedTargets.Count - _maxPrintedRows} more in the mod log (shelves.index lines).");
        }
    }

    [HideFromIl2Cpp]
    private void StartTransfer(IModLog log, IShelfGateway gateway, AimWatcher aimWatcher, TransferDriver driver, string[] parts,
        string arguments)
    {
        AimTarget? aim = aimWatcher.GetCurrent();
        if (driver.IsRunning)
        {
            log.Write(LogLevel.Warn, "transfer.request", new KeyValuePair<string, object?>[]
            {
                new("outcome", "refused"),
                new("reason", "busy"),
                new("args", arguments),
            });
            Print("A transfer is already running; wait for it or type: transfer cancel");
            return;
        }

        if (parts.Length != 2 || !TryParsePositive(parts[0], out int amount))
        {
            Block(log, gateway, aim, "usage", arguments, _usage);
            return;
        }

        if (aim is null)
        {
            Block(log, gateway, null, "no aimed slot", arguments, "Aim at a shelf slot that holds items, then run the command.");
            return;
        }

        if (!TryParsePositive(parts[1], out int index) || index > _indexedTargets.Count)
        {
            string detail = _indexedTargets.Count == 0 ? "no shelf list" : $"index outside 1..{_indexedTargets.Count}";
            string message = _indexedTargets.Count == 0
                ? "No shelf list yet; run: transfer list"
                : $"Index must be 1 to {_indexedTargets.Count}; run transfer list to refresh.";
            Block(log, gateway, aim, detail, arguments, message);
            return;
        }

        ShelfRef target = _indexedTargets[index - 1].Shelf;
        if (target.Key == aim.Shelf.Key)
        {
            Block(log, gateway, aim, "target is the source", arguments, "That index is the shelf you are aiming at.");
            return;
        }

        // `transfer list` rescans and can map a duplicate key to the other twin; the source must be the looked-at shelf.
        if (!aimWatcher.TrackCurrent())
        {
            Block(log, gateway, aim, "aim lost", arguments, "Could not confirm the aimed shelf; aim at the slot and try again.");
            return;
        }

        TransferRequest request = new(aim.Shelf, aim.Slot, target, aim.Item, amount);
        if (!driver.TryStart(request, report => Print(StopReasonText.Format(report).Text)))
        {
            Block(log, gateway, aim, "driver not ready", arguments, "Transfer could not start, see the mod log.");
            return;
        }

        Print($"Moving up to {amount} of item {aim.Item.Value} to #{index} {target.TypeName}...");
    }

    // Mirrors the runner's own transfer.stop fields, so a refused command reads the same in the log as a refused plan.
    [HideFromIl2Cpp]
    private void Block(IModLog log, IShelfGateway gateway, AimTarget? aim, string detail, string arguments, string message)
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
        Print(message);
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
