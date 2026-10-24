namespace WattLedger.Desktop;

/// <summary>UI-free interaction state machine for the power chart: viewport (follow-live, pan, zoom)
/// and selection transitions, shared by the desktop front ends and testable without any UI framework.</summary>
internal sealed class ChartInteractionState
{
    private const double DefaultFollowSeconds = 1800;

    private ChartTimeRange? _viewport;
    private DateTimeOffset? _selectionAnchor;
    private double _followSeconds = DefaultFollowSeconds;

    public bool IsFollowingLive { get; private set; } = true;
    public ChartSelectionStatistics? Selection { get; private set; }
    public ChartTimeRange? Viewport => _viewport;
    public ChartTimeRange VisibleRange(IReadOnlyList<PowerPoint> samples) => _viewport ?? ChartSeries.Extent(samples);

    public bool DecidePan(ChartInteractionMode mode, bool middleButton, bool shift) =>
        mode == ChartInteractionMode.Pan || middleButton || shift;

    /// <summary>Stops following live data without moving the viewport (drag start).</summary>
    public void StopFollowingLive() => IsFollowingLive = false;

    /// <summary>Adjusts the viewport and selection after the (already normalized) sample set changed.</summary>
    public void OnSamplesChanged(IReadOnlyList<PowerPoint> samples)
    {
        var extent = ChartSeries.Extent(samples);
        if (_viewport is { } prior && (prior.End < extent.Start || prior.Start > extent.End))
        {
            IsFollowingLive = true;
            _followSeconds = DefaultFollowSeconds;
        }
        if (IsFollowingLive) _viewport = LatestRange(extent);
        else if (_viewport is { } view) _viewport = view.Pan(0, extent);
        if (Selection is { } selection)
        {
            if (samples.Count == 0 || selection.End < samples[0].Timestamp || selection.Start > samples[^1].Timestamp)
                ClearSelection();
            else SelectRange(samples, selection.Start, selection.End);
        }
    }

    public void Pan(IReadOnlyList<PowerPoint> samples, double seconds)
    {
        if (!double.IsFinite(seconds)) return;
        IsFollowingLive = false;
        _viewport = VisibleRange(samples).Pan(seconds, ChartSeries.Extent(samples));
    }

    /// <summary>Pans from a remembered drag-origin range (mouse drag); keeps the follow-live mode.</summary>
    public void PanFrom(IReadOnlyList<PowerPoint> samples, ChartTimeRange dragRange, double seconds) =>
        _viewport = dragRange.Pan(seconds, ChartSeries.Extent(samples));

    public void FollowLive(IReadOnlyList<PowerPoint> samples)
    {
        var extent = ChartSeries.Extent(samples);
        var visible = VisibleRange(samples);
        _followSeconds = visible.Seconds < extent.Seconds ? visible.Seconds : DefaultFollowSeconds;
        IsFollowingLive = true;
        _viewport = LatestRange(extent);
    }

    public void ResetView(IReadOnlyList<PowerPoint> samples)
    {
        IsFollowingLive = false;
        _viewport = ChartSeries.Extent(samples);
    }

    public void ZoomAt(IReadOnlyList<PowerPoint> samples, double factor, double anchor)
    {
        IsFollowingLive = false;
        _viewport = VisibleRange(samples).Zoom(factor, anchor, ChartSeries.Extent(samples));
    }

    public void ZoomToSelection(IReadOnlyList<PowerPoint> samples)
    {
        if (Selection is not { DurationSeconds: > 0 } selection) return;
        IsFollowingLive = false;
        _viewport = new ChartTimeRange(selection.Start, selection.End).Zoom(1, .5, ChartSeries.Extent(samples));
    }

    /// <summary>Anchors a selection drag at the nearest sample (or clears when there is none).</summary>
    public void BeginSelection(IReadOnlyList<PowerPoint> samples, DateTimeOffset? anchor)
    {
        _selectionAnchor = anchor;
        if (anchor is { } value) SelectRange(samples, value, value);
        else ClearSelection();
    }

    /// <summary>Extends an anchored selection to a sample timestamp; returns false when no anchor exists.</summary>
    public bool ExtendSelection(IReadOnlyList<PowerPoint> samples, DateTimeOffset time)
    {
        if (_selectionAnchor is not { } anchor) return false;
        Selection = ChartSeries.Summarize(samples, anchor, time);
        return true;
    }

    public void SelectRange(IReadOnlyList<PowerPoint> samples, DateTimeOffset start, DateTimeOffset end) =>
        Selection = ChartSeries.Summarize(samples, start, end);

    public void ClearSelection()
    {
        Selection = null;
        _selectionAnchor = null;
    }

    private ChartTimeRange LatestRange(ChartTimeRange extent) =>
        new(extent.End.AddSeconds(-Math.Min(_followSeconds, extent.Seconds)), extent.End);
}
