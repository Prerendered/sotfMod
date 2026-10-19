// purpose: the transfer overlay (§02): the transfer key on an aimed shelf slot opens a left-anchored IMGUI panel with the
//          source slot and the other shelves grouped by base; a row click selects it, a header click collapses it, and
//          each row's rename / group link edits its label inline. Esc closes it with nothing moved
// owns:    OverlayUi
// deps:    OverlayModel, ShelfTargetScan, ShelfListBuilder, IShelfGateway (reads only), TransferCommand (CaptureAim,
//          ReleaseAim, IsCapturedShelfGone, GetShelfName), LabelsFeature (Store, SetLabel, IsValidName, SaveNow),
//          LabelPruneGuard, InputBlocker, OverlayStyles, ModConfig (TransferKey, PruneMode), IModLog, UnityEngine IMGUI
// caution: I1/I2 - no Confirm path yet: never calls TakeOne/StoreOne or the driver. Selection, collapse and Esc go through
//          OverlayModel (I4: it refuses a dimmed row); this class renders the model and forwards input. I5 - labels are
//          written only through LabelsFeature.SetLabel. The list is cached: rebuilt on a 1 m move or a label save, never
//          per frame, and the gateway is read once per open. Update and OnGUI are P2 boundaries: a throw logs
//          error event=ui.overlay, gives input back at once and halts the overlay for the session

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Il2CppInterop.Runtime.Attributes;

using UnityEngine;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Labels;
using ShelfTransfer.Core.Logging;
using ShelfTransfer.Core.Shelves;
using ShelfTransfer.Core.Ui;
using ShelfTransfer.Plugin.Commands;
using ShelfTransfer.Plugin.Config;
using ShelfTransfer.Plugin.Labels;

using LogLevel = ShelfTransfer.Core.Logging.LogLevel;

namespace ShelfTransfer.Plugin.Ui;

public sealed class OverlayUi : MonoBehaviour
{
    private const string _inputBlockName = "ShelfTransfer.OverlayInput";
    private const string _logEvent = "ui.overlay";
    private const string _labelLogEvent = "ui.label";
    private const string _pruneLogEvent = "labels.prune";
    private const string _pruneCandidateLogEvent = "labels.prune_candidate";
    private const string _editControl = "ShelfTransfer.OverlayLabelEdit";

    // Criterion 2 allows 1 m against a tape check, so a smaller move never needs new rows.
    private const float _rebuildDistance = 1f;
    private const float _noteSeconds = 4f;
    private const int _maxLabelChars = 22;
    private const int _groupChipColumns = 3;

    private const int _closeButtonWidth = 56;
    private const int _rowIndent = 16;
    private const int _distanceColumnWidth = 56;
    private const int _bearingColumnWidth = 36;
    private const int _fitColumnWidth = 72;
    private const int _renameLinkWidth = 56;
    private const int _groupLinkWidth = 48;
    private const int _scrollbarWidth = 20;

    private const string _titleText = "Transfer";
    private const string _closeText = "Esc";
    private const string _sendToText = "Send to";
    private const string _hintText = "Click a shelf to select it. Esc closes, nothing is moved";
    private const string _editHintText = "Enter saves the label, Esc cancels the edit";
    private const string _cannotHoldText = "can't hold";
    private const string _renameText = "rename";
    private const string _groupText = "group";
    private const string _saveText = "save";
    private const string _cancelText = "cancel";
    private const string _noGroupText = "None";
    private const string _truncatedSuffix = "...";
    private const string _savedText = "Label saved";
    private const string _saveFailedText = "Could not save labels, see log";
    private const string _saveBlockedText = "Labels are read-only this session, see log";
    private const string _noStoreText = "Labels are not available, see log";
    private const string _invalidCharText = "Names cannot contain control characters.";

    private static readonly string _tooLongText = $"Names stop at {LabelsFeature.MaxNameLength} characters.";
    private static readonly IReadOnlyDictionary<string, ShelfLabel> _noLabels = new Dictionary<string, ShelfLabel>();

    // A list click is recorded while drawing and applied after the list, not captured in a handler closure: a capturing
    // lambda in a draw method allocates on every IMGUI event, clicked or not.
    private enum ListClick
    {
        None,
        ToggleGroup,
        SelectRow,
        RenameRow,
        RegroupRow,
        SaveEdit,
        CancelEdit,
    }

    private IModLog? _log;
    private ModConfig? _config;
    private TransferCommand? _command;
    private IShelfGateway? _gateway;
    private Func<IReadOnlyList<ShelfRef>>? _listShelves;
    private bool _isHalted;

    private readonly InputBlocker _inputBlocker = new(_inputBlockName, _logEvent);
    private LabelPruneGuard? _pruneGuard;

    // Rebuilt from the model on a list change or a group toggle only, so no row text is formatted per frame. Key is the
    // shelf key for a row and the group name for a header.
    private readonly List<(bool IsHeader, string Key, bool IsDimmed, string Label, string Distance, string Bearing,
        string Fit)> _lines = new();

