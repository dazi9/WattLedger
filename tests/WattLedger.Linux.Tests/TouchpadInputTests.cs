using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using WattLedger.Desktop;
using WattLedger.Linux;

namespace WattLedger.Linux.Tests;

public sealed class TouchpadInputTests
{
    [AvaloniaFact]
    public void Fractional_scroll_and_control_horizontal_scroll_zoom_at_the_pointer()
    {
        var (window, chart) = OpenChart();
        try
        {
            var point = PlotPoint(window, chart, .25);
            var initial = chart.VisibleRange;
            var anchoredTime = initial.Start.AddSeconds(initial.Seconds * .25);
            for (var i = 0; i < 10; i++) window.MouseWheel(point, new Vector(0, .1));
            Assert.Equal(initial.Seconds * .8, chart.VisibleRange.Seconds, 5);
            Assert.InRange(Math.Abs((chart.VisibleRange.Start.AddSeconds(chart.VisibleRange.Seconds * .25) - anchoredTime).TotalMilliseconds), 0, .01);
            Assert.False(chart.IsFollowingLive);

            var beforeControl = chart.VisibleRange;
            window.MouseWheel(point, new Vector(.25, 0), RawInputModifiers.Control);
            Assert.Equal(beforeControl.Seconds * Math.Pow(.8, .25), chart.VisibleRange.Seconds, 5);

            var beforePan = chart.VisibleRange;
            window.MouseWheel(point, new Vector(.25, .01));
            Assert.Equal(beforePan.Seconds, chart.VisibleRange.Seconds, 5);
            Assert.True(chart.VisibleRange.Start < beforePan.Start);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Native_magnify_uses_relative_delta_and_does_not_repeat_its_compatibility_wheel()
    {
        var (window, chart) = OpenChart();
        try
        {
            using var pointer = new Pointer(12001, PointerType.Mouse, true);
            var properties = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other);
            var point = PlotPoint(window, chart, .5);
            var initial = chart.VisibleRange;
            var magnify = new PointerDeltaEventArgs(InputElement.PointerTouchPadGestureMagnifyEvent,
                chart, pointer, window, point, 123, properties, KeyModifiers.None, new Vector(.1, .1));
            chart.RaiseEvent(magnify);
            Assert.True(magnify.Handled);
            Assert.Equal(initial.Seconds / 1.1, chart.VisibleRange.Seconds, 5);
            var afterMagnify = chart.VisibleRange;
            var duplicate = new PointerWheelEventArgs(chart, pointer, window, point, 123,
                properties, KeyModifiers.None, new Vector(0, 1));
            chart.RaiseEvent(duplicate);
            Assert.True(duplicate.Handled);
            Assert.Equal(afterMagnify, chart.VisibleRange);

            var invalid = new PointerDeltaEventArgs(InputElement.PointerTouchPadGestureMagnifyEvent,
                chart, pointer, window, point, 124, properties, KeyModifiers.None, new Vector(double.NaN, double.NaN));
            chart.RaiseEvent(invalid);
            Assert.Equal(afterMagnify, chart.VisibleRange);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Two_contact_pinch_flows_through_recognizer_without_compounding_cumulative_scale()
    {
        var (window, chart) = OpenChart();
        try
        {
            chart.InteractionMode = ChartInteractionMode.Select;
            chart.SelectRange(chart.Samples[400].Timestamp, chart.Samples[500].Timestamp);
            var selectionBeforePinch = chart.Selection;
            var firstPosition = PlotPoint(window, chart, .3);
            var secondPosition = PlotPoint(window, chart, .7);
            var initial = chart.VisibleRange;
            using var first = window.TouchBegin(firstPosition);
            using var second = window.TouchBegin(secondPosition);
            window.TouchMove(second, PlotPoint(window, chart, .9));
            Assert.Equal(initial.Seconds / 1.5, chart.VisibleRange.Seconds, 3);
            window.TouchMove(second, PlotPoint(window, chart, 1));
            Assert.Equal(initial.Seconds / 1.75, chart.VisibleRange.Seconds, 3);
            Assert.Equal(selectionBeforePinch, chart.Selection);
            Assert.False(chart.IsFollowingLive);
            window.TouchEnd(second, PlotPoint(window, chart, 1));
            window.TouchEnd(first, firstPosition);

            var afterPinch = chart.VisibleRange;
            window.MouseWheel(PlotPoint(window, chart, .5), new Vector(0, .1));
            Assert.True(chart.VisibleRange.Seconds < afterPinch.Seconds);
            window.MouseDown(PlotPoint(window, chart, .4), MouseButton.Left);
            window.MouseMove(PlotPoint(window, chart, .6));
            window.MouseUp(PlotPoint(window, chart, .6), MouseButton.Left);
            Assert.NotEqual(selectionBeforePinch, chart.Selection);
            Assert.NotNull(chart.Selection);
        }
        finally { window.Close(); }
    }

    private static (Window Window, PowerChart Chart) OpenChart()
    {
        var start = new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);
        var chart = new PowerChart
        {
            Samples = Enumerable.Range(0, 721).Select(i => new PowerPoint(start.AddSeconds(i * 5), 12, 50)).ToArray()
        };
        var window = new Window { Width = 600, Height = 220, Content = chart };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, chart);
    }

    private static Point PlotPoint(Window window, PowerChart chart, double fraction)
    {
        var origin = chart.TranslatePoint(default, window)!.Value;
        var layout = PowerChartLayout.Calculate(chart.Samples, chart.Bounds.Width, chart.Bounds.Height, viewport: chart.VisibleRange);
        return new(origin.X + layout.PlotLeft + (layout.PlotRight - layout.PlotLeft) * fraction, origin.Y + layout.PlotTop + 30);
    }
}
