using DesktopThemeMode = WattLedger.Desktop.ThemeMode;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WattLedger.Core.Engine;
using WattLedger.Core.Reporting;
using WattLedger.Desktop;

namespace WattLedger.App;

public partial class MainWindow : Window
{
    private readonly WattLedgerService _service;
    private readonly MainViewModel _viewModel;
    private DispatcherTimer? _liveTimer;
    private DispatcherTimer? _reportTimer;
    private bool _loaded;
    private bool _allowClose;
    private bool _stopping;
    private Task _refreshTask = Task.CompletedTask;
    private Task _exportTask = Task.CompletedTask;
    private bool CanRefresh => !_stopping && IsVisible && WindowState != WindowState.Minimized;

    public MainWindow(WattLedgerService service)
    {
        InitializeComponent();
        _service = service;
        _viewModel = new MainViewModel(service);
        DataContext = _viewModel;
        ThemeSelector.ItemsSource = Enum.GetValues<DesktopThemeMode>();
        ThemeSelector.SelectedItem = (Application.Current as App)?.CurrentThemeMode ?? DesktopThemeMode.System;
        if (Application.Current is App app) app.ThemeChanged += OnThemeChanged;
        Closed += (_, _) =>
        {
            if (Application.Current is App current) current.ThemeChanged -= OnThemeChanged;
        };

        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(1120, Math.Max(960, workArea.Width - 40));
        Height = Math.Min(1000, Math.Max(640, workArea.Height - 40));

        using (var icon = TrayIconFactory.Create())
        {
            Icon = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }

        _viewModel.ChartUpdated += (_, _) => { Chart.Samples = _viewModel.ChartSamples; };
        Chart.SelectionChanged += (_, _) => UpdateSelectionSummary();
        Chart.ViewChanged += (_, _) => UpdateChartRange();
        IsVisibleChanged += (_, _) => RefreshIfVisible();
        StateChanged += (_, _) => RefreshIfVisible();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        UpdateTitleBarTheme();
    }

    private void UpdateTitleBarTheme()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            var dark = (Application.Current as App)?.IsDarkTheme == true ? 1 : 0;
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