    private OverlayModel? _model;
    private IReadOnlyList<ShelfCandidate> _candidates = Array.Empty<ShelfCandidate>();
    private int _sourceCount;
    private LabelStore? _store;
    private Vector3 _builtAt;
    private string _itemText = string.Empty;
    private string? _unitText;
    private float _itemWidth;
    private OverlayStyles? _itemWidthStyles;
    private string _shelfText = string.Empty;
    private Vector2 _listScroll;

    // Inline label edit (card 16's write path): the edited row's key, which field, and the text typed so far. Chip text
    // is cut once at BeginEdit so the chips draw without formatting.
    private string? _editKey;
    private bool _isEditingGroup;
    private string _editText = string.Empty;
    private IReadOnlyList<(string Name, string Text)> _editGroups = Array.Empty<(string, string)>();
    private bool _shouldFocusEdit;

    private string? _note;
    private bool _isNoteError;
    private float _noteUntil;

    public OverlayUi(IntPtr ptr) : base(ptr) { }

    // Managed parameters cannot cross into Il2Cpp; hidden so ClassInjector does not try to expose it.
    [HideFromIl2Cpp]
    public void Init(IModLog log, ModConfig config, TransferCommand command, IShelfGateway gateway,
        Func<IReadOnlyList<ShelfRef>> listShelves)
    {
        _log = log;
        _config = config;
        _command = command;
        _gateway = gateway;
        _listShelves = listShelves;
    }

    public void Update()
    {
        if (_log is null || _config is null || _command is null || _gateway is null || _listShelves is null || _isHalted)
        {
            return;
        }

        try
        {
            Tick(_log, _config, _command, _gateway, _listShelves);
        }
        catch (Exception ex)
        {
            Halt(_log, ex);
        }
    }

    public void OnGUI()
    {
        if (_log is null || _isHalted || _model is null)
        {
            return;
        }

        try
        {
            Draw(_log);
        }
        catch (Exception ex)
        {
            Halt(_log, ex);
        }
    }

    public void OnDestroy() => _inputBlocker.ReleaseNow();

    [HideFromIl2Cpp]
    private void Tick(IModLog log, ModConfig config, TransferCommand command, IShelfGateway gateway,
        Func<IReadOnlyList<ShelfRef>> listShelves)
    {
        _inputBlocker.Tick(log);
        if (_model is null)
        {
            // Not while any panel holds input: in the label editor the key is a character of the name, and while
            // releasing the cursor is still a panel's, so Block would record it as the state to restore.
            KeyCode key = config.TransferKey.Value;
            if (key != KeyCode.None && !InputBlocker.IsAnyHeld && Input.GetKeyDown(key))
            {
                TryOpen(log, command, gateway, listShelves, key, config.PruneMode.Value);
            }

            return;
        }

        string? lostReason = GetLostReason(command);
        if (lostReason is not null)
        {
            Close(log, lostReason);
            return;
        }

        HandlePanelKey(log, _inputBlocker.ReadPanelKey());
        Camera camera = Camera.main;
        if (_model is OverlayModel model && camera != null
            && (camera.transform.position - _builtAt).sqrMagnitude > _rebuildDistance * _rebuildDistance)
        {
            RebuildList(log, model, camera, "moved");
        }
    }

    [HideFromIl2Cpp]
    private void TryOpen(IModLog log, TransferCommand command, IShelfGateway gateway,
        Func<IReadOnlyList<ShelfRef>> listShelves, KeyCode key, LabelPruneMode pruneMode)
    {
        Camera camera = Camera.main;
        if (camera == null)
        {
            WriteNoOpen(log, "no_world");
            return;
        }

        // A free cursor means a game menu, inventory or chat owns the screen; opening over it would fight its input.
        CursorLockMode cursor = Cursor.lockState;
        if (cursor != CursorLockMode.Locked)
        {
            WriteNoOpen(log, "cursor_free", cursor.ToString());
            return;
        }

        AimTarget? aim = command.CaptureAim();
        if (aim is null)
        {
            command.ReleaseAim();
            WriteNoOpen(log, "no_slot");
            return;
        }

        // Read before the listing: List() rescans and may map a duplicate key to its twin, while CaptureAim has just
        // tracked the looked-at shelf. The gateway gives 0 for an unknown unit and OverlayModel refuses < 1.
        int unit = Math.Max(1, gateway.GetTransferUnit(aim.Shelf, aim.Slot, aim.Item));
        int sourceCount = gateway.GetCount(aim.Shelf, aim.Item);
        IReadOnlyList<ShelfRef> shelves = listShelves();
        // The same scan as the typed prompt's list (card 80): shelves only, each read once per open.
        _candidates = ShelfTargetScan.ReadCandidates(gateway, shelves, aim.Item);
        _sourceCount = sourceCount;
        _store = LabelsFeature.Store;

        OverlayModel model = new(aim, unit);
        ShelfList list = RebuildList(null, model, camera, "open");
        // The prune sees every storage the registry listed, not the filtered targets, so no label looks stale by type.
        (string Outcome, int Pruned, int Pending, int Candidates) prune = PruneLabels(log, _store, shelves, aim, pruneMode);

        _itemText = string.Format(CultureInfo.InvariantCulture, "Item {0} x {1}", aim.Item.Value, aim.Count);
        _unitText = unit > 1 ? string.Format(CultureInfo.InvariantCulture, "(moves in {0}s)", unit) : null;
        _itemWidthStyles = null;
        _shelfText = "From " + command.GetShelfName(aim.Shelf);
        _listScroll = Vector2.zero;
        _note = null;
        _model = model;

        _inputBlocker.Block();
        log.Write(LogLevel.Info, _logEvent, new KeyValuePair<string, object?>[]
        {
            new("state", "open"),
            new("shelf", aim.Shelf.Key),
            new("item", aim.Item.Value),
            new("count", aim.Count),
            new("slot", aim.Slot.Index),
            new("unit", unit),
            new("key", key.ToString()),
            new("targets", list.Groups.Sum(group => group.Rows.Count)),
            new("pruned", prune.Pruned),
            new("prune", prune.Outcome),
            new("prune_pending", prune.Pending),
            new("prune_candidates", prune.Candidates),
        });
        WriteListLine(log, list, "open");
    }

