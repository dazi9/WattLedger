using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WattLedger.Desktop;

namespace WattLedger.App;

/// <summary>WPF input/render adapter over the same samples, layout and integration as Linux.</summary>
public sealed class PowerChart : FrameworkElement
{
    private PowerPoint[] _samples = [];
    private ChartTimeRange? _viewport;
    private PowerChartLayout? _layout;
    private Size _layoutSize;
    private readonly List<(Brush? Fill, Pen? Pen, StreamGeometry Geometry)> _geometry = [];
    private readonly List<(Brush Brush, Point Position, double Radius)> _dots = [];
    private readonly List<(FormattedText Text, Point Position)> _labels = [];
    private Point? _dragStart;
    private ChartTimeRange _dragRange;
    private DateTimeOffset? _selectionAnchor;
    private PowerPoint? _hover;
    private bool _panning;
    private double _followSeconds = 1800;
    private HwndSource? _source;

    public PowerChart()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        Loaded += (_, _) =>
        {
            _source = PresentationSource.FromVisual(this) as HwndSource;
            _source?.AddHook(WindowMessage);
        };
        Unloaded += (_, _) => { _source?.RemoveHook(WindowMessage); _source = null; };
    }

    public event EventHandler? SelectionChanged;
    public event EventHandler? ViewChanged;
    internal ChartInteractionMode InteractionMode { get; set; }
    public bool IsFollowingLive { get; private set; } = true;
    internal ChartSelectionStatistics? Selection { get; private set; }
    internal ChartTimeRange VisibleRange => _viewport ?? ChartSeries.Extent(_samples);
    internal bool CanPanEarlier => VisibleRange.Start > ChartSeries.Extent(_samples).Start;
    internal bool CanPanLater => VisibleRange.End < ChartSeries.Extent(_samples).End;
    internal IReadOnlyList<PowerPoint> Samples
    {
        get => _samples;
        set
        {
            _samples = ChartSeries.Normalize(value);
            var extent = ChartSeries.Extent(_samples);
            if (_viewport is { } prior && (prior.End < extent.Start || prior.Start > extent.End))
            {
                IsFollowingLive = true;
                _followSeconds = 1800;
            }
            if (IsFollowingLive) _viewport = LatestRange(extent);
            else if (_viewport is { } view) _viewport = view.Pan(0, extent);
            if (Selection is { } selection)
            {
                if (_samples.Length == 0 || selection.End < _samples[0].Timestamp || selection.Start > _samples[^1].Timestamp)
                    ClearSelection();
                else SelectRange(selection.Start, selection.End);
            }
            ChangedView();
        }
    }

    private ChartTimeRange LatestRange(ChartTimeRange extent) =>
        new(extent.End.AddSeconds(-Math.Min(_followSeconds, extent.Seconds)), extent.End);

    public void Zoom(double factor) => ZoomAt(factor, .5);
    public void Pan(double seconds)
    {
        if (!double.IsFinite(seconds)) return;
        IsFollowingLive = false;
        _viewport = VisibleRange.Pan(seconds, ChartSeries.Extent(_samples));
        ChangedView();
    }
    public void FollowLive()
    {
        var extent = ChartSeries.Extent(_samples);
        _followSeconds = VisibleRange.Seconds < extent.Seconds ? VisibleRange.Seconds : 1800;
        IsFollowingLive = true;
        _viewport = LatestRange(extent);
        ChangedView();
    }
    public void ResetView() { IsFollowingLive = false; _viewport = ChartSeries.Extent(_samples); ChangedView(); }
    public void ClearSelection()
    {
        Selection = null;
        _selectionAnchor = null;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }
    internal void SelectRange(DateTimeOffset start, DateTimeOffset end)
    {
        Selection = ChartSeries.Summarize(_samples, start, end);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }
    public void ZoomToSelection()
    {
        if (Selection is not { DurationSeconds: > 0 } selection) return;
        IsFollowingLive = false;
        _viewport = new ChartTimeRange(selection.Start, selection.End).Zoom(1, .5, ChartSeries.Extent(_samples));
        ChangedView();
    }
    private void ZoomAt(double factor, double anchor)
    {
        IsFollowingLive = false;
        _viewport = VisibleRange.Zoom(factor, anchor, ChartSeries.Extent(_samples));
        ChangedView();
    }
    public void InvalidateTheme() { _layout = null; InvalidateVisual(); }
    private void ChangedView()
    {
        _layout = null;
        _hover = null;
        ViewChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var layout = EnsureLayout();
        var point = e.GetPosition(this);
        if (!InPlot(point, layout)) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Pan(-e.Delta / 120d * VisibleRange.Seconds * .1);
        else ZoomAt(Math.Pow(.8, Math.Clamp(e.Delta / 120d, -5, 5)),
            (point.X - layout.PlotLeft) / (layout.PlotRight - layout.PlotLeft));
        e.Handled = true;
    }

    private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmMouseHWheel = 0x020E;
        if (msg != wmMouseHWheel || !IsVisible || ActualWidth < 150) return IntPtr.Zero;
        var packed = lParam.ToInt64();
        var point = PointFromScreen(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
        if (!InPlot(point, EnsureLayout())) return IntPtr.Zero;
        // WM_MOUSEHWHEEL positive means scroll right, unlike Avalonia's content-motion delta.
        var delta = (short)((wParam.ToInt64() >> 16) & 0xffff) / 120d;
        Pan(delta * VisibleRange.Seconds * .1);
        handled = true;
        return IntPtr.Zero;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Middle)) return;
        var point = e.GetPosition(this);
        var layout = EnsureLayout();
        if (!InPlot(point, layout)) return;
        Focus();
        if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left) { ResetView(); e.Handled = true; return; }
        _dragStart = point;
        _dragRange = VisibleRange;
        IsFollowingLive = false;
        ViewChanged?.Invoke(this, EventArgs.Empty);
        _panning = InteractionMode == ChartInteractionMode.Pan || e.ChangedButton == MouseButton.Middle ||
                   Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (!_panning)
        {
            _selectionAnchor = ChartSeries.Nearest(_samples, TimeAt(point.X, layout), VisibleRange)?.Timestamp;
            if (_selectionAnchor is { } anchor) SelectRange(anchor, anchor);
            else ClearSelection();
        }
        CaptureMouse();
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        var layout = EnsureLayout();
        if (_dragStart is { } drag)
        {
            if (_panning)
            {
                IsFollowingLive = false;
                _viewport = _dragRange.Pan(-(point.X - drag.X) / (layout.PlotRight - layout.PlotLeft) * _dragRange.Seconds,
                    ChartSeries.Extent(_samples));
                ChangedView();
            }
            else if (_selectionAnchor is { } anchor && ChartSeries.Nearest(_samples, TimeAt(point.X, layout), VisibleRange) is { } sample)
                SelectRange(anchor, sample.Timestamp);
        }
        _hover = !(_dragStart is not null && _panning) && InPlot(point, layout)
            ? ChartSeries.Nearest(_samples, TimeAt(point.X, layout), VisibleRange) : null;
        InvalidateVisual();
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragStart is null) return;
        _dragStart = null;
        ReleaseMouseCapture();
        e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); _dragStart = null; }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); _hover = null; InvalidateVisual(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape: ClearSelection(); break;
            case Key.Home: ResetView(); break;
            case Key.End: FollowLive(); break;
            case Key.Add: case Key.OemPlus: Zoom(.8); break;
            case Key.Subtract: case Key.OemMinus: Zoom(1.25); break;
            case Key.Left: Pan(-VisibleRange.Seconds * .2); break;
            case Key.Right: Pan(VisibleRange.Seconds * .2); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (ActualWidth < 150 || ActualHeight < 70) return;
        var layout = EnsureLayout();
        context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var gridPen = new Pen(Resource("ChartGridBrush", "BorderBrush"), 1);
        for (var i = 0; i <= 5; i++)
        {
            var y = layout.PlotTop + (layout.PlotBottom - layout.PlotTop) * i / 5;
            context.DrawLine(gridPen, new Point(layout.PlotLeft, y), new Point(layout.PlotRight, y));
        }
        context.DrawLine(new Pen(Resource("ChartZeroBrush", "MutedBrush"), 1.2),
            new Point(layout.PlotLeft, layout.ZeroY), new Point(layout.PlotRight, layout.ZeroY));
        foreach (var tick in layout.TimeTicks)
            context.DrawLine(gridPen, new Point(tick.X, layout.PlotTop), new Point(tick.X, layout.PlotBottom));
        foreach (var item in _labels) context.DrawText(item.Text, item.Position);
        context.PushClip(new RectangleGeometry(new Rect(layout.PlotLeft, layout.PlotTop,
            layout.PlotRight - layout.PlotLeft, layout.PlotBottom - layout.PlotTop)));
        foreach (var item in _geometry) context.DrawGeometry(item.Fill, item.Pen, item.Geometry);
        foreach (var item in _dots) context.DrawEllipse(item.Brush, null, item.Position, item.Radius, item.Radius);
        if (Selection is { } selection)
        {
            var left = XAt(selection.Start, layout);
            var right = XAt(selection.End, layout);
            var blue = Resource("ChartBatteryBrush", "BlueBrush");
            var fill = Resource("ChartSelectionBrush", "BlueBrush").CloneCurrentValue();
            if (TryFindResource("ChartSelectionBrush") is null) fill.Opacity = .18;
            context.DrawRectangle(fill, null, new Rect(left, layout.PlotTop, Math.Max(0, right - left), layout.PlotBottom - layout.PlotTop));
            context.DrawLine(new Pen(blue, 1), new Point(left, layout.PlotTop), new Point(left, layout.PlotBottom));
            context.DrawLine(new Pen(blue, 1), new Point(right, layout.PlotTop), new Point(right, layout.PlotBottom));
        }
        if (_hover is { } hover)
        {
            var x = XAt(hover.Timestamp, layout);
            context.DrawLine(new Pen(Resource("MutedBrush"), 1) { DashStyle = DashStyles.Dot },
                new Point(x, layout.PlotTop), new Point(x, layout.PlotBottom));
        }
        context.Pop();
        if (_hover is { } hovered)
        {
            var power = hovered.Watts is { } watts ? $"{watts:+0.00;-0.00;0.00} W" : "power unavailable";
            var percent = hovered.BatteryPercent is { } battery ? $"{battery}%" : "battery unavailable";
            var text = Text($"{hovered.Timestamp.ToLocalTime():MM-dd HH:mm:ss} · {power} · {percent}", 11);
            var x = Math.Max(layout.PlotLeft, Math.Min(XAt(hovered.Timestamp, layout) + 10, ActualWidth - text.Width - 12));
            context.DrawRoundedRectangle(Resource("ChartHoverBrush", "CardBrush"), null,
                new Rect(x - 5, layout.PlotTop + 4, text.Width + 10, text.Height + 8), 4, 4);
            context.DrawText(text, new Point(x, layout.PlotTop + 8));
        }
        if (layout.ActualStart is null) context.DrawText(Text("No samples in this range", 12), new Point(layout.PlotLeft + 10, layout.PlotTop + 12));
    }

    private PowerChartLayout EnsureLayout()
    {
        if (_layout is not null && _layoutSize == RenderSize) return _layout;
        _layoutSize = RenderSize;
        var layout = PowerChartLayout.CalculatePrepared(_samples, ActualWidth, ActualHeight, viewport: _viewport);
        _layout = layout;
        _geometry.Clear(); _dots.Clear(); _labels.Clear();
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
            _labels.Add((text, new Point(Math.Clamp(tick.X - text.Width / 2, layout.PlotLeft,
                Math.Max(layout.PlotLeft, layout.PlotRight - text.Width)), layout.PlotBottom + 8)));
        }
        var power = Resource("ChartPowerBrush", "GreenBrush");
        var battery = Resource("ChartBatteryBrush", "BlueBrush");
        var area = power.CloneCurrentValue(); area.Opacity = .16;
        var powerPen = new Pen(power, 2.2) { LineJoin = PenLineJoin.Round };
        var batteryPen = new Pen(battery, 1.6) { DashStyle = DashStyles.Dash };
        foreach (var run in layout.PowerRuns)
        {
            if (run.Points.Count == 1) _dots.Add((power, PointAt(run.Points[0]), 3.5));
            else { _geometry.Add((area, null, Geometry(run.Points, layout.ZeroY))); _geometry.Add((null, powerPen, Geometry(run.Points))); }
        }
        foreach (var run in layout.BatteryRuns)
        {
            if (run.Count == 1) _dots.Add((battery, PointAt(run[0]), 2.5));
            else _geometry.Add((null, batteryPen, Geometry(run)));
        }
        return layout;
    }
    private static Point PointAt(PowerChartPoint point) => new(point.Position.X, point.Position.Y);
    private static StreamGeometry Geometry(IReadOnlyList<PowerChartPoint> points, double? baseline = null)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var first = points[0].Position;
            context.BeginFigure(new Point(first.X, baseline ?? first.Y), baseline.HasValue, baseline.HasValue);
            foreach (var point in points) context.LineTo(PointAt(point), true, false);
            if (baseline is { } y) context.LineTo(new Point(points[^1].Position.X, y), true, false);
        }
        geometry.Freeze();
        return geometry;
    }
    private Brush Resource(string key, string? fallback = null) => TryFindResource(key) as Brush ??
        (fallback is not null ? TryFindResource(fallback) as Brush : null) ?? Brushes.Gray;
    private FormattedText Text(string text, double size = 9) => new(text, CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Resource("MutedBrush"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private static DateTimeOffset TimeAt(double x, PowerChartLayout layout) => layout.TimeStart.AddSeconds(
        Math.Clamp((x - layout.PlotLeft) / (layout.PlotRight - layout.PlotLeft), 0, 1) * (layout.TimeEnd - layout.TimeStart).TotalSeconds);
    private static double XAt(DateTimeOffset time, PowerChartLayout layout) => layout.PlotLeft +
        (time - layout.TimeStart).TotalSeconds / (layout.TimeEnd - layout.TimeStart).TotalSeconds * (layout.PlotRight - layout.PlotLeft);
    private static bool InPlot(Point point, PowerChartLayout layout) => point.X >= layout.PlotLeft && point.X <= layout.PlotRight && point.Y >= layout.PlotTop && point.Y <= layout.PlotBottom;
}
