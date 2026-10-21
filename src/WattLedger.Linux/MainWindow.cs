using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WattLedger.Core.Engine;
using WattLedger.Core.Models;
using WattLedger.Core.Reporting;

namespace WattLedger.Linux;

internal sealed class MainWindow : Window
{
    private readonly WattLedgerService _service;
    private readonly bool _startInTray;
    private readonly Action _shutdown;
    private readonly TextBlock _power = ValueText("—", 34, "#3DDC97");
    private readonly TextBlock _powerDetail = DetailText("Waiting for the first sample");
    private readonly TextBlock _battery = ValueText("—", 30, "#E9EEF5");
    private readonly TextBlock _batteryDetail = DetailText("Battery state unavailable");
    private readonly TextBlock _remaining = ValueText("—", 30, "#5AA9FF");
    private readonly TextBlock _remainingDetail = DetailText("Estimated from recent samples");
    private readonly TextBlock _today = ValueText("0.0 Wh", 30, "#FFB454");
    private readonly TextBlock _todayDetail = DetailText("No recorded battery use yet");
    private readonly TextBlock _status = DetailText("Starting local sampler…");
    private readonly TextBlock _platformStatus = DetailText("");
    private readonly TextBlock _powerState = DetailText("STARTING");
    private readonly TextBlock _sampleCount = DetailText("No samples yet");
    private readonly StackPanel _ledgerRows = new() { Spacing = 0 };
    private readonly PowerChart _chart = new();
    private readonly Button _startupButton;
    private readonly TrayIcon _trayIcon;
    private readonly DispatcherTimer _trayPollTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _trayHostAvailable;
    private bool _trayRegistered;
    private bool _allowClose;
    private bool _disposed;
    private bool _checkingClose;

    public MainWindow(WattLedgerService service, bool startInTray, Action shutdown)
    {
        _service = service;
        _startInTray = startInTray;
        _shutdown = shutdown;
        _startupButton = ActionButton(StartupRegistration.IsEnabled ? "Disable start at login" : "Start at login", ToggleStartup);
        Title = "WattLedger";
        Width = 900;
        Height = 720;
        MinWidth = 740;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = LedgerIcon.CreateWindowIcon();
        Content = CreateContent();
        _trayIcon = CreateTrayIcon();

        _service.StatusUpdated += OnStatusUpdated;
        _service.SampleRecorded += OnSampleRecorded;
        _service.Error += OnError;
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
        _trayPollTimer.Tick += OnTrayPoll;
    }

    public void SetStartupError(string message) => Post(() => _status.Text = message);

    public void SetPlatformStatus(string message) => Post(() => _platformStatus.Text = message);