    // Rows from the cached candidates: only the player position and the labels can have changed since the open.
    // A null log skips the list line (the open writes it after its own line).
    [HideFromIl2Cpp]
    private ShelfList RebuildList(IModLog? log, OverlayModel model, Camera camera, string reason)
    {
        Vector3 player = camera.transform.position;
        _builtAt = player;
        ShelfList list = ShelfListBuilder.Build(_candidates, model.Source.Shelf.Key, _sourceCount,
            new WorldPosition(player.x, player.y, player.z), _store?.Entries ?? _noLabels, model.TransferUnit);
        model.OnShelfList(list);
        BuildLines(model);
        if (log is not null)
        {
            WriteListLine(log, list, reason);
        }

        return list;
    }

    [HideFromIl2Cpp]
    private static void WriteListLine(IModLog log, ShelfList list, string reason)
    {
        log.Write(LogLevel.Info, _logEvent, new KeyValuePair<string, object?>[]
        {
            new("state", "list"),
            new("reason", reason),
            new("shelves", list.Groups.Sum(group => group.Rows.Count)),
            new("groups", list.Groups.Count),
        });
    }

    // §07 prune on open. LabelPruneGuard decides (two consecutive scans, never the shared unknown file). DryRun (the
    // default until card 82: the registry missed real shelves) only logs each due key once; Archive moves each entry to
    // <world>.pruned.json before removing it and a failed save puts them back (I5: a prune never loses data).
    [HideFromIl2Cpp]
    private (string Outcome, int Pruned, int Pending, int Candidates) PruneLabels(
        IModLog log, LabelStore? store, IReadOnlyList<ShelfRef> shelves, AimTarget aim, LabelPruneMode mode)
    {
        if (store is null)
        {
            return ("no_store", 0, 0, 0);
        }

        // A .cfg mode change starts the two-open run over rather than carrying dry-run sightings into a real prune.
        if (_pruneGuard is null || _pruneGuard.Mode != mode)
        {
            _pruneGuard = new LabelPruneGuard(mode);
        }

        string world = store.SanitisedWorldId;
        LabelPruneResult result = _pruneGuard.Prune(store, shelves.Select(shelf => shelf.Key).ToList(), aim.Shelf.Key);
        if (result.Outcome == LabelPruneOutcome.DryRun)
        {
            foreach (PrunedLabel entry in result.NewCandidates)
            {
                log.Write(LogLevel.Info, _pruneCandidateLogEvent, new KeyValuePair<string, object?>[]
                {
                    new("world", world),
                    new("shelf", entry.Key),
                    new("label", entry.Label.Label),
                    new("group", entry.Label.Group),
                });
            }

            return (FormatPruneOutcome(result.Outcome), 0, result.Pending, result.Candidates);
        }

        if (result.Outcome == LabelPruneOutcome.ArchiveFailed)
        {
            string outcome = FormatPruneOutcome(result.Outcome);
            log.Write(LogLevel.Warn, _pruneLogEvent, new KeyValuePair<string, object?>[]
            {
                new("world", world),
                new("outcome", outcome),
                new("pending", result.Pending),
                new("detail", result.Detail),
            });
            return (outcome, 0, result.Pending, result.Candidates);
        }

        foreach (PrunedLabel entry in result.Pruned)
        {
            log.Write(LogLevel.Info, _pruneLogEvent, new KeyValuePair<string, object?>[]
            {
                new("world", world),
                new("shelf", entry.Key),
                new("label", entry.Label.Label),
                new("group", entry.Label.Group),
                new("archived", true),
            });
        }

        // SaveNow logs labels.write (JsonLabelFileSystem, or LabelsFeature when blocked).
        LabelSaveResult? saved = LabelPruneGuard.SavePruned(store, result.Pruned, LabelsFeature.SaveNow);
        if (saved is { Status: not LabelSaveStatus.Saved })
        {
            log.Write(LogLevel.Warn, _pruneLogEvent, new KeyValuePair<string, object?>[]
            {
                new("world", world),
                new("outcome", "save_failed"),
                new("status", saved.Status.ToString()),
                new("restored", result.Pruned.Count),
                new("detail", saved.Detail),
            });
            return ("save_failed", 0, result.Pending, result.Candidates);
        }

        return (FormatPruneOutcome(result.Outcome), result.Pruned.Count, result.Pending, result.Candidates);
    }

