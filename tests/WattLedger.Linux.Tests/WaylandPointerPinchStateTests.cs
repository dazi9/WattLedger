using Avalonia.Wayland.Server.Transient;

namespace WattLedger.Linux.Tests;

public sealed class WaylandPointerPinchStateTests
{
    [Fact]
    public void CumulativeScaleIsConvertedToIncrementalMagnification()
    {
        var state = new WaylandPointerPinchState();
        state.Begin();
        Assert.True(state.TryUpdate(1.25, out var first));
        Assert.True(state.TryUpdate(1.5, out var second));
        Assert.Equal(.25, first, 12);
        Assert.Equal(.2, second, 12);
        Assert.Equal(1.5, (1 + first) * (1 + second), 12);
    }

    [Fact]
    public void EndAndCancelDropLateUpdatesAndNextBeginResetsBaseline()
    {
        var state = new WaylandPointerPinchState();
        Assert.False(state.TryUpdate(2, out _));
        state.Begin();
        Assert.True(state.IsActive);
        Assert.True(state.TryUpdate(2, out _));
        state.End();
        Assert.False(state.IsActive);
        Assert.False(state.TryUpdate(4, out _));
        state.Begin();
        Assert.True(state.TryUpdate(1.1, out var delta));
        Assert.Equal(.1, delta, 12);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidPacketsDoNotPoisonTheScaleBaseline(double invalid)
    {
        var state = new WaylandPointerPinchState();
        state.Begin();
        Assert.True(state.TryUpdate(1.25, out _));
        Assert.False(state.TryUpdate(invalid, out _));
        Assert.True(state.TryUpdate(1.5, out var delta));
        Assert.Equal(.2, delta, 12);
    }

    [Fact]
    public void TranslationOnlyPacketsDoNotProduceMagnification()
    {
        var state = new WaylandPointerPinchState();
        state.Begin();
        Assert.False(state.TryUpdate(1, out _));
        Assert.True(state.TryUpdate(.8, out var delta));
        Assert.Equal(-.2, delta, 12);
        Assert.False(state.TryUpdate(.8, out _));
    }
}
