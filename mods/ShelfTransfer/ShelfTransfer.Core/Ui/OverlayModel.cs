// purpose: the overlay's behaviour (states, selection, amount, confirm, cancel, result) as a pure model the IMGUI card renders
// owns:    OverlayModel, OverlayState, OverlayAction
// deps:    AimTarget, ShelfList, ShelfGroup, ShelfRow, AmountClamp, TransferRequest, TransferReport, StopReasonText
// caution: I4 - Confirm only ever emits AmountClamp's result for the selected row, and only when it is > 0.
//          D7 - Esc while Running yields Cancel once; the Plugin forwards it to TransferRunner.RequestCancel.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Shelves;
using ShelfTransfer.Core.Transfer;

namespace ShelfTransfer.Core.Ui;

public enum OverlayState
{
    Loading,
    Ready,
    Empty,
    Error,
    Running,
    Result,
    Closed,
}

/// <summary>What the renderer must do after <see cref="OverlayModel.Escape"/>.</summary>
public enum OverlayAction
{
    None,
    /// <summary>Overlay closed before any transfer; nothing moved.</summary>
    Close,
    /// <summary>Forward to TransferRunner.RequestCancel; the overlay stays open until the report arrives.</summary>
    Cancel,
    /// <summary>Result panel dismissed; overlay closed.</summary>
    Dismiss,
}

public sealed class OverlayModel
{
    public const string LoadingText = "Scanning shelves...";
    public const string EmptyText = "No other shelves found";
    public const string ErrorText = "Could not list shelves, see log";
    public const string NoTargetText = "No shelf can take this item";
    public const string RunningText = "Moving items...";
    public const string CancellingText = "Stopping after the current item...";
    public const string SelectTargetReason = "Select a target shelf";
    public const string AmountEmptyReason = "Enter an amount";
    public const string AmountNotNumberReason = "Amount must be a whole number";
    public const string AmountTooSmallReason = "Amount must be at least 1";
    public const string NothingToMoveReason = "Nothing to move";
    public const string TargetFullReason = "Nothing fits on that shelf";
    public const string RunningReason = "A transfer is running";
    public const string FinishedReason = "The transfer has finished";
    public const string ClosedReason = "The overlay is closed";

    private static readonly IReadOnlyList<ShelfGroup> _noGroups = Array.Empty<ShelfGroup>();

    private readonly Dictionary<string, bool> _expandedByGroup = new(StringComparer.Ordinal);
    private IReadOnlyList<ShelfGroup> _listedGroups = _noGroups;
    private bool _hasSelectableRow;

    // AmountClamp.All until the player steps or types, so a refreshed list re-derives "All" from the new Fits.
    private int _requestedAmount = AmountClamp.All;

    // Non-null while the player's text is not a usable amount; the field keeps showing it so typing is not fought.
    private string? _invalidAmountText;

    // Null exactly when Amount is confirmable.
    private string? _amountReason;

    public OverlayModel(AimTarget source)
    {
        Source = source;
    }

    public OverlayState State { get; private set; } = OverlayState.Loading;

    public AimTarget Source { get; }

    /// <summary>Groups as listed, with Expanded reflecting the player's toggles. Rebuilt on change only, safe to read per frame.</summary>
    public IReadOnlyList<ShelfGroup> Groups { get; private set; } = _noGroups;

    public ShelfRow? SelectedRow { get; private set; }

    /// <summary>The amount Confirm would request; 0 when nothing valid is entered or no row is selected.</summary>
    public int Amount { get; private set; }

    /// <summary>What the amount field shows: the clamped amount, or the player's text while it is invalid.</summary>
    public string AmountDisplay { get; private set; } = string.Empty;

    public bool IsCancelRequested { get; private set; }

    public bool ConfirmEnabled => ConfirmDisabledReason is null;

    /// <summary>Null exactly when Confirm is enabled.</summary>
    public string? ConfirmDisabledReason => State switch
    {
        OverlayState.Loading => LoadingText,
        OverlayState.Empty => EmptyText,
        OverlayState.Error => ErrorText,
        OverlayState.Running => RunningReason,
        OverlayState.Result => FinishedReason,
        OverlayState.Closed => ClosedReason,
        _ => GetReadyDisabledReason(),
    };