    [HideFromIl2Cpp]
    private static string FormatPruneOutcome(LabelPruneOutcome outcome) => outcome switch
    {
        LabelPruneOutcome.Checked => "checked",
        LabelPruneOutcome.DryRun => "dry_run",
        LabelPruneOutcome.SkippedUnknownWorld => "skipped_unknown_world",
        LabelPruneOutcome.SkippedNoShelves => "skipped_no_shelves",
        LabelPruneOutcome.ArchiveFailed => "archive_failed",
        _ => outcome.ToString(),
    };

    [HideFromIl2Cpp]
    private void BuildLines(OverlayModel model)
    {
        _lines.Clear();
        foreach (ShelfGroup group in model.Groups)
        {
            string header = string.Format(CultureInfo.InvariantCulture, "{0} {1} ({2})",
                group.Expanded ? "v" : ">", FormatLabel(group.Name), group.Rows.Count);
            _lines.Add((true, group.Name, false, header, string.Empty, string.Empty, string.Empty));
            if (!group.Expanded)
            {
                continue;
            }

            foreach (ShelfRow row in group.Rows)
            {
                _lines.Add((false, row.Shelf.Key, !row.Selectable, FormatLabel(row.Label),
                    string.Format(CultureInfo.InvariantCulture, "{0:0} m", row.Distance),
                    row.Bearing,
                    row.Selectable ? string.Format(CultureInfo.InvariantCulture, "fits {0}", row.Fits) : _cannotHoldText));
            }
        }
    }

    // Cut with "..." so a long name never runs under the number columns; never splits a surrogate pair.
    [HideFromIl2Cpp]
    private static string FormatLabel(string label)
    {
        if (label.Length <= _maxLabelChars)
        {
            return label;
        }

        int cut = _maxLabelChars - _truncatedSuffix.Length;
        if (char.IsHighSurrogate(label[cut - 1]))
        {
            cut--;
        }

        return label[..cut] + _truncatedSuffix;
    }

    [HideFromIl2Cpp]
    private static string? GetLostReason(TransferCommand command)
    {
        // Loading screens and the world teardown have no main camera (the same signal AimWatcher uses).
        if (Camera.main == null)
        {
            return "world_left";
        }

        return command.IsCapturedShelfGone ? "shelf_gone" : null;
    }

    // While a label edit is open Enter saves it and Esc cancels only the edit, as in the label editor (card 16).
    [HideFromIl2Cpp]
    private void HandlePanelKey(IModLog log, KeyCode key)
    {
        if (key == KeyCode.None)
        {
            return;
        }

        if (key == KeyCode.Escape)
        {
            HandleEscape(log, "esc");
        }
        else if (_editKey is not null)
        {
            SubmitEdit(log);
        }
    }

    // The Esc key and the Esc button: an open edit is cancelled first, the next press closes.
    [HideFromIl2Cpp]
    private void HandleEscape(IModLog log, string reason)
    {
        if (_editKey is not null)
        {
            CancelEdit(log, reason);
        }
        else
        {
            Escape(log, reason);
        }
    }

    // The model decides what Esc means; before a Confirm exists it can only close (nothing moved, card 42 criterion 5).
    [HideFromIl2Cpp]
    private void Escape(IModLog log, string reason)
    {
        OverlayAction action = _model?.Escape() ?? OverlayAction.None;
        if (action is OverlayAction.Close or OverlayAction.Dismiss)
        {
            Close(log, reason);
        }
    }

    [HideFromIl2Cpp]
    private void Close(IModLog log, string reason)
    {
        if (_editKey is not null)
        {
            CancelEdit(log, reason);
        }

        log.Write(LogLevel.Info, _logEvent, new KeyValuePair<string, object?>[]
        {
            new("state", "close"),
            new("reason", reason),
        });
        ResetOverlay();
        _inputBlocker.BeginRelease(reason);
    }

    [HideFromIl2Cpp]
    private void ResetOverlay()
    {
        _model = null;
        _lines.Clear();
        _candidates = Array.Empty<ShelfCandidate>();
        _store = null;
        _note = null;
        EndEdit();
        _command?.ReleaseAim();
    }

    [HideFromIl2Cpp]
    private void Halt(IModLog log, Exception ex)
    {
        _isHalted = true;
        log.WriteException(_logEvent, ex);
        if (_model is not null)
        {
            log.Write(LogLevel.Info, _logEvent, new KeyValuePair<string, object?>[]
            {
                new("state", "close"),
                new("reason", "error"),
            });
        }

        ResetOverlay();
        _inputBlocker.ReleaseNow();
    }

    [HideFromIl2Cpp]
    private void Select(IModLog log, OverlayModel model, string key)
    {
        // The model refuses a dimmed row (I4) and keeps the current selection; the refusal is logged as evidence.
        if (!model.SelectRow(key))
        {
            log.Write(LogLevel.Info, _logEvent, new KeyValuePair<string, object?>[]
            {
                new("state", "refuse"),
                new("target", key),
            });
            return;
        }

        log.Write(LogLevel.Info, _logEvent, new KeyValuePair<string, object?>[]
        {
            new("state", "select"),
            new("target", key),
            new("fits", model.SelectedRow?.Fits),
        });
    }

