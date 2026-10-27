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
    private readonly ChartInteractionState _state = new();
    private PowerPoint[] _samples = [];
    private PowerChartLayout? _layout;
    private Size _layoutSize;
    private readonly List<(IBrush? Fill, IPen? Pen, StreamGeometry Geometry)> _geometry = [];
    private readonly List<(IBrush Brush, Point Position, double Radius)> _dots = [];
    private readonly List<(FormattedText Text, Point Position)> _labels = [];
    private PowerPoint? _hover;
    private FormattedText? _hoverText;
    private Point? _dragStart;
    private ChartTimeRange _dragRange;
    private bool _panning;
    private ChartInteractionMode _interactionMode;
    private readonly ChartScaleSession _pinchScale = new();
    private readonly HashSet<IPointer> _touchContacts = [];
    private bool _pinching;
    private ChartSelectionStatistics? _selectionBeforeTouch;
    private bool _hasTouchSelectionSnapshot;
    private ulong _lastMagnifyTimestamp;
    private readonly bool _traceInput = Environment.GetEnvironmentVariable("WATTLEDGER_INPUT_TRACE") == "1";

    public PowerChart()
    {
        Focusable = true;
        ClipToBounds = true;
        UpdateCursor();
        GestureRecognizers.Add(new PinchGestureRecognizer());
        AddHandler(PinchEvent, OnPinch);
        AddHandler(PinchEndedEvent, OnPinchEnded);
        AddHandler(PointerTouchPadGestureMagnifyEvent, OnTouchPadMagnify);
        DetachedFromVisualTree += (_, _) =>
        {
            _touchContacts.Clear();
            EndPinch();
        };
        ActualThemeVariantChanged += (_, _) =>
        {
            _layout = null;
            _hoverText = null;
            InvalidateVisual();
        };
    }

    public event EventHandler? SelectionChanged;
    public event EventHandler? ViewChanged;
    public ChartSelectionStatistics? Selection => _state.Selection;
    public ChartTimeRange DataRange => ChartSeries.Extent(_samples);
    public ChartTimeRange VisibleRange => _state.VisibleRange(_samples);
    public bool IsFollowingLive => _state.IsFollowingLive;
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
            var selectionBefore = _state.Selection;
            _state.OnSamplesChanged(_samples);
            if (!ReferenceEquals(selectionBefore, _state.Selection))
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }

            _hover = null;
            _hoverText = null;
            ChangedView();
        }
    }

    public void Zoom(double factor) => ZoomAt(factor, .5);
    public void ResetView()
    {
        _state.ResetView(_samples);
        ChangedView();
    }
    public void FollowLive()
    {
        _state.FollowLive(_samples);
        ChangedView();
    }
    public void Pan(double seconds)
    {
        if (!double.IsFinite(seconds)) return;
        _state.Pan(_samples, seconds);
        ChangedView();
    }
    private void UpdateCursor() => Cursor = new Cursor(
        InteractionMode == ChartInteractionMode.Pan ? StandardCursorType.SizeAll : StandardCursorType.Cross);

    public void ClearSelection()
    {
        _state.ClearSelection();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }
    public void SelectRange(DateTimeOffset start, DateTimeOffset end)
    {
        _state.SelectRange(_samples, start, end);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }
    public void ZoomToSelection()
    {
        if (Selection is not { DurationSeconds: > 0 }) return;
        _state.ZoomToSelection(_samples);
        ChangedView();
    }
    private void ZoomAt(double factor, double anchor)
    {
        _state.ZoomAt(_samples, factor, anchor);
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
        // A backend can emit a compatibility wheel packet for the same native magnify.
        if (_pinching || (e.Timestamp != 0 && e.Timestamp == _lastMagnifyTimestamp))
        {
            e.Handled = true;
            return;
        }
        var x = double.IsFinite(e.Delta.X) ? e.Delta.X : 0;
        var y = double.IsFinite(e.Delta.Y) ? e.Delta.Y : 0;
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        // Keep sub-detent touchpad values intact. Ctrl explicitly requests zoom even
        // when a backend reports the continuous gesture on the horizontal axis only.
        if (!control && Math.Abs(x) > Math.Abs(y))
            Pan(-Math.Clamp(x, -5, 5) * VisibleRange.Seconds * .1);
        else
        {
            var steps = y != 0 ? y : control ? x : 0;
            if (!ApplyGestureZoom(ChartGestureScale.WheelFactor(steps), point, layout)) return;
        }
        if (_traceInput) Console.Error.WriteLine($"WattLedger input: wheel x={x:R} y={y:R} ctrl={control} span={VisibleRange.Seconds:R}s");
        e.Handled = true;
    }

    private void OnTouchPadMagnify(object? sender, PointerDeltaEventArgs e)
    {
        var layout = EnsureLayout();
        var point = e.GetPosition(this);
        if (_pinching || !InPlot(point, layout)) return;
        // Avalonia's native magnify event carries a per-packet magnification delta,
        // not the cumulative Scale used by the multi-contact Pinch recognizer.
        var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        var factor = ChartGestureScale.MagnificationFactor(1 + delta);
        if (!ApplyGestureZoom(factor, point, layout)) return;
        _lastMagnifyTimestamp = e.Timestamp;
        _dragStart = null;
        if (_traceInput) Console.Error.WriteLine($"WattLedger input: magnify delta={delta:R} factor={factor:R} span={VisibleRange.Seconds:R}s");
        e.Handled = true;
    }

    private void OnPinch(object? sender, PinchEventArgs e)
    {
        var layout = EnsureLayout();
        if (!InPlot(e.ScaleOrigin, layout)) return;
        if (!_pinching)
        {
            _pinching = true;
            _pinchScale.Begin();
            _dragStart = null;
            // The first touch may already have started a single-contact selection.
            // Restore it when a second contact turns that interaction into pinch.
            if (_hasTouchSelectionSnapshot)
            {
                if (_selectionBeforeTouch is { } selection)
                    _state.SelectRange(_samples, selection.Start, selection.End);
                else
                    _state.ClearSelection();
                _hasTouchSelectionSnapshot = false;
                _selectionBeforeTouch = null;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        var factor = _pinchScale.Update(e.Scale);
        ApplyGestureZoom(factor, e.ScaleOrigin, layout);
        if (_traceInput) Console.Error.WriteLine($"WattLedger input: contact-pinch scale={e.Scale:R} factor={factor:R} span={VisibleRange.Seconds:R}s");
        e.Handled = true;
    }

    private void OnPinchEnded(object? sender, PinchEndedEventArgs e)
    {
        EndPinch();
        e.Handled = true;
    }

    private void EndPinch()
    {
        _pinching = false;
        _pinchScale.End();
        _touchContacts.Clear();
        _hasTouchSelectionSnapshot = false;
        _selectionBeforeTouch = null;
    }

    private bool ApplyGestureZoom(double factor, Point point, PowerChartLayout layout)
    {
        if (!double.IsFinite(factor) || factor <= 0 || factor == 1) return false;
        ZoomAt(factor, (point.X - layout.PlotLeft) / (layout.PlotRight - layout.PlotLeft));
        return true;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        var layout = EnsureLayout();
        if (!InPlot(point, layout)) return;
        if (e.Pointer.Type is PointerType.Touch or PointerType.Pen)
        {
            if (!_hasTouchSelectionSnapshot)
            {
                _selectionBeforeTouch = _state.Selection;
                _hasTouchSelectionSnapshot = true;
            }
            _touchContacts.Add(e.Pointer);
            if (_touchContacts.Count > 1)
            {
                _dragStart = null;
                return;
            }
        }
        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed && !properties.IsMiddleButtonPressed) return;
        Focus();
        if (e.Pointer.Type == PointerType.Mouse && e.ClickCount == 2 && properties.IsLeftButtonPressed)
        {
            ResetView();
            e.Handled = true;
            return;
        }
        _dragStart = point;
        _dragRange = VisibleRange;
        _panning = _state.DecidePan(_interactionMode, properties.IsMiddleButtonPressed,
            e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        // Follow-live pauses on the first drag movement below, so a plain click never pauses it.
        if (!_panning)
        {
            _state.BeginSelection(_samples, ChartSeries.Nearest(_samples, TimeAt(point.X, layout), VisibleRange)?.Timestamp);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        var layout = EnsureLayout();
        if (_touchContacts.Count > 1 || _pinching) _dragStart = null;
        if (_dragStart is { } drag)
        {
            if (_state.IsFollowingLive)
            {
                _state.StopFollowingLive();
                ViewChanged?.Invoke(this, EventArgs.Empty);
            }
            if (_panning)
            {
                _state.PanFrom(_samples, _dragRange,
                    -(point.X - drag.X) / (layout.PlotRight - layout.PlotLeft) * _dragRange.Seconds);
                ChangedView();
            }
            else if (ChartSeries.Nearest(_samples, TimeAt(point.X, layout), VisibleRange) is { } sample &&
                     _state.ExtendSelection(_samples, sample.Timestamp))
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
            }
        }
        var hover = !_pinching && !(_dragStart is not null && _panning) && InPlot(point, layout)
            ? ChartSeries.Nearest(_samples, TimeAt(point.X, layout), VisibleRange) : null;
        if (_hover != hover)
        {
            _hover = hover;
            _hoverText = null;
            InvalidateVisual();
        }
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _touchContacts.Remove(e.Pointer);
        if (_touchContacts.Count == 0 && !_pinching)
        {
            _hasTouchSelectionSnapshot = false;
            _selectionBeforeTouch = null;
        }
        var wasDragging = _dragStart is not null;
        _dragStart = null;
        if (e.Pointer.Captured == this) e.Pointer.Capture(null);
        if (wasDragging) e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _touchContacts.Remove(e.Pointer);
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
        var layout = PowerChartLayout.CalculatePrepared(_samples, Bounds.Width, Bounds.Height, viewport: _state.Viewport);
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
