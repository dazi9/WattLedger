using WattLedger.Desktop;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace WattLedger.Linux;

internal sealed class PowerChart : Control
{
    private static readonly IBrush GridBrush = Brush("ChartGridBrush");
    private static readonly IBrush LabelBrush = Brush("ChartLabelBrush");
    private static readonly IBrush PowerBrush = Brush("ChartPowerBrush");
    private static readonly IBrush BatteryBrush = Brush("ChartBatteryBrush");
    private static readonly IBrush SelectionBrush = Brush("ChartSelectionBrush");
    private static readonly IBrush HoverBrush = Brush("ChartHoverBrush");
    private static readonly IPen GridPen = new Pen(GridBrush, 1);
    private static readonly IPen ZeroPen = new Pen(Brush("ChartZeroBrush"), 1.2);
    private static readonly IPen PowerPen = new Pen(PowerBrush, 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly IPen BatteryPen = new Pen(BatteryBrush, 1.6, dashStyle: DashStyle.Dash);
    private static readonly IPen CursorPen = new Pen(LabelBrush, 1, dashStyle: DashStyle.Dot);
    private static readonly IBrush AreaBrush = ThemeResources.ChartAreaBrush;
    private PowerPoint[] _samples = [];
    private ChartTimeRange? _viewport;
    private PowerChartLayout? _layout;
    private Size _layoutSize;
    private readonly List<(IBrush? Fill, IPen? Pen, StreamGeometry Geometry)> _geometry = [];
    private readonly List<(IBrush Brush, Point Position, double Radius)> _dots = [];
    private readonly List<(FormattedText Text, Point Position)> _labels = [];
    private PowerPoint? _hover;
    private FormattedText? _hoverText;
    private Point? _dragStart;
    private ChartTimeRange _dragRange;
    private DateTimeOffset? _selectionAnchor;
    private bool _panning;
    private ChartInteractionMode _interactionMode;
    private double _followWindowSeconds = 30 * 60;

    public PowerChart()
    {
        Focusable = true;
        ClipToBounds = true;
        UpdateCursor();
        ActualThemeVariantChanged += (_, _) =>
        {
            _layout = null;
            _hoverText = null;
            InvalidateVisual();
        };
    }

    public event EventHandler? SelectionChanged;
    public event EventHandler? ViewChanged;
    public ChartSelectionStatistics? Selection { get; private set; }
    public ChartTimeRange DataRange => ChartSeries.Extent(_samples);
    public ChartTimeRange VisibleRange => _viewport ?? DataRange;
    public bool IsFollowingLive { get; private set; } = true;
    public bool CanPanEarlier => VisibleRange.Start > DataRange.Start;
    public bool CanPanLater => VisibleRange.End < DataRange.End;
    public ChartInteractionMode InteractionMode
    {
        get => _interactionMode;
        set
        {
            if (_interactionMode == value) return;
            _interactionMode = value;
            UpdateCursor();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<PowerPoint> Samples
    {
        get => _samples;
        set
        {
            _samples = ChartSeries.Normalize(value);
            var extent = DataRange;
            if (_viewport is { } prior && (prior.End < extent.Start || prior.Start > extent.End))
            {
                IsFollowingLive = true;
                _followWindowSeconds = 30 * 60;
            }
            if (IsFollowingLive) _viewport = LatestRange(extent, _followWindowSeconds);
            else if (_viewport is { } view) _viewport = view.Pan(0, extent);
            if (Selection is { } selection)
            {
                if (_samples.Length == 0 || selection.End < _samples[0].Timestamp || selection.Start > _samples[^1].Timestamp)
                    ClearSelection();
                else SelectRange(selection.Start, selection.End);
            }
            _hover = null;
            _hoverText = null;
            ChangedView();
        }
    }

    public void Zoom(double factor) => ZoomAt(factor, .5);
    public void ResetView()
    {
        IsFollowingLive = false;
        _viewport = DataRange;
        ChangedView();
    }
    public void FollowLive()
    {
        var extent = DataRange;
        _followWindowSeconds = VisibleRange.Seconds >= extent.Seconds ? 30 * 60 : VisibleRange.Seconds;
        IsFollowingLive = true;
        _viewport = LatestRange(extent, _followWindowSeconds);
        ChangedView();
    }
    public void Pan(double seconds)
    {
        if (!double.IsFinite(seconds)) return;
        IsFollowingLive = false;
        _viewport = VisibleRange.Pan(seconds, DataRange);
        ChangedView();
    }
    private static ChartTimeRange LatestRange(ChartTimeRange extent, double seconds) =>
        new(extent.End.AddSeconds(-Math.Min(seconds, extent.Seconds)), extent.End);
    private void UpdateCursor() => Cursor = new Cursor(
        InteractionMode == ChartInteractionMode.Pan ? StandardCursorType.SizeAll : StandardCursorType.Cross);

    public void ClearSelection()
    {
        Selection = null;
        _selectionAnchor = null;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }
    public void SelectRange(DateTimeOffset start, DateTimeOffset end)
    {
        Selection = ChartSeries.Summarize(_samples, start, end);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }
    public void ZoomToSelection()
    {
        if (Selection is not { DurationSeconds: > 0 } selected) return;
        var range = new ChartTimeRange(selected.Start, selected.End);
        IsFollowingLive = false;
        _viewport = range.Zoom(1, .5, ChartSeries.Extent(_samples));
        ChangedView();
    }
    private void ZoomAt(double factor, double anchor)
    {
        IsFollowingLive = false;
        _viewport = VisibleRange.Zoom(factor, anchor, ChartSeries.Extent(_samples));
        ChangedView();
    }
    private void ChangedView()
    {
        _layout = null;
        _hover = null;
        _hoverText = null;
        ViewChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var layout = EnsureLayout();
        var point = e.GetPosition(this);
        if (!InPlot(point, layout)) return;
        // A two-finger horizontal scroll moves the plotted content with the gesture.
        // Prefer the dominant axis so a little diagonal noise does not zoom while panning.
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))
            Pan(-e.Delta.X * VisibleRange.Seconds * .1);
        else if (e.Delta.Y != 0)
            ZoomAt(Math.Pow(.8, Math.Clamp(e.Delta.Y, -5, 5)), (point.X - layout.PlotLeft) / (layout.PlotRight - layout.PlotLeft));
        else return;
        e.Handled = true;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        var layout = EnsureLayout();
        if (!InPlot(point, layout)) return;
        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed && !properties.IsMiddleButtonPressed) return;
        Focus();
        if (e.ClickCount == 2 && properties.IsLeftButtonPressed)
        {
            ResetView();
            e.Handled = true;
            return;
        }
        _dragStart = point;
        _dragRange = VisibleRange;
        _panning = InteractionMode == ChartInteractionMode.Pan || properties.IsMiddleButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        IsFollowingLive = false;
        ChangedView();
        if (!_panning)
        {
            _selectionAnchor = ChartSeries.Nearest(_samples, TimeAt(point.X, layout), VisibleRange)?.Timestamp;
            if (_selectionAnchor is { } anchor) SelectRange(anchor, anchor);
            else ClearSelection();
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        var layout = EnsureLayout();
        if (_dragStart is { } drag)
        {
            if (_panning)
            {
                _viewport = _dragRange.Pan(-(point.X - drag.X) / (layout.PlotRight - layout.PlotLeft) * _dragRange.Seconds,
                    ChartSeries.Extent(_samples));
                ChangedView();
            }
            else if (_selectionAnchor is { } anchor && ChartSeries.Nearest(_samples, TimeAt(point.X, layout), VisibleRange) is { } sample)
                SelectRange(anchor, sample.Timestamp);
        }
        var hover = !(_dragStart is not null && _panning) && InPlot(point, layout)
            ? ChartSeries.Nearest(_samples, TimeAt(point.X, layout), VisibleRange) : null;
        if (_hover != hover) _hoverText = null;
        _hover = hover;
        InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStart is null) return;
        _dragStart = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragStart = null;
    }
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        _hoverText = null;
        InvalidateVisual();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape: ClearSelection(); break;
            case Key.Home: ResetView(); break;
            case Key.Add: case Key.OemPlus: Zoom(.8); break;
            case Key.Subtract: case Key.OemMinus: Zoom(1.25); break;
            case Key.Left: Pan(-VisibleRange.Seconds * .2); break;
            case Key.Right: Pan(VisibleRange.Seconds * .2); break;
            case Key.End: FollowLive(); break;
            default: return;
        }
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 150 || Bounds.Height < 70) return;
        var layout = EnsureLayout();
        // A transparent background makes the complete plot respond to pointer events.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        for (var i = 0; i <= 5; i++)
        {
            var y = layout.PlotTop + (layout.PlotBottom - layout.PlotTop) * i / 5;
            context.DrawLine(GridPen, new Point(layout.PlotLeft, y), new Point(layout.PlotRight, y));
        }
        context.DrawLine(ZeroPen, new Point(layout.PlotLeft, layout.ZeroY), new Point(layout.PlotRight, layout.ZeroY));
        foreach (var tick in layout.TimeTicks)
            context.DrawLine(GridPen, new Point(tick.X, layout.PlotTop), new Point(tick.X, layout.PlotBottom));
        foreach (var item in _labels) context.DrawText(item.Text, item.Position);
        using (context.PushClip(new Rect(layout.PlotLeft, layout.PlotTop, layout.PlotRight - layout.PlotLeft, layout.PlotBottom - layout.PlotTop)))
        {
            foreach (var item in _geometry) context.DrawGeometry(item.Fill, item.Pen, item.Geometry);
            foreach (var item in _dots) context.DrawEllipse(item.Brush, null, item.Position, item.Radius, item.Radius);
            if (Selection is { } selection)
            {
                var left = XAt(selection.Start, layout);
                var right = XAt(selection.End, layout);
                context.DrawRectangle(SelectionBrush, null, new Rect(left, layout.PlotTop, Math.Max(0, right - left), layout.PlotBottom - layout.PlotTop));
                context.DrawLine(BatteryPen, new Point(left, layout.PlotTop), new Point(left, layout.PlotBottom));
                context.DrawLine(BatteryPen, new Point(right, layout.PlotTop), new Point(right, layout.PlotBottom));
            }
            if (_hover is { } hover && hover.Timestamp >= layout.TimeStart && hover.Timestamp <= layout.TimeEnd)
            {
                var x = XAt(hover.Timestamp, layout);
                context.DrawLine(CursorPen, new Point(x, layout.PlotTop), new Point(x, layout.PlotBottom));
            }
        }
        if (_hover is { } hovered && hovered.Timestamp >= layout.TimeStart && hovered.Timestamp <= layout.TimeEnd)
        {
            var power = hovered.Watts is { } watts ? $"{watts:+0.00;-0.00;0.00} W" : "power unavailable";
            var percent = hovered.BatteryPercent is { } battery ? $"{battery}%" : "battery unavailable";
            var text = _hoverText ??= Text($"{hovered.Timestamp.ToLocalTime():MM-dd HH:mm:ss}  ·  {power}  ·  {percent}", 11);
            var x = Math.Max(layout.PlotLeft, Math.Min(XAt(hovered.Timestamp, layout) + 10, Bounds.Width - text.Width - 12));
            context.DrawRectangle(HoverBrush, null, new Rect(x - 5, layout.PlotTop + 4, text.Width + 10, text.Height + 8), 4, 4);
            context.DrawText(text, new Point(x, layout.PlotTop + 8));
        }
        if (layout.ActualStart is null) context.DrawText(Text("No samples in this range", 12), new Point(layout.PlotLeft + 10, layout.PlotTop + 12));
    }

    private PowerChartLayout EnsureLayout()
    {
        if (_layout is not null && _layoutSize == Bounds.Size) return _layout;
        _layoutSize = Bounds.Size;
        var layout = PowerChartLayout.CalculatePrepared(_samples, Bounds.Width, Bounds.Height, viewport: _viewport);
        _layout = layout;
        _geometry.Clear();
        _dots.Clear();
        _labels.Clear();
        for (var i = 0; i <= 5; i++)
        {
            var value = layout.PowerMaximum - (layout.PowerMaximum - layout.PowerMinimum) * i / 5;
            var y = layout.PlotTop + (layout.PlotBottom - layout.PlotTop) * i / 5;
            _labels.Add((Text($"{value:0} W"), new Point(2, y - 7)));
            _labels.Add((Text($"{100 - i * 20}%"), new Point(layout.PlotRight + 6, y - 7)));
        }
        foreach (var tick in layout.TimeTicks)
        {
            var text = Text(tick.Label);
            var x = Math.Clamp(tick.X - text.Width / 2, layout.PlotLeft, Math.Max(layout.PlotLeft, layout.PlotRight - text.Width));
            _labels.Add((text, new Point(x, layout.PlotBottom + 8)));
        }
        foreach (var run in layout.PowerRuns)
        {
            if (run.Points.Count == 1)
            {
                var point = run.Points[0].Position;
                _dots.Add((PowerBrush, new Point(point.X, point.Y), 3.5));
                continue;
            }
            _geometry.Add((AreaBrush, null, Geometry(run.Points, layout.ZeroY)));
            _geometry.Add((null, PowerPen, Geometry(run.Points)));
        }
        foreach (var run in layout.BatteryRuns)
        {
            if (run.Count == 1)
            {
                var point = run[0].Position;
                _dots.Add((BatteryBrush, new Point(point.X, point.Y), 2.5));
            }
            else _geometry.Add((null, BatteryPen, Geometry(run)));
        }
        return layout;
    }

    private static StreamGeometry Geometry(IReadOnlyList<PowerChartPoint> points, double? baseline = null)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        var first = points[0].Position;
        context.BeginFigure(new Point(first.X, baseline ?? first.Y), baseline.HasValue);
        foreach (var item in points) context.LineTo(new Point(item.Position.X, item.Position.Y));
        if (baseline is { } y) context.LineTo(new Point(points[^1].Position.X, y));
        context.EndFigure(baseline.HasValue);
        return geometry;
    }
    private static DateTimeOffset TimeAt(double x, PowerChartLayout layout) => layout.TimeStart.AddSeconds(
        Math.Clamp((x - layout.PlotLeft) / (layout.PlotRight - layout.PlotLeft), 0, 1) * (layout.TimeEnd - layout.TimeStart).TotalSeconds);
    private static double XAt(DateTimeOffset time, PowerChartLayout layout) => layout.PlotLeft +
        (time - layout.TimeStart).TotalSeconds / (layout.TimeEnd - layout.TimeStart).TotalSeconds * (layout.PlotRight - layout.PlotLeft);
    private static bool InPlot(Point point, PowerChartLayout layout) => point.X >= layout.PlotLeft && point.X <= layout.PlotRight && point.Y >= layout.PlotTop && point.Y <= layout.PlotBottom;
    private static IBrush Brush(string key) => ThemeResources.Brush(key);
    private static FormattedText Text(string text, double size = 9) => new(text, System.Globalization.CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight, new Typeface("sans-serif"), size, LabelBrush);
}