    /// <summary>A panel message in place of, or above, the list; null when the list speaks for itself.</summary>
    public string? StatusText => State switch
    {
        OverlayState.Loading => LoadingText,
        OverlayState.Empty => EmptyText,
        OverlayState.Error => ErrorText,
        OverlayState.Ready when !_hasSelectableRow => NoTargetText,
        OverlayState.Running => IsCancelRequested ? CancellingText : RunningText,
        _ => null,
    };

    /// <summary>Set when State is Result.</summary>
    public string? ResultText { get; private set; }

    public bool AutoClose { get; private set; }

    private bool CanBrowse => State is OverlayState.Loading or OverlayState.Ready or OverlayState.Empty or OverlayState.Error;

    /// <summary>First listing or a refresh; keeps toggles, and the selection while its row is still selectable.</summary>
    public void OnShelfList(ShelfList list)
    {
        if (!CanBrowse)
        {
            return;
        }

        if (list.IsEmpty)
        {
            State = OverlayState.Empty;
            ClearList();
            return;
        }

        State = OverlayState.Ready;
        _listedGroups = list.Groups;
        _hasSelectableRow = _listedGroups.Any(group => group.Rows.Any(row => row.Selectable));
        foreach (string staleName in _expandedByGroup.Keys.Where(name => !_listedGroups.Any(group => group.Name == name)).ToList())
        {
            _expandedByGroup.Remove(staleName);
        }

        RebuildGroups();
        ShelfRow? kept = SelectedRow is null ? null : FindRow(SelectedRow.Shelf.Key);
        if (kept is { Selectable: true })
        {
            SelectedRow = kept;
            UpdateAmount();
        }
        else
        {
            ClearSelection();
        }
    }

    public void OnListFailed()
    {
        if (!CanBrowse)
        {
            return;
        }

        State = OverlayState.Error;
        ClearList();
    }

    public void ToggleGroup(string groupName)
    {
        if (State != OverlayState.Ready)
        {
            return;
        }

        ShelfGroup? group = Groups.FirstOrDefault(candidate => candidate.Name == groupName);
        if (group is null)
        {
            return;
        }

        _expandedByGroup[groupName] = !group.Expanded;
        RebuildGroups();
    }

    /// <summary>Refuses unknown and unselectable rows (I4); a refusal leaves the current selection as it was.</summary>
    public bool SelectRow(string shelfKey)
    {
        if (State != OverlayState.Ready)
        {
            return false;
        }

        ShelfRow? row = FindRow(shelfKey);
        if (row is not { Selectable: true })
        {
            return false;
        }

        SelectedRow = row;
        _requestedAmount = AmountClamp.All;
        _invalidAmountText = null;
        UpdateAmount();
        return true;
    }

    public void SetAmountText(string? text)
    {
        if (State != OverlayState.Ready || SelectedRow is null)
        {
            return;
        }

        string raw = text ?? string.Empty;
        string trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            SetInvalidAmount(raw, AmountEmptyReason);
            return;
        }

