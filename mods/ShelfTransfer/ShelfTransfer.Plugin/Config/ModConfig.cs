// purpose: every BepInEx config entry the mod reads, bound once into BepInEx/config/<guid>.cfg (P5)
// owns:    ModConfig
// deps:    BepInEx ConfigFile, UnityEngine.KeyCode (interop), LabelPruneMode
// caution: read entries through .Value each time, never cache, so .cfg edits apply live

using BepInEx.Configuration;

using UnityEngine;

using ShelfTransfer.Core.Labels;

namespace ShelfTransfer.Plugin.Config;

public sealed class ModConfig
{
    private const string _keysSection = "Keys";
    private const string _transferSection = "Transfer";
    private const string _labelsSection = "Labels";
    private const string _debugSection = "Debug";

    private const int _itemsPerFrameDefault = 1;
    private const int _itemsPerFrameMin = 1;
    private const int _itemsPerFrameMax = 5;
    private const int _confirmTicksDefault = 30;
    private const int _confirmTicksMin = 0;
    private const int _confirmTicksMax = 300;
    private const int _faultInjectOff = 0;

    public ModConfig(ConfigFile file)
    {
        // A new entry name, not "Transfer": BepInEx keeps a saved value over a new default, so the old L would stick.
        // Kept as "TransferPrompt" now that it opens the overlay, so a key the User already set stays the transfer key.
        TransferKey = file.Bind(_keysSection, "TransferPrompt", KeyCode.Equals,
            "Opens the transfer overlay while you aim at a shelf slot (pick a target shelf; Esc closes it). "
            + "Default: the =/+ key on the main row. The typed prompt is on TransferPromptAlt.");
        TransferAltKey = file.Bind(_keysSection, "TransferPromptAlt", KeyCode.KeypadPlus,
            "Opens the typed transfer prompt, the fallback to the overlay (list, <count> <index or label>, cancel); "
            + "None turns it off. Default: numpad +.");
        LabelKey = file.Bind(_keysSection, "Label", KeyCode.K,
            "Opens the label editor for the shelf you are looking at.");
        DiscoveryKey = file.Bind(_keysSection, "Discovery", KeyCode.F8,
            "Writes a discovery dump of the aimed object to the mod log. Only works when Debug.DiscoveryEnabled is true.");

        ItemsPerFrame = file.Bind(_transferSection, "ItemsPerFrame", _itemsPerFrameDefault, new ConfigDescription(
            "Take-and-store cycles run per frame during a transfer. A cycle is one game chunk: one take, then one store per item "
            + "(1 item for most, 10 for rifle ammo). Higher is faster; 1 is the safest.",
            new AcceptableValueRange<int>(_itemsPerFrameMin, _itemsPerFrameMax)));
        ConfirmTicks = file.Bind(_transferSection, "ConfirmTicks", _confirmTicksDefault, new ConfigDescription(
            "Per-step wait, in frames, for the host to confirm each take and store on a friend-hosted game before the "
            + "transfer stops. Single-player and hosted games confirm at once; 0 never waits.",
            new AcceptableValueRange<int>(_confirmTicksMin, _confirmTicksMax)));

        // DryRun until card 82 shows the shelf scan finds every shelf: it missed real shelves and pruned their labels.
        PruneMode = file.Bind(_labelsSection, "PruneMode", LabelPruneMode.DryRun,
            "What the transfer overlay does with labels of shelves it could not find on two opens in a row. DryRun only "
            + "logs them (labels.prune_candidate) and changes nothing. Archive moves labels of shelves not found twice "
            + "into <world>.pruned.json; leave on DryRun until card 82.");

        DiscoveryEnabled = file.Bind(_debugSection, "DiscoveryEnabled", false,
            "Enables the Discovery key. Debug aid for finding game storage types; leave off for normal play.");
        // Upper bound is only there so AcceptableValueRange can reject negatives.
        InjectFaultAfterItems = file.Bind(_debugSection, "InjectFaultAfterItems", _faultInjectOff, new ConfigDescription(
            "Test aid: simulate a gateway failure after this many items moved, to check the stop report. 0 means off.",
            new AcceptableValueRange<int>(_faultInjectOff, int.MaxValue)));
    }

    public ConfigEntry<KeyCode> TransferKey { get; }

    public ConfigEntry<KeyCode> TransferAltKey { get; }

    public ConfigEntry<KeyCode> LabelKey { get; }

    public ConfigEntry<KeyCode> DiscoveryKey { get; }

    public ConfigEntry<int> ItemsPerFrame { get; }

    public ConfigEntry<int> ConfirmTicks { get; }

    public ConfigEntry<LabelPruneMode> PruneMode { get; }

    public ConfigEntry<bool> DiscoveryEnabled { get; }

    public ConfigEntry<int> InjectFaultAfterItems { get; }
}
