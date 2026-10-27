using System.Runtime.InteropServices;
using WattLedger.App;

namespace WattLedger.App.Tests;

public sealed class WindowsChartGestureTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(120)]
    public void NativeWheelFlagsDoNotDependOnGlobalKeyboardState(short delta)
    {
        var packet = new IntPtr(((long)(ushort)delta << 16) | 0x000C);
        Assert.True(WindowsChartGestureInterop.IsControlDown(packet));
        Assert.True(WindowsChartGestureInterop.IsShiftDown(packet));
        Assert.Equal(delta / 120d, WindowsChartGestureInterop.WheelSteps(packet), 12);
    }

    [Fact]
    public void NativeScreenCoordinatesRetainNegativeMonitorPositions()
    {
        var packet = new IntPtr(((long)(ushort)120 << 16) | unchecked((ushort)-1400));
        Assert.Equal(((short)-1400, (short)120), WindowsChartGestureInterop.ScreenPoint(packet));
    }

    [Fact]
    public void GestureInfoMatchesNativePointerAlignment()
    {
        Assert.Equal(IntPtr.Size == 8 ? 56 : 48, Marshal.SizeOf<WindowsChartGestureInterop.GestureInfo>());
        Assert.Equal(IntPtr.Size == 8 ? 40 : 32,
            Marshal.OffsetOf<WindowsChartGestureInterop.GestureInfo>(nameof(WindowsChartGestureInterop.GestureInfo.Arguments)).ToInt32());
    }

    [Fact]
    public void NativeZoomStartsWithoutJumpAndAppliesOnlyIncrementalDistance()
    {
        var session = new WindowsChartZoomSession();
        Assert.True(session.NativeZoom(1, WindowsChartGestureInterop.Begin, 100, true, out var begin));
        Assert.Equal(1, begin);
        Assert.True(session.NativeZoom(1, 0, 120, false, out var first));
        Assert.True(session.NativeZoom(1, WindowsChartGestureInterop.End, 150, false, out var last));
        Assert.Equal(2d / 3, first * last, 12);
        Assert.False(session.IsActive);
    }

    [Fact]
    public void AnOutsideGestureIsNotOwnedAndANewSequenceResetsTheBaseline()
    {
        var session = new WindowsChartZoomSession();
        Assert.False(session.NativeZoom(1, WindowsChartGestureInterop.Begin, 100, false, out _));
        Assert.False(session.IsActive);
        Assert.True(session.NativeZoom(2, 0, 200, true, out var begin));
        Assert.Equal(1, begin);
        Assert.True(session.NativeZoom(3, 0, 400, true, out var next));
        Assert.Equal(1, next);
        session.EndNative();
        Assert.False(session.IsActive);
    }

    [Fact]
    public void DirectTouchAndNativeGestureCannotApplyTheSameScaleTwice()
    {
        var session = new WindowsChartZoomSession();
        Assert.True(session.BeginManipulation());
        Assert.True(session.NativeZoom(1, 0, 200, true, out var ignored));
        Assert.Equal(1, ignored);
        Assert.True(session.IsManipulationActive);
        session.EndManipulation();
        Assert.True(session.NativeZoom(2, WindowsChartGestureInterop.Begin, 200, true, out _));
        Assert.False(session.BeginManipulation());
        session.Reset();
        Assert.False(session.IsActive);
    }

    [Theory]
    [InlineData(1, 4, .5)]
    [InlineData(4, 1, .5)]
    [InlineData(2, 2, .5)]
    [InlineData(-2, -2, 1)]
    [InlineData(0, 2, 1)]
    [InlineData(double.NaN, 2, 1)]
    public void DirectTouchScaleUsesBothAxesAndRejectsInvalidValues(double x, double y, double expected) =>
        Assert.Equal(expected, WindowsChartGestureInterop.ManipulationFactor(x, y), 12);
}
