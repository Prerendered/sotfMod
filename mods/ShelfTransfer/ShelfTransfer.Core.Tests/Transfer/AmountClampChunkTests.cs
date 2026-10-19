// purpose: pins the chunk-aware AmountClamp (card 68, I4 amended): whole chunks only, a target must fit a whole chunk, unit 1 equals the 3-arg clamp
// owns:    AmountClampChunkTests
// deps:    AmountClamp, ClampResult, ClampReason

using System;

using ShelfTransfer.Core.Transfer;

using Xunit;

namespace ShelfTransfer.Core.Tests.Transfer;

public sealed class AmountClampChunkTests
{
    // Criteria 2 and 3
    [Theory]
    [InlineData(53, 100, 100, 50)]
    [InlineData(53, 100, 25, 20)]
    [InlineData(AmountClamp.All, 30, 25, 20)]
    [InlineData(AmountClamp.All, 25, 100, 20)]
    [InlineData(AmountClamp.All, 10, 10, 10)]
    [InlineData(10, 100, 100, 10)]
    [InlineData(19, 100, 100, 10)]
    [InlineData(20, 100, 100, 20)]
    [InlineData(AmountClamp.All, 59, 59, 50)]
    public void Clamp_UnitTen_RoundsDownToWholeChunks(int requested, int source, int free, int expected)
    {
        ClampResult clamp = AmountClamp.Clamp(requested, source, free, 10);

        Assert.Equal(ClampReason.Ok, clamp.Reason);
        Assert.Equal(expected, clamp.Amount);
    }

    // Criterion 3: the target cannot fit one chunk
    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public void Clamp_TargetFitsLessThanOneChunk_TargetFull(int free)
    {
        ClampResult clamp = AmountClamp.Clamp(53, 100, free, 10);

        Assert.Equal(0, clamp.Amount);
        Assert.Equal(ClampReason.TargetFull, clamp.Reason);
    }

    [Fact]
    public void Clamp_TargetFreeZeroOrNegative_TargetFullFromTheCountClamp()
    {
        Assert.Equal(ClampReason.TargetFull, AmountClamp.Clamp(53, 100, 0, 10).Reason);
        Assert.Equal(ClampReason.TargetFull, AmountClamp.Clamp(53, 100, -3, 10).Reason);
    }

    [Fact]
    public void Clamp_SourceHoldsLessThanOneChunkAndTargetRoomy_NothingToMove()
    {
        ClampResult clamp = AmountClamp.Clamp(AmountClamp.All, 9, 100, 10);

        Assert.Equal(0, clamp.Amount);
        Assert.Equal(ClampReason.NothingToMove, clamp.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Clamp_SourceZeroOrLess_NothingToMove(int source)
    {
        Assert.Equal(ClampReason.NothingToMove, AmountClamp.Clamp(53, source, 100, 10).Reason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public void Clamp_RequestedLessThanOneChunk_InvalidAmount(int requested)
    {
        ClampResult clamp = AmountClamp.Clamp(requested, 100, 100, 10);

        Assert.Equal(0, clamp.Amount);
        Assert.Equal(ClampReason.InvalidAmount, clamp.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(int.MinValue)]
    public void Clamp_RequestedZeroOrNegative_InvalidAmountWinsOverEverything(int requested)
    {
        Assert.Equal(ClampReason.InvalidAmount, AmountClamp.Clamp(requested, 0, 0, 10).Reason);
    }

    // Card 69 (overlay wiring) owns the should-fix: with the overlay's 4-arg call (Fits = min(source, free)) a source of 5 and
    // a roomy target arrive here as (All, 5, 5, 10), and the target side is checked first, so the reason is TargetFull while the
    // runner says NothingToMove. This pins the CURRENT behaviour; card 69 flips both expectations to NothingToMove.
    [Fact]
    public void Clamp_SourceUnderOneChunkWithFitsAsFree_ReportsTargetFullUntilCard69()
    {
        ClampResult overlayShape = AmountClamp.Clamp(AmountClamp.All, 5, 5, 10);
        Assert.Equal(0, overlayShape.Amount);
        Assert.Equal(ClampReason.TargetFull, overlayShape.Reason);

        ClampResult runnerShape = AmountClamp.Clamp(AmountClamp.All, 5, 100, 10);
        Assert.Equal(ClampReason.NothingToMove, runnerShape.Reason);
    }

    [Fact]
    public void Clamp_AllInputsIntMaxValue_RoundsDownWithoutOverflow()
    {
        ClampResult clamp = AmountClamp.Clamp(int.MaxValue, int.MaxValue, int.MaxValue, 10);

        Assert.Equal(ClampReason.Ok, clamp.Reason);
        Assert.Equal(int.MaxValue / 10 * 10, clamp.Amount);
    }

    // Criterion 5: unit 1 is the 3-arg clamp, bit for bit
    [Fact]
    public void Clamp_UnitOne_EqualsTheThreeArgClampOnAGrid()
    {
        int[] values = { int.MinValue, -1, 0, 1, 2, 5, 9, 10, 11, 53, int.MaxValue };
        foreach (int requested in values)
        {
            foreach (int source in values)
            {
                foreach (int free in values)
                {
                    Assert.Equal(AmountClamp.Clamp(requested, source, free), AmountClamp.Clamp(requested, source, free, 1));
                }
            }
        }
    }

    // Invariant 4: an Ok amount is a positive whole number of chunks, within request, source and target
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(64)]
    public void Clamp_AnyOkResult_IsPositiveWholeChunksWithinAllThreeBounds(int unit)
    {
        int[] values = { 1, 2, 3, 9, 10, 11, 19, 20, 25, 53, 64, 65, 100, 1000 };
        foreach (int requested in values)
        {
            foreach (int source in values)
            {
                foreach (int free in values)
                {
                    ClampResult clamp = AmountClamp.Clamp(requested, source, free, unit);
                    if (clamp.Reason == ClampReason.Ok)
                    {
                        Assert.True(clamp.Amount > 0);
                        Assert.Equal(0, clamp.Amount % unit);
                        Assert.True(clamp.Amount <= Math.Min(requested, Math.Min(source, free)));
                        Assert.True(clamp.Amount + unit > Math.Min(requested, Math.Min(source, free)), "largest whole-chunk amount");
                    }
                    else
                    {
                        Assert.Equal(0, clamp.Amount);
                        Assert.True(Math.Min(requested, Math.Min(source, free)) < unit);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Clamp_UnitBelowOne_Throws(int unit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AmountClamp.Clamp(5, 5, 5, unit));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RoundDownToChunk_UnitBelowOne_Throws(int unit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AmountClamp.RoundDownToChunk(5, unit));
    }

    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(9, 10, 0)]
    [InlineData(10, 10, 10)]
    [InlineData(53, 10, 50)]
    [InlineData(53, 1, 53)]
    [InlineData(int.MaxValue, 10, 2147483640)]
    public void RoundDownToChunk_RoundsDown(int amount, int unit, int expected)
    {
        Assert.Equal(expected, AmountClamp.RoundDownToChunk(amount, unit));
    }
}