    [HideFromIl2Cpp]
    private void ToggleGroup(IModLog log, OverlayModel model, string groupName)
    {
        model.ToggleGroup(groupName);
        BuildLines(model);
        log.Write(LogLevel.Info, _logEvent, new KeyValuePair<string, object?>[]
        {
            new("state", "toggle"),
            new("group", groupName),
            new("expanded", model.Groups.FirstOrDefault(group => group.Name == groupName)?.Expanded),
        });
    }

    [HideFromIl2Cpp]
    private void BeginEdit(IModLog log, string key, bool isGroup)
    {
        if (_editKey is not null)
        {
            CancelEdit(log, "switch");
        }

        LabelStore? store = _store;
        if (store is null || !ReferenceEquals(LabelsFeature.Store, store))
        {
            log.Write(LogLevel.Info, _labelLogEvent, new KeyValuePair<string, object?>[]
            {
                new("action", "none"),
                new("reason", "no_store"),
                new("shelf", key),
            });
            ShowNote(_noStoreText, true);
            return;
        }

        ShelfLabel? existing = store.Get(key);
        _editKey = key;
        _isEditingGroup = isGroup;
        _editText = (isGroup ? existing?.Group : existing?.Label) ?? string.Empty;
        _editGroups = isGroup
            ? store.ListGroups().Select(group => (group, FormatLabel(group))).ToList()
            : Array.Empty<(string, string)>();
        _shouldFocusEdit = true;
        _note = null;
        log.Write(LogLevel.Info, _labelLogEvent, new KeyValuePair<string, object?>[]
        {
            new("action", "open"),
            new("shelf", key),
            new("label", existing?.Label),
            new("group", existing?.Group),
            new("groups", store.ListGroups().Count),
            new("field", isGroup ? "group" : "label"),
            new("from", "overlay"),
        });
    }

    // The label editor's rule and write path (card 16): IsValidName first, then ui.label set|clear, then SetLabel, which
    // saves and logs labels.write. The field not being edited keeps its stored value.
    [HideFromIl2Cpp]
    private void SubmitEdit(IModLog log)
    {
        string? key = _editKey;
        LabelStore? store = _store;
        if (key is null || _model is not OverlayModel model)
        {
            return;
        }

        Camera camera = Camera.main;
        if (store is null || !ReferenceEquals(LabelsFeature.Store, store) || camera == null)
        {
            CancelEdit(log, "world_left");
            return;
        }

        ShelfLabel? existing = store.Get(key);
        string text = _editText.Trim();
        string? label = _isEditingGroup ? existing?.Label : text;
        string? group = _isEditingGroup ? (text.Length == 0 ? null : text) : existing?.Group;
        if (!LabelsFeature.IsValidName(label) || !LabelsFeature.IsValidName(group))
        {
            bool isTooLong = (label?.Trim().Length ?? 0) > LabelsFeature.MaxNameLength
                || (group?.Trim().Length ?? 0) > LabelsFeature.MaxNameLength;
            ShowNote(isTooLong ? _tooLongText : _invalidCharText, true);
            log.Write(LogLevel.Info, _labelLogEvent, new KeyValuePair<string, object?>[]
            {
                new("action", "reject"),
                new("shelf", key),
                new("reason", isTooLong ? "too_long" : "invalid_char"),
            });
            return;
        }

        // The store keeps a cleared label as "" and no entry as null; either matches an empty field. Nothing to save.
        if (string.Equals(label ?? string.Empty, existing?.Label ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(group, existing?.Group, StringComparison.Ordinal))
        {
            CancelEdit(log, "unchanged");
            return;
        }

        string action = string.IsNullOrWhiteSpace(label) && group is null ? "clear" : "set";
        log.Write(LogLevel.Info, _labelLogEvent, new KeyValuePair<string, object?>[]
        {
            new("action", action),
            new("shelf", key),
            new("label", label),
            new("group", group),
        });

        // Ended first, so a throw from the save still leaves no edit open.
        bool wasGroupEdit = _isEditingGroup;
        EndEdit();
        ShowSaveResult(LabelsFeature.SetLabel(key, label, group));
        RebuildList(log, model, camera, "label");
        if (wasGroupEdit)
        {
            // A regrouped row would vanish into a collapsed group.
            model.ExpandGroupOf(key);
            BuildLines(model);
        }
    }

    [HideFromIl2Cpp]
    private void CancelEdit(IModLog log, string reason)
    {
        log.Write(LogLevel.Info, _labelLogEvent, new KeyValuePair<string, object?>[]
        {
            new("action", "cancel"),
            new("shelf", _editKey),
            new("reason", reason),
        });
        EndEdit();
    }

    [HideFromIl2Cpp]
    private void EndEdit()
    {
        _editKey = null;
        _isEditingGroup = false;
        _editText = string.Empty;
        _editGroups = Array.Empty<(string, string)>();
        _shouldFocusEdit = false;
    }

    [HideFromIl2Cpp]
    private void ShowSaveResult(LabelSaveResult? saved)
    {
        (string text, bool isError) = saved?.Status switch
        {
            LabelSaveStatus.Saved => (_savedText, false),
            LabelSaveStatus.Failed => (_saveFailedText, true),
            LabelSaveStatus.Blocked => (_saveBlockedText, true),
            _ => (_noStoreText, true),
        };
        ShowNote(text, isError);
    }

    [HideFromIl2Cpp]
    private void ShowNote(string text, bool isError)
    {
        _note = text;
        _isNoteError = isError;
        _noteUntil = Time.unscaledTime + _noteSeconds;
    }

    [HideFromIl2Cpp]
    private void Draw(IModLog log)
    {
        // Esc never reaches Input.GetKeyDown while input is blocked (card 54); the IMGUI event does.
        HandlePanelKey(log, _inputBlocker.ReadPanelKeyEvent());
        OverlayModel? model = _model;
        if (model is null)
        {
            return;
        }

        OverlayStyles styles = OverlayStyles.Get();
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), styles.ScrimTexture);

