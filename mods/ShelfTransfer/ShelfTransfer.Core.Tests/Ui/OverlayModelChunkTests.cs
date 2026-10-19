// purpose: pins the overlay amount in whole chunks (card 68, I4 amended): typed, default All, stepper and refresh, and its agreement with the runner's plan
// owns:    OverlayModelChunkTests
// deps:    OverlayModel, AimTarget, ShelfList, ShelfRow, TransferRunner, FakeShelfGateway

using System;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Shelves;
using ShelfTransfer.Core.Tests.Fakes;
using ShelfTransfer.Core.Transfer;
using ShelfTransfer.Core.Ui;

using Xunit;

namespace ShelfTransfer.Core.Tests.Ui;

public sealed class OverlayModelChunkTests
{
    private const string _tooSmallTen = "Amount must be at least 10; this item moves in 10s";

    private static readonly ShelfRef _sourceShelf = new("src", "StorageShelf", new WorldPosition(0f, 0f, 0f));
    private static readonly SlotRef _slot = new(9);
    private static readonly ItemId _item = new(42);

    private static ShelfRow Row(string key, int fits) =>
        new(new ShelfRef(key, "StorageShelf", new WorldPosition(1f, 0f, 1f)), "Shelf " + key, 1.5f, "N", fits, fits > 0);

    private static ShelfList ListOf(params ShelfRow[] rows) => new(new[] { new ShelfGroup("Near", true, rows) }, Array.Empty<string>());

    private static OverlayModel Selected(int sourceCount, int fits, int unit = 10)
    {
        OverlayModel model = new(new AimTarget(_sourceShelf, _slot, _item, sourceCount), unit);
        model.OnShelfList(ListOf(Row("a", fits)));
        Assert.True(model.SelectRow("a"));
        return model;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_UnitBelowOne_Throws(int unit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayModel(new AimTarget(_sourceShelf, _slot, _item, 5), unit));
    }

    [Fact]
    public void Ctor_DefaultUnit_IsOne()
    {
        Assert.Equal(1, new OverlayModel(new AimTarget(_sourceShelf, _slot, _item, 5)).TransferUnit);
    }

    // Criterion 2 in the overlay: the default "All" on Fits 53 is 50
    [Fact]
    public void SelectRow_DefaultAllOnFitsFiftyThree_AmountIsFifty()
    {
        OverlayModel model = Selected(sourceCount: 60, fits: 53);

        Assert.Equal(50, model.Amount);
        Assert.Equal("50", model.AmountDisplay);
        Assert.True(model.ConfirmEnabled);
    }