    private Control CreateContent()
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Spacing = 18,
                Margin = new Thickness(26, 22, 26, 20),
                Children =
                {
                    CreateHeader(),
                    CreateMetricGrid(),
                    CreatePanel("TODAY’S POWER CURVE", _chart, 156),
                    CreateLedgerPanel(),
                    CreateFooter()
                }
            }
        };
        return scroll;
    }

    private Control CreateHeader()
    {
        var title = new TextBlock
        {
            Text = "WattLedger",
            FontSize = 25,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("#F3F6FA")
        };
        var sub = new TextBlock
        {
            Text = "A quiet, local energy journal",
            FontSize = 12,
            Foreground = Brush("#8B98A9"),
            Margin = new Thickness(0, 3, 0, 0)
        };
        var titleStack = new StackPanel { Children = { title, sub } };
        _powerState.FontSize = 11;
        _powerState.FontWeight = FontWeight.SemiBold;
        _powerState.Foreground = Brush("#5AA9FF");
        _powerState.HorizontalAlignment = HorizontalAlignment.Right;
        _powerState.VerticalAlignment = VerticalAlignment.Center;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(titleStack);
        Grid.SetColumn(_powerState, 1);
        header.Children.Add(_powerState);
        return header;
    }

    private Control CreateMetricGrid()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
            ColumnSpacing = 12
        };
        AddMetric(grid, 0, "POWER", _power, _powerDetail);
        AddMetric(grid, 1, "BATTERY", _battery, _batteryDetail);
        AddMetric(grid, 2, "TIME LEFT", _remaining, _remainingDetail);
        AddMetric(grid, 3, "USED TODAY", _today, _todayDetail);
        return grid;
    }

    private static void AddMetric(Grid grid, int column, string label, TextBlock value, TextBlock detail)
    {
        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("#8793A2")
        });
        stack.Children.Add(value);
        stack.Children.Add(detail);
        var card = new Border
        {
            Background = Brush("#18212C"),
            BorderBrush = Brush("#273341"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(15, 13),
            Child = stack
        };
        Grid.SetColumn(card, column);
        grid.Children.Add(card);
    }

    private static Control CreatePanel(string heading, Control child, double height)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(PanelHeading(heading));
        body.Children.Add(new Border
        {
            Height = height,
            Background = Brush("#141C26"),
            CornerRadius = new CornerRadius(9),
            Child = child
        });
        return body;
    }

    private Control CreateLedgerPanel()
    {
        var panel = new StackPanel { Spacing = 10 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(PanelHeading("TODAY’S LEDGER"));
        _sampleCount.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_sampleCount, 1);
        header.Children.Add(_sampleCount);
        panel.Children.Add(header);

        panel.Children.Add(LedgerHeader());
        var ledger = new ScrollViewer
        {
            MaxHeight = 230,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _ledgerRows
        };
        panel.Children.Add(new Border
        {
            Background = Brush("#141C26"),
            BorderBrush = Brush("#273341"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Child = ledger
        });
        return panel;
    }

    private Control CreateFooter()
    {
        var bar = new WrapPanel();
        bar.Children.Add(ActionButton("Export CSV", () => ExportAsync("csv")));
        bar.Children.Add(ActionButton("Export Markdown", () => ExportAsync("md")));
        bar.Children.Add(ActionButton("Export JSON", () => ExportAsync("json")));
        bar.Children.Add(ActionButton("Open data folder", OpenDataFolder));
        bar.Children.Add(_startupButton);
        bar.Children.Add(ActionButton("Hide to tray", HideToTrayAsync));
        bar.Children.Add(ActionButton("Quit", Quit));
        var footer = new StackPanel { Spacing = 9 };
        footer.Children.Add(bar);
        footer.Children.Add(_status);
        footer.Children.Add(_platformStatus);
        return footer;
    }

    private static TextBlock PanelHeading(string value) => new()
    {
        Text = value,
        FontSize = 10,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brush("#A9B5C3")
    };

    private static Button ActionButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(12, 7),
            FontSize = 11,
            Background = Brush("#202B38"),
            Foreground = Brush("#E9EEF5"),
            BorderBrush = Brush("#334253"),
            BorderThickness = new Thickness(1)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private TrayIcon CreateTrayIcon()
    {
        var tray = new TrayIcon
        {
            Icon = LedgerIcon.CreateWindowIcon(),
            ToolTipText = "WattLedger",
            IsVisible = true,
            Menu = CreateTrayMenu()
        };
        tray.Clicked += (_, _) => ShowWindow();
        try
        {
            TrayIcon.SetIcons(Application.Current!, new TrayIcons { tray });
            _trayRegistered = true;
        }
        catch (Exception ex)
        {
            _platformStatus.Text = $"System tray unavailable: {ex.Message}";
        }

        return tray;
    }

    private NativeMenu CreateTrayMenu()
    {
        var menu = new NativeMenu();
        var show = new NativeMenuItem("Open WattLedger");
        show.Click += (_, _) => ShowWindow();
        var data = new NativeMenuItem("Open data folder");
        data.Click += (_, _) => OpenDataFolder();
        var exit = new NativeMenuItem("Quit");
        exit.Click += (_, _) => Quit();
        menu.Add(show);
        menu.Add(data);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(exit);
        return menu;
    }

    private async void OnOpened(object? sender, EventArgs args)
    {
        _trayPollTimer.Start();
        try
        {
            var hasWatcher = await TrayHostDetector.HasWatcherAsync();
            _trayHostAvailable = hasWatcher && _trayRegistered;
            if (!_trayHostAvailable)
            {
                _platformStatus.Text = "No StatusNotifier host found. WattLedger stays available in this window.";
                return;
            }

            if (_startInTray)
            {
                Hide();
            }
        }
        catch (Exception ex)
        {
            _trayHostAvailable = false;
            _platformStatus.Text = $"Could not confirm a tray host; window stays available. {ex.Message}";
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        if (_checkingClose) return;
        _checkingClose = true;
        _ = CheckCloseDestinationAsync();
    }

    private async Task CheckCloseDestinationAsync()
    {
        bool available;
        try { available = _trayRegistered && await TrayHostDetector.HasWatcherAsync(); }
        catch { available = false; }

        _trayHostAvailable = available;
        _checkingClose = false;
        if (available)
        {
            Hide();
            _platformStatus.Text = "WattLedger is still running in the system tray.";
        }
        else
        {
            _platformStatus.Text = "No StatusNotifier host is available; closing WattLedger.";
            _allowClose = true;
            _shutdown();
        }
    }

    private async void HideToTrayAsync()
    {
        try
        {
            if (_trayRegistered && await TrayHostDetector.HasWatcherAsync())
            {
                _trayHostAvailable = true;
                Hide();
                _platformStatus.Text = "WattLedger is still running in the system tray.";
                return;
            }
        }
        catch { }

        _trayHostAvailable = false;
        _platformStatus.Text = "No StatusNotifier host is available; the window stays open.";
    }

    private async void OnTrayPoll(object? sender, EventArgs args)
    {
        try
        {
            var available = _trayRegistered && await TrayHostDetector.HasWatcherAsync();
            if (_trayHostAvailable && !available && !IsVisible)
            {
                ShowWindow();
                _platformStatus.Text = "System tray host disappeared; WattLedger window restored.";
            }
            _trayHostAvailable = available;
        }
        catch
        {
            if (_trayHostAvailable && !IsVisible)
            {
                ShowWindow();
                _platformStatus.Text = "System tray host is unreachable; WattLedger window restored.";
            }
            _trayHostAvailable = false;
        }
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        _disposed = true;
        _trayPollTimer.Stop();
        _service.StatusUpdated -= OnStatusUpdated;
        _service.SampleRecorded -= OnSampleRecorded;
        _service.Error -= OnError;
        _trayIcon.Dispose();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons());
    }

    private void OnStatusUpdated(object? sender, LedgerStatus status) => Post(() => ApplyStatus(status));

    private void OnSampleRecorded(object? sender, SampleRecord sample) => Post(RefreshReport);

    private void OnError(object? sender, string message) => Post(() => _status.Text = $"Sampling error: {message}");

    private void ApplyStatus(LedgerStatus status)
    {
        var battery = status.Battery;
        var power = battery.IsDischarging ? battery.DischargeWatts : battery.IsCharging ? battery.ChargeWatts : null;
        var estimatedPower = false;
        if (power is null && battery.IsDischarging && status.RecentAverageWatts is > 0)
        {
            power = status.RecentAverageWatts;
            estimatedPower = true;
        }
        else if (power is null && battery.IsCharging && status.RecentAverageWatts is < -0.1)
        {
            power = Math.Abs(status.RecentAverageWatts.Value);
            estimatedPower = true;
        }
        _power.Text = battery.IsDischarging && power is { } discharge
            ? $"−{discharge:0.0} W"
            : battery.IsCharging && power is { } charge
                ? $"+{charge:0.0} W"
                : "—";
        _power.Foreground = Brush(battery.IsCharging ? "#5AA9FF" : battery.IsDischarging ? "#3DDC97" : "#8B98A9");
        _powerDetail.Text = status.Rapl.PackageWatts is { } package
            ? $"SoC {package:0.0} W · CPU {status.Rapl.CpuWatts.GetValueOrDefault():0.0} W"
            : power is not null ? estimatedPower ? "Estimated from capacity change" : "Battery rate" : "Power rate unavailable";
        _battery.Text = battery.Percent is { } percent ? $"{percent}%" : "—";
        var health = battery.HealthPercent is { } h ? $" · {h:0}% health" : "";
        if (battery.PercentIsEstimated) health += " · estimated";
        var capacity = battery.RemainingCapacityMwh is { } remaining && battery.FullChargeCapacityMwh is { } full
            ? $"{remaining / 1000d:0.0} / {full / 1000d:0.0} Wh"
            : battery.DeviceName ?? "Capacity unavailable";
        _batteryDetail.Text = capacity + health;

        if (battery.IsDischarging && status.EstimatedRemainingHours is { } hours)
        {
            _remaining.Text = $"{hours:0.0} h";
            _remainingDetail.Text = status.RecentAverageWatts is { } average ? $"recent average {average:0.0} W" : "estimated remaining";
        }
        else if (battery.IsCharging && battery.ChargeWatts is > 0.1 && battery.FullChargeCapacityMwh is { } fullCharge && battery.RemainingCapacityMwh is { } remainingCharge)
        {
            _remaining.Text = $"{Math.Max(0, (fullCharge - remainingCharge) / 1000d / battery.ChargeWatts.Value):0.0} h";
            _remainingDetail.Text = "until full";
        }
        else
        {
            _remaining.Text = battery.IsAcOnline ? "AC" : "—";
            _remainingDetail.Text = battery.IsAcOnline ? "plugged in" : "waiting for discharge data";
        }

        _powerState.Text = battery.BatteryCount == 0 ? "NO BATTERY"
            : battery.IsCharging ? "CHARGING"
            : battery.IsDischarging ? "ON BATTERY"
            : battery.IsAcOnline ? "AC POWER" : "STATE UNKNOWN";
        _powerState.Foreground = Brush(battery.IsCharging || battery.IsAcOnline ? "#5AA9FF" : battery.IsDischarging ? "#3DDC97" : "#8B98A9");
        _status.Text = $"Recording locally · last sample {status.Timestamp.ToLocalTime():HH:mm:ss}";
        _trayIcon.ToolTipText = $"WattLedger · {_battery.Text} · {_power.Text}";
    }

    private void RefreshReport()
    {
        try
        {
            var report = _service.GetTodayReport();
            _today.Text = $"{report.DischargeWh:0.0} Wh";
            _todayDetail.Text = $"active {ReportBuilder.FormatDuration(report.ActiveSeconds)} · standby {ReportBuilder.FormatDuration(report.StandbySeconds)}" +
                                (report.StandbyEnergyIncomplete ? " · some sleep-gap energy unavailable" : "");

            var start = new DateTimeOffset(DateTime.Today);
            var now = DateTimeOffset.Now;
            var samples = _service.GetSamples(start, now, 240);
            _chart.Samples = samples.Select(sample => new PowerPoint(sample.Timestamp, sample.NetDischargeWatts)).ToArray();
            _sampleCount.Text = $"{samples.Count} samples";
            _ledgerRows.Children.Clear();
            foreach (var session in report.Sessions.Where(session => session.DurationSeconds > 0)
                         .OrderByDescending(session => session.Start).Take(40))
            {
                _ledgerRows.Children.Add(LedgerRow(session));
            }

            if (report.Sessions.Count == 0)
            {
                _ledgerRows.Children.Add(new TextBlock
                {
                    Text = "Ledger entries appear as battery and charging sessions are recorded.",
                    Foreground = Brush("#8390A0"),
                    FontSize = 11,
                    Margin = new Thickness(12, 10)
                });
            }
        }
        catch (Exception ex)
        {
            _status.Text = $"Report unavailable: {ex.Message}";
        }
    }

    private static Control LedgerHeader()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.15*,1.15*,1.1*,1.1*,1*,1*"),
            Margin = new Thickness(12, 0, 12, 5)
        };
        string[] headings = ["START", "END", "STATE", "DURATION", "BATTERY", "ENERGY"];
        for (var index = 0; index < headings.Length; index++)
        {
            var label = new TextBlock { Text = headings[index], FontSize = 9, Foreground = Brush("#758395") };
            Grid.SetColumn(label, index);
            grid.Children.Add(label);
        }
        return grid;
    }

    private static Control LedgerRow(SessionRecord session)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.15*,1.15*,1.1*,1.1*,1*,1*"),
            Margin = new Thickness(12, 8, 12, 8)
        };
        var fields = new[]
        {
            session.Start.ToLocalTime().ToString("HH:mm"),
            session.End.ToLocalTime().ToString("HH:mm"),
            SessionStateLabel(session),
            ReportBuilder.FormatDuration(session.DurationSeconds),
            $"{FormatPercent(session.StartPercent)} → {FormatPercent(session.EndPercent)}",
            (session.Type is "SleepGap" or "ModernStandby") &&
            session.Note?.Contains("energy unavailable", StringComparison.OrdinalIgnoreCase) == true
                ? "Unavailable"
                : $"{session.EnergyWh:0.00} Wh"
        };
        for (var index = 0; index < fields.Length; index++)
        {
            var text = new TextBlock { Text = fields[index], FontSize = 10, Foreground = Brush("#CDD5DF") };
            Grid.SetColumn(text, index);
            grid.Children.Add(text);
        }

        return new Border
        {
            BorderBrush = Brush("#25313E"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid
        };
    }

    private static string SessionStateLabel(SessionRecord session)
    {
        if (session.Type == "ModernStandby")
        {
            return "Modern Standby";
        }

        if (session.Type == "SleepGap")
        {
            var confirmed = session.Note?.Contains("Suspend/resume event confirmed", StringComparison.Ordinal) == true;
            return confirmed ? "Sleep gap (confirmed)" : "Sleep gap (inferred)";
        }

        return ReportBuilder.StateLabel(session.Type).Split('·')[0].Trim();
    }

    private async void ExportAsync(string format)
    {
        try
        {
            var extension = format == "md" ? "md" : format;
            var type = format switch
            {
                "csv" => new FilePickerFileType("CSV") { Patterns = ["*.csv"] },
                "md" => new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
                _ => new FilePickerFileType("JSON") { Patterns = ["*.json"] }
            };
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = $"Export today as {format.ToUpperInvariant()}",
                SuggestedFileName = $"wattledger-{DateTime.Today:yyyy-MM-dd}.{extension}",
                FileTypeChoices = [type],
                DefaultExtension = extension
            });
            var path = file?.TryGetLocalPath();
            if (path is null) return;

            var report = _service.GetTodayReport();
            switch (format)
            {
                case "csv":
                    var start = new DateTimeOffset(DateTime.Today);
                    ReportBuilder.ExportCsv(_service.GetSamples(start, DateTimeOffset.Now), path);
                    break;
                case "md":
                    ReportBuilder.ExportMarkdown(report, path);
                    break;
                default:
                    ReportBuilder.ExportJson(report, path);
                    break;
            }

            _status.Text = $"Exported to {path}";
        }
        catch (Exception ex)
        {
            _status.Text = $"Export failed: {ex.Message}";
        }
    }

    private void OpenDataFolder()
    {
        try
        {
            var directory = Path.GetDirectoryName(_service.DatabasePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("The database folder is not available.");
            }

            Directory.CreateDirectory(directory);
            var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(directory);
            Process.Start(start);
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not open data folder: {ex.Message}";
        }
    }

    private void ToggleStartup()
    {
        try
        {
            StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled, _service.DatabasePath);
            _startupButton.Content = StartupRegistration.IsEnabled ? "Disable start at login" : "Start at login";
            _status.Text = StartupRegistration.IsEnabled ? "WattLedger will start at login." : "Start at login disabled.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not update start-at-login setting: {ex.Message}";
        }
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Quit()
    {
        _allowClose = true;
        _shutdown();
    }

    private void Post(Action action)
    {
        if (_disposed) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed) action();
        });
    }

    private static string FormatPercent(int? percent) => percent is null ? "—" : $"{percent}%";

    private static TextBlock ValueText(string value, double size, string color) => new()
    {
        Text = value,
        FontSize = size,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brush(color)
    };

    private static TextBlock DetailText(string value) => new()
    {
        Text = value,
        FontSize = 10,
        Foreground = Brush("#8B98A9"),
        TextWrapping = TextWrapping.Wrap
    };

    private static SolidColorBrush Brush(string value) => new(Color.Parse(value));
}
