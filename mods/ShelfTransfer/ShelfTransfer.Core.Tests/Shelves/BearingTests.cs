// purpose: pins the 8-point compass mapping, sector boundaries, and the undefined-direction cases
// owns:    BearingTests
// deps:    Bearing, WorldPosition

using System;

using ShelfTransfer.Core.Gateway;
using ShelfTransfer.Core.Shelves;

using Xunit;

namespace ShelfTransfer.Core.Tests.Shelves;

public sealed class BearingTests
{
    private static readonly WorldPosition _origin = new(0f, 0f, 0f);

    [Theory]
    [InlineData(0f, 5f, "N")]
    [InlineData(5f, 5f, "NE")]
    [InlineData(5f, 0f, "E")]
    [InlineData(5f, -5f, "SE")]
    [InlineData(0f, -5f, "S")]
    [InlineData(-5f, -5f, "SW")]
    [InlineData(-5f, 0f, "W")]
    [InlineData(-5f, 5f, "NW")]
    public void Format_MapsKnownVectorsToCompassPoints(float east, float north, string expected)
    {
        Assert.Equal(expected, Bearing.Format(_origin, new WorldPosition(east, 0f, north)));
    }

    [Fact]
    public void Format_ZeroDistance_ReturnsNoneWithoutThrowing()
    {
        Assert.Equal(Bearing.None, Bearing.Format(_origin, _origin));
        Assert.Equal(Bearing.None, string.Empty);
    }

    [Fact]
    public void Format_BelowMinHorizontalDistance_ReturnsNone()
    {
        Assert.Equal(Bearing.None, Bearing.Format(_origin, new WorldPosition(0.005f, 0f, 0.005f)));
    }

    [Fact]
    public void Format_AtOrAboveMinHorizontalDistance_ReturnsDirection()
    {
        Assert.Equal("E", Bearing.Format(_origin, new WorldPosition(0.02f, 0f, 0f)));
    }

    [Fact]
    public void Format_VerticalOnlyDifference_ReturnsNone()
    {
        Assert.Equal(Bearing.None, Bearing.Format(_origin, new WorldPosition(0f, 50f, 0f)));
    }

    [Fact]
    public void Format_HeightDifferenceDoesNotChangeBearing()
    {
        string level = Bearing.Format(_origin, new WorldPosition(3f, 0f, 3f));
        string above = Bearing.Format(_origin, new WorldPosition(3f, 100f, 3f));
        string below = Bearing.Format(_origin, new WorldPosition(3f, -100f, 3f));

        Assert.Equal("NE", level);
        Assert.Equal(level, above);
        Assert.Equal(level, below);
    }

    [Fact]
    public void Format_IsRelativeToFromPosition()
    {
        Assert.Equal("S", Bearing.Format(new WorldPosition(10f, 0f, 10f), new WorldPosition(10f, 0f, 4f)));
    }

    // tan(22.5 deg) = 0.41421356; nudge either side of each boundary (float cannot hit it exactly).
    [Theory]
    [InlineData(0.4142f, 1f, "N")]
    [InlineData(0.4143f, 1f, "NE")]
    [InlineData(1f, 0.4143f, "NE")]
    [InlineData(1f, 0.4142f, "E")]
    [InlineData(1f, -0.4142f, "E")]
    [InlineData(1f, -0.4143f, "SE")]
    [InlineData(0.4143f, -1f, "SE")]
    [InlineData(0.4142f, -1f, "S")]
    [InlineData(-0.4142f, -1f, "S")]
    [InlineData(-0.4143f, -1f, "SW")]
    [InlineData(-1f, -0.4143f, "SW")]
    [InlineData(-1f, -0.4142f, "W")]
    [InlineData(-1f, 0.4142f, "W")]
    [InlineData(-1f, 0.4143f, "NW")]
    [InlineData(-0.4143f, 1f, "NW")]
    [InlineData(-0.4142f, 1f, "N")]
    public void Format_SectorBoundaries_SplitAtTwentyTwoPointFiveDegrees(float east, float north, string expected)
    {
        Assert.Equal(expected, Bearing.Format(_origin, new WorldPosition(east, 0f, north)));
    }

    [Fact]
    public void Format_ExactBoundary_IsOneOfTheTwoAdjacentPoints()
    {
        // Float cannot represent tan(22.5 deg) exactly, so the exact clockwise tie-break is not assertable here.
        string result = Bearing.Format(_origin, new WorldPosition((float)Math.Tan(Math.PI / 8), 0f, 1f));

        Assert.Contains(result, new[] { "N", "NE" });
    }

    [Theory]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(0f, 0f, float.NaN)]
    [InlineData(float.PositiveInfinity, 0f, 1f)]
    [InlineData(1f, 0f, float.NegativeInfinity)]
    public void Format_NonFiniteTarget_ReturnsNone(float x, float y, float z)
    {
        Assert.Equal(Bearing.None, Bearing.Format(_origin, new WorldPosition(x, y, z)));
    }

    [Fact]
    public void Format_NonFiniteFrom_ReturnsNone()
    {
        Assert.Equal(Bearing.None, Bearing.Format(new WorldPosition(float.NaN, 0f, 0f), new WorldPosition(1f, 0f, 1f)));
    }

    [Fact]
    public void Format_ExtremeFiniteSpan_DoesNotThrow()
    {
        string result = Bearing.Format(new WorldPosition(float.MinValue, 0f, 0f), new WorldPosition(float.MaxValue, 0f, 0f));

        Assert.Equal("E", result);
    }
}
