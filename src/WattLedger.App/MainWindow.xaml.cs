using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WattLedger.Core.Engine;
using WattLedger.Core.Reporting;

namespace WattLedger.App;

public partial class MainWindow : Window
{
    private readonly WattLedgerService _service;
    private readonly MainViewModel _viewModel;
    private DispatcherTimer? _liveTimer;
    private DispatcherTimer? _reportTimer;
    private bool _loaded;
    private bool _allowClose;

    public MainWindow(WattLedgerService service)
    {
        InitializeComponent();
        _service = service;
        _viewModel = new MainViewModel(service);
        DataContext = _viewModel;

        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(1120, Math.Max(960, workArea.Width - 40));
        Height = Math.Min(800, Math.Max(640, workArea.Height - 40));

        using (var icon = TrayIconFactory.Create())
        {
            Icon = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }

        _viewModel.ChartUpdated += (_, _) => DrawChart();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var dark = 1;
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
            }
        }
        catch
        {
            // Dark title bar is cosmetic; ignore on older Windows builds.
        }
    }

    public void AllowClose() => _allowClose = true;

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void HideToTray() => Hide();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            await _service.StartAsync();
            _viewModel.RefreshAll();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "WattLedger could not start", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _liveTimer.Tick += (_, _) => _viewModel.RefreshLive();
        _liveTimer.Start();

        _reportTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _reportTimer.Tick += (_, _) => _viewModel.RefreshReport();
        _reportTimer.Start();

        UpdateAutostartButton();
        DrawChart();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            _liveTimer?.Stop();
            _reportTimer?.Stop();
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"wattledger-{DateTime.Now:yyyy-MM-dd}.csv"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var start = new DateTimeOffset(DateTime.Today);
            var end = start.AddDays(1);
            var samples = _service.GetSamples(start, end);
            ReportBuilder.ExportCsv(samples, dialog.FileName);
            MessageBox.Show(this, $"Exported {samples.Count:N0} samples to:\n{dialog.FileName}", "WattLedger", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md",
            FileName = $"wattledger-{DateTime.Now:yyyy-MM-dd}.md"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ReportBuilder.ExportMarkdown(_service.GetTodayReport(), dialog.FileName);
            MessageBox.Show(this, $"Report written to:\n{dialog.FileName}", "WattLedger", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_service.DatabasePath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot open data folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AutostartService.SetEnabled(!AutostartService.IsEnabled);
            UpdateAutostartButton();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot update startup setting", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateAutostartButton()
    {
        AutostartButton.Content = AutostartService.IsEnabled
            ? "Start with Windows: On"
            : "Start with Windows: Off";
    }

    private void DrawChart()
    {
        ChartCanvas.Children.Clear();

        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        if (width < 80 || height < 80)
        {
            return;
        }

        var samples = _viewModel.ChartSamples;
        if (samples.Count < 2)
        {
            AddText("Collecting samples…", 12, 12, 12, FindBrush("MutedBrush"));
            return;
        }

        const double left = 46;
        const double right = 44;
        const double top = 10;
        const double bottom = 26;
        var plotWidth = Math.Max(10, width - left - right);
        var plotHeight = Math.Max(10, height - top - bottom);

        var start = samples[0].Timestamp;
        var end = samples[^1].Timestamp;
        var totalSeconds = Math.Max(1, (end - start).TotalSeconds);

        var maxPower = Math.Max(5, samples.Max(s => s.PowerWatts) * 1.15);
        maxPower = Math.Ceiling(maxPower / 5) * 5;

        var gridBrush = FindBrush("BorderBrush");
        var mutedBrush = FindBrush("MutedBrush");
        var greenBrush = FindBrush("GreenBrush");
        var blueBrush = FindBrush("BlueBrush");

        for (var i = 0; i <= 5; i++)
        {
            var y = top + plotHeight * i / 5;
            ChartCanvas.Children.Add(new Line
            {
                X1 = left,
                Y1 = y,
                X2 = left + plotWidth,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 1,
                Opacity = i is 0 or 5 ? 0.8 : 0.45
            });

            var powerLabel = maxPower * (1 - i / 5.0);
            AddText($"{powerLabel:0} W", 2, y - 7, 10, mutedBrush);

            var batteryLabel = 100 - i * 20;
            AddText($"{batteryLabel}%", left + plotWidth + 8, y - 7, 10, mutedBrush);
        }

        var powerPoints = new PointCollection();
        var batteryPoints = new PointCollection();
        var batteryAvailable = false;

        foreach (var sample in samples)
        {
            var x = left + (sample.Timestamp - start).TotalSeconds / totalSeconds * plotWidth;
            var powerY = top + plotHeight - Math.Clamp(sample.PowerWatts / maxPower, 0, 1) * plotHeight;
            powerPoints.Add(new Point(x, powerY));

            if (sample.BatteryPercent is { } percent)
            {
                batteryAvailable = true;
                var batteryY = top + plotHeight - Math.Clamp(percent / 100.0, 0, 1) * plotHeight;
                batteryPoints.Add(new Point(x, batteryY));
            }
        }

        if (powerPoints.Count >= 2)
        {
            var areaPoints = new PointCollection(powerPoints);
            areaPoints.Add(new Point(left + plotWidth, top + plotHeight));
            areaPoints.Add(new Point(left, top + plotHeight));

            ChartCanvas.Children.Add(new Polygon
            {
                Points = areaPoints,
                Fill = new LinearGradientBrush(
                    Color.FromArgb(80, 61, 220, 151),
                    Color.FromArgb(0, 61, 220, 151),
                    new Point(0, 0),
                    new Point(0, 1)),
                StrokeThickness = 0
            });

            ChartCanvas.Children.Add(new Polyline
            {
                Points = powerPoints,
                Stroke = greenBrush,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        if (batteryAvailable && batteryPoints.Count >= 2)
        {
            ChartCanvas.Children.Add(new Polyline
            {
                Points = batteryPoints,
                Stroke = blueBrush,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 4 },
                StrokeDashCap = PenLineCap.Round
            });
        }

        AddText(start.ToLocalTime().ToString("HH:mm"), left, top + plotHeight + 6, 10, mutedBrush);
        AddText(end.ToLocalTime().ToString("HH:mm"), left + plotWidth - 34, top + plotHeight + 6, 10, mutedBrush);
    }

    private void AddText(string text, double x, double y, double fontSize, Brush brush)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            Foreground = brush,
            FontFamily = new FontFamily("Segoe UI")
        };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        ChartCanvas.Children.Add(block);
    }

    private Brush FindBrush(string key) =>
        TryFindResource(key) as Brush ?? Brushes.Gray;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
