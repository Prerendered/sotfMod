// purpose: the transfer overlay (§02): the transfer key on an aimed shelf slot opens a left-anchored IMGUI panel with the
//          source slot (item, count, shelf) and a read-only target list; Esc closes it with nothing moved
// owns:    OverlayUi
// deps:    OverlayModel, ShelfListBuilder, IShelfGateway (reads only), TransferCommand (CaptureAim, ReleaseAim,
//          IsCapturedShelfGone, GetShelfName), LabelsFeature (Store, SaveNow), LabelPruneGuard, InputBlocker,
//          OverlayStyles, ModConfig (TransferKey, PruneMode), IModLog, UnityEngine IMGUI
// caution: I1/I2 - no Confirm path yet: never calls TakeOne/StoreOne or the driver. Esc goes through OverlayModel.Escape;
//          this class renders the model and forwards input. Update and OnGUI are P2 boundaries: a throw logs
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
    private const string _pruneLogEvent = "labels.prune";
    private const string _pruneCandidateLogEvent = "labels.prune_candidate";

    private const int _closeButtonWidth = 56;
    private const int _rowIndent = 16;
    private const int _distanceColumnWidth = 56;
    private const int _bearingColumnWidth = 36;
    private const int _fitColumnWidth = 96;
    private const int _scrollbarWidth = 20;

    private const string _titleText = "Transfer";
    private const string _closeText = "Esc";
    private const string _sendToText = "Send to";
    private const string _hintText = "Esc closes, nothing is moved";
    private const string _cannotHoldText = "can't hold";

    private static readonly IReadOnlyDictionary<string, ShelfLabel> _noLabels = new Dictionary<string, ShelfLabel>();

    private IModLog? _log;
    private ModConfig? _config;
    private TransferCommand? _command;
    private IShelfGateway? _gateway;
    private Func<IReadOnlyList<ShelfRef>>? _listShelves;
    private bool _isHalted;

    private readonly InputBlocker _inputBlocker = new(_inputBlockName, _logEvent);
    private LabelPruneGuard? _pruneGuard;

    // Built once at open: the list cannot change while this card's overlay is open, so no text is formatted per frame.
    private readonly List<(bool IsHeader, bool IsDimmed, string Label, string Distance, string Bearing, string Fit)> _lines = new();

    private OverlayModel? _model;
    private string _itemText = string.Empty;
    private string? _unitText;
    private float _itemWidth;
    private OverlayStyles? _itemWidthStyles;
    private string _shelfText = string.Empty;
    private Vector2 _listScroll;

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
        }
        else
        {
            HandlePanelKey(log, _inputBlocker.ReadPanelKey());
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
        LabelStore? store = LabelsFeature.Store;
        Vector3 player = camera.transform.position;
        ShelfList list = BuildTargetList(gateway, shelves, aim, sourceCount, unit,
            new WorldPosition(player.x, player.y, player.z), store?.Entries ?? _noLabels);
        (string Outcome, int Pruned, int Pending, int Candidates) prune = PruneLabels(log, store, shelves, aim, pruneMode);

        OverlayModel model = new(aim, unit);
        model.OnShelfList(list);
        BuildLines(model);
        _itemText = string.Format(CultureInfo.InvariantCulture, "Item {0} x {1}", aim.Item.Value, aim.Count);
        _unitText = unit > 1 ? string.Format(CultureInfo.InvariantCulture, "(moves in {0}s)", unit) : null;
        _itemWidthStyles = null;
        _shelfText = "From " + command.GetShelfName(aim.Shelf);
        _listScroll = Vector2.zero;
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
    }

    // The same candidate reads as the typed prompt's list (TransferCommand), so both agree on which shelves exist and
    // their fits; labels are passed in here so the rows group by base.
    [HideFromIl2Cpp]
    private static ShelfList BuildTargetList(IShelfGateway gateway, IReadOnlyList<ShelfRef> shelves, AimTarget aim,
        int sourceCount, int unit, WorldPosition player, IReadOnlyDictionary<string, ShelfLabel> labels)
    {
        List<ShelfCandidate> candidates = new(shelves.Count);
        foreach (ShelfRef shelf in shelves)
        {
            // CanHold then GetFreeCapacity back to back share one hook walk in the gateway.
            bool canHold = gateway.CanHold(shelf, aim.Item);
            int free = gateway.GetFreeCapacity(shelf, aim.Item);
            candidates.Add(new ShelfCandidate(shelf, free, canHold));
        }

        return ShelfListBuilder.Build(candidates, aim.Shelf.Key, sourceCount, player, labels, unit);
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
                group.Expanded ? "v" : ">", group.Name, group.Rows.Count);
            _lines.Add((true, false, header, string.Empty, string.Empty, string.Empty));
            if (!group.Expanded)
            {
                continue;
            }

            foreach (ShelfRow row in group.Rows)
            {
                _lines.Add((false, !row.Selectable, row.Label,
                    string.Format(CultureInfo.InvariantCulture, "{0:0} m", row.Distance),
                    row.Bearing,
                    row.Selectable ? string.Format(CultureInfo.InvariantCulture, "fits {0}", row.Fits) : _cannotHoldText));
            }
        }
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

    [HideFromIl2Cpp]
    private void HandlePanelKey(IModLog log, KeyCode key)
    {
        if (key == KeyCode.Escape)
        {
            Escape(log, "esc");
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
            Escape(log, "button");
            return;
        }

        y += styles.TitleLineHeight + gap;
        y = DrawSource(styles, x, y, innerWidth);
        y += gap;
        GUI.DrawTexture(new Rect(x, y, innerWidth, 1f), styles.DividerTexture);
        y += 1f + gap;
        GUI.Label(new Rect(x, y, innerWidth, styles.SmallLineHeight), _sendToText, styles.Small);
        y += styles.SmallLineHeight;

        DrawList(styles, status, new Rect(x, y, innerWidth, listHeight), contentHeight);
        y += listHeight + gap;
        GUI.Label(new Rect(x, y, innerWidth, styles.SmallLineHeight), _hintText, styles.Small);
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
        foreach ((bool isHeader, _, _, _, _, _) in _lines)
        {
            height += styles.Px(isHeader ? OverlayStyles.GroupHeaderHeight : OverlayStyles.RowHeight);
        }

        return height;
    }

    // Read-only until card 21 adds hover, selection and rename; rows follow design.md's fixed columns so numbers align.
    [HideFromIl2Cpp]
    private void DrawList(OverlayStyles styles, string? status, Rect area, float contentHeight)
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

        float indent = styles.Px(_rowIndent);
        float fitWidth = styles.Px(_fitColumnWidth);
        float bearingWidth = styles.Px(_bearingColumnWidth);
        float distanceWidth = styles.Px(_distanceColumnWidth);
        float gap = styles.Px(OverlayStyles.Space2);
        foreach ((bool isHeader, bool isDimmed, string label, string distance, string bearing, string fit) in _lines)
        {
            if (isHeader)
            {
                float headerHeight = styles.Px(OverlayStyles.GroupHeaderHeight);
                GUI.Label(new Rect(0f, y, viewWidth, headerHeight), label, styles.GroupHeader);
                y += headerHeight;
                continue;
            }

            float rowHeight = styles.Px(OverlayStyles.RowHeight);
            float fitX = viewWidth - fitWidth;
            float bearingX = fitX - gap - bearingWidth;
            float distanceX = bearingX - gap - distanceWidth;
            GUI.Label(new Rect(indent, y, Math.Max(0f, distanceX - indent - gap), rowHeight), label,
                isDimmed ? styles.RowDisabled : styles.Body);
            GUI.Label(new Rect(distanceX, y, distanceWidth, rowHeight), distance, styles.SmallRight);
            GUI.Label(new Rect(bearingX, y, bearingWidth, rowHeight), bearing, styles.Small);
            GUI.Label(new Rect(fitX, y, fitWidth, rowHeight), fit, isDimmed ? styles.SmallDisabled : styles.SmallRight);
            GUI.DrawTexture(new Rect(0f, y + rowHeight - 1f, viewWidth, 1f), styles.DividerTexture);
            y += rowHeight;
        }

        GUI.EndScrollView();
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
