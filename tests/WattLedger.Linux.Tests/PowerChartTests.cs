using WattLedger.Desktop;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WattLedger.Linux;

namespace WattLedger.Linux.Tests;

public sealed class PowerChartTests
{
    [Fact]
    public void Short_sample_window_uses_data_range_and_keeps_point_spacing()
    {
        var first = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);
        var layout = PowerChartLayout.Calculate(
        [
            new PowerPoint(first, 4),
            new PowerPoint(first.AddSeconds(10), 6)
        ], 440, 160);

        var run = Assert.Single(layout.PowerRuns);
        Assert.Equal(2, run.Points.Count);
        Assert.InRange(run.Points[1].Position.X - run.Points[0].Position.X, 40, 100);
        Assert.True(layout.TimeStart < first);
        Assert.True(layout.TimeEnd > first.AddSeconds(10));
        Assert.NotEmpty(layout.TimeTicks);
    }

    [Fact]
    public void Expanded_time_window_does_not_compress_samples_to_midnight()
    {
        var first = DateTimeOffset.Now.Date.AddHours(18);
        var layout = PowerChartLayout.Calculate(
        [
            new PowerPoint(first, 2),
            new PowerPoint(first.AddMinutes(1), 3)
        ], 440, 160);

        var points = Assert.Single(layout.PowerRuns).Points;
        Assert.InRange(points[0].Position.X, layout.PlotLeft, layout.PlotLeft + 10);
        Assert.InRange(points[1].Position.X, layout.PlotRight - 10, layout.PlotRight);
    }

    [Fact]
    public void Negative_power_is_below_zero_and_both_signs_share_power_color_geometry()
    {
        var first = DateTimeOffset.Now;
        var layout = PowerChartLayout.Calculate(
        [
            new PowerPoint(first, 5),
            new PowerPoint(first.AddMinutes(1), -5)
        ], 440, 160);

        Assert.Equal(2, layout.PowerRuns.Count);
        Assert.True(layout.PowerRuns[0].Points[0].Position.Y < layout.ZeroY);
        Assert.True(layout.PowerRuns[1].Points[^1].Position.Y > layout.ZeroY);
        Assert.Contains(layout.PowerRuns.SelectMany(run => run.Points), point => point.Watts == 0);
    }

    [Fact]
    public void One_sided_power_uses_zero_at_the_nearest_edge()
    {
        var first = DateTimeOffset.Now;
        var positive = PowerChartLayout.Calculate(
        [new PowerPoint(first, 3), new PowerPoint(first.AddMinutes(1), 8)], 440, 160);
        var negative = PowerChartLayout.Calculate(
        [new PowerPoint(first, -3), new PowerPoint(first.AddMinutes(1), -8)], 440, 160);

        Assert.Equal(positive.PlotBottom, positive.ZeroY);
        Assert.Equal(0d, positive.PowerMinimum);
        Assert.True(positive.PowerMaximum > 8);
        Assert.Equal(negative.PlotTop, negative.ZeroY);
        Assert.Equal(0d, negative.PowerMaximum);
        Assert.True(negative.PowerMinimum < -8);
    }

    [Fact]
    public void Battery_percentage_maps_between_chart_edges_independently_of_power()
    {
        var first = DateTimeOffset.Now;
        var layout = PowerChartLayout.Calculate(
        [
            new PowerPoint(first, 2, 100),
            new PowerPoint(first.AddMinutes(1), 4, 0)
        ], 440, 160);
        var points = Assert.Single(layout.BatteryRuns);

        Assert.Equal(layout.PlotTop, points[0].Position.Y);
        Assert.Equal(layout.PlotBottom, points[1].Position.Y);
        Assert.Equal(layout.PlotBottom, layout.ZeroY);
    }

    [Fact]
    public void Sub_five_minute_axis_uses_distinct_second_precision_labels()
    {
        var first = new DateTimeOffset(2026, 10, 22, 8, 0, 1, TimeSpan.FromHours(8));
        var layout = PowerChartLayout.Calculate(
        [new PowerPoint(first, 1), new PowerPoint(first.AddSeconds(45), 2)], 440, 160);

        Assert.Equal(first.ToLocalTime().ToString("HH:mm:ss"), layout.TimeTicks[0].Label);
        Assert.Equal(first.AddSeconds(45).ToLocalTime().ToString("HH:mm:ss"), layout.TimeTicks[^1].Label);
        Assert.Equal(layout.TimeTicks.Count, layout.TimeTicks.Select(tick => tick.Label).Distinct().Count());
    }

    [Fact]
    public void Closely_spaced_sample_endpoints_do_not_produce_overlapping_time_labels()
    {
        var first = DateTimeOffset.Now;
        var layout = PowerChartLayout.Calculate([new(first, 1), new(first.AddSeconds(1), 2)], 440, 160);

        for (var i = 1; i < layout.TimeTicks.Count; i++)
            Assert.True(layout.TimeTicks[i].X - layout.TimeTicks[i - 1].X >= 54);
    }

    [Fact]
    public void Missing_samples_sleep_gaps_and_duplicate_timestamps_break_or_collapse_runs()
    {
        var first = DateTimeOffset.Now;
        var layout = PowerChartLayout.Calculate(
        [
            new PowerPoint(first, 1, 50),
            new PowerPoint(first, 2, 51),
            new PowerPoint(first.AddMinutes(1), null, null),
            new PowerPoint(first.AddMinutes(2), 3, 52),
            new PowerPoint(first.AddMinutes(20), 4, 53)
        ], 440, 160);

        Assert.Equal(3, layout.PowerRuns.Count);
        Assert.Equal(3, layout.BatteryRuns.Count);
        Assert.Single(layout.PowerRuns[0].Points);
        Assert.Equal(2, layout.PowerRuns[0].Points[0].Watts);
    }

    [Fact]
    public void Single_point_and_empty_series_have_valid_layouts()
    {
        var one = PowerChartLayout.Calculate([new PowerPoint(DateTimeOffset.Now, -2, 45)], 440, 160);
        var empty = PowerChartLayout.Calculate(Array.Empty<PowerPoint>(), 440, 160);

        Assert.Single(Assert.Single(one.PowerRuns).Points);
        Assert.Single(Assert.Single(one.BatteryRuns));
        Assert.Empty(empty.PowerRuns);
        Assert.Empty(empty.BatteryRuns);
        Assert.True(empty.TimeEnd > empty.TimeStart);
    }

    [AvaloniaFact]
    public void Narrow_chart_bounds_render_without_clamp_errors_during_resize()
    {
        foreach (var width in new[] { 30, 124, 149, 150, 175 })
        {
            var chart = new PowerChart
            {
                Width = width,
                Height = 160,
                Samples = [new PowerPoint(DateTimeOffset.Now, 4, 67)]
            };
            chart.Measure(new Size(width, 160));
            chart.Arrange(new Rect(0, 0, width, 160));
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, 160));

            bitmap.Render(chart);
        }
    }

    [Fact]
    public void Zoom_preserves_pointer_anchor_and_pan_stays_within_extent()
    {
        var first = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);
        var extent = new ChartTimeRange(first, first.AddHours(1));
        var zoomed = extent.Zoom(.5, .25, extent);

        Assert.Equal(1800, zoomed.Seconds);
        Assert.Equal(first.AddMinutes(15), zoomed.Start.AddSeconds(zoomed.Seconds * .25));
        Assert.Equal(extent.Start, zoomed.Pan(-10_000, extent).Start);
        Assert.Equal(extent.End, zoomed.Pan(10_000, extent).End);
        Assert.Equal(extent, zoomed.Zoom(100, .5, extent));
        Assert.Equal(10, zoomed.Zoom(.0001, .5, extent).Seconds);
    }

    [Fact]
    public void Selection_uses_time_weighted_power_and_interpolates_custom_boundaries()
    {
        var first = DateTimeOffset.Now;
        PowerPoint[] samples =
        [new(first, 0), new(first.AddSeconds(10), 20), new(first.AddSeconds(60), 20)];

        var all = ChartSeries.Summarize(samples, first, first.AddSeconds(60));
        var clipped = ChartSeries.Summarize(samples, first.AddSeconds(5), first.AddSeconds(15));

        Assert.Equal(1100d / 3600, all.NetEnergyWh, 9);
        Assert.Equal(1100d / 60, all.AverageWatts!.Value, 9);
        Assert.Equal(175d / 3600, clipped.NetEnergyWh, 9);
        Assert.Equal(10, clipped.CoveredSeconds);
        Assert.Equal(1, clipped.SampleCount);
        Assert.Equal(1, clipped.Coverage);
        Assert.Equal(10, clipped.MinimumWatts);
        Assert.Equal(20, clipped.MaximumWatts);
    }

    [Fact]
    public void Crossing_zero_counts_charge_and_discharge_energy_separately()
    {
        var first = DateTimeOffset.Now;
        var stats = ChartSeries.Summarize([new(first, 10), new(first.AddSeconds(60), -10)], first.AddSeconds(60), first);

        Assert.Equal(150d / 3600, stats.ChargeEnergyWh, 9);
        Assert.Equal(150d / 3600, stats.DischargeEnergyWh, 9);
        Assert.Equal(0, stats.NetEnergyWh);
        Assert.Equal(0, stats.AverageWatts);
        Assert.Equal(first, stats.Start);
    }

    [Fact]
    public void Missing_power_long_gaps_and_confirmed_short_sleep_are_excluded_from_coverage()
    {
        var first = DateTimeOffset.Now;
        PowerPoint[] samples =
        [
            new(first, 12), new(first.AddSeconds(30), 12),
            new(first.AddSeconds(40), null), new(first.AddSeconds(50), 12),
            new(first.AddSeconds(120), 12), new(first.AddSeconds(130), 12, BreakBefore: true),
            new(first.AddSeconds(140), 12)
        ];
        var stats = ChartSeries.Summarize(samples, first, first.AddSeconds(140));

        Assert.Equal(40, stats.CoveredSeconds);
        Assert.Equal(40d / 140, stats.Coverage, 9);
        Assert.Equal(12, stats.AverageWatts);
        Assert.Equal(12d * 40 / 3600, stats.NetEnergyWh, 9);
        var layout = PowerChartLayout.Calculate(samples, 440, 160);
        Assert.Equal(4, layout.PowerRuns.Count);
    }

    [Fact]
    public void Nonfinite_samples_remain_unavailable_and_do_not_poison_axes_or_statistics()
    {
        var first = DateTimeOffset.Now;
        var samples = ChartSeries.Normalize(
        [new(first, double.NaN), new(first.AddSeconds(10), double.PositiveInfinity), new(first.AddSeconds(20), 4)]);
        var layout = PowerChartLayout.Calculate(samples, 440, 160);
        var stats = ChartSeries.Summarize(samples, first, first.AddSeconds(20));

        Assert.True(double.IsFinite(layout.PowerMaximum));
        Assert.Single(Assert.Single(layout.PowerRuns).Points);
        Assert.Equal(0, stats.CoveredSeconds);
        Assert.Null(stats.AverageWatts);
    }

    [Fact]
    public void Duplicate_timestamp_retains_confirmed_sleep_boundary_from_any_row()
    {
        var first = DateTimeOffset.Now;
        var samples = ChartSeries.Normalize(
        [new(first, 10), new(first.AddSeconds(20), 8, BreakBefore: true), new(first.AddSeconds(20), 12)]);
        var layout = PowerChartLayout.Calculate(samples, 440, 160);
        var stats = ChartSeries.Summarize(samples, first, first.AddSeconds(20));

        Assert.Equal(2, samples.Length);
        Assert.True(samples[1].BreakBefore);
        Assert.Equal(12, samples[1].Watts);
        Assert.Equal(2, layout.PowerRuns.Count);
        Assert.Equal(0, stats.CoveredSeconds);
    }

    [Fact]
    public void Pixel_reduction_keeps_spikes_and_raw_selection_energy()
    {
        var first = DateTimeOffset.Now;
        var samples = Enumerable.Range(0, 20_000).Select(i => new PowerPoint(first.AddSeconds(i), i == 10_001 ? 100 : 2, 50)).ToArray();
        var layout = PowerChartLayout.Calculate(samples, 440, 160);
        var points = Assert.Single(layout.PowerRuns).Points;

        Assert.True(points.Count < 1600);
        Assert.Contains(points, point => point.Watts == 100);
        var stats = ChartSeries.Summarize(samples, first, samples[^1].Timestamp);
        Assert.Equal(20_000, stats.SampleCount);
        Assert.Equal((2d * 19_999 + 98) / 3600, stats.NetEnergyWh, 8);
    }

    [Fact]
    public void Viewport_interpolates_visible_edges_and_rescales_power_without_offscreen_peak()
    {
        var first = DateTimeOffset.Now;
        PowerPoint[] samples = [new(first, 100), new(first.AddSeconds(60), 10), new(first.AddSeconds(120), 20)];
        var layout = PowerChartLayout.Calculate(samples, 440, 160,
            viewport: new ChartTimeRange(first.AddSeconds(75), first.AddSeconds(105)));
        var run = Assert.Single(layout.PowerRuns);

        Assert.Equal(first.AddSeconds(75), layout.TimeStart);
        Assert.Equal(12.5, run.Points[0].Watts);
        Assert.Equal(17.5, run.Points[^1].Watts);
        Assert.True(layout.PowerMaximum < 30);
        Assert.Equal(layout.PlotLeft, run.Points[0].Position.X);
        Assert.Equal(layout.PlotRight, run.Points[^1].Position.X);
    }

    [Fact]
    public void Empty_gap_viewport_has_no_inspectable_point_and_nearest_stays_within_view()
    {
        var first = DateTimeOffset.Now;
        PowerPoint[] samples = [new(first, 4), new(first.AddHours(1), 6)];
        var gap = new ChartTimeRange(first.AddMinutes(20), first.AddMinutes(40));
        var layout = PowerChartLayout.Calculate(samples, 440, 160, viewport: gap);

        Assert.Null(ChartSeries.Nearest(samples, first.AddMinutes(30), gap));
        Assert.Empty(layout.PowerRuns);
        Assert.Null(layout.ActualStart);
        var edge = new ChartTimeRange(first.AddSeconds(10), first.AddHours(1));
        Assert.Equal(samples[1], ChartSeries.Nearest(samples, first.AddSeconds(10), edge));
    }

    [AvaloniaFact]
    public void Chart_selection_and_zoom_controls_keep_original_points_and_restore_full_range()
    {
        var first = DateTimeOffset.Now;
        var chart = new PowerChart
        {
            Width = 440,
            Height = 160,
            Samples = Enumerable.Range(0, 121).Select(i => new PowerPoint(first.AddSeconds(i), 12, 50)).ToArray()
        };
        chart.Measure(new Size(440, 160));
        chart.Arrange(new Rect(0, 0, 440, 160));
        var changes = 0;
        chart.SelectionChanged += (_, _) => changes++;
        chart.SelectRange(first.AddSeconds(10), first.AddSeconds(40));
        chart.ZoomToSelection();
        Assert.Equal(30, chart.VisibleRange.Seconds);
        Assert.Equal(31, chart.Selection!.SampleCount);
        Assert.Equal(.1, chart.Selection.NetEnergyWh, 9);
        chart.ResetView();
        Assert.Equal(120, chart.VisibleRange.Seconds);
        chart.ClearSelection();
        Assert.Null(chart.Selection);
        Assert.Equal(2, changes);
        Assert.Equal(121, chart.Samples.Count);
        using var bitmap = new RenderTargetBitmap(new PixelSize(440, 160));
        bitmap.Render(chart);
    }

    [AvaloniaFact]
    public void Pointer_wheel_drag_selection_and_middle_button_pan_work_together()
    {
        var first = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);
        var chart = new PowerChart
        {
            Samples = Enumerable.Range(0, 121).Select(i => new PowerPoint(first.AddSeconds(i), 12, 50)).ToArray()
        };
        var window = new Window { Width = 600, Height = 220, Content = chart };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            using var bitmap = new RenderTargetBitmap(new PixelSize(600, 220));
            bitmap.Render(window);
            var origin = chart.TranslatePoint(default, window)!.Value;
            var layout = PowerChartLayout.Calculate(chart.Samples, chart.Bounds.Width, chart.Bounds.Height);
            Point At(double fraction) => new(origin.X + layout.PlotLeft + (layout.PlotRight - layout.PlotLeft) * fraction, origin.Y + layout.PlotTop + 30);

            window.MouseWheel(At(.25), new Vector(0, 1));
            Assert.Equal(96, chart.VisibleRange.Seconds, 5);
            Assert.Equal(first.AddSeconds(30), chart.VisibleRange.Start.AddSeconds(chart.VisibleRange.Seconds * .25));
            chart.InteractionMode = ChartInteractionMode.Select;
            window.MouseDown(At(.25), MouseButton.Left);
            window.MouseMove(At(.75));
            window.MouseUp(At(.75), MouseButton.Left);
            Assert.Equal(first.AddSeconds(30), chart.Selection!.Start);
            Assert.Equal(first.AddSeconds(78), chart.Selection.End);
            Assert.Equal(49, chart.Selection.SampleCount);

            var beforePan = chart.VisibleRange;
            window.MouseDown(At(.5), MouseButton.Middle);
            window.MouseMove(At(.4));
            window.MouseUp(At(.4), MouseButton.Middle);
            Assert.True(chart.VisibleRange.Start > beforePan.Start);
            Assert.Equal(beforePan.Seconds, chart.VisibleRange.Seconds, 5);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Clicking_an_empty_gap_view_does_not_select_offscreen_samples()
    {
        var first = DateTimeOffset.Now;
        var chart = new PowerChart { InteractionMode = ChartInteractionMode.Select, Samples = [new(first, 4), new(first.AddHours(1), 6)] };
        var window = new Window { Width = 600, Height = 220, Content = chart };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            chart.SelectRange(first.AddMinutes(20), first.AddMinutes(40));
            chart.ZoomToSelection();
            using var bitmap = new RenderTargetBitmap(new PixelSize(600, 220));
            bitmap.Render(window);
            var origin = chart.TranslatePoint(default, window)!.Value;
            var point = new Point(origin.X + chart.Bounds.Width / 2, origin.Y + 60);

            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.Null(chart.Selection);
            Assert.Equal(1200, chart.VisibleRange.Seconds, 5);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Default_left_drag_and_horizontal_scroll_pan_without_zooming_first()
    {
        var first = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);
        var samples = Enumerable.Range(0, 721).Select(i => new PowerPoint(first.AddSeconds(i * 5), 12, 50)).ToArray();
        var chart = new PowerChart { Samples = samples };
        var window = new Window { Width = 600, Height = 220, Content = chart };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var origin = chart.TranslatePoint(default, window)!.Value;
            var layout = PowerChartLayout.Calculate(chart.Samples, chart.Bounds.Width, chart.Bounds.Height, viewport: chart.VisibleRange);
            Point At(double fraction) => new(origin.X + layout.PlotLeft + (layout.PlotRight - layout.PlotLeft) * fraction, origin.Y + layout.PlotTop + 30);
            Assert.Equal(ChartInteractionMode.Pan, chart.InteractionMode);
            Assert.True(chart.IsFollowingLive);
            Assert.Equal(first.AddMinutes(30), chart.VisibleRange.Start);
            Assert.True(chart.CanPanEarlier);
            Assert.False(chart.CanPanLater);

            window.MouseDown(At(.5), MouseButton.Left);
            window.MouseMove(At(.7));
            window.MouseUp(At(.7), MouseButton.Left);
            Assert.InRange((chart.VisibleRange.Start - first.AddMinutes(24)).TotalMilliseconds, -1, 1);
            Assert.Equal(1800, chart.VisibleRange.Seconds, 5);
            Assert.Null(chart.Selection);
            Assert.False(chart.IsFollowingLive);
            Assert.True(chart.CanPanLater);

            var beforeWheel = chart.VisibleRange;
            window.MouseWheel(At(.5), new Vector(1, .05));
            Assert.InRange((beforeWheel.Start - chart.VisibleRange.Start).TotalSeconds, 179.999, 180.001);
            Assert.Equal(beforeWheel.Seconds, chart.VisibleRange.Seconds, 5);

            var paused = chart.VisibleRange;
            chart.Samples = [.. samples, new(first.AddMinutes(61), 14, 49)];
            Assert.Equal(paused, chart.VisibleRange);
            chart.FollowLive();
            Assert.True(chart.IsFollowingLive);
            Assert.Equal(first.AddMinutes(61), chart.VisibleRange.End);
            Assert.Equal(1800, chart.VisibleRange.Seconds, 5);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Follow_grows_short_history_then_preserves_zoom_width_and_reset_exposes_boundaries()
    {
        var first = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);
        var chart = new PowerChart { Samples = [new(first, 4), new(first.AddMinutes(1), 6)] };
        Assert.Equal(60, chart.VisibleRange.Seconds);
        chart.Samples = [new(first, 4), new(first.AddMinutes(20), 6)];
        Assert.Equal(1200, chart.VisibleRange.Seconds);
        chart.Samples = [new(first, 4), new(first.AddHours(2), 6)];
        Assert.Equal(1800, chart.VisibleRange.Seconds);
        Assert.Equal(first.AddHours(2), chart.VisibleRange.End);
        chart.Zoom(.5);
        Assert.False(chart.IsFollowingLive);
        chart.FollowLive();
        Assert.Equal(900, chart.VisibleRange.Seconds);
        chart.Samples = [new(first, 4), new(first.AddHours(3), 6)];
        Assert.Equal(900, chart.VisibleRange.Seconds);
        Assert.Equal(first.AddHours(3), chart.VisibleRange.End);

        chart.Pan(-100_000);
        Assert.False(chart.CanPanEarlier);
        Assert.True(chart.CanPanLater);
        chart.ResetView();
        Assert.False(chart.IsFollowingLive);
        Assert.Equal(10800, chart.VisibleRange.Seconds);
        Assert.False(chart.CanPanEarlier);
        Assert.False(chart.CanPanLater);
        var fitSnapshot = chart.VisibleRange;
        chart.Samples = [new(first, 4), new(first.AddHours(3).AddMinutes(1), 6)];
        Assert.Equal(fitSnapshot, chart.VisibleRange);
        Assert.True(chart.CanPanLater);
        chart.ResetView();
        chart.FollowLive();
        Assert.Equal(1800, chart.VisibleRange.Seconds);

        chart.Pan(-500);
        chart.Samples = [new(first.AddDays(1), 4), new(first.AddDays(1).AddHours(1), 6)];
        Assert.True(chart.IsFollowingLive);
        Assert.Equal(first.AddDays(1).AddMinutes(30), chart.VisibleRange.Start);
    }

}