    private void OnThemeChanged(object? sender, EventArgs args)
    {
        UpdateTitleBarTheme();
        _viewModel.RefreshTheme();
        DrawChart();
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (Application.Current is not App app || ThemeSelector.SelectedItem is not DesktopThemeMode mode || mode == app.CurrentThemeMode) return;
        try { app.SetThemeMode(mode); }
        catch (Exception ex)
        {
            ThemeSelector.SelectedItem = app.CurrentThemeMode;
            MessageBox.Show(this, ex.Message, "Could not save theme preference", MessageBoxButton.OK, MessageBoxImage.Error);
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
        RefreshIfVisible();
    }

    public void HideToTray() => Hide();

    public void SetStartupFailure(string message) => _viewModel.SetStartupFailure(message);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        RefreshIfVisible();

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _liveTimer.Tick += (_, _) => { if (CanRefresh) _viewModel.RefreshLive(); };
        _liveTimer.Start();

        _reportTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _reportTimer.Tick += (_, _) => RefreshIfVisible();
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

    private void RefreshIfVisible()
    {
        if (!_loaded || !CanRefresh) return;
        _viewModel.RefreshLive();
        if (_refreshTask.IsCompleted) _refreshTask = _viewModel.RefreshReportAsync();
    }

    public void InvalidateReport()
    {
        _viewModel.InvalidateReport();
        if (!Dispatcher.CheckAccess()) Dispatcher.BeginInvoke(RefreshIfVisible);
        else RefreshIfVisible();
    }

    public async Task StopBackgroundWorkAsync()
    {
        _stopping = true;
        _liveTimer?.Stop();
        _reportTimer?.Stop();
        try { await Task.WhenAll(_refreshTask, _exportTask); }
        catch { /* Export/report handlers already surface operation failures. */ }
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e) => await ExportAsync("csv");
    private async void ExportReport_Click(object sender, RoutedEventArgs e) => await ExportAsync("md");
    private async void ExportJson_Click(object sender, RoutedEventArgs e) => await ExportAsync("json");

    private async Task ExportAsync(string format)
    {
        if (!_exportTask.IsCompleted || _stopping) return;
        var dialog = new SaveFileDialog
        {
            Filter = format switch { "csv" => "CSV (*.csv)|*.csv", "json" => "JSON (*.json)|*.json", _ => "Markdown (*.md)|*.md" },
            FileName = $"wattledger-{DateTime.Now:yyyy-MM-dd}.{format}"
        };
        if (dialog.ShowDialog(this) != true || _stopping) return;
        try
        {
            var date = DateOnly.FromDateTime(DateTime.Today);
            var end = DateTimeOffset.Now;
            var path = dialog.FileName;
            _exportTask = Task.Run(() =>
            {
                if (format == "csv")
                {
                    var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local));
                    ReportBuilder.ExportCsv(_service.GetSamples(start, end), path);
                }
                else if (format == "json") ReportBuilder.ExportJson(_service.GetReport(date), path);
                else ReportBuilder.ExportMarkdown(_service.GetReport(date), path);
            });
            await _exportTask;
            if (!_stopping) MessageBox.Show(this, $"Exported to:\n{path}", "WattLedger", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            if (!_stopping) MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void DrawChart() => Chart.InvalidateTheme();

    private void PanMode_Click(object sender, RoutedEventArgs e) => SetChartMode(ChartInteractionMode.Pan);
    private void SelectMode_Click(object sender, RoutedEventArgs e) => SetChartMode(ChartInteractionMode.Select);
    private void SetChartMode(ChartInteractionMode mode)
    {
        Chart.InteractionMode = mode;
        Chart.Cursor = mode == ChartInteractionMode.Pan ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Cross;
        PanModeButton.Content = mode == ChartInteractionMode.Pan ? "Pan ✓" : "Pan";
        SelectModeButton.Content = mode == ChartInteractionMode.Select ? "Select points ✓" : "Select points";
    }
    private void Earlier_Click(object sender, RoutedEventArgs e) => Chart.Pan(-Chart.VisibleRange.Seconds * .2);
    private void Later_Click(object sender, RoutedEventArgs e) => Chart.Pan(Chart.VisibleRange.Seconds * .2);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Chart.Zoom(.8);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Chart.Zoom(1.25);
    private void ResetChart_Click(object sender, RoutedEventArgs e) => Chart.ResetView();
    private void FollowLive_Click(object sender, RoutedEventArgs e) => Chart.FollowLive();
    private void FitSelection_Click(object sender, RoutedEventArgs e) => Chart.ZoomToSelection();
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => Chart.ClearSelection();
    private void UpdateChartRange()
    {
        var range = Chart.VisibleRange;
        ChartRange.Text = Chart.Samples.Count == 0 ? "No recorded samples today" :
            $"{range.Start.ToLocalTime():HH:mm:ss} – {range.End.ToLocalTime():HH:mm:ss} · {Chart.Samples.Count:N0} original samples · " +
            (Chart.IsFollowingLive ? "following live" : "paused view");
        EarlierButton.IsEnabled = Chart.CanPanEarlier;
        LaterButton.IsEnabled = Chart.CanPanLater;
        FollowButton.Content = Chart.IsFollowingLive ? "Following live ✓" : "Follow live";
    }
    private void UpdateSelectionSummary()
    {
        if (Chart.Selection is not { } stats)
        {
            SelectionStart.Text = SelectionEnd.Text = "";
            SelectionSummary.Text = "Choose Select points and drag, or enter two times to calculate a range.";
            return;
        }
        if (!SelectionStart.IsKeyboardFocused && !SelectionEnd.IsKeyboardFocused)
        {
            SelectionStart.Text = stats.Start.ToLocalTime().ToString("HH:mm:ss");
            SelectionEnd.Text = stats.End.ToLocalTime().ToString("HH:mm:ss");
        }
        var energy = stats.CoveredSeconds > 0
            ? $"net {stats.NetEnergyWh:+0.000;-0.000;0.000} Wh · used {stats.DischargeEnergyWh:0.000} Wh · charged {stats.ChargeEnergyWh:0.000} Wh · avg {stats.AverageWatts:0.00} W"
            : "No continuous power readings in this range";
        SelectionSummary.Text = $"{stats.SampleCount:N0} points · {ReportBuilder.FormatDuration(stats.DurationSeconds)} · {energy}\n" +
            $"Coverage {stats.Coverage:P1} ({ReportBuilder.FormatDuration(stats.CoveredSeconds)})" +
            (stats.Coverage < 1 ? " · partial" : "") +
            (stats.MinimumWatts is { } min && stats.MaximumWatts is { } max ? $" · min {min:0.00} / max {max:0.00} W" : "");
    }
    private void CalculateSelection_Click(object sender, RoutedEventArgs e)
    {
        string[] formats = ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss"];
        if (!TimeOnly.TryParseExact(SelectionStart.Text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !TimeOnly.TryParseExact(SelectionEnd.Text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) || end <= start)
        {
            SelectionSummary.Text = "Enter two times as HH:mm or HH:mm:ss, with the end after the start.";
            return;
        }
        var date = Chart.Samples.Count > 0 ? DateOnly.FromDateTime(Chart.Samples[0].Timestamp.LocalDateTime) : DateOnly.FromDateTime(DateTime.Today);
        var from = new DateTimeOffset(date.ToDateTime(start, DateTimeKind.Local));
        var to = new DateTimeOffset(date.ToDateTime(end, DateTimeKind.Local));
        if (Chart.Samples.Count == 0 || to < Chart.Samples[0].Timestamp || from > Chart.Samples[^1].Timestamp)
        {
            SelectionSummary.Text = "Choose a range that overlaps today's recorded samples.";
            return;
        }
        Chart.SelectRange(from, to);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
