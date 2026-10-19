// purpose: pins card 23 on the model side: the amount note (why the field shows less than typed) and what Confirm emits and refuses
// owns:    OverlayModelConfirmTests
// deps:    OverlayModel, OverlayState, OverlayAction, AimTarget, ShelfList, ShelfRow, TransferRequest, TransferReport, AmountClamp

using System;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Shelves;
using ShelfTransfer.Core.Transfer;
using ShelfTransfer.Core.Ui;

using Xunit;

namespace ShelfTransfer.Core.Tests.Ui;

public sealed class OverlayModelConfirmTests
{
    private const string _rounded10 = "Rounded down to whole chunks of 10";

    private static readonly ShelfRef _sourceShelf = new("src", "StorageShelf", new WorldPosition(0f, 0f, 0f));
    private static readonly SlotRef _slot = new(9);
    private static readonly ItemId _item = new(42);

    private static ShelfRow Row(string key, int fits) =>
        new(new ShelfRef(key, "StorageShelf", new WorldPosition(1f, 0f, 1f)), "Shelf " + key, 1.5f, "N", fits, fits > 0);

    private static ShelfList ListOf(params ShelfRow[] rows) => new(new[] { new ShelfGroup("Near", true, rows) }, Array.Empty<string>());

    private static OverlayModel Selected(int sourceCount, int fits, int unit = 1)
    {
        OverlayModel model = new(new AimTarget(_sourceShelf, _slot, _item, sourceCount), unit);
        model.OnShelfList(ListOf(Row("a", fits)));
        Assert.True(model.SelectRow("a"));
        return model;
    }

    // --- AmountNote ---

    [Fact]
    public void AmountNote_UnitOneTypedMoreThanFits_IsClamped()
    {
        OverlayModel model = Selected(sourceCount: 20, fits: 4);

        model.SetAmountText("6");

        Assert.Equal(4, model.Amount);
        Assert.Equal(OverlayModel.ClampedToFitNote, model.AmountNote);
        Assert.Equal("Clamped to what fits", model.AmountNote);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("3")]
    [InlineData(" 4 ")]
    public void AmountNote_UnitOneTypedWithinFits_IsNull(string text)
    {
        OverlayModel model = Selected(sourceCount: 20, fits: 4);

        model.SetAmountText(text);

        Assert.Null(model.AmountNote);
    }

