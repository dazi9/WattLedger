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
    private readonly bool _monitorTrayHost;
    private readonly Action _shutdown;
    private readonly ReportRefreshGate _reportRefresh = new();
    private readonly TextBlock _power = ValueText("—", 34, "#3DDC97");
    private readonly TextBlock _powerDetail = DetailText("Waiting for the first sample");
    private readonly TextBlock _battery = ValueText("—", 30, "#E9EEF5");
    private readonly TextBlock _batteryDetail = DetailText("Battery state unavailable");
    private readonly TextBlock _remaining = ValueText("—", 30, "#5AA9FF");
    private readonly TextBlock _remainingDetail = DetailText("Estimated from recent samples");
    private readonly TextBlock _today = ValueText("0.0 Wh", 30, "#3DDC97");
    private readonly TextBlock _todayDetail = DetailText("No recorded battery use yet");
    private readonly TextBlock _status = DetailText("Starting local sampler…");
    private readonly TextBlock _platformStatus = DetailText("");
    private readonly TextBlock _powerState = DetailText("STARTING");
    private readonly Border _powerStateDot = new()
    {
        Width = 8,
        Height = 8,
        CornerRadius = new CornerRadius(4),
        Background = Brush("#5AA9FF"),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _sampleCount = DetailText("No samples yet");
    private readonly StackPanel _ledgerRows = new() { Spacing = 0 };
    private readonly PowerChart _chart = new();
    private readonly Button _startupButton;
    private readonly TrayIcon _trayIcon;
    private readonly DispatcherTimer _reportRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly TrayHostDetector _trayHostDetector;
    private bool _trayHostAvailable;
    private bool _trayRegistered;
    private bool _allowClose;
    private bool _disposed;
    private bool _checkingClose;
    private LedgerStatus? _latestStatus;
    private Task? _disposeTask;
    private DateTimeOffset _lastReportRefreshUtc;
    internal IReadOnlyList<PowerPoint> ChartSamples => _chart.Samples;

    public MainWindow(
        WattLedgerService service,
        bool startInTray,
        Action shutdown,
        bool monitorTrayHost = true,
        TrayHostDetector? trayHostDetector = null)
    {
        _service = service;
        _startInTray = startInTray;
        _monitorTrayHost = monitorTrayHost;
        _trayHostDetector = trayHostDetector ?? new TrayHostDetector();
        _shutdown = shutdown;
        _startupButton = ActionButton(StartupRegistration.IsEnabled ? "Disable start at login" : "Start at login", ToggleStartup);
        Title = "WattLedger";
        Width = 1120;
        Height = 800;
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
        _reportRefreshTimer.Tick += OnReportRefresh;
        _trayHostDetector.AvailabilityChanged += OnTrayHostAvailabilityChanged;
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
                    CreateChartPanel(),
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
            Text = "Local-first battery energy journal",
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
        var stateContents = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Children = { _powerStateDot, _powerState }
        };
        var statePill = new Border
        {
            Background = Brush("#14181F"),
            BorderBrush = Brush("#273341"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(13, 7),
            Child = stateContents,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(statePill, 1);
        header.Children.Add(statePill);
        return header;
    }

    private Control CreateMetricGrid()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
            ColumnSpacing = 12
        };
        AddMetric(grid, 0, "LIVE POWER", _power, _powerDetail);
        AddMetric(grid, 1, "BATTERY", _battery, _batteryDetail);
        AddMetric(grid, 2, "TIME LEFT", _remaining, _remainingDetail);
        AddMetric(grid, 3, "TODAY", _today, _todayDetail);
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
            Background = Brush("#14181F"),
            BorderBrush = Brush("#232A35"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15),
            MinHeight = 128,
            Padding = new Thickness(15, 13),
            Child = stack
        };
        Grid.SetColumn(card, column);
        grid.Children.Add(card);
    }

    private Control CreateChartPanel()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var heading = PanelHeading("Power & battery · today");
        heading.FontSize = 15;
        heading.Foreground = Brush("#E9EEF5");
        header.Children.Add(heading);
        var legend = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 18 };
        legend.Children.Add(LegendItem("Power", "#3DDC97"));
        legend.Children.Add(LegendItem("Battery", "#5AA9FF"));
        Grid.SetColumn(legend, 1);
        header.Children.Add(legend);
        return PanelBorder(new StackPanel
        {
            Spacing = 14,
            Children =
            {
                header,
                new Border
                {
                    Height = 210,
                    Background = Brush("#14181F"),
                    CornerRadius = new CornerRadius(9),
                    Child = _chart
                }
            }
        });
    }

    private static Control LegendItem(string label, string color)
    {
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(new Border
        {
            Width = 16,
            Height = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brush(color)
        });
        row.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Brush("#8B98A9") });
        return row;
    }

    private static Border PanelBorder(Control child) => new()
    {
        Background = Brush("#14181F"),
        BorderBrush = Brush("#232A35"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(15),
        Padding = new Thickness(18, 16),
        Child = child
    };

    private Control CreateLedgerPanel()
    {
        var panel = new StackPanel { Spacing = 10 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(PanelHeading("TODAY’S LEDGER"));
        _sampleCount.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_sampleCount, 1);
        header.Children.Add(_sampleCount);
        panel.Children.Add(header);

        var ledgerContent = new StackPanel { MinWidth = 650 };
        ledgerContent.Children.Add(LedgerHeader());
        ledgerContent.Children.Add(_ledgerRows);
        var ledger = new ScrollViewer
        {
            MaxHeight = 230,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = ledgerContent
        };
        panel.Children.Add(ledger);
        return PanelBorder(panel);
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
        _reportRefresh.SetVisible(true);
        RefreshReportNow();
        if (!_monitorTrayHost) return;
        try
        {
            await _trayHostDetector.StartAsync();
            _trayHostDetectorStarted = true;
            _trayHostAvailable = _trayRegistered && _trayHostDetector.IsAvailable == true;
            if (_startInTray && _trayHostAvailable) HideToTray();
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
        var available = _trayRegistered && _trayHostDetector.IsAvailable == true;
        if (!_trayHostDetectorStarted)
        {
            try
            {
                await _trayHostDetector.StartAsync();
                _trayHostDetectorStarted = true;
                _trayHostAvailable = _trayRegistered && _trayHostDetector.IsAvailable == true;
                available = _trayHostAvailable;
            }
            catch { available = false; }
        }
        _checkingClose = false;
        if (available)
        {
            HideToTray();
            _platformStatus.Text = "WattLedger is still running in the system tray.";
        }
        else
        {
            _platformStatus.Text = "No StatusNotifier host is available; closing WattLedger.";
            _allowClose = true;
            _shutdown();
        }
    }

    private void HideToTrayAsync()
    {
        if (_trayRegistered && _trayHostAvailable) HideToTray();
        else _platformStatus.Text = "No StatusNotifier host is available; the window stays open.";
    }

    private void HideToTray()
    {
        _reportRefresh.SetVisible(false);
        _reportRefreshTimer.Stop();
        Hide();
        _platformStatus.Text = "WattLedger is still running in the system tray.";
    }

    private bool _trayHostDetectorStarted;

    internal void OnTrayHostAvailabilityChanged(object? sender, bool available) => Post(() =>
    {
        available &= _trayRegistered;
        var disappeared = _trayHostAvailable && !available;
        _trayHostAvailable = available;
        if (available && !IsVisible && _startInTray) return;
        if (!available && !IsVisible && disappeared)
        {
            ShowWindow();
            _platformStatus.Text = "System tray host disappeared; WattLedger window restored.";
        }
        else if (!available && IsVisible)
        {
            _platformStatus.Text = "No StatusNotifier host found. WattLedger stays available in this window.";
        }
    });

    private void OnClosed(object? sender, EventArgs args)
    {
        _ = DisposeAsync();
    }

    public ValueTask DisposeAsync()
    {
        _disposeTask ??= DisposeCoreAsync();
        return new ValueTask(_disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        _reportRefreshTimer.Stop();
        _reportRefreshTimer.Tick -= OnReportRefresh;
        _trayHostDetector.AvailabilityChanged -= OnTrayHostAvailabilityChanged;
        _service.StatusUpdated -= OnStatusUpdated;
        _service.SampleRecorded -= OnSampleRecorded;
        _service.Error -= OnError;
        await _trayHostDetector.DisposeAsync();
        _trayIcon.Dispose();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons());
    }

    private void OnStatusUpdated(object? sender, LedgerStatus status) => Post(() =>
    {
        _latestStatus = status;
        if (IsVisible) ApplyStatus(status);
        else UpdateTrayTooltip(status);
    });

    internal void OnSampleRecorded(object? sender, SampleRecord sample) => Post(() =>
    {
        var becameVisible = _reportRefresh.SetVisible(IsVisible);
        if (!IsVisible)
        {
            _reportRefreshTimer.Stop();
        }

        if (becameVisible)
        {
            _reportRefresh.MarkDirty();
            RefreshReportNow();
            return;
        }

        if (_reportRefresh.MarkDirty())
        {
            ScheduleReportRefresh();
        }
    });

    private void OnError(object? sender, string message) => Post(() =>
    {
        if (IsVisible) _status.Text = $"Sampling error: {message}";
        else _trayIcon.ToolTipText = "WattLedger · sampling error";
    });

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
        var stateColor = battery.IsCharging || battery.IsAcOnline ? "#5AA9FF" : battery.IsDischarging ? "#3DDC97" : "#8B98A9";
        _powerState.Foreground = Brush(stateColor);
        _powerStateDot.Background = Brush(stateColor);
        _status.Text = $"Recording locally · last sample {status.Timestamp.ToLocalTime():HH:mm:ss}";
        _trayIcon.ToolTipText = $"WattLedger · {_battery.Text} · {_power.Text}";
    }

    private void OnReportRefresh(object? sender, EventArgs args)
    {
        _reportRefreshTimer.Stop();
        RefreshReportNow();
    }

    private void ScheduleReportRefresh()
    {
        var now = DateTimeOffset.UtcNow;
        var delay = TimeSpan.FromMilliseconds(350);
        if (_lastReportRefreshUtc != default)
        {
            var earliest = _lastReportRefreshUtc.AddSeconds(20) - now;
            if (earliest > delay) delay = earliest;
        }

        _reportRefreshTimer.Stop();
        _reportRefreshTimer.Interval = delay;
        _reportRefreshTimer.Start();
    }

    private void RefreshReportNow()
    {
        _reportRefresh.SetVisible(IsVisible);
        if (!IsVisible)
        {
            _reportRefreshTimer.Stop();
            return;
        }

        if (!_reportRefresh.TryConsumeRefresh()) return;
        _lastReportRefreshUtc = DateTimeOffset.UtcNow;
        try
        {
            var report = _service.GetTodayReport();
            _today.Text = $"{report.DischargeWh:0.0} Wh";
            _todayDetail.Text = $"active {ReportBuilder.FormatDuration(report.ActiveSeconds)} · standby {ReportBuilder.FormatDuration(report.StandbySeconds)}" +
                                (report.StandbyEnergyIncomplete ? " · some sleep-gap energy unavailable" : "");

            var start = new DateTimeOffset(DateTime.Today);
            var now = DateTimeOffset.Now;
            var samples = _service.GetSamples(start, now, 240);
            _chart.Samples = samples.Select(sample => new PowerPoint(sample.Timestamp, sample.NetDischargeWatts, sample.BatteryPercent)).ToArray();
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
            ColumnDefinitions = new ColumnDefinitions("1.15*,1.15*,1.1*,1.1*,1*,1*,.8*"),
            Margin = new Thickness(12, 0, 12, 5)
        };
        string[] headings = ["START", "END", "STATE", "DURATION", "BATTERY", "ENERGY", "AVG W"];
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
            ColumnDefinitions = new ColumnDefinitions("1.15*,1.15*,1.1*,1.1*,1*,1*,.8*"),
            Margin = new Thickness(12, 8, 12, 8)
        };
        var fields = new[]
        {
            session.Start.ToLocalTime().ToString("HH:mm:ss"),
            session.End.ToLocalTime().ToString("HH:mm:ss"),
            SessionStateLabel(session),
            ReportBuilder.FormatDuration(session.DurationSeconds),
            $"{FormatPercent(session.StartPercent)} → {FormatPercent(session.EndPercent)}",
            (session.Type is "SleepGap" or "ModernStandby") &&
            session.Note?.Contains("energy unavailable", StringComparison.OrdinalIgnoreCase) == true
                ? "Unavailable"
                : $"{session.EnergyWh:0.00} Wh",
            FormatAverageWatts(session)
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

    private static string FormatAverageWatts(SessionRecord session)
    {
        var energyUnavailable = (session.Type is "SleepGap" or "ModernStandby") &&
                                session.Note?.Contains("energy unavailable", StringComparison.OrdinalIgnoreCase) == true;
        return energyUnavailable || session.DurationSeconds <= 0
            ? "—"
            : $"{session.AverageWatts:0.0} W";
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

    public void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_reportRefresh.SetVisible(IsVisible)) RefreshReportNow();
        if (_latestStatus is not null) ApplyStatus(_latestStatus);
    }

    private void UpdateTrayTooltip(LedgerStatus status)
    {
        var battery = status.Battery.Percent is { } percent ? $"{percent}%" : "—";
        var rate = status.Battery.IsDischarging && status.Battery.DischargeWatts is { } discharge
            ? $"−{discharge:0.0} W"
            : status.Battery.IsCharging && status.Battery.ChargeWatts is { } charge
                ? $"+{charge:0.0} W"
                : "—";
        _trayIcon.ToolTipText = $"WattLedger · {battery} · {rate}";
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