        float padding = styles.Px(OverlayStyles.Space4);
        float gap = styles.Px(OverlayStyles.Space2);
        float width = styles.PanelWidthPx;
        float innerWidth = width - (2f * padding);
        string? status = model.StatusText;

        float headerHeight = styles.TitleLineHeight + gap + styles.TitleLineHeight + styles.BodyLineHeight + gap
            + 1f + gap + styles.SmallLineHeight;
        float footerHeight = gap + styles.SmallLineHeight;
        float contentHeight = (status is null ? 0f : styles.BodyLineHeight) + GetLinesHeight(styles);
        float maxListHeight = Math.Max((Screen.height * OverlayStyles.PanelMaxHeightFraction) - headerHeight - footerHeight
            - (2f * padding), styles.Px(OverlayStyles.RowHeight));
        float listHeight = Math.Min(contentHeight, maxListHeight);
        float height = (2f * padding) + headerHeight + listHeight + footerHeight;
        Rect panel = new(Screen.width * OverlayStyles.PanelLeftFraction, (Screen.height - height) / 2f, width, height);
        DrawPanel(styles, panel);

        float x = panel.x + padding;
        float y = panel.y + padding;
        GUI.Label(new Rect(x, y, innerWidth, styles.TitleLineHeight), _titleText, styles.Title);
        float closeWidth = styles.Px(_closeButtonWidth);
        if (GUI.Button(new Rect(x + innerWidth - closeWidth, y, closeWidth, styles.TitleLineHeight), _closeText, styles.Button))
        {
            HandleEscape(log, "button");
            return;
        }

        y += styles.TitleLineHeight + gap;
        y = DrawSource(styles, x, y, innerWidth);
        y += gap;
        GUI.DrawTexture(new Rect(x, y, innerWidth, 1f), styles.DividerTexture);
        y += 1f + gap;
        GUI.Label(new Rect(x, y, innerWidth, styles.SmallLineHeight), _sendToText, styles.Small);
        y += styles.SmallLineHeight;