    [Fact]
    public void AmountNote_UnitTenTypedFiftyThreeRoomy_IsRoundedNote()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100, unit: 10);

        model.SetAmountText("53");

        Assert.Equal(50, model.Amount);
        Assert.Equal(_rounded10, model.AmountNote);
    }

    [Fact]
    public void AmountNote_UnitTenTypedFiftyThreeRoomThirty_IsClamped()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 30, unit: 10);

        model.SetAmountText("53");

        Assert.Equal(30, model.Amount);
        Assert.Equal(OverlayModel.ClampedToFitNote, model.AmountNote);
    }

    [Fact]
    public void AmountNote_UnitTenTypedFiftyThreeRoomExactlyFifty_IsRoundedNote()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 50, unit: 10);

        model.SetAmountText("53");

        Assert.Equal(50, model.Amount);
        Assert.Equal(_rounded10, model.AmountNote);
    }

    // Pins current wording: a source-limited cut reads "Clamped to what fits" (card 86 may reword it).
    [Fact]
    public void AmountNote_SourceLimited_IsClampedWording()
    {
        OverlayModel model = Selected(sourceCount: 30, fits: 100, unit: 10);

        model.SetAmountText("53");

        Assert.Equal(30, model.Amount);
        Assert.Equal("Clamped to what fits", model.AmountNote);
    }

    [Fact]
    public void AmountNote_UnitTenTypedFifty_IsNull()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100, unit: 10);

        model.SetAmountText("50");

        Assert.Null(model.AmountNote);
    }

    [Fact]
    public void AmountNote_TypedFiveAtUnitTen_IsNullAndConfirmIsDisabled()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100, unit: 10);

        model.SetAmountText("5");

        Assert.Null(model.AmountNote);
        Assert.Equal(0, model.Amount);
        Assert.False(model.ConfirmEnabled);
    }

    [Fact]
    public void AmountNote_HugeDigitRun_IsClamped()
    {
        OverlayModel model = Selected(sourceCount: 20, fits: 4);

        model.SetAmountText("99999999999999999999");

        Assert.Equal(4, model.Amount);
        Assert.Equal(OverlayModel.ClampedToFitNote, model.AmountNote);
    }

    [Fact]
    public void AmountNote_NextTypedValueReplacesIt()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100, unit: 10);
        model.SetAmountText("53");
        Assert.NotNull(model.AmountNote);

        model.SetAmountText("50");

        Assert.Null(model.AmountNote);
    }

    [Fact]
    public void AmountNote_ClearedByStep()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100, unit: 10);
        model.SetAmountText("53");

        model.StepAmount(1);

        Assert.Equal(60, model.Amount);
        Assert.Null(model.AmountNote);
    }

    [Fact]
    public void AmountNote_StepThatClampsToFitsSetsNoNote()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 50, unit: 10);
        model.SetAmountText("50");

        model.StepAmount(1);

        Assert.Equal(50, model.Amount);
        Assert.Null(model.AmountNote);
    }

    [Fact]
    public void AmountNote_ClearedByAllViaReselect()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100, unit: 10);
        model.SetAmountText("53");

        Assert.True(model.SelectRow("a"));

        Assert.Equal(100, model.Amount);
        Assert.Null(model.AmountNote);
    }

    [Fact]
    public void AmountNote_ClearedBySelectingAnotherRow()
    {
        OverlayModel model = new(new AimTarget(_sourceShelf, _slot, _item, 100), 10);
        model.OnShelfList(ListOf(Row("a", 100), Row("b", 30)));
        model.SelectRow("a");
        model.SetAmountText("53");

        Assert.True(model.SelectRow("b"));

        Assert.Null(model.AmountNote);
    }

    [Fact]
    public void AmountNote_ClearedByRefresh()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100, unit: 10);
        model.SetAmountText("53");

        model.OnShelfList(ListOf(Row("a", 100)));

        Assert.Equal(50, model.Amount);
        Assert.Null(model.AmountNote);
    }

    [Fact]
    public void AmountNote_ClearedByInvalidText()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100, unit: 10);
        model.SetAmountText("53");

        model.SetAmountText("abc");

        Assert.Null(model.AmountNote);

        model.SetAmountText("53");
        model.SetAmountText("");
        Assert.Null(model.AmountNote);
    }

    [Fact]
    public void AmountNote_ClearedWhenSelectionIsDroppedByRefresh()
    {
        OverlayModel model = new(new AimTarget(_sourceShelf, _slot, _item, 100), 10);
        model.OnShelfList(ListOf(Row("a", 100), Row("b", 100)));
        model.SelectRow("a");
        model.SetAmountText("53");

        model.OnShelfList(ListOf(Row("b", 100)));

        Assert.Null(model.SelectedRow);
        Assert.Null(model.AmountNote);
    }

    [Fact]
    public void AmountNote_NullBeforeAnythingIsTypedAndWithoutSelection()
    {
        OverlayModel model = new(new AimTarget(_sourceShelf, _slot, _item, 100), 10);
        Assert.Null(model.AmountNote);
        model.OnShelfList(ListOf(Row("a", 100)));
        model.SetAmountText("53");
        Assert.Null(model.AmountNote);
        model.SelectRow("a");
        Assert.Null(model.AmountNote);
    }

    [Fact]
    public void AmountNote_StepperAndRefreshNeverSetOne()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100, unit: 10);

        model.StepAmount(1);
        Assert.Null(model.AmountNote);
        model.StepAmount(500);
        Assert.Null(model.AmountNote);
        model.OnShelfList(ListOf(Row("a", 40)));
        Assert.Equal(40, model.Amount);
        Assert.Null(model.AmountNote);
    }

    // --- Confirm (model side) ---

    [Fact]
    public void Confirm_DefaultAll_EmitsTheClampedAmountNotTheSentinel()
    {
        OverlayModel model = Selected(sourceCount: 6, fits: 50);

        TransferRequest? request = model.Confirm();

        Assert.NotNull(request);
        Assert.Equal(6, request!.Amount);
        Assert.NotEqual(AmountClamp.All, request.Amount);
        Assert.Equal("a", request.Target.Key);
        Assert.Equal(_sourceShelf, request.Source);
        Assert.Equal(_slot, request.SourceSlot);
        Assert.Equal(_item, request.Item);
        Assert.Equal(OverlayState.Running, model.State);
    }

    [Fact]
    public void Confirm_AllOnChunkedItem_EmitsWholeChunks()
    {
        OverlayModel model = Selected(sourceCount: 57, fits: 50, unit: 10);

        Assert.Equal(50, model.Confirm()!.Amount);
    }

    [Fact]
    public void Confirm_AfterStep_EmitsTheStepAmount()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100, unit: 10);
        model.SetAmountText("20");
        model.StepAmount(1);

        Assert.Equal(30, model.Confirm()!.Amount);
    }

    [Fact]
    public void Confirm_StepPastEverything_EmitsFitsNotOverflow()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 70, unit: 10);

        model.StepAmount(int.MaxValue);

        Assert.Equal(70, model.Confirm()!.Amount);
    }

    [Fact]
    public void Confirm_TypedFiftyThree_EmitsFifty()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100, unit: 10);
        model.SetAmountText("53");

        Assert.Equal(50, model.Confirm()!.Amount);
    }

    [Fact]
    public void Confirm_TypedSixAgainstFreeFour_EmitsFour()
    {
        OverlayModel model = Selected(sourceCount: 20, fits: 4);
        model.SetAmountText("6");

        Assert.Equal(4, model.Confirm()!.Amount);
    }

    [Fact]
    public void Confirm_TypedHugeDigitRun_EmitsFitsNotTheSentinel()
    {
        OverlayModel model = Selected(sourceCount: 20, fits: 20);
        model.SetAmountText("2147483647");

        Assert.Equal(20, model.Confirm()!.Amount);
    }

    [Fact]
    public void Confirm_ThreeOfSix_EmitsThree()
    {
        OverlayModel model = Selected(sourceCount: 6, fits: 6);
        model.SetAmountText("3");

        Assert.Equal(3, model.Confirm()!.Amount);
    }

    [Fact]
    public void Confirm_SecondCallWhileRunning_ReturnsNull()
    {
        OverlayModel model = Selected(sourceCount: 6, fits: 6);
        Assert.NotNull(model.Confirm());

        Assert.Null(model.Confirm());
        Assert.Null(model.Confirm());
        Assert.Equal(OverlayState.Running, model.State);
        Assert.False(model.ConfirmEnabled);
    }

    [Fact]
    public void Confirm_InputWhileRunning_DoesNotReenable()
    {
        OverlayModel model = Selected(sourceCount: 6, fits: 6);
        model.Confirm();

        model.SetAmountText("2");
        model.StepAmount(1);
        model.SelectRow("a");

        Assert.Null(model.Confirm());
        Assert.Equal(OverlayModel.RunningReason, model.ConfirmDisabledReason);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-3")]
    [InlineData("x")]
    public void Confirm_DisabledForUnusableAmounts(string text)
    {
        OverlayModel model = Selected(sourceCount: 6, fits: 6);
        model.SetAmountText(text);

        Assert.False(model.ConfirmEnabled);
        Assert.False(string.IsNullOrWhiteSpace(model.ConfirmDisabledReason));
        Assert.Null(model.Confirm());
        Assert.Equal(OverlayState.Ready, model.State);
    }

    [Fact]
    public void ConfirmDisabledReason_ReasonsMatchTheirCauses()
    {
        OverlayModel model = Selected(sourceCount: 6, fits: 6);

        model.SetAmountText("0");
        Assert.Equal(OverlayModel.AmountTooSmallReason, model.ConfirmDisabledReason);
        model.SetAmountText("");
        Assert.Equal(OverlayModel.AmountEmptyReason, model.ConfirmDisabledReason);
        model.SetAmountText("1.5");
        Assert.Equal(OverlayModel.AmountNotNumberReason, model.ConfirmDisabledReason);
    }

    [Fact]
    public void Confirm_NoTargetSelected_DisabledWithSelectReason()
    {
        OverlayModel model = new(new AimTarget(_sourceShelf, _slot, _item, 6));
        model.OnShelfList(ListOf(Row("a", 6)));

        Assert.False(model.ConfirmEnabled);
        Assert.Equal(OverlayModel.SelectTargetReason, model.ConfirmDisabledReason);
        Assert.Null(model.Confirm());
    }

    [Fact]
    public void Confirm_BeforeAnyListing_IsNull()
    {
        OverlayModel model = new(new AimTarget(_sourceShelf, _slot, _item, 6));

        Assert.Null(model.Confirm());
        Assert.Equal(OverlayModel.LoadingText, model.ConfirmDisabledReason);
    }

    [Fact]
    public void Confirm_UnselectableRowCannotBeSelectedSoConfirmStaysDisabled()
    {
        OverlayModel model = new(new AimTarget(_sourceShelf, _slot, _item, 100), 10);
        model.OnShelfList(ListOf(Row("full", 0)));

        Assert.False(model.SelectRow("full"));
        Assert.Null(model.Confirm());
    }

    [Theory]
    [InlineData(StopReason.BlockedBeforeStart)]
    [InlineData(StopReason.Completed)]
    [InlineData(StopReason.Cancelled)]
    public void OnTransferFinished_AnyReport_LeavesRunningAndConfirmStaysClosed(StopReason reason)
    {
        OverlayModel model = Selected(sourceCount: 6, fits: 6);
        model.Confirm();

        model.OnTransferFinished(new TransferReport(reason, 6, 0, 6, 0, null));

        Assert.NotEqual(OverlayState.Running, model.State);
        Assert.Equal(OverlayState.Result, model.State);
        Assert.False(string.IsNullOrWhiteSpace(model.ResultText));
        Assert.Null(model.Confirm());
        Assert.Equal(OverlayModel.FinishedReason, model.ConfirmDisabledReason);
    }

    [Fact]
    public void OnTransferFinished_BlockedBeforeStart_CanBeDismissedWithEsc()
    {
        OverlayModel model = Selected(sourceCount: 6, fits: 6);
        model.Confirm();
        model.OnTransferFinished(new TransferReport(StopReason.BlockedBeforeStart, 6, 0, 6, 0, null));

        Assert.Equal(OverlayAction.Dismiss, model.Escape());
        Assert.Equal(OverlayState.Closed, model.State);
    }

    [Fact]
    public void Escape_TwiceWhileRunning_YieldsOneCancel()
    {
        OverlayModel model = Selected(sourceCount: 6, fits: 6);
        model.Confirm();

        Assert.Equal(OverlayAction.Cancel, model.Escape());
        Assert.Equal(OverlayAction.None, model.Escape());
    }

    [Fact]
    public void Confirm_NeverEmitsMoreThanSourceOrFitsOrNonPositive_Sweep()
    {
        foreach (int unit in new[] { 1, 2, 10 })
        {
            foreach (int source in new[] { 0, 1, 9, 10, 25, 100 })
            {
                foreach (int fits in new[] { 0, 1, 10, 30, 100 })
                {
                    foreach (string? text in new string?[] { null, "1", "10", "53", "99999999999", "-1", "0", "a" })
                    {
                        OverlayModel model = new(new AimTarget(_sourceShelf, _slot, _item, source), unit);
                        model.OnShelfList(ListOf(Row("a", fits - (fits % unit))));
                        if (!model.SelectRow("a"))
                        {
                            Assert.Null(model.Confirm());
                            continue;
                        }

                        if (text is not null)
                        {
                            model.SetAmountText(text);
                        }

                        TransferRequest? request = model.Confirm();
                        if (request is null)
                        {
                            continue;
                        }

                        Assert.InRange(request.Amount, 1, Math.Min(source, fits));
                        Assert.Equal(0, request.Amount % unit);
                        Assert.NotEqual(AmountClamp.All, request.Amount);
                    }
                }
            }
        }
    }
}
