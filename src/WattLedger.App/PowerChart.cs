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
    private readonly ChartInteractionState _state = new();
    private PowerChartLayout? _layout;
    private Size _layoutSize;
    private readonly List<(Brush? Fill, Pen? Pen, StreamGeometry Geometry)> _geometry = [];
    private readonly List<(Brush Brush, Point Position, double Radius)> _dots = [];
    private readonly List<(FormattedText Text, Point Position)> _labels = [];
    private Point? _dragStart;
    private ChartTimeRange _dragRange;
    private PowerPoint? _hover;
    private FormattedText? _hoverText;
    private FormattedText? _emptyText;
    private bool _panning;
    private HwndSource? _source;
    private readonly WindowsChartZoomSession _zoomInput = new();
    private static readonly bool TraceInput = Environment.GetEnvironmentVariable("WATTLEDGER_INPUT_TRACE") == "1";
    // Render assets are rebuilt with the layout (theme changes clear it) and frozen so each frame only draws.
    private Pen? _gridPen;
    private Pen? _zeroPen;
    private Pen? _edgePen;
    private Pen? _cursorPen;
    private Pen? _powerPen;
    private Pen? _batteryPen;
    private Brush? _selectionFill;
    private Brush? _hoverBackBrush;

    public PowerChart()
    {
        Focusable = true;
        ClipToBounds = true;
        IsManipulationEnabled = true;
        Cursor = Cursors.Hand;
        Loaded += (_, _) =>
        {
            _source?.RemoveHook(WindowMessage);
            _source = PresentationSource.FromVisual(this) as HwndSource;
            _source?.AddHook(WindowMessage);
        };
        Unloaded += (_, _) => { _source?.RemoveHook(WindowMessage); _source = null; ResetGestureInput(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) ResetGestureInput(); };
    }

    public event EventHandler? SelectionChanged;
    public event EventHandler? ViewChanged;
    internal ChartInteractionMode InteractionMode { get; set; }
    public bool IsFollowingLive => _state.IsFollowingLive;
    internal ChartSelectionStatistics? Selection => _state.Selection;
    internal ChartTimeRange VisibleRange => _state.VisibleRange(_samples);
    internal bool CanPanEarlier => VisibleRange.Start > ChartSeries.Extent(_samples).Start;
    internal bool CanPanLater => VisibleRange.End < ChartSeries.Extent(_samples).End;
    internal IReadOnlyList<PowerPoint> Samples
    {
        get => _samples;
        set
        {
            _samples = ChartSeries.Normalize(value);
            var before = _state.Selection;
            _state.OnSamplesChanged(_samples);
            if (!ReferenceEquals(before, _state.Selection)) SelectionChanged?.Invoke(this, EventArgs.Empty);
            ChangedView();
        }
    }

    public void Zoom(double factor) => ZoomAt(factor, .5);
    public void Pan(double seconds)
    {
        if (!double.IsFinite(seconds)) return;
        _state.Pan(_samples, seconds);
        ChangedView();
    }
    public void FollowLive()
    {
        _state.FollowLive(_samples);
        ChangedView();
    }
    public void ResetView() { _state.ResetView(_samples); ChangedView(); }
    public void ClearSelection()
    {
        _state.ClearSelection();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }
    internal void SelectRange(DateTimeOffset start, DateTimeOffset end)
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
    public void InvalidateTheme() { _layout = null; _hoverText = null; _emptyText = null; InvalidateVisual(); }
    private void ChangedView()
    {
        _layout = null;
        _hover = null;
        _hoverText = null;
        ViewChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var layout = EnsureLayout();
        var point = e.GetPosition(this);
        if (!InPlot(point, layout)) return;
        ApplyWheel(e.Delta / 120d, point, horizontal: false,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    private void ApplyWheel(double steps, Point point, bool horizontal, bool control, bool shift)
    {
        if (TraceInput) Console.Error.WriteLine($"WattLedger chart input: wheel steps={steps:R}, horizontal={horizontal}, ctrl={control}, shift={shift}, zoomOwner={_zoomInput.IsActive}");
        // Consumed native/direct-touch gestures must not also apply promoted wheel packets.
        if (_zoomInput.IsActive || !double.IsFinite(steps) || steps == 0) return;
        if (!control && (horizontal || shift))
            Pan((horizontal ? 1 : -1) * Math.Clamp(steps, -5, 5) * VisibleRange.Seconds * .1);
        else
        {
            var factor = ChartGestureScale.WheelFactor(steps);
            if (factor != 1) ZoomAt(factor, AnchorAt(point.X, EnsureLayout()));
        }
    }

    private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is 0x0008 or 0x001F) // WM_KILLFOCUS / WM_CANCELMODE
        {
            ResetGestureInput();
            return IntPtr.Zero;
        }
        if (!IsVisible || ActualWidth < 150 || ActualHeight < 70) return IntPtr.Zero;
        if (msg == WindowsChartGestureInterop.Gesture)
        {
            if (!WindowsChartGestureInterop.TryRead(lParam, out var gesture)) return IntPtr.Zero;
            // GID_BEGIN/GID_END must reach DefWindowProc. Only a zoom we actually own closes its handle here.
            if (gesture.Id == WindowsChartGestureInterop.GestureEnd)
            {
                _zoomInput.EndNative();
                return IntPtr.Zero;
            }
            if (gesture.Id != WindowsChartGestureInterop.Zoom) return IntPtr.Zero;
            var point = PointFromScreen(new Point(gesture.X, gesture.Y));
            var layout = EnsureLayout();
            if (!_zoomInput.NativeZoom(gesture.SequenceId, gesture.Flags, gesture.Arguments, InPlot(point, layout), out var factor))
                return IntPtr.Zero;
            handled = true;
            try
            {
                CancelPointerDrag();
                if (TraceInput) Console.Error.WriteLine($"WattLedger chart input: native zoom sequence={gesture.SequenceId}, flags={gesture.Flags}, distance={gesture.Arguments}, factor={factor:R}");
                if (factor != 1) ZoomAt(factor, AnchorAt(point.X, layout));
            }
            finally { WindowsChartGestureInterop.CloseGestureInfoHandle(lParam); }
            return IntPtr.Zero;
        }
        // Precision Touchpads can generate Ctrl+wheel without changing WPF's global keyboard state.
        // Read MK_CONTROL from the message and consume it before WPF can route a duplicate MouseWheel.
        var horizontal = msg == WindowsChartGestureInterop.MouseHorizontalWheel;
        if (!horizontal && (msg != WindowsChartGestureInterop.MouseWheel || !WindowsChartGestureInterop.IsControlDown(wParam)))
            return IntPtr.Zero;
        var location = WindowsChartGestureInterop.ScreenPoint(lParam);
        var wheelPoint = PointFromScreen(new Point(location.X, location.Y));
        if (!InPlot(wheelPoint, EnsureLayout())) return IntPtr.Zero;
        ApplyWheel(WindowsChartGestureInterop.WheelSteps(wParam), wheelPoint, horizontal,
            WindowsChartGestureInterop.IsControlDown(wParam), WindowsChartGestureInterop.IsShiftDown(wParam));
        handled = true;
        return IntPtr.Zero;
    }

    protected override void OnManipulationStarting(ManipulationStartingEventArgs e)
    {
        base.OnManipulationStarting(e);
        var contacts = e.Manipulators.Select(contact => contact.GetPosition(this)).ToArray();
        if (contacts.Length == 0 || !InPlot(new Point(contacts.Average(point => point.X), contacts.Average(point => point.Y)), EnsureLayout()))
        {
            e.Cancel();
            return;
        }
        // This is Windows Touch, not a promise that every touchpad produces WPF touch contacts.
        e.ManipulationContainer = this;
        e.Mode = ManipulationModes.Scale;
        e.IsSingleTouchEnabled = false;
        e.Handled = true;
    }

    protected override void OnManipulationStarted(ManipulationStartedEventArgs e)
    {
        base.OnManipulationStarted(e);
        if (!InPlot(e.ManipulationOrigin, EnsureLayout()) || !_zoomInput.BeginManipulation())
            Manipulation.CompleteManipulation(this);
        else CancelPointerDrag();
        e.Handled = true;
    }

    protected override void OnManipulationDelta(ManipulationDeltaEventArgs e)
    {
        base.OnManipulationDelta(e);
        if (!_zoomInput.IsManipulationActive || e.IsInertial)
        {
            e.Complete();
            e.Handled = true;
            return;
        }
        var scale = e.DeltaManipulation.Scale;
        var factor = WindowsChartGestureInterop.ManipulationFactor(scale.X, scale.Y);
        if (TraceInput) Console.Error.WriteLine($"WattLedger chart input: WPF manipulation scale=({scale.X:R},{scale.Y:R}), factor={factor:R}");
        if (factor != 1) ZoomAt(factor, AnchorAt(e.ManipulationOrigin.X, EnsureLayout()));
        e.Handled = true;
    }

    protected override void OnManipulationCompleted(ManipulationCompletedEventArgs e)
    {
        base.OnManipulationCompleted(e);
        _zoomInput.EndManipulation();
        e.Handled = true;
    }

    private void CancelPointerDrag()
    {
        _dragStart = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
    private void ResetGestureInput()
    {
        if (Manipulation.IsManipulationActive(this)) Manipulation.CompleteManipulation(this);
        _zoomInput.Reset();
        CancelPointerDrag();
    }
    private static double AnchorAt(double x, PowerChartLayout layout) => double.IsFinite(x)
        ? Math.Clamp((x - layout.PlotLeft) / (layout.PlotRight - layout.PlotLeft), 0, 1) : .5;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (_zoomInput.IsActive) { e.Handled = true; return; }
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Middle)) return;
        var point = e.GetPosition(this);
        var layout = EnsureLayout();
        if (!InPlot(point, layout)) return;
        Focus();
        if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left) { ResetView(); e.Handled = true; return; }
        _dragStart = point;
        _dragRange = VisibleRange;
        // Follow-live pauses on the first drag movement below, so a plain click never pauses it.
        ViewChanged?.Invoke(this, EventArgs.Empty);
        _panning = _state.DecidePan(InteractionMode, e.ChangedButton == MouseButton.Middle,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        if (!_panning)
        {
            _state.BeginSelection(_samples, ChartSeries.Nearest(_samples, TimeAt(point.X, layout), VisibleRange)?.Timestamp);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
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
            if (_state.IsFollowingLive)
            {
                // First actual movement pauses follow-live; a press without movement keeps following.
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
        var hover = !(_dragStart is not null && _panning) && InPlot(point, layout)
            ? ChartSeries.Nearest(_samples, TimeAt(point.X, layout), VisibleRange) : null;
        if (_hover != hover) _hoverText = null;
        _hover = hover;
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
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); _hover = null; _hoverText = null; InvalidateVisual(); }
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
        var gridPen = _gridPen!;
        context.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        for (var i = 0; i <= 5; i++)
        {
            var y = layout.PlotTop + (layout.PlotBottom - layout.PlotTop) * i / 5;
            context.DrawLine(gridPen, new Point(layout.PlotLeft, y), new Point(layout.PlotRight, y));
        }
        context.DrawLine(_zeroPen!,
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
            context.DrawRectangle(_selectionFill!, null, new Rect(left, layout.PlotTop, Math.Max(0, right - left), layout.PlotBottom - layout.PlotTop));
            context.DrawLine(_edgePen!, new Point(left, layout.PlotTop), new Point(left, layout.PlotBottom));
            context.DrawLine(_edgePen!, new Point(right, layout.PlotTop), new Point(right, layout.PlotBottom));
        }
        if (_hover is { } hover)
        {
            var x = XAt(hover.Timestamp, layout);
            context.DrawLine(_cursorPen!,
                new Point(x, layout.PlotTop), new Point(x, layout.PlotBottom));
        }
        context.Pop();
        if (_hover is { } hovered)
        {
            var power = hovered.Watts is { } watts ? $"{watts:+0.00;-0.00;0.00} W" : "power unavailable";
            var percent = hovered.BatteryPercent is { } battery ? $"{battery}%" : "battery unavailable";
            var text = _hoverText ??= Text($"{hovered.Timestamp.ToLocalTime():MM-dd HH:mm:ss} · {power} · {percent}", 11);
            var x = Math.Max(layout.PlotLeft, Math.Min(XAt(hovered.Timestamp, layout) + 10, ActualWidth - text.Width - 12));
            context.DrawRoundedRectangle(_hoverBackBrush!, null,
                new Rect(x - 5, layout.PlotTop + 4, text.Width + 10, text.Height + 8), 4, 4);
            context.DrawText(text, new Point(x, layout.PlotTop + 8));
        }
        if (layout.ActualStart is null) context.DrawText(_emptyText ??= Text("No samples in this range", 12), new Point(layout.PlotLeft + 10, layout.PlotTop + 12));
    }

    private PowerChartLayout EnsureLayout()
    {
        if (_layout is not null && _layoutSize == RenderSize) return _layout;
        _layoutSize = RenderSize;
        var layout = PowerChartLayout.CalculatePrepared(_samples, ActualWidth, ActualHeight, viewport: _state.Viewport);
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
        var area = power.CloneCurrentValue(); area.Opacity = .16; area.Freeze();
        _powerPen = new Pen(power, 2.2) { LineJoin = PenLineJoin.Round }; _powerPen.Freeze();
        _batteryPen = new Pen(battery, 1.6) { DashStyle = DashStyles.Dash }; _batteryPen.Freeze();
        _gridPen = new Pen(Resource("ChartGridBrush", "BorderBrush"), 1); _gridPen.Freeze();
        _zeroPen = new Pen(Resource("ChartZeroBrush", "MutedBrush"), 1.2); _zeroPen.Freeze();
        _edgePen = new Pen(battery, 1); _edgePen.Freeze();
        _cursorPen = new Pen(Resource("MutedBrush"), 1) { DashStyle = DashStyles.Dot }; _cursorPen.Freeze();
        _selectionFill = Resource("ChartSelectionBrush", "BlueBrush").CloneCurrentValue();
        if (TryFindResource("ChartSelectionBrush") is null) _selectionFill.Opacity = .18;
        _selectionFill.Freeze();
        _hoverBackBrush = Resource("ChartHoverBrush", "CardBrush");
        foreach (var run in layout.PowerRuns)
        {
            if (run.Points.Count == 1) _dots.Add((power, PointAt(run.Points[0]), 3.5));
            else { _geometry.Add((area, null, Geometry(run.Points, layout.ZeroY))); _geometry.Add((null, _powerPen, Geometry(run.Points))); }
        }
        foreach (var run in layout.BatteryRuns)
        {
            if (run.Count == 1) _dots.Add((battery, PointAt(run[0]), 2.5));
            else _geometry.Add((null, _batteryPen, Geometry(run)));
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
