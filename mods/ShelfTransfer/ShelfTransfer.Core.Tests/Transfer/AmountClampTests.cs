// purpose: pins AmountClamp's min rule, All sentinel, reason precedence and overflow safety (invariant 4)
// owns:    AmountClampTests
// deps:    AmountClamp, ClampResult, ClampReason

using System;
using System.Linq;
using System.Reflection;

using ShelfTransfer.Core.Transfer;

using Xunit;

namespace ShelfTransfer.Core.Tests.Transfer;

public sealed class AmountClampTests
{
    // Criterion 1
    [Theory]
    [InlineData(6, 6, 4, 4)]
    [InlineData(3, 6, 6, 3)]
    [InlineData(9, 5, 7, 5)]
    [InlineData(1, 1, 1, 1)]
    [InlineData(5, 5, 5, 5)]
    public void Clamp_PositiveInputs_ReturnsMinOfThree(int requested, int source, int free, int expected)
    {
        ClampResult result = AmountClamp.Clamp(requested, source, free);

        Assert.Equal(expected, result.Amount);
        Assert.Equal(ClampReason.Ok, result.Reason);
    }

    // Criterion 2
    [Fact]
    public void All_IsIntMaxValue()
    {
        Assert.Equal(int.MaxValue, AmountClamp.All);
    }

    [Theory]
    [InlineData(6, 4, 4)]
    [InlineData(4, 6, 4)]
    [InlineData(5, 5, 5)]
    [InlineData(1, int.MaxValue, 1)]
    public void Clamp_All_ResolvesToMinOfSourceAndFree(int source, int free, int expected)
    {
        ClampResult result = AmountClamp.Clamp(AmountClamp.All, source, free);

        Assert.Equal(expected, result.Amount);
        Assert.Equal(ClampReason.Ok, result.Reason);
    }

    // Criterion 3
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Clamp_SourceZeroOrLess_ReturnsNothingToMove(int source)
    {
        ClampResult result = AmountClamp.Clamp(5, source, 5);

        Assert.Equal(0, result.Amount);
        Assert.Equal(ClampReason.NothingToMove, result.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Clamp_FreeZeroOrLess_ReturnsTargetFull(int free)
    {
        ClampResult result = AmountClamp.Clamp(5, 5, free);

        Assert.Equal(0, result.Amount);
        Assert.Equal(ClampReason.TargetFull, result.Reason);
    }

    [Fact]
    public void Clamp_SourceAndFreeBothEmpty_NothingToMoveWinsOverTargetFull()
    {
        ClampResult result = AmountClamp.Clamp(5, 0, 0);

        Assert.Equal(0, result.Amount);
        Assert.Equal(ClampReason.NothingToMove, result.Reason);
    }

    // Criterion 4
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Clamp_RequestedZeroOrNegative_ReturnsInvalidAmount(int requested)
    {
        ClampResult result = AmountClamp.Clamp(requested, 5, 5);

        Assert.Equal(0, result.Amount);
        Assert.Equal(ClampReason.InvalidAmount, result.Reason);
    }

    [Fact]
    public void Clamp_InvalidAmountWinsOverEveryOtherReason()
    {
        ClampResult result = AmountClamp.Clamp(0, 0, 0);

        Assert.Equal(0, result.Amount);
        Assert.Equal(ClampReason.InvalidAmount, result.Reason);
    }

    [Theory]
    [InlineData(-5, -5, -5)]
    [InlineData(int.MinValue, int.MinValue, int.MinValue)]
    [InlineData(-1, 5, 5)]
    [InlineData(5, -1, 5)]
    [InlineData(5, 5, -1)]
    [InlineData(0, int.MaxValue, int.MaxValue)]
    public void Clamp_AnyNonPositiveInput_NeverReturnsNegativeAmount(int requested, int source, int free)
    {
        ClampResult result = AmountClamp.Clamp(requested, source, free);

        Assert.Equal(0, result.Amount);
        Assert.NotEqual(ClampReason.Ok, result.Reason);
    }

    // Criterion 5
    [Fact]
    public void Clamp_AllInputsIntMaxValue_ReturnsIntMaxValueWithoutOverflow()
    {
        ClampResult result = AmountClamp.Clamp(int.MaxValue, int.MaxValue, int.MaxValue);

        Assert.Equal(int.MaxValue, result.Amount);
        Assert.Equal(ClampReason.Ok, result.Reason);
    }

    [Theory]
    [InlineData(int.MaxValue, int.MaxValue, 1, 1)]
    [InlineData(int.MaxValue, 1, int.MaxValue, 1)]
    [InlineData(1, int.MaxValue, int.MaxValue, 1)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue - 1, int.MaxValue - 1)]
    public void Clamp_IntMaxValueMixedInputs_ReturnsMin(int requested, int source, int free, int expected)
    {
        ClampResult result = AmountClamp.Clamp(requested, source, free);

        Assert.Equal(expected, result.Amount);
        Assert.Equal(ClampReason.Ok, result.Reason);
    }

    // Criterion 6
    [Fact]
    public void AmountClamp_IsStaticClass()
    {
        Type type = typeof(AmountClamp);

        Assert.True(type.IsAbstract && type.IsSealed);
    }

    [Fact]
    public void AmountClamp_ExposesNoGatewayTypeInAnyMemberOrParameter()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        const string gatewayNamespace = "ShelfTransfer.Core.Gateway";
        Type type = typeof(AmountClamp);

        var used = type.GetMethods(all).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType))
            .Concat(type.GetFields(all).Select(f => f.FieldType))
            .Concat(type.GetProperties(all).Select(p => p.PropertyType))
            .ToList();

        Assert.DoesNotContain(used, t => t.Namespace == gatewayNamespace);
        Assert.DoesNotContain(type.GetFields(all), f => !f.IsLiteral);
    }
}