        // Applied after the list is drawn: a click that rebuilds _lines must not change it while it is enumerated.
        (ListClick click, string clickKey) = DrawList(model, styles, status, new Rect(x, y, innerWidth, listHeight),
            contentHeight);
        y += listHeight + gap;
        DrawFooter(styles, new Rect(x, y, innerWidth, styles.SmallLineHeight));
        ApplyClick(log, model, click, clickKey);
    }

    [HideFromIl2Cpp]
    private void ApplyClick(IModLog log, OverlayModel model, ListClick click, string key)
    {
        switch (click)
        {
            case ListClick.ToggleGroup:
                ToggleGroup(log, model, key);
                break;
            case ListClick.SelectRow:
                Select(log, model, key);
                break;
            case ListClick.RenameRow:
                BeginEdit(log, key, isGroup: false);
                break;
            case ListClick.RegroupRow:
                BeginEdit(log, key, isGroup: true);
                break;
            case ListClick.SaveEdit:
                SubmitEdit(log);
                break;
            case ListClick.CancelEdit:
                CancelEdit(log, "button");
                break;
        }
    }

    [HideFromIl2Cpp]
    private void DrawFooter(OverlayStyles styles, Rect area)
    {
        if (_note is not null && Time.unscaledTime >= _noteUntil)
        {
            _note = null;
        }

        if (_note is not null)
        {
            GUI.Label(area, _note, _isNoteError ? styles.SmallError : styles.SmallSuccess);
        }
        else
        {
            GUI.Label(area, _editKey is null ? _hintText : _editHintText, styles.Small);
        }
    }

    [HideFromIl2Cpp]
    private static void DrawPanel(OverlayStyles styles, Rect panel)
    {
        // One elevation level: a 1 px Border-coloured edge at 40% around the Surface fill, no shadow (design D7).
        GUI.DrawTexture(panel, styles.PanelEdgeTexture);
        GUI.DrawTexture(new Rect(panel.x + 1f, panel.y + 1f, panel.width - 2f, panel.height - 2f), styles.PanelTexture);
    }

    [HideFromIl2Cpp]
    private float DrawSource(OverlayStyles styles, float x, float y, float width)
    {
        GUI.Label(new Rect(x, y, width, styles.TitleLineHeight), _itemText, styles.Title);
        if (_unitText is not null)
        {
            // Measured once per open (and per style rebuild), not in TryOpen: the styles need GUI.skin, valid only in OnGUI.
            if (!ReferenceEquals(_itemWidthStyles, styles))
            {
                _itemWidth = styles.Title.CalcSize(new GUIContent(_itemText)).x;
                _itemWidthStyles = styles;
            }

            float itemWidth = _itemWidth + styles.Px(OverlayStyles.Space2);
            GUI.Label(new Rect(x + itemWidth, y, Math.Max(0f, width - itemWidth), styles.TitleLineHeight), _unitText,
                styles.MutedBody);
        }

        y += styles.TitleLineHeight;
        GUI.Label(new Rect(x, y, width, styles.BodyLineHeight), _shelfText, styles.MutedBody);
        return y + styles.BodyLineHeight;
    }

    [HideFromIl2Cpp]
    private float GetLinesHeight(OverlayStyles styles)
    {
        float height = 0f;
        foreach ((bool isHeader, string key, _, _, _, _, _) in _lines)
        {
            height += styles.Px(isHeader ? OverlayStyles.GroupHeaderHeight : OverlayStyles.RowHeight);
            if (!isHeader && _isEditingGroup && key == _editKey)
            {
                height += GetChipsHeight(styles);
            }
        }

        return height;
    }

    [HideFromIl2Cpp]
    private float GetChipsHeight(OverlayStyles styles)
    {
        int rows = (_editGroups.Count + _groupChipColumns) / _groupChipColumns;
        float gap = styles.Px(OverlayStyles.Space2);
        return (rows * (styles.Px(OverlayStyles.ChipHeight) + gap)) + gap;
    }

    // Rows follow design.md's fixed columns so numbers align; returns the click to apply once drawing is done.
    [HideFromIl2Cpp]
    private (ListClick Click, string Key) DrawList(OverlayModel model, OverlayStyles styles, string? status, Rect area,
        float contentHeight)
    {
        bool isScrolling = contentHeight > area.height;
        float viewWidth = isScrolling ? area.width - styles.Px(_scrollbarWidth) : area.width;
        _listScroll = GUI.BeginScrollView(area, _listScroll, new Rect(0f, 0f, viewWidth, contentHeight));
        float y = 0f;
        if (status is not null)
        {
            GUI.Label(new Rect(0f, y, viewWidth, styles.BodyLineHeight), status, styles.MutedBody);
            y += styles.BodyLineHeight;
        }

        // Read once per draw, not per row (each read is an interop call), and inside the scroll view so it is in list
        // coordinates.
        Vector2 mouse = Event.current.mousePosition;
        (ListClick Click, string Key) clicked = (ListClick.None, string.Empty);
        string? selectedKey = model.SelectedRow?.Shelf.Key;
        foreach ((bool isHeader, string key, bool isDimmed, string label, string distance, string bearing, string fit)
            in _lines)
        {
            if (isHeader)
            {
                float headerHeight = styles.Px(OverlayStyles.GroupHeaderHeight);
                Rect header = new(0f, y, viewWidth, headerHeight);
                if (header.Contains(mouse))
                {
                    GUI.DrawTexture(header, styles.RowHoverTexture);
                }

                GUI.Label(header, label, styles.GroupHeader);
                if (GUI.Button(header, string.Empty, styles.RowHit) && clicked.Click == ListClick.None)
                {
                    clicked = (ListClick.ToggleGroup, key);
                }

                y += headerHeight;
                continue;
            }

            Rect row = new(0f, y, viewWidth, styles.Px(OverlayStyles.RowHeight));
            ListClick rowClick = key == _editKey
                ? DrawEditRow(styles, row)
                : DrawRow(styles, row, mouse, key == selectedKey, isDimmed, label, distance, bearing, fit);
            if (rowClick != ListClick.None && clicked.Click == ListClick.None)
            {
                clicked = (rowClick, key);
            }

            y += row.height;
            if (_isEditingGroup && key == _editKey)
            {
                y = DrawGroupChips(styles, viewWidth, y);
            }
        }

        GUI.EndScrollView();
        return clicked;
    }

    [HideFromIl2Cpp]
    private static ListClick DrawRow(OverlayStyles styles, Rect row, Vector2 mouse, bool isSelected, bool isDimmed,
        string label, string distance, string bearing, string fit)
    {
        // A dimmed row gets no hover fill (design: disabled) but still forwards its click; the model refuses it.
        if (isSelected)
        {
            GUI.DrawTexture(row, styles.RowSelectedTexture);
            float barWidth = styles.Px(OverlayStyles.SelectedBarWidth);
            GUI.DrawTexture(new Rect(row.x, row.y, barWidth, row.height), styles.AccentTexture);
        }
        else if (!isDimmed && row.Contains(mouse))
        {
            GUI.DrawTexture(row, styles.RowHoverTexture);
        }

        (float labelX, float distanceX, float bearingX, float fitX, float renameX, float groupX) =
            GetColumns(styles, row.width);
        float gap = styles.Px(OverlayStyles.Space2);
        GUIStyle labelStyle = isSelected ? styles.BodyOnAccent : isDimmed ? styles.RowDisabled : styles.Body;
        GUIStyle numberStyle = isSelected ? styles.SmallOnAccentRight : styles.SmallRight;
        GUI.Label(new Rect(labelX, row.y, Math.Max(0f, distanceX - labelX - gap), row.height), label, labelStyle);
        GUI.Label(new Rect(distanceX, row.y, styles.Px(_distanceColumnWidth), row.height), distance, numberStyle);
        GUI.Label(new Rect(bearingX, row.y, styles.Px(_bearingColumnWidth), row.height), bearing,
            isSelected ? styles.SmallOnAccent : styles.Small);
        GUI.Label(new Rect(fitX, row.y, styles.Px(_fitColumnWidth), row.height), fit,
            isDimmed ? styles.SmallDisabled : numberStyle);
        GUI.DrawTexture(new Rect(row.x, row.yMax - 1f, row.width, 1f), styles.DividerTexture);

        GUIStyle linkStyle = isSelected ? styles.LinkOnAccent : styles.Link;
        if (GUI.Button(new Rect(renameX, row.y, styles.Px(_renameLinkWidth), row.height), _renameText, linkStyle))
        {
            return ListClick.RenameRow;
        }

        if (GUI.Button(new Rect(groupX, row.y, styles.Px(_groupLinkWidth), row.height), _groupText, linkStyle))
        {
            return ListClick.RegroupRow;
        }

        // The links sit outside this target, so a link click never also selects the row.
        if (GUI.Button(new Rect(row.x, row.y, renameX - gap - row.x, row.height), string.Empty, styles.RowHit))
        {
            return ListClick.SelectRow;
        }

        return ListClick.None;
    }

    [HideFromIl2Cpp]
    private ListClick DrawEditRow(OverlayStyles styles, Rect row)
    {
        (float labelX, _, _, _, float renameX, float groupX) = GetColumns(styles, row.width);
        float inset = styles.Px(OverlayStyles.Space1);
        float gap = styles.Px(OverlayStyles.Space2);
        GUI.SetNextControlName(_editControl);
        _editText = GUI.TextField(new Rect(labelX, row.y + inset, renameX - gap - labelX, row.height - (2f * inset)),
            _editText, LabelsFeature.MaxNameLength, styles.TextField);
        if (_shouldFocusEdit)
        {
            GUI.FocusControl(_editControl);
            _shouldFocusEdit = false;
        }

        if (GUI.Button(new Rect(renameX, row.y, styles.Px(_renameLinkWidth), row.height), _saveText, styles.Link))
        {
            return ListClick.SaveEdit;
        }

        if (GUI.Button(new Rect(groupX, row.y, styles.Px(_groupLinkWidth), row.height), _cancelText, styles.Link))
        {
            return ListClick.CancelEdit;
        }

        return ListClick.None;
    }

    // "None" first, then each existing group, as in the label editor; a click fills the field and Enter saves it.
    [HideFromIl2Cpp]
    private float DrawGroupChips(OverlayStyles styles, float width, float y)
    {
        float indent = styles.Px(_rowIndent);
        float gap = styles.Px(OverlayStyles.Space2);
        float chipHeight = styles.Px(OverlayStyles.ChipHeight);
        float chipWidth = (width - indent - ((_groupChipColumns - 1) * gap)) / _groupChipColumns;
        ReadOnlySpan<char> typed = _editText.AsSpan().Trim();
        y += gap;
        for (int index = 0; index <= _editGroups.Count; index++)
        {
            (string group, string text) = index == 0 ? (string.Empty, _noGroupText) : _editGroups[index - 1];
            Rect chip = new(indent + ((index % _groupChipColumns) * (chipWidth + gap)),
                y + ((index / _groupChipColumns) * (chipHeight + gap)), chipWidth, chipHeight);
            bool isPicked = typed.Equals(group.AsSpan(), StringComparison.Ordinal);
            GUIStyle chipStyle = isPicked ? styles.ChipSelected : styles.Button;
            if (GUI.Button(chip, text, chipStyle))
            {
                _editText = group;
                _shouldFocusEdit = true;
            }
        }

        return y - gap + GetChipsHeight(styles);
    }

    [HideFromIl2Cpp]
    private static (float Label, float Distance, float Bearing, float Fit, float Rename, float Group) GetColumns(
        OverlayStyles styles, float width)
    {
        float gap = styles.Px(OverlayStyles.Space2);
        float groupX = width - styles.Px(_groupLinkWidth);
        float renameX = groupX - styles.Px(OverlayStyles.Space1) - styles.Px(_renameLinkWidth);
        float fitX = renameX - gap - styles.Px(_fitColumnWidth);
        float bearingX = fitX - gap - styles.Px(_bearingColumnWidth);
        float distanceX = bearingX - gap - styles.Px(_distanceColumnWidth);
        return (styles.Px(_rowIndent), distanceX, bearingX, fitX, renameX, groupX);
    }

    [HideFromIl2Cpp]
    private static void WriteNoOpen(IModLog log, string reason, string? cursor = null)
    {
        List<KeyValuePair<string, object?>> line = new()
        {
            new("state", "none"),
            new("reason", reason),
        };
        if (cursor is not null)
        {
            line.Add(new("cursor", cursor));
        }

        log.Write(LogLevel.Info, _logEvent, line);
    }
}
