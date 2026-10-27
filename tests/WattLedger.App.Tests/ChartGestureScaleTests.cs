using WattLedger.Desktop;

namespace WattLedger.App.Tests;

public sealed class ChartGestureScaleTests
{
    [Theory]
    [InlineData(.01)]
    [InlineData(-.01)]
    public void FractionalWheelInputIsNotRoundedAway(double steps)
    {
        Assert.Equal(Math.Pow(.8, steps), ChartGestureScale.WheelFactor(steps), 12);
        Assert.NotEqual(1, ChartGestureScale.WheelFactor(steps));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidWheelInputDoesNothing(double steps) => Assert.Equal(1, ChartGestureScale.WheelFactor(steps));

    [Theory]
    [InlineData(2, .5)]
    [InlineData(.5, 2)]
    [InlineData(100, .2)]
    [InlineData(.001, 5)]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(double.NaN, 1)]
    public void FingerExpansionShrinksTheTimeWindow(double scale, double factor) =>
        Assert.Equal(factor, ChartGestureScale.MagnificationFactor(scale), 12);

    [Fact]
    public void CumulativePacketsAreAppliedIncrementally()
    {
        var session = new ChartScaleSession();
        session.Begin(100);
        var factor = session.Update(120) * session.Update(150);
        Assert.Equal(2d / 3, factor, 12);
    }

    [Fact]
    public void InvalidPacketDoesNotPoisonBaselineAndNewGestureDoesNotJump()
    {
        var session = new ChartScaleSession();
        session.Begin(100);
        Assert.Equal(1, session.Update(double.NaN));
        Assert.Equal(1, session.Update(0));
        Assert.Equal(2d / 3, session.Update(150), 12);
        session.End();
        Assert.Equal(1, session.Update(300));
        Assert.Equal(.5, session.Update(600));
    }
}