    [Fact]
    public void SetAmountText_FiftyThree_ClampsToFiftyAndShowsFifty()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100);

        model.SetAmountText("53");

        Assert.Equal(50, model.Amount);
        Assert.Equal("50", model.AmountDisplay);
        Assert.True(model.ConfirmEnabled);
    }

    [Fact]
    public void SetAmountText_FiveAtUnitTen_ShowsTheChunkReasonAndDisablesConfirm()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100);

        model.SetAmountText("5");

        Assert.Equal(0, model.Amount);
        Assert.Equal("5", model.AmountDisplay);
        Assert.False(model.ConfirmEnabled);
        Assert.Equal(_tooSmallTen, model.ConfirmDisabledReason);
        Assert.Null(model.Confirm());
    }

    [Fact]
    public void SetAmountText_ZeroAtUnitTen_ShowsTheChunkReason()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100);

        model.SetAmountText("0");

        Assert.Equal(_tooSmallTen, model.ConfirmDisabledReason);
        Assert.False(model.ConfirmEnabled);
    }

    [Fact]
    public void StepAmount_UpAndDown_MovesInWholeChunks()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100);
        model.SetAmountText("30");

        model.StepAmount(1);
        Assert.Equal(40, model.Amount);

        model.StepAmount(-2);
        Assert.Equal(20, model.Amount);
    }

    [Fact]
    public void StepAmount_DownFromOneChunk_FloorsAtOneChunk()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100);
        model.SetAmountText("10");

        model.StepAmount(-1);
        Assert.Equal(10, model.Amount);

        model.StepAmount(-50);
        Assert.Equal(10, model.Amount);
        Assert.True(model.ConfirmEnabled);
    }

    [Fact]
    public void StepAmount_UpPastFits_ClampsToTheLargestWholeChunk()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 53);
        model.SetAmountText("50");

        model.StepAmount(1);

        Assert.Equal(50, model.Amount);
    }

    [Fact]
    public void StepAmount_ExtremeDelta_DoesNotOverflow()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100);

        model.StepAmount(int.MaxValue);
        Assert.Equal(100, model.Amount);

        model.StepAmount(int.MinValue);
        Assert.Equal(10, model.Amount);
    }

    [Fact]
    public void StepAmount_FromAnInvalidTypedAmount_RecoversToAWholeChunk()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100);
        model.SetAmountText("5");

        model.StepAmount(1);

        Assert.Equal(10, model.Amount);
        Assert.True(model.ConfirmEnabled);
    }

    // Refresh shrinking Fits under one chunk
    [Fact]
    public void OnShelfList_FitsShrinksBelowOneChunk_NothingFitsAndConfirmDisabled()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100);
        Assert.Equal(100, model.Amount);

        model.OnShelfList(ListOf(Row("a", 4)));

        Assert.Equal(0, model.Amount);
        Assert.False(model.ConfirmEnabled);
        Assert.Equal(OverlayModel.TargetFullReason, model.ConfirmDisabledReason);
    }

    [Fact]
    public void OnShelfList_FitsShrinksToTwentyFive_DefaultAllFollowsToTwenty()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100);

        model.OnShelfList(ListOf(Row("a", 25)));

        Assert.Equal(20, model.Amount);
        Assert.True(model.ConfirmEnabled);
    }

    // Card 69 owns the should-fix: a source of 5 with a roomy target yields Fits = 5 (min(source, free)), which the 4-arg
    // clamp reads as "target cannot fit a chunk", while the runner says NothingToMove. The amount agrees (0); this pins
    // the CURRENT text, which card 69 changes to NothingToMoveReason when it wires the overlay.
    [Fact]
    public void SelectRow_SourceUnderOneChunk_ShowsTargetFullTextUntilCard69()
    {
        OverlayModel model = Selected(sourceCount: 5, fits: 5);

        Assert.Equal(0, model.Amount);
        Assert.False(model.ConfirmEnabled);
        Assert.Equal(OverlayModel.TargetFullReason, model.ConfirmDisabledReason);
    }

    [Fact]
    public void Confirm_AtUnitTen_RequestsAWholeNumberOfChunks()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100);
        model.SetAmountText("53");

        TransferRequest? request = model.Confirm();

        Assert.NotNull(request);
        Assert.Equal(50, request!.Amount);
    }

    // Criterion 5: unit 1 behaves as before
    [Fact]
    public void UnitOne_TypedFive_IsFiveAndZeroKeepsTheOldReason()
    {
        OverlayModel model = Selected(sourceCount: 100, fits: 100, unit: 1);

        model.SetAmountText("5");
        Assert.Equal(5, model.Amount);

        model.SetAmountText("0");
        Assert.Equal(OverlayModel.AmountTooSmallReason, model.ConfirmDisabledReason);

        model.SetAmountText("3");
        model.StepAmount(1);
        Assert.Equal(4, model.Amount);
        model.StepAmount(-10);
        Assert.Equal(1, model.Amount);
    }

    // I4: the amount the overlay would request is the amount the runner plans, for every ask, source and room
    [Theory]
    [InlineData(10)]
    [InlineData(3)]
    [InlineData(1)]
    public void Confirm_AgreesWithTheRunnerPlanOnAGrid(int unit)
    {
        int[] sources = { 5, 10, 25, 59, 100 };
        int[] capacities = { 1, 9, 10, 25, 53, 1000 };
        int[] asks = { 1, 9, 10, 19, 53, 1000 };
        foreach (int source in sources)
        {
            foreach (int capacity in capacities)
            {
                foreach (int ask in asks)
                {
                    int fits = Math.Min(source, capacity);
                    OverlayModel model = new(new AimTarget(_sourceShelf, _slot, _item, source), unit);
                    model.OnShelfList(ListOf(Row("a", fits)));
                    bool isSelected = model.SelectRow("a");
                    if (isSelected)
                    {
                        model.SetAmountText(ask.ToString());
                    }

                    FakeShelfGateway gateway = new() { TransferUnit = unit };
                    ShelfRef dstRef = new("a", "StorageShelf", default);
                    gateway.Add(_sourceShelf, source);
                    gateway.Add(dstRef, 0).Capacity = capacity;
                    TransferRunner runner = new(gateway, new FakeModLog(), new TransferRequest(_sourceShelf, _slot, dstRef, _item, ask));
                    runner.Tick(1);

                    int overlayAmount = isSelected ? model.Amount : 0;
                    Assert.True(overlayAmount == runner.Requested, $"unit {unit} source {source} capacity {capacity} ask {ask}: overlay {overlayAmount} runner {runner.Requested}");
                }
            }
        }
    }
}
