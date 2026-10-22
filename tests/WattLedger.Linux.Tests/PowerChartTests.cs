using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
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
}
