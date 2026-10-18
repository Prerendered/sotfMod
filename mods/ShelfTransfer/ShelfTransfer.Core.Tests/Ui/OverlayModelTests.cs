// purpose: pins the overlay model: state machine, selection, amount clamp, confirm, escape, refresh, I4 and Core purity
// owns:    OverlayModelTests
// deps:    OverlayModel, OverlayState, OverlayAction, AimTarget, ShelfList, ShelfGroup, ShelfRow, TransferReport

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Shelves;
using ShelfTransfer.Core.Transfer;
using ShelfTransfer.Core.Ui;

using Xunit;

namespace ShelfTransfer.Core.Tests.Ui;

public sealed class OverlayModelTests
{
    private const int _sourceCount = 10;

    private static readonly ShelfRef _sourceShelf = new("src", "StorageShelf", new WorldPosition(0f, 0f, 0f));
    private static readonly SlotRef _slot = new(1, 2);
    private static readonly ItemId _item = new(42);

    private static AimTarget Source(int count = _sourceCount) => new(_sourceShelf, _slot, _item, count);

    private static ShelfRow Row(string key, int fits, bool? selectable = null) =>
        new(new ShelfRef(key, "StorageShelf", new WorldPosition(1f, 0f, 1f)), "Shelf " + key, 1.5f, "N", fits, selectable ?? fits > 0);

    private static ShelfGroup Group(string name, bool expanded, params ShelfRow[] rows) => new(name, expanded, rows);

    private static ShelfList List(params ShelfGroup[] groups) => new(groups, Array.Empty<string>());

    // Near (expanded): a fits 4, b fits 8, c fits 0 (unselectable). Far (collapsed): d fits 5.
    private static ShelfList DefaultList() =>
        List(
            Group("Near", true, Row("a", 4), Row("b", 8), Row("c", 0)),
            Group("Far", false, Row("d", 5)));

    private static ShelfList EmptyList() => List();

    private static TransferReport Finished(StopReason reason = StopReason.Completed, int moved = 6, int remaining = 0, int stranded = 0) =>
        new(reason, moved + remaining, moved, remaining, stranded, null);

    private static OverlayModel ReadyModel(int count = _sourceCount, ShelfList? list = null)
    {
        var model = new OverlayModel(Source(count));
        model.OnShelfList(list ?? DefaultList());
        return model;
    }

    private static OverlayModel SelectedModel(string key = "a", int count = _sourceCount, ShelfList? list = null)
    {
        OverlayModel model = ReadyModel(count, list);
        Assert.True(model.SelectRow(key));
        return model;
    }