        if (!int.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int typed))
        {
            // A run of digits too long for int is still a number; it clamps like "All".
            if (!trimmed.All(digit => digit is >= '0' and <= '9'))
            {
                SetInvalidAmount(raw, AmountNotNumberReason);
                return;
            }

            typed = AmountClamp.All;
        }

        ApplyRequestedAmount(typed, raw);
    }

    /// <summary>Stepper; floors at 1 so it never produces an invalid amount, and clamps to the row like typing does.</summary>
    public void StepAmount(int delta)
    {
        if (State != OverlayState.Ready || SelectedRow is null)
        {
            return;
        }

        int requested = (int)Math.Clamp((long)Amount + delta, 1, int.MaxValue);
        ApplyRequestedAmount(requested, null);
    }

    /// <summary>Returns the one request for this overlay and moves to Running; null when Confirm is disabled or already used.</summary>
    public TransferRequest? Confirm()
    {
        // Amount is re-checked here so I4 does not rest on the reason logic alone.
        if (!ConfirmEnabled || SelectedRow is not ShelfRow row || Amount <= 0)
        {
            return null;
        }

        State = OverlayState.Running;
        return new TransferRequest(Source.Shelf, Source.Slot, row.Shelf, Source.Item, Amount);
    }

    public OverlayAction Escape()
    {
        switch (State)
        {
            case OverlayState.Running:
                if (IsCancelRequested)
                {
                    return OverlayAction.None;
                }

                IsCancelRequested = true;
                return OverlayAction.Cancel;
            case OverlayState.Result:
                State = OverlayState.Closed;
                return OverlayAction.Dismiss;
            case OverlayState.Closed:
                return OverlayAction.None;
            default:
                State = OverlayState.Closed;
                return OverlayAction.Close;
        }
    }

    /// <summary>A click on the result panel, or the renderer's auto-close timer; only acts in Result.</summary>
    public bool Dismiss()
    {
        if (State != OverlayState.Result)
        {
            return false;
        }

        State = OverlayState.Closed;
        return true;
    }

    public void OnTransferFinished(TransferReport report)
    {
        if (State != OverlayState.Running)
        {
            return;
        }

        ResultLine line = StopReasonText.Format(report);
        ResultText = line.Text;
        AutoClose = line.AutoClose;
        State = OverlayState.Result;
    }

    private string? GetReadyDisabledReason()
    {
        if (SelectedRow is null)
        {
            return _hasSelectableRow ? SelectTargetReason : NoTargetText;
        }

        return _amountReason;
    }

    private void ApplyRequestedAmount(int requested, string? typedText)
    {
        ClampResult clamp = ClampAgainstSelectedRow(requested);
        if (clamp.Reason == ClampReason.InvalidAmount)
        {
            SetInvalidAmount(typedText ?? requested.ToString(CultureInfo.InvariantCulture), AmountTooSmallReason);
            return;
        }

        // Stores what the field shows, so a refresh never grows an amount the player already saw clamped.
        _requestedAmount = clamp.Reason == ClampReason.Ok ? clamp.Amount : requested;
        _invalidAmountText = null;
        UpdateAmount();
    }

    private void UpdateAmount()
    {
        if (_invalidAmountText is not null)
        {
            return;
        }

        ClampResult clamp = ClampAgainstSelectedRow(_requestedAmount);
        Amount = clamp.Reason == ClampReason.Ok ? clamp.Amount : 0;
        AmountDisplay = Amount.ToString(CultureInfo.InvariantCulture);
        _amountReason = clamp.Reason switch
        {
            ClampReason.Ok => null,
            ClampReason.NothingToMove => NothingToMoveReason,
            ClampReason.TargetFull => TargetFullReason,
            _ => AmountTooSmallReason,
        };
    }

    // Fits is already AmountClamp's "All" for this row, so it stands in for the target's free capacity.
    private ClampResult ClampAgainstSelectedRow(int requested) =>
        AmountClamp.Clamp(requested, Source.Count, SelectedRow?.Fits ?? 0);

    private void SetInvalidAmount(string text, string reason)
    {
        _invalidAmountText = text;
        _amountReason = reason;
        Amount = 0;
        AmountDisplay = text;
    }

    private void ClearSelection()
    {
        SelectedRow = null;
        _requestedAmount = AmountClamp.All;
        _invalidAmountText = null;
        _amountReason = null;
        Amount = 0;
        AmountDisplay = string.Empty;
    }

    private void ClearList()
    {
        _listedGroups = _noGroups;
        _hasSelectableRow = false;
        _expandedByGroup.Clear();
        Groups = _noGroups;
        ClearSelection();
    }

    private void RebuildGroups()
    {
        Groups = _listedGroups
            .Select(group => _expandedByGroup.TryGetValue(group.Name, out bool expanded) ? group with { Expanded = expanded } : group)
            .ToList();
    }

    private ShelfRow? FindRow(string shelfKey) =>
        _listedGroups.SelectMany(group => group.Rows).FirstOrDefault(row => row.Shelf.Key == shelfKey);
}
