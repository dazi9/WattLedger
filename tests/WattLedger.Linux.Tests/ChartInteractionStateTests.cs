using WattLedger.Desktop;

namespace WattLedger.Linux.Tests;

public sealed class ChartInteractionStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    /// <summary>61 samples every 60 s over one hour: extent is exactly [T0, T0+3600 s].</summary>
    private static PowerPoint[] Hour(double watts = 10) =>
        Enumerable.Range(0, 61).Select(i => new PowerPoint(T0.AddSeconds(i * 60), watts)).ToArray();

    [Fact]
    public void Initial_state_follows_live_and_shows_full_extent()
    {
        var samples = Hour();
        var state = new ChartInteractionState();
        Assert.True(state.IsFollowingLive);
        Assert.Null(state.Viewport);
        var extent = ChartSeries.Extent(samples);
        Assert.Equal(extent, state.VisibleRange(samples));
    }

    [Fact]
    public void Sample_change_while_following_keeps_latest_half_hour_window()
    {
        var state = new ChartInteractionState();
        state.OnSamplesChanged(Hour());
        Assert.True(state.IsFollowingLive);
        Assert.Equal(T0.AddSeconds(1800), state.Viewport!.Value.Start);
        Assert.Equal(T0.AddSeconds(3600), state.Viewport.Value.End);
    }

    [Fact]
    public void Pan_exits_follow_live_and_clamps_to_extent()
    {
        var samples = Hour();
        var state = new ChartInteractionState();
        state.OnSamplesChanged(samples);
        state.Pan(samples, 7200);
        Assert.False(state.IsFollowingLive);
        Assert.Equal(T0.AddSeconds(1800), state.Viewport!.Value.Start);
        Assert.Equal(T0.AddSeconds(3600), state.Viewport.Value.End);
    }

    [Fact]
    public void Pan_ignores_non_finite_seconds()
    {
        var samples = Hour();
        var state = new ChartInteractionState();
        state.OnSamplesChanged(samples);
        var before = state.Viewport;
        state.Pan(samples, double.NaN);
        state.Pan(samples, double.PositiveInfinity);
        Assert.Equal(before, state.Viewport);
    }

    [Fact]
    public void Zoom_anchors_at_the_cursor_fraction()
    {
        var samples = Hour();
        var state = new ChartInteractionState();
        state.OnSamplesChanged(samples);
        state.ZoomAt(samples, .5, 0);
        Assert.Equal(T0.AddSeconds(1800), state.Viewport!.Value.Start);
        Assert.Equal(T0.AddSeconds(2700), state.Viewport.Value.End);
        state.FollowLive(samples);
        Assert.Equal(T0.AddSeconds(2700), state.Viewport!.Value.Start);
        Assert.Equal(T0.AddSeconds(3600), state.Viewport.Value.End);
        state.ZoomAt(samples, .5, 1);
        Assert.Equal(T0.AddSeconds(3150), state.Viewport!.Value.Start);
        Assert.Equal(T0.AddSeconds(3600), state.Viewport.Value.End);
    }

    [Fact]
    public void Follow_live_restores_the_latest_window_after_manual_navigation()
    {
        var samples = Hour();
        var state = new ChartInteractionState();
        state.OnSamplesChanged(samples);
        state.Pan(samples, -600);
        Assert.False(state.IsFollowingLive);
        state.FollowLive(samples);
        Assert.True(state.IsFollowingLive);
        Assert.Equal(T0.AddSeconds(1800), state.Viewport!.Value.Start);
        Assert.Equal(T0.AddSeconds(3600), state.Viewport.Value.End);
    }

    [Fact]
    public void Extent_jump_re_engages_follow_live()
    {
        var state = new ChartInteractionState();
        state.OnSamplesChanged(Hour());
        var day = T0.AddDays(1);
        var far = Enumerable.Range(0, 61).Select(i => new PowerPoint(day.AddSeconds(i * 60), 10)).ToArray();
        state.OnSamplesChanged(far);
        Assert.True(state.IsFollowingLive);
        Assert.Equal(day.AddSeconds(1800), state.Viewport!.Value.Start);
        Assert.Equal(day.AddSeconds(3600), state.Viewport.Value.End);
    }

    [Fact]
    public void Reset_view_shows_the_full_extent_without_following()
    {
        var samples = Hour();
        var state = new ChartInteractionState();
        state.OnSamplesChanged(samples);
        state.ResetView(samples);
        Assert.False(state.IsFollowingLive);
        Assert.Equal(T0, state.Viewport!.Value.Start);
        Assert.Equal(T0.AddSeconds(3600), state.Viewport.Value.End);
    }

    [Fact]
    public void Selection_anchor_extends_over_drag_and_zooms_to_range()
    {
        var samples = Hour();
        var state = new ChartInteractionState();
        state.OnSamplesChanged(samples);
        state.BeginSelection(samples, T0.AddSeconds(600));
        Assert.Equal(T0.AddSeconds(600), state.Selection!.Start);
        Assert.Equal(T0.AddSeconds(600), state.Selection.End);
        Assert.True(state.ExtendSelection(samples, T0.AddSeconds(1200)));
        Assert.Equal(T0.AddSeconds(600), state.Selection!.Start);
        Assert.Equal(T0.AddSeconds(1200), state.Selection.End);
        Assert.Equal(11, state.Selection.SampleCount);
        state.ZoomToSelection(samples);
        Assert.False(state.IsFollowingLive);
        Assert.Equal(T0.AddSeconds(600), state.Viewport!.Value.Start);
        Assert.Equal(T0.AddSeconds(1200), state.Viewport.Value.End);
    }

    [Fact]
    public void Extend_selection_without_anchor_is_rejected()
    {
        var samples = Hour();
        var state = new ChartInteractionState();
        Assert.False(state.ExtendSelection(samples, T0.AddSeconds(600)));
        Assert.Null(state.Selection);
    }

    [Fact]
    public void Selection_is_dropped_when_new_samples_no_longer_overlap()
    {
        var samples = Hour();
        var state = new ChartInteractionState();
        state.OnSamplesChanged(samples);
        state.BeginSelection(samples, T0.AddSeconds(600));
        Assert.NotNull(state.Selection);
        var day = T0.AddDays(1);
        state.OnSamplesChanged([new PowerPoint(day, 10)]);
        Assert.Null(state.Selection);
    }

    [Fact]
    public void Pan_mode_decision_covers_mode_middle_and_shift()
    {
        var state = new ChartInteractionState();
        Assert.True(state.DecidePan(ChartInteractionMode.Pan, middleButton: false, shift: false));
        Assert.True(state.DecidePan(ChartInteractionMode.Select, middleButton: true, shift: false));
        Assert.True(state.DecidePan(ChartInteractionMode.Select, middleButton: false, shift: true));
        Assert.False(state.DecidePan(ChartInteractionMode.Select, middleButton: false, shift: false));
    }

    [Fact]
    public void Drag_panning_moves_from_the_drag_origin_not_the_current_view()
    {
        var samples = Hour();
        var state = new ChartInteractionState();
        state.OnSamplesChanged(samples);
        var dragRange = state.Viewport!.Value;
        state.Pan(samples, -600);
        Assert.NotEqual(dragRange, state.Viewport);
        state.PanFrom(samples, dragRange, -60);
        Assert.Equal(dragRange.Pan(-60, ChartSeries.Extent(samples)), state.Viewport);
    }

    [Fact]
    public void Drag_origin_pan_keeps_follow_live_until_the_control_stops_it()
    {
        var samples = Hour();
        var state = new ChartInteractionState();
        state.OnSamplesChanged(samples);
        var dragRange = state.Viewport!.Value;
        // A press without movement (a plain click) must not pause follow-live…
        state.PanFrom(samples, dragRange, 0);
        Assert.True(state.IsFollowingLive);
        // …the control stops it explicitly once the pointer actually moves.
        state.StopFollowingLive();
        state.PanFrom(samples, dragRange, -60);
        Assert.False(state.IsFollowingLive);
        Assert.Equal(dragRange.Pan(-60, ChartSeries.Extent(samples)), state.Viewport);
    }
}