    private static void AssertReasonConsistent(OverlayModel model)
    {
        Assert.Equal(model.ConfirmDisabledReason is null, model.ConfirmEnabled);
        if (model.ConfirmEnabled)
        {
            Assert.Equal(OverlayState.Ready, model.State);
            Assert.NotNull(model.SelectedRow);
            Assert.True(model.Amount > 0);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(model.ConfirmDisabledReason));
        }
    }

    private static void WithCulture(string name, Action action)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(name);
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // ---- criterion 1: states

    [Fact]
    public void New_StartsLoading_WithLoadingText_AndConfirmDisabled()
    {
        var model = new OverlayModel(Source());

        Assert.Equal(OverlayState.Loading, model.State);
        Assert.Equal("Scanning shelves...", model.StatusText);
        Assert.Equal(OverlayModel.LoadingText, model.StatusText);
        Assert.False(model.ConfirmEnabled);
        Assert.NotNull(model.ConfirmDisabledReason);
        Assert.Empty(model.Groups);
        Assert.Null(model.SelectedRow);
        Assert.Equal(0, model.Amount);
        Assert.False(model.IsCancelRequested);
        Assert.Null(model.ResultText);
        Assert.Same(_sourceShelf, model.Source.Shelf);
    }

    [Fact]
    public void OnShelfList_Empty_GoesEmpty_WithExactText_AndConfirmDisabled()
    {
        var model = new OverlayModel(Source());

        model.OnShelfList(EmptyList());

        Assert.Equal(OverlayState.Empty, model.State);
        Assert.Equal("No other shelves found", model.StatusText);
        Assert.Equal(OverlayModel.EmptyText, model.StatusText);
        Assert.False(model.ConfirmEnabled);
        Assert.Null(model.Confirm());
    }

    [Fact]
    public void OnListFailed_GoesError_WithExactText_AndConfirmDisabled()
    {
        var model = new OverlayModel(Source());

        model.OnListFailed();

        Assert.Equal(OverlayState.Error, model.State);
        Assert.Equal("Could not list shelves, see log", model.StatusText);
        Assert.Equal(OverlayModel.ErrorText, model.StatusText);
        Assert.False(model.ConfirmEnabled);
        Assert.Null(model.Confirm());
    }

    [Fact]
    public void OnShelfList_NonEmpty_GoesReady()
    {
        OverlayModel model = ReadyModel();

        Assert.Equal(OverlayState.Ready, model.State);
        Assert.Equal(2, model.Groups.Count);
        Assert.False(model.ConfirmEnabled);
    }

    [Fact]
    public void Confirm_InLoading_ReturnsNull_AndStaysLoading()
    {
        var model = new OverlayModel(Source());

        Assert.Null(model.Confirm());
        Assert.Equal(OverlayState.Loading, model.State);
    }

    [Fact]
    public void FullHappyPath_Loading_Ready_Running_Result_Closed()
    {
        var model = new OverlayModel(Source());
        Assert.Equal(OverlayState.Loading, model.State);

        model.OnShelfList(DefaultList());
        Assert.Equal(OverlayState.Ready, model.State);

        Assert.True(model.SelectRow("a"));
        Assert.NotNull(model.Confirm());
        Assert.Equal(OverlayState.Running, model.State);

        model.OnTransferFinished(Finished());
        Assert.Equal(OverlayState.Result, model.State);

        Assert.True(model.Dismiss());
        Assert.Equal(OverlayState.Closed, model.State);
    }

    [Fact]
    public void ErrorCanRecoverToReady_AndEmptyCanRecoverToReady()
    {
        var fromError = new OverlayModel(Source());
        fromError.OnListFailed();
        fromError.OnShelfList(DefaultList());
        Assert.Equal(OverlayState.Ready, fromError.State);

        var fromEmpty = new OverlayModel(Source());
        fromEmpty.OnShelfList(EmptyList());
        fromEmpty.OnShelfList(DefaultList());
        Assert.Equal(OverlayState.Ready, fromEmpty.State);
    }

    [Fact]
    public void Ready_NoSelectableRow_StatusSaysNoShelfCanTake_AndConfirmDisabled()
    {
        OverlayModel model = ReadyModel(list: List(Group("Near", true, Row("c", 0))));

        Assert.Equal("No shelf can take this item", model.StatusText);
        Assert.Equal(OverlayModel.NoTargetText, model.ConfirmDisabledReason);
        Assert.False(model.ConfirmEnabled);
    }

    // ---- criterion 1: full transition table including ignored inputs

    private static readonly string[] _inputs =
    {
        "list", "emptyList", "failed", "toggle", "select", "amount", "step", "confirm", "escape", "dismiss", "finished",
    };

    private static OverlayModel MakeIn(OverlayState state)
    {
        var model = new OverlayModel(Source());
        switch (state)
        {
            case OverlayState.Loading:
                break;
            case OverlayState.Ready:
                model.OnShelfList(DefaultList());
                Assert.True(model.SelectRow("a"));
                break;
            case OverlayState.Empty:
                model.OnShelfList(EmptyList());
                break;
            case OverlayState.Error:
                model.OnListFailed();
                break;
            case OverlayState.Running:
                model.OnShelfList(DefaultList());
                Assert.True(model.SelectRow("a"));
                Assert.NotNull(model.Confirm());
                break;
            case OverlayState.Result:
                model.OnShelfList(DefaultList());
                Assert.True(model.SelectRow("a"));
                Assert.NotNull(model.Confirm());
                model.OnTransferFinished(Finished());
                break;
            case OverlayState.Closed:
                model.Escape();
                break;
        }

        Assert.Equal(state, model.State);
        return model;
    }

    private static void Apply(OverlayModel model, string input)
    {
        switch (input)
        {
            case "list": model.OnShelfList(DefaultList()); break;
            case "emptyList": model.OnShelfList(EmptyList()); break;
            case "failed": model.OnListFailed(); break;
            case "toggle": model.ToggleGroup("Near"); break;
            case "select": model.SelectRow("a"); break;
            case "amount": model.SetAmountText("1"); break;
            case "step": model.StepAmount(1); break;
            case "confirm": model.Confirm(); break;
            case "escape": model.Escape(); break;
            case "dismiss": model.Dismiss(); break;
            case "finished": model.OnTransferFinished(Finished()); break;
            default: throw new ArgumentOutOfRangeException(nameof(input), input, null);
        }
    }

    private static OverlayState Expected(OverlayState state, string input) => state switch
    {
        OverlayState.Loading or OverlayState.Empty or OverlayState.Error => input switch
        {
            "list" => OverlayState.Ready,
            "emptyList" => OverlayState.Empty,
            "failed" => OverlayState.Error,
            "escape" => OverlayState.Closed,
            _ => state,
        },
        OverlayState.Ready => input switch
        {
            "list" => OverlayState.Ready,
            "emptyList" => OverlayState.Empty,
            "failed" => OverlayState.Error,
            "confirm" => OverlayState.Running,
            "escape" => OverlayState.Closed,
            _ => OverlayState.Ready,
        },
        OverlayState.Running => input == "finished" ? OverlayState.Result : OverlayState.Running,
        OverlayState.Result => input is "escape" or "dismiss" ? OverlayState.Closed : OverlayState.Result,
        _ => OverlayState.Closed,
    };

    public static IEnumerable<object[]> TransitionCases() =>
        from state in Enum.GetValues<OverlayState>()
        from input in _inputs
        select new object[] { state, input };

    [Theory]
    [MemberData(nameof(TransitionCases))]
    public void TransitionTable_EveryStateAndInput(OverlayState state, string input)
    {
        OverlayModel model = MakeIn(state);

        Apply(model, input);

        Assert.Equal(Expected(state, input), model.State);
        AssertReasonConsistent(model);
    }

    [Theory]
    [InlineData(OverlayState.Running)]
    [InlineData(OverlayState.Result)]
    [InlineData(OverlayState.Closed)]
    public void IgnoredStates_DoNotChangeListSelectionOrAmount(OverlayState state)
    {
        OverlayModel model = MakeIn(state);
        string? selectedKey = model.SelectedRow?.Shelf.Key;
        int amount = model.Amount;
        IReadOnlyList<ShelfGroup> groups = model.Groups;

        model.OnShelfList(List(Group("Other", true, Row("z", 9))));
        model.OnListFailed();
        model.ToggleGroup("Near");
        Assert.False(model.SelectRow("b"));
        model.SetAmountText("2");
        model.StepAmount(3);

        Assert.Equal(state, model.State);
        Assert.Equal(selectedKey, model.SelectedRow?.Shelf.Key);
        Assert.Equal(amount, model.Amount);
        Assert.Equal(groups.Select(g => g.Name), model.Groups.Select(g => g.Name));
        Assert.Equal(groups.Select(g => g.Expanded), model.Groups.Select(g => g.Expanded));
    }

    [Fact]
    public void TransferFinished_OutsideRunning_IsIgnored_ResultTextStaysNull()
    {
        foreach (OverlayState state in new[] { OverlayState.Loading, OverlayState.Ready, OverlayState.Empty, OverlayState.Error, OverlayState.Closed })
        {
            OverlayModel model = MakeIn(state);

            model.OnTransferFinished(Finished());

            Assert.Equal(state, model.State);
            Assert.Null(model.ResultText);
        }
    }

    [Fact]
    public void OnTransferFinished_FirstReportWins_SecondIgnored()
    {
        OverlayModel model = MakeIn(OverlayState.Running);

        model.OnTransferFinished(Finished(StopReason.Completed, moved: 6, remaining: 0));
        model.OnTransferFinished(Finished(StopReason.Cancelled, moved: 1, remaining: 5));

        Assert.Equal("Moved 6, remaining 0", model.ResultText);
        Assert.True(model.AutoClose);
    }

    // ---- criterion 2: selection and groups

    [Fact]
    public void SelectRow_Unselectable_IsRefused_SelectionUnchanged()
    {
        OverlayModel model = SelectedModel("a");

        Assert.False(model.SelectRow("c"));

        Assert.Equal("a", model.SelectedRow?.Shelf.Key);
        Assert.Equal(4, model.Amount);
    }

    [Fact]
    public void SelectRow_Unselectable_WithNothingSelected_StaysNull()
    {
        OverlayModel model = ReadyModel();

        Assert.False(model.SelectRow("c"));

        Assert.Null(model.SelectedRow);
        Assert.False(model.ConfirmEnabled);
    }

    [Fact]
    public void SelectRow_UnknownKey_IsRefused_SelectionUnchanged()
    {
        OverlayModel model = SelectedModel("a");

        Assert.False(model.SelectRow("nope"));
        Assert.False(model.SelectRow(string.Empty));
        Assert.False(model.SelectRow("A"));

        Assert.Equal("a", model.SelectedRow?.Shelf.Key);
    }

    [Fact]
    public void SelectRow_InCollapsedGroup_StillSelectsByKey()
    {
        OverlayModel model = ReadyModel();

        Assert.True(model.SelectRow("d"));
        Assert.Equal("d", model.SelectedRow?.Shelf.Key);
    }

    [Fact]
    public void Groups_StartExactlyAsShelfGroupExpandedSays()
    {
        OverlayModel model = ReadyModel();

        Assert.Equal(new[] { "Near", "Far" }, model.Groups.Select(g => g.Name));
        Assert.Equal(new[] { true, false }, model.Groups.Select(g => g.Expanded));
    }

    [Fact]
    public void ToggleGroup_FlipsExpanded_BothWays_AndBackAgain()
    {
        OverlayModel model = ReadyModel();

        model.ToggleGroup("Far");
        Assert.True(model.Groups.Single(g => g.Name == "Far").Expanded);
        model.ToggleGroup("Near");
        Assert.False(model.Groups.Single(g => g.Name == "Near").Expanded);
        model.ToggleGroup("Near");
        model.ToggleGroup("Far");

        Assert.Equal(new[] { true, false }, model.Groups.Select(g => g.Expanded));
    }

    [Fact]
    public void ToggleGroup_UnknownName_IsIgnored()
    {
        OverlayModel model = ReadyModel();

        model.ToggleGroup("Nope");

        Assert.Equal(new[] { true, false }, model.Groups.Select(g => g.Expanded));
    }

    [Fact]
    public void ToggleGroup_InLoading_IsIgnored()
    {
        var model = new OverlayModel(Source());

        model.ToggleGroup("Near");

        Assert.Equal(OverlayState.Loading, model.State);
        Assert.Empty(model.Groups);
    }

    [Fact]
    public void ToggleGroup_KeepsRowsAndOrder()
    {
        OverlayModel model = ReadyModel();

        model.ToggleGroup("Near");

        Assert.Equal(new[] { "a", "b", "c" }, model.Groups[0].Rows.Select(r => r.Shelf.Key));
    }

    [Fact]
    public void Confirm_DisabledUntilSelectableRowSelected_WithSelectTargetReason()
    {
        OverlayModel model = ReadyModel();

        Assert.False(model.ConfirmEnabled);
        Assert.Equal(OverlayModel.SelectTargetReason, model.ConfirmDisabledReason);
        Assert.Null(model.Confirm());
        Assert.Equal(OverlayState.Ready, model.State);

        model.SelectRow("a");

        Assert.True(model.ConfirmEnabled);
        Assert.Null(model.ConfirmDisabledReason);
    }

    [Fact]
    public void Confirm_AfterRefusedSelection_StillDisabled()
    {
        OverlayModel model = ReadyModel();

        model.SelectRow("c");

        Assert.False(model.ConfirmEnabled);
        Assert.Null(model.Confirm());
    }

    // ---- criterion 3: amount

    [Fact]
    public void Amount_DefaultsToAll_MinOfSourceCountAndFits()
    {
        OverlayModel limitedByFits = SelectedModel("a", count: 10);
        Assert.Equal(4, limitedByFits.Amount);
        Assert.Equal("4", limitedByFits.AmountDisplay);

        OverlayModel limitedBySource = SelectedModel("b", count: 3);
        Assert.Equal(3, limitedBySource.Amount);
        Assert.Equal("3", limitedBySource.AmountDisplay);

        OverlayModel equal = SelectedModel("a", count: 4);
        Assert.Equal(4, equal.Amount);
    }

    [Fact]
    public void SelectRow_Success_ResetsAmountToAllOfNewRow()
    {
        OverlayModel model = SelectedModel("a");
        model.SetAmountText("2");
        Assert.Equal(2, model.Amount);

        Assert.True(model.SelectRow("b"));

        Assert.Equal(8, model.Amount);
        Assert.Equal("8", model.AmountDisplay);
    }

    [Fact]
    public void SelectRow_AfterInvalidText_ResetsToValidAll()
    {
        OverlayModel model = SelectedModel("a");
        model.SetAmountText("abc");
        Assert.False(model.ConfirmEnabled);

        Assert.True(model.SelectRow("a"));

        Assert.Equal(4, model.Amount);
        Assert.True(model.ConfirmEnabled);
    }

    [Fact]
    public void SelectRow_Refused_DoesNotResetTypedAmount()
    {
        OverlayModel model = SelectedModel("a");
        model.SetAmountText("2");

        Assert.False(model.SelectRow("c"));

        Assert.Equal(2, model.Amount);
    }

    [Fact]
    public void SetAmountText_OverFits_ShowsClampedValue()
    {
        OverlayModel model = SelectedModel("a");

        model.SetAmountText("6");

        Assert.Equal(4, model.Amount);
        Assert.Equal("4", model.AmountDisplay);
        Assert.True(model.ConfirmEnabled);
    }

    [Fact]
    public void SetAmountText_OverSourceCount_ClampsToSourceCount()
    {
        OverlayModel model = SelectedModel("b", count: 3);

        model.SetAmountText("7");

        Assert.Equal(3, model.Amount);
        Assert.Equal("3", model.AmountDisplay);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("3", 3)]
    [InlineData("4", 4)]
    [InlineData(" 3 ", 3)]
    [InlineData("+2", 2)]
    [InlineData("007", 7)]
    [InlineData("99999999999999999999", 4)]
    [InlineData("2147483647", 4)]
    [InlineData("2147483648", 4)]
    public void SetAmountText_Valid_SetsClampedAmount(string text, int expected)
    {
        OverlayModel model = SelectedModel("b", count: 10);
        model.SelectRow("a");

        model.SetAmountText(text);

        Assert.Equal(Math.Min(expected, 4), model.Amount);
        Assert.Equal(Math.Min(expected, 4).ToString(CultureInfo.InvariantCulture), model.AmountDisplay);
        Assert.True(model.ConfirmEnabled);
        Assert.Null(model.ConfirmDisabledReason);
    }

    [Theory]
    [InlineData("0", "Amount must be at least 1")]
    [InlineData("-1", "Amount must be at least 1")]
    [InlineData("-007", "Amount must be at least 1")]
    [InlineData("", "Enter an amount")]
    [InlineData("   ", "Enter an amount")]
    [InlineData(null, "Enter an amount")]
    [InlineData("abc", "Amount must be a whole number")]
    [InlineData("1.5", "Amount must be a whole number")]
    [InlineData("1e3", "Amount must be a whole number")]
    [InlineData("3x", "Amount must be a whole number")]
    [InlineData("--3", "Amount must be a whole number")]
    [InlineData("1 2", "Amount must be a whole number")]
    public void SetAmountText_Invalid_DisablesConfirm_WithReason_AmountZero(string? text, string reason)
    {
        OverlayModel model = SelectedModel("a");

        model.SetAmountText(text);

        Assert.Equal(0, model.Amount);
        Assert.False(model.ConfirmEnabled);
        Assert.Equal(reason, model.ConfirmDisabledReason);
        Assert.Null(model.Confirm());
        Assert.Equal(OverlayState.Ready, model.State);
    }

    [Fact]
    public void SetAmountText_ReasonConstants_MatchTheirLiterals()
    {
        Assert.Equal("Enter an amount", OverlayModel.AmountEmptyReason);
        Assert.Equal("Amount must be a whole number", OverlayModel.AmountNotNumberReason);
        Assert.Equal("Amount must be at least 1", OverlayModel.AmountTooSmallReason);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5")]
    public void SetAmountText_Invalid_KeepsRawTextInDisplay(string text)
    {
        OverlayModel model = SelectedModel("a");

        model.SetAmountText(text);

        Assert.Equal(text, model.AmountDisplay);
    }

    [Fact]
    public void SetAmountText_OverlongNegativeDigits_StillDisablesConfirm()
    {
        OverlayModel model = SelectedModel("a");

        model.SetAmountText("-99999999999999999999");

        Assert.Equal(0, model.Amount);
        Assert.False(model.ConfirmEnabled);
        Assert.NotNull(model.ConfirmDisabledReason);
    }

    [Fact]
    public void SetAmountText_InvalidThenValid_Recovers()
    {
        OverlayModel model = SelectedModel("a");
        model.SetAmountText("abc");
        Assert.False(model.ConfirmEnabled);

        model.SetAmountText("2");

        Assert.Equal(2, model.Amount);
        Assert.True(model.ConfirmEnabled);
        Assert.Equal("2", model.AmountDisplay);
    }

    [Fact]
    public void SetAmountText_WithoutSelection_IsIgnored()
    {
        OverlayModel model = ReadyModel();

        model.SetAmountText("3");

        Assert.Equal(0, model.Amount);
        Assert.Equal(OverlayModel.SelectTargetReason, model.ConfirmDisabledReason);
    }

    [Fact]
    public void Amount_SourceCountZero_NothingToMove_DisablesConfirm()
    {
        OverlayModel model = SelectedModel("a", count: 0);

        Assert.Equal(0, model.Amount);
        Assert.False(model.ConfirmEnabled);
        Assert.Equal(OverlayModel.NothingToMoveReason, model.ConfirmDisabledReason);
        Assert.Null(model.Confirm());
    }

    [Fact]
    public void Amount_SelectableRowWithZeroFits_TargetFull_DisablesConfirm()
    {
        OverlayModel model = ReadyModel(list: List(Group("G", true, Row("x", 0, selectable: true))));
        Assert.True(model.SelectRow("x"));

        Assert.Equal(0, model.Amount);
        Assert.False(model.ConfirmEnabled);
        Assert.Equal(OverlayModel.TargetFullReason, model.ConfirmDisabledReason);
        Assert.Null(model.Confirm());
    }

    [Fact]
    public void StepAmount_StaysWithinOneAndFits()
    {
        OverlayModel model = SelectedModel("a");
        model.SetAmountText("2");

        model.StepAmount(1);
        Assert.Equal(3, model.Amount);
        model.StepAmount(1);
        Assert.Equal(4, model.Amount);
        model.StepAmount(1);
        Assert.Equal(4, model.Amount);
        model.StepAmount(-1);
        Assert.Equal(3, model.Amount);
        model.StepAmount(-10);
        Assert.Equal(1, model.Amount);
        model.StepAmount(-1);
        Assert.Equal(1, model.Amount);
        Assert.True(model.ConfirmEnabled);
        Assert.Equal("1", model.AmountDisplay);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    public void StepAmount_ExtremeDeltas_NeverLeaveRange(int delta)
    {
        OverlayModel model = SelectedModel("a");

        model.StepAmount(delta);

        Assert.InRange(model.Amount, 1, 4);
        Assert.True(model.ConfirmEnabled);
    }

    [Fact]
    public void StepAmount_FromInvalidText_RecoversToValid()
    {
        OverlayModel model = SelectedModel("a");
        model.SetAmountText("abc");

        model.StepAmount(1);

        Assert.Equal(1, model.Amount);
        Assert.True(model.ConfirmEnabled);
        Assert.Equal("1", model.AmountDisplay);
    }

    [Fact]
    public void StepAmount_WithoutSelection_IsIgnored()
    {
        OverlayModel model = ReadyModel();

        model.StepAmount(1);

        Assert.Equal(0, model.Amount);
        Assert.False(model.ConfirmEnabled);
    }

    [Fact]
    public void StepAmount_SourceCountSmallerThanFits_CapsAtSourceCount()
    {
        OverlayModel model = SelectedModel("b", count: 3);

        model.StepAmount(5);

        Assert.Equal(3, model.Amount);
    }

    // ---- criterion 3: culture

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    public void SetAmountText_UsesInvariantParsing_UnderAnyCulture(string culture)
    {
        WithCulture(culture, () =>
        {
            OverlayModel model = SelectedModel("b");

            model.SetAmountText("5");
            Assert.Equal(5, model.Amount);
            Assert.Equal("5", model.AmountDisplay);

            model.SetAmountText("1.000");
            Assert.False(model.ConfirmEnabled);
            Assert.Equal(OverlayModel.AmountNotNumberReason, model.ConfirmDisabledReason);

            model.SetAmountText("1,000");
            Assert.False(model.ConfirmEnabled);
            Assert.Equal(OverlayModel.AmountNotNumberReason, model.ConfirmDisabledReason);

            model.SetAmountText("-3");
            Assert.Equal(OverlayModel.AmountTooSmallReason, model.ConfirmDisabledReason);
        });
    }

    [Fact]
    public void AmountDisplay_AndResultText_UnderGerman_HaveNoGroupSeparators()
    {
        WithCulture("de-DE", () =>
        {
            OverlayModel model = ReadyModel(5000, List(Group("G", true, Row("big", 4000))));
            Assert.True(model.SelectRow("big"));

            Assert.Equal("4000", model.AmountDisplay);
            Assert.NotNull(model.Confirm());
            model.OnTransferFinished(Finished(moved: 4000, remaining: 1000));
            Assert.Equal("Moved 4000, remaining 1000", model.ResultText);
        });
    }

    // ---- criterion 4: confirm

    [Fact]
    public void Confirm_Ready_ValidAmount_YieldsOneRequest_AndRuns()
    {
        OverlayModel model = SelectedModel("a");
        model.SetAmountText("3");

        TransferRequest? request = model.Confirm();

        Assert.NotNull(request);
        Assert.Same(_sourceShelf, request.Source);
        Assert.Same(_slot, request.SourceSlot);
        Assert.Equal("a", request.Target.Key);
        Assert.Equal(model.SelectedRow?.Shelf, request.Target);
        Assert.Equal(_item, request.Item);
        Assert.Equal(3, request.Amount);
        Assert.Equal(OverlayState.Running, model.State);
    }

    [Fact]
    public void Confirm_DefaultAmount_RequestsAll()
    {
        OverlayModel model = SelectedModel("a");

        TransferRequest? request = model.Confirm();

        Assert.Equal(4, request?.Amount);
    }

    [Fact]
    public void Confirm_TypedOverFits_RequestsClampedAmount()
    {
        OverlayModel model = SelectedModel("a");
        model.SetAmountText("6");

        Assert.Equal(4, model.Confirm()?.Amount);
    }

    [Fact]
    public void Confirm_Twice_SecondYieldsNothing()
    {
        OverlayModel model = SelectedModel("a");

        Assert.NotNull(model.Confirm());
        Assert.Null(model.Confirm());
        Assert.Null(model.Confirm());
        Assert.Equal(OverlayState.Running, model.State);
    }

    [Fact]
    public void Confirm_AfterResult_YieldsNothing()
    {
        OverlayModel model = MakeIn(OverlayState.Result);

        Assert.Null(model.Confirm());
        Assert.Equal(OverlayState.Result, model.State);
    }

    [Fact]
    public void Confirm_InvalidAmount_YieldsNothing_StaysReady()
    {
        OverlayModel model = SelectedModel("a");
        model.SetAmountText("0");

        Assert.Null(model.Confirm());
        Assert.Equal(OverlayState.Ready, model.State);
    }

    [Fact]
    public void Confirm_ReasonDisabledWhileRunning_AndAfter()
    {
        OverlayModel model = MakeIn(OverlayState.Running);
        Assert.Equal(OverlayModel.RunningReason, model.ConfirmDisabledReason);

        model.OnTransferFinished(Finished());
        Assert.Equal(OverlayModel.FinishedReason, model.ConfirmDisabledReason);

        model.Dismiss();
        Assert.Equal(OverlayModel.ClosedReason, model.ConfirmDisabledReason);
        Assert.False(model.ConfirmEnabled);
    }

    // ---- criterion 5: escape

    [Theory]
    [InlineData(OverlayState.Loading)]
    [InlineData(OverlayState.Ready)]
    [InlineData(OverlayState.Empty)]
    [InlineData(OverlayState.Error)]
    public void Escape_BeforeRunning_Closes_NoRequest(OverlayState state)
    {
        OverlayModel model = MakeIn(state);

        Assert.Equal(OverlayAction.Close, model.Escape());

        Assert.Equal(OverlayState.Closed, model.State);
        Assert.False(model.IsCancelRequested);
        Assert.Null(model.Confirm());
    }

    [Fact]
    public void Escape_InRunning_Cancels_Once_AndStaysRunning()
    {
        OverlayModel model = MakeIn(OverlayState.Running);
        Assert.False(model.IsCancelRequested);

        Assert.Equal(OverlayAction.Cancel, model.Escape());
        Assert.Equal(OverlayState.Running, model.State);
        Assert.True(model.IsCancelRequested);

        Assert.Equal(OverlayAction.None, model.Escape());
        Assert.Equal(OverlayAction.None, model.Escape());
        Assert.Equal(OverlayState.Running, model.State);
        Assert.True(model.IsCancelRequested);
    }

    [Fact]
    public void Escape_InRunning_StatusTextTurnsToCancelling()
    {
        OverlayModel model = MakeIn(OverlayState.Running);
        Assert.Equal(OverlayModel.RunningText, model.StatusText);

        model.Escape();

        Assert.Equal(OverlayModel.CancellingText, model.StatusText);
    }

    [Fact]
    public void Escape_CancelThenFinished_ReachesResult_ThenEscapeDismisses()
    {
        OverlayModel model = MakeIn(OverlayState.Running);
        Assert.Equal(OverlayAction.Cancel, model.Escape());

        model.OnTransferFinished(Finished(StopReason.Cancelled, moved: 3, remaining: 7));
        Assert.Equal(OverlayState.Result, model.State);
        Assert.Equal("Stopped: cancelled. Moved 3, remaining 7, no item left in your inventory.", model.ResultText);
        Assert.False(model.AutoClose);

        Assert.Equal(OverlayAction.Dismiss, model.Escape());
        Assert.Equal(OverlayState.Closed, model.State);
        Assert.Equal(OverlayAction.None, model.Escape());
    }

    [Fact]
    public void Escape_InResult_Dismisses_ThenClosedIsNone()
    {
        OverlayModel model = MakeIn(OverlayState.Result);

        Assert.Equal(OverlayAction.Dismiss, model.Escape());
        Assert.Equal(OverlayState.Closed, model.State);
        Assert.Equal(OverlayAction.None, model.Escape());
    }

    [Fact]
    public void Dismiss_ClickInResult_Closes_AndOnlyThere()
    {
        OverlayModel result = MakeIn(OverlayState.Result);
        Assert.True(result.Dismiss());
        Assert.Equal(OverlayState.Closed, result.State);
        Assert.False(result.Dismiss());

        foreach (OverlayState state in new[] { OverlayState.Loading, OverlayState.Ready, OverlayState.Empty, OverlayState.Error, OverlayState.Running })
        {
            OverlayModel model = MakeIn(state);
            Assert.False(model.Dismiss());
            Assert.Equal(state, model.State);
        }
    }

    [Fact]
    public void Escape_ManyPresses_NeverMoreThanOneCancel()
    {
        OverlayModel model = MakeIn(OverlayState.Running);

        int cancels = Enumerable.Range(0, 50).Count(_ => model.Escape() == OverlayAction.Cancel);

        Assert.Equal(1, cancels);
        Assert.Equal(OverlayState.Running, model.State);
    }

    // ---- criterion 6 via the model

    [Fact]
    public void Result_Completed_SetsResultText_AndAutoCloseTrue()
    {
        OverlayModel model = MakeIn(OverlayState.Running);

        model.OnTransferFinished(Finished(StopReason.Completed, moved: 6, remaining: 0));

        Assert.Equal("Moved 6, remaining 0", model.ResultText);
        Assert.True(model.AutoClose);
    }

    [Fact]
    public void Result_Stranded_SaysInInventory_AutoCloseFalse()
    {
        OverlayModel model = MakeIn(OverlayState.Running);

        model.OnTransferFinished(Finished(StopReason.StoreRefused, moved: 2, remaining: 7, stranded: 1));

        Assert.Contains("1 item is in your inventory, the rest is on the source", model.ResultText, StringComparison.Ordinal);
        Assert.DoesNotContain("lost", model.ResultText, StringComparison.OrdinalIgnoreCase);
        Assert.False(model.AutoClose);
    }

    [Fact]
    public void Result_BeforeFinish_AutoCloseFalse_ResultTextNull()
    {
        OverlayModel model = MakeIn(OverlayState.Running);

        Assert.Null(model.ResultText);
        Assert.False(model.AutoClose);
    }

    // ---- refresh behaviour

    [Fact]
    public void Refresh_KeepsToggledGroupsByName()
    {
        OverlayModel model = ReadyModel();
        model.ToggleGroup("Far");
        model.ToggleGroup("Near");

        model.OnShelfList(DefaultList());

        Assert.Equal(new[] { false, true }, model.Groups.Select(g => g.Expanded));
    }

    [Fact]
    public void Refresh_ToggleKeptByNameEvenIfGroupOrderChanges()
    {
        OverlayModel model = ReadyModel();
        model.ToggleGroup("Far");

        model.OnShelfList(List(Group("Far", false, Row("d", 5)), Group("Near", true, Row("a", 4))));

        Assert.True(model.Groups.Single(g => g.Name == "Far").Expanded);
    }

    [Fact]
    public void Refresh_GroupGone_ThenBack_ForgetsItsOldToggle()
    {
        OverlayModel model = ReadyModel();
        model.ToggleGroup("Far");

        model.OnShelfList(List(Group("Near", true, Row("a", 4))));
        model.OnShelfList(DefaultList());

        Assert.False(model.Groups.Single(g => g.Name == "Far").Expanded);
    }

    [Fact]
    public void Refresh_KeepsSelection_WhileRowStillSelectable()
    {
        OverlayModel model = SelectedModel("a");

        model.OnShelfList(DefaultList());

        Assert.Equal("a", model.SelectedRow?.Shelf.Key);
        Assert.True(model.ConfirmEnabled);
    }

    [Fact]
    public void Refresh_SelectionUsesTheNewRowData()
    {
        OverlayModel model = SelectedModel("a");

        model.OnShelfList(List(Group("Near", true, Row("a", 6))));

        Assert.Equal(6, model.SelectedRow?.Fits);
        Assert.Equal(6, model.Amount);
    }

    [Fact]
    public void Refresh_RowBecomesUnselectable_DropsSelection()
    {
        OverlayModel model = SelectedModel("a");

        model.OnShelfList(List(Group("Near", true, Row("a", 0), Row("b", 3))));

        Assert.Null(model.SelectedRow);
        Assert.False(model.ConfirmEnabled);
        Assert.Equal(OverlayModel.SelectTargetReason, model.ConfirmDisabledReason);
        Assert.Equal(0, model.Amount);
        Assert.Null(model.Confirm());
    }

    [Fact]
    public void Refresh_RowDisappears_DropsSelection()
    {
        OverlayModel model = SelectedModel("a");

        model.OnShelfList(List(Group("Near", true, Row("b", 3))));

        Assert.Null(model.SelectedRow);
        Assert.False(model.ConfirmEnabled);
    }

    [Fact]
    public void Refresh_AllUnselectable_ShowsNoTargetText()
    {
        OverlayModel model = SelectedModel("a");

        model.OnShelfList(List(Group("Near", true, Row("a", 0))));

        Assert.Equal(OverlayModel.NoTargetText, model.StatusText);
        Assert.Equal(OverlayModel.NoTargetText, model.ConfirmDisabledReason);
    }

    [Fact]
    public void Refresh_DefaultAll_FollowsNewFits()
    {
        OverlayModel model = SelectedModel("a");
        Assert.Equal(4, model.Amount);

        model.OnShelfList(List(Group("Near", true, Row("a", 9))));
        Assert.Equal(9, model.Amount);

        model.OnShelfList(List(Group("Near", true, Row("a", 2))));
        Assert.Equal(2, model.Amount);
    }

    [Fact]
    public void Refresh_TypedAmount_IsReClampedDown_WhenFitsShrinks()
    {
        OverlayModel model = SelectedModel("b");
        model.SetAmountText("6");
        Assert.Equal(6, model.Amount);

        model.OnShelfList(List(Group("Near", true, Row("b", 3))));

        Assert.Equal(3, model.Amount);
        Assert.Equal("3", model.AmountDisplay);
        Assert.True(model.ConfirmEnabled);
    }

    [Fact]
    public void Refresh_TypedAmount_KeptAsItsClampedValue_NeverGrowsBack()
    {
        OverlayModel model = SelectedModel("b");
        model.SetAmountText("6");

        model.OnShelfList(List(Group("Near", true, Row("b", 3))));
        Assert.Equal(3, model.Amount);

        model.OnShelfList(List(Group("Near", true, Row("b", 9))));

        Assert.Equal(3, model.Amount);
    }

    [Fact]
    public void Refresh_SteppedAmount_Kept_WhenStillFits()
    {
        OverlayModel model = SelectedModel("b");
        model.SetAmountText("5");

        model.OnShelfList(List(Group("Near", true, Row("b", 8))));

        Assert.Equal(5, model.Amount);
    }

    [Fact]
    public void Refresh_WhileAmountTextInvalid_StaysInvalid()
    {
        OverlayModel model = SelectedModel("a");
        model.SetAmountText("abc");

        model.OnShelfList(DefaultList());

        Assert.Equal("abc", model.AmountDisplay);
        Assert.Equal(0, model.Amount);
        Assert.False(model.ConfirmEnabled);
    }

    [Fact]
    public void Refresh_ToEmpty_ThenReady_HasNoSelection()
    {
        OverlayModel model = SelectedModel("a");

        model.OnShelfList(EmptyList());
        Assert.Equal(OverlayState.Empty, model.State);
        Assert.Empty(model.Groups);
        Assert.Null(model.SelectedRow);

        model.OnShelfList(DefaultList());
        Assert.Equal(OverlayState.Ready, model.State);
        Assert.Null(model.SelectedRow);
        Assert.False(model.ConfirmEnabled);
    }

    [Fact]
    public void Refresh_ToError_ClearsSelection()
    {
        OverlayModel model = SelectedModel("a");

        model.OnListFailed();

        Assert.Equal(OverlayState.Error, model.State);
        Assert.Null(model.SelectedRow);
        Assert.Empty(model.Groups);
        Assert.Equal(0, model.Amount);
    }

    [Fact]
    public void Refresh_SourceCountSmall_ReClampsToSourceCount()
    {
        OverlayModel model = SelectedModel("a", count: 2);
        Assert.Equal(2, model.Amount);

        model.OnShelfList(List(Group("Near", true, Row("a", 9))));

        Assert.Equal(2, model.Amount);
    }

    [Fact]
    public void Groups_Reference_IsStableBetweenReads()
    {
        OverlayModel model = ReadyModel();

        Assert.Same(model.Groups, model.Groups);
    }

    // ---- criterion 7: purity

    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (type.HasElementType && type.GetElementType() is Type element)
        {
            foreach (Type inner in Unwrap(element))
            {
                yield return inner;
            }

            yield break;
        }

        yield return type;
        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments().SelectMany(Unwrap))
            {
                yield return argument;
            }
        }
    }

    private static bool IsForbidden(Type type)
    {
        string ns = type.Namespace ?? string.Empty;
        string assembly = type.Assembly.GetName().Name ?? string.Empty;
        string[] banned = { "UnityEngine", "Unity", "Il2Cpp", "Il2CppInterop", "MelonLoader", "BepInEx", "HarmonyLib", "Sons" };
        bool bannedName = banned.Any(prefix =>
            ns.StartsWith(prefix, StringComparison.Ordinal) || assembly.StartsWith(prefix, StringComparison.Ordinal));
        bool gatewayInterface = type.IsInterface && ns == "ShelfTransfer.Core.Gateway";
        return bannedName || gatewayInterface;
    }

    private static List<Type> UiTypes() =>
        typeof(OverlayModel).Assembly.GetTypes()
            .Where(t => t.Namespace == "ShelfTransfer.Core.Ui")
            .ToList();

    private static IEnumerable<(string Where, Type Type)> ReferencedTypes(Type owner)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (FieldInfo field in owner.GetFields(all))
        {
            foreach (Type t in Unwrap(field.FieldType))
            {
                yield return ($"{owner.Name}.{field.Name} (field)", t);
            }
        }

        foreach (PropertyInfo property in owner.GetProperties(all))
        {
            foreach (Type t in Unwrap(property.PropertyType))
            {
                yield return ($"{owner.Name}.{property.Name} (property)", t);
            }
        }

        foreach (MethodBase method in owner.GetMethods(all).Cast<MethodBase>().Concat(owner.GetConstructors(all)))
        {
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                foreach (Type t in Unwrap(parameter.ParameterType))
                {
                    yield return ($"{owner.Name}.{method.Name}({parameter.Name})", t);
                }
            }

            if (method is MethodInfo info)
            {
                foreach (Type t in Unwrap(info.ReturnType))
                {
                    yield return ($"{owner.Name}.{method.Name} (return)", t);
                }
            }
        }
    }

    [Fact]
    public void UiNamespace_HoldsNoGameOrGatewayType_InAnyFieldPropertyOrSignature()
    {
        List<Type> uiTypes = UiTypes();
        Assert.Contains(typeof(OverlayModel), uiTypes);
        Assert.Contains(typeof(StopReasonText), uiTypes);

        // Nested compiler types (closures, state machines) are in the namespace too and are covered by GetTypes.
        var referenced = uiTypes.SelectMany(ReferencedTypes).ToList();
        Assert.Contains(referenced, r => r.Type == typeof(AimTarget));

        List<string> offenders = referenced.Where(r => IsForbidden(r.Type)).Select(r => $"{r.Where}: {r.Type.FullName}").ToList();
        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    [Fact]
    public void CoreAssembly_ReferencesNoUnityOrIl2CppAssembly()
    {
        string[] names = typeof(OverlayModel).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

        Assert.DoesNotContain(names, n => n.StartsWith("UnityEngine", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.StartsWith("Il2Cpp", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.StartsWith("Assembly-CSharp", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.StartsWith("MelonLoader", StringComparison.Ordinal));
    }

    [Fact]
    public void OverlayModel_PublicApi_TakesNoGatewayInterface()
    {
        IEnumerable<Type> parameterTypes = typeof(OverlayModel)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Concat(typeof(OverlayModel).GetMethods().SelectMany(m => m.GetParameters()))
            .SelectMany(p => Unwrap(p.ParameterType));

        Assert.DoesNotContain(parameterTypes, t => t == typeof(IShelfGateway));
    }

    // ---- I4 property: no input sequence yields an out-of-range request

    private static readonly string?[] _textPool =
    {
        null, string.Empty, " ", "0", "-1", "1", "2", "3", "4", "6", "9", "99", "abc", "1.5", "1,000", "99999999999999999999", "-99999999999999999999", "+2", " 4 ",
    };

    private static readonly int[] _deltaPool = { int.MinValue, -100, -1, 0, 1, 2, 100, int.MaxValue };

    private static readonly string[] _keyPool = { "k0", "k1", "k2", "k3", "k4", "k5", "unknown" };

    private static ShelfList RandomList(Random rng)
    {
        var keys = new Queue<string>(_keyPool.Take(6).OrderBy(_ => rng.Next()));
        var groups = new List<ShelfGroup>();
        int groupCount = rng.Next(0, 4);
        for (int g = 0; g < groupCount && keys.Count > 0; g++)
        {
            var rows = new List<ShelfRow>();
            int rowCount = rng.Next(1, 4);
            for (int r = 0; r < rowCount && keys.Count > 0; r++)
            {
                int fits = rng.Next(0, 10);

                // Hostile flags on purpose: Selectable true with Fits 0, and false with Fits > 0.
                bool selectable = rng.Next(5) == 0 ? !(fits > 0) : fits > 0;
                rows.Add(Row(keys.Dequeue(), fits, selectable));
            }

            groups.Add(new ShelfGroup("g" + g, rng.Next(2) == 0, rows));
        }

        return List(groups.ToArray());
    }

    [Fact]
    public void I4_NoRandomSequence_YieldsRequestOutsideOneToFits()
    {
        int requests = 0;
        for (int seed = 0; seed < 400; seed++)
        {
            var rng = new Random(seed);
            int sourceCount = rng.Next(0, 13);
            var model = new OverlayModel(Source(sourceCount));
            bool confirmedOnce = false;

            for (int step = 0; step < 80; step++)
            {
                switch (rng.Next(8))
                {
                    case 0:
                        model.OnShelfList(RandomList(rng));
                        break;
                    case 1:
                    case 2:
                        model.SelectRow(_keyPool[rng.Next(_keyPool.Length)]);
                        break;
                    case 3:
                        model.SetAmountText(_textPool[rng.Next(_textPool.Length)]);
                        break;
                    case 4:
                        model.StepAmount(_deltaPool[rng.Next(_deltaPool.Length)]);
                        break;
                    case 5:
                        model.ToggleGroup("g" + rng.Next(3));
                        break;
                    case 6:
                        if (rng.Next(10) == 0)
                        {
                            model.OnListFailed();
                        }

                        break;
                    default:
                        ShelfRow? selected = model.SelectedRow;
                        TransferRequest? request = model.Confirm();
                        if (request is not null)
                        {
                            requests++;
                            Assert.False(confirmedOnce, $"seed {seed}: second request");
                            confirmedOnce = true;
                            Assert.NotNull(selected);
                            Assert.True(selected.Selectable);
                            Assert.InRange(request.Amount, 1, Math.Min(selected.Fits, sourceCount));
                            Assert.Equal(selected.Shelf, request.Target);
                            Assert.Equal(_sourceShelf, request.Source);
                            Assert.Equal(_item, request.Item);
                            Assert.Equal(OverlayState.Running, model.State);
                        }

                        break;
                }

                AssertReasonConsistent(model);
                if (model.ConfirmEnabled)
                {
                    Assert.InRange(model.Amount, 1, Math.Min(model.SelectedRow!.Fits, sourceCount));
                }
            }
        }

        Assert.True(requests > 20, $"property test too weak: only {requests} requests");
    }

    [Fact]
    public void I4_Exhaustive_EveryAmountText_AgainstEveryFits_NeverExceedsFits()
    {
        foreach (int fits in new[] { 1, 2, 4, 9 })
        {
            foreach (int count in new[] { 1, 3, 10 })
            {
                foreach (string? text in _textPool)
                {
                    OverlayModel model = ReadyModel(count, List(Group("G", true, Row("x", fits))));
                    model.SelectRow("x");
                    model.SetAmountText(text);

                    TransferRequest? request = model.Confirm();

                    if (request is not null)
                    {
                        Assert.InRange(request.Amount, 1, Math.Min(fits, count));
                    }
                }
            }
        }
    }
}
