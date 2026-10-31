using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using WattLedger.Core.Engine;
using WattLedger.Core.Models;
using WattLedger.Core.Reporting;
using WattLedger.Desktop;
using static WattLedger.Desktop.AppText;

namespace WattLedger.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static Brush GreenBrush => ThemeBrush("GreenBrush");
    private static Brush OrangeBrush => ThemeBrush("OrangeBrush");
    private static Brush BlueBrush => ThemeBrush("BlueBrush");
    private static Brush MutedBrush => ThemeBrush("MutedBrush");
    private static Brush RedBrush => ThemeBrush("RedBrush");

    private readonly WattLedgerService _service;
    private readonly DashboardDataLoader _dataLoader;
    private string _powerText = "—";
    private string _powerDetailText = Get("Waiting for the first sample");
    private string _batteryText = "—";
    private string _batteryDetailText = "—";
    private string _timeLeftText = "—";
    private string _timeLeftDetailText = "—";
    private string _todayEnergyText = "—";
    private string _todayDetailText = "—";
    private string _stateText = Get("STARTING");
    private string _statusText = Get("Collecting the first sample…");
    private Brush _powerBrush = GreenBrush;
    private Brush _stateBrush = BlueBrush;
    private IReadOnlyList<PowerPoint> _chartSamples = Array.Empty<PowerPoint>();
    private string? _startupError;
    private DayReport? _lastReport;
    private IReadOnlyList<AppResourceRow> _lastApps = Array.Empty<AppResourceRow>();

    public MainViewModel(WattLedgerService service)
    {
        _service = service;
        _dataLoader = new DashboardDataLoader(service);
        _service.Error += (_, message) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                StatusText = Format("Sampling error: {0}", message);
            }
            else
            {
                dispatcher.BeginInvoke(() => StatusText = Format("Sampling error: {0}", message));
            }
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ChartUpdated;

    public ObservableCollection<SessionRow> Sessions { get; } = new();

    public ObservableCollection<AppRow> Apps { get; } = new();

    public string PowerText
    {
        get => _powerText;
        private set => Set(ref _powerText, value);
    }

    public string PowerDetailText
    {
        get => _powerDetailText;
        private set => Set(ref _powerDetailText, value);
    }

    public string BatteryText
    {
        get => _batteryText;
        private set => Set(ref _batteryText, value);
    }

    public string BatteryDetailText
    {
        get => _batteryDetailText;
        private set => Set(ref _batteryDetailText, value);
    }

    public string TimeLeftText
    {
        get => _timeLeftText;
        private set => Set(ref _timeLeftText, value);
    }

    public string TimeLeftDetailText
    {
        get => _timeLeftDetailText;
        private set => Set(ref _timeLeftDetailText, value);
    }

    public string TodayEnergyText
    {
        get => _todayEnergyText;
        private set => Set(ref _todayEnergyText, value);
    }

    public string TodayDetailText
    {
        get => _todayDetailText;
        private set => Set(ref _todayDetailText, value);
    }

    public string StateText
    {
        get => _stateText;
        private set => Set(ref _stateText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public Brush PowerBrush
    {
        get => _powerBrush;
        private set => Set(ref _powerBrush, value);
    }

    public Brush StateBrush
    {
        get => _stateBrush;
        private set => Set(ref _stateBrush, value);
    }

    internal IReadOnlyList<PowerPoint> ChartSamples
    {
        get => _chartSamples;
        private set => Set(ref _chartSamples, value);
    }

    public void RefreshLive()
    {
        if (_startupError is not null) return;
        try
        {
            if (_service.Latest is { } status) ApplyStatus(status);
        }
        catch (Exception ex)
        {
            StatusText = Format("Sampling error: {0}", ex.Message);
        }
    }

    public void SetStartupFailure(string message)
    {
        _startupError = message;
        StateText = Get("NOT RECORDING");
        StateBrush = RedBrush;
        PowerDetailText = Get("Sampler could not start");
        StatusText = Format("Not recording: {0}", message);
    }

    public async Task RefreshReportAsync()
    {
        try
        {
            var snapshot = await Task.Run(() => _dataLoader.Load(DateTimeOffset.Now));
            var report = snapshot.Report;
            ApplyReport(report, snapshot.Apps);

            ChartSamples = snapshot.Samples;

            if (_startupError is null)
            {
                if (_service.Latest is { } latest) ApplyStatus(latest);
                else StatusText = Get("Waiting for the first sample…");
            }
            ChartUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusText = Format("Report error: {0}", ex.Message);
        }
    }

    private void ApplyReport(DayReport report, IReadOnlyList<AppResourceRow> apps)
    {
        _lastReport = report;
        _lastApps = apps;
        TodayEnergyText = $"{report.DischargeWh:0.0} Wh";
        TodayDetailText = Format("active {0} · standby {1}", ReportBuilder.FormatDuration(report.ActiveSeconds), ReportBuilder.FormatDuration(report.StandbySeconds));

        Sessions.Clear();
        foreach (var session in report.Sessions
                     .Where(s => s.DurationSeconds > 0)
                     .OrderByDescending(s => s.Start)
                     .Take(60))
        {
            Sessions.Add(new SessionRow(
                session.Start.ToLocalTime().ToString("HH:mm:ss"),
                session.End.ToLocalTime().ToString("HH:mm:ss"),
                AppText.SessionState(session.Type),
                ReportBuilder.FormatDuration(session.DurationSeconds),
                $"{FormatPercent(session.StartPercent)} → {FormatPercent(session.EndPercent)}",
                $"{session.EnergyWh:0.00} Wh",
                $"{session.AverageWatts:0.0} W"));
        }

        Apps.Clear();
        foreach (var app in apps)
        {
            Apps.Add(new AppRow(app.Name, app.Cpu, string.IsNullOrEmpty(app.Energy) ? "—" : app.Energy));
        }
    }

    public void RefreshLanguage()
    {
        if (_lastReport is { } report) ApplyReport(report, _lastApps);
        if (_startupError is not null) SetStartupFailure(_startupError);
        else if (_service.Latest is { } status) ApplyStatus(status);
        else
        {
            PowerDetailText = Get("Waiting for the first sample");
            StateText = Get("STARTING");
            StatusText = Get("Collecting the first sample…");
        }
    }

    public void InvalidateReport() => _dataLoader.Invalidate();

    private void ApplyStatus(LedgerStatus status)
    {
        var battery = status.Battery;
        var discharge = battery.DischargeWatts
                        ?? (battery.IsDischarging && status.RecentAverageWatts is > 0 ? status.RecentAverageWatts : null);
        var charge = battery.ChargeWatts
                     ?? (battery.IsCharging && status.RecentAverageWatts is < 0 ? -status.RecentAverageWatts : null);
        var powerEstimated = (battery.IsDischarging && battery.DischargeWatts is null && discharge is not null) ||
                             (battery.IsCharging && battery.ChargeWatts is null && charge is not null);

        if (battery.IsDischarging && discharge is { } dischargeWatts)
        {
            PowerText = $"+{dischargeWatts:0.0} W";
            PowerBrush = GreenBrush;
        }
        else if (battery.IsCharging && charge is { } chargeWattsValue)
        {
            PowerText = $"-{chargeWattsValue:0.0} W";
            PowerBrush = BlueBrush;
        }
        else
        {
            PowerText = "—";
            PowerBrush = MutedBrush;
        }

        PowerDetailText = status.Rapl.PackageWatts is { } package
            ? $"SoC {package:0.0} W · CPU {status.Rapl.CpuWatts.GetValueOrDefault():0.0} W · GPU {status.Rapl.GpuWatts.GetValueOrDefault():0.0} W"
            : Get("RAPL / Energy Meter unavailable");

        if (powerEstimated)
        {
            PowerDetailText += Get(" · estimated from capacity");
        }

        BatteryText = FormatPercent(battery.Percent);

        var health = battery.HealthPercent is { } h ? Format("{0:0.0}% health", h) : Get("health unknown");
        if (battery.PercentIsEstimated) health += Get(" · estimated");
        var cycles = battery.CycleCount is { } c ? Format(" · {0} cycles", c) : "";
        BatteryDetailText = battery.RemainingCapacityMwh is { } remaining && battery.FullChargeCapacityMwh is { } full
            ? $"{remaining / 1000.0:0.0} / {full / 1000.0:0.0} Wh · {health}{cycles}"
            : $"{health}{cycles}";

        if (battery.IsDischarging && status.EstimatedRemainingHours is { } hours)
        {
            TimeLeftText = $"{hours:0.0} h";
            TimeLeftDetailText = status.RecentAverageWatts is { } average
                ? Format("recent average {0:0.0} W", average)
                : Get("integrating recent samples");
        }
        else if (battery.IsCharging &&
                 battery.ChargeWatts is { } chargeWatts &&
                 battery.FullChargeCapacityMwh is { } fullCharge &&
                 battery.RemainingCapacityMwh is { } remainingCharge &&
                 chargeWatts > 0.1)
        {
            var hoursToFull = (fullCharge - remainingCharge) / 1000.0 / chargeWatts;
            TimeLeftText = $"{Math.Max(0, hoursToFull):0.0} h";
            TimeLeftDetailText = Format("estimated to full · charging at {0:0.0} W", chargeWatts);
        }
        else
        {
            TimeLeftText = battery.IsAcOnline ? Get("AC") : "—";
            TimeLeftDetailText = battery.IsAcOnline ? Get("plugged in") : Get("waiting for power data");
        }

        StateText = battery.BatteryCount == 0 ? Get("NO BATTERY")
            : battery.IsCharging ? Get("CHARGING")
            : battery.IsDischarging ? Get("ON BATTERY")
            : battery.IsAcOnline ? Get("AC POWER") : Get("STATE UNKNOWN");
        StateBrush = battery.IsCharging || battery.IsAcOnline ? BlueBrush
            : battery.IsDischarging ? GreenBrush : MutedBrush;
        StatusText = battery.IsCharging || battery.IsDischarging || battery.IsAcOnline
            ? Format("Recording locally · last sample {0:HH:mm:ss}", status.Timestamp.ToLocalTime())
            : Format("State unavailable · session tracking paused · last reading {0:HH:mm:ss}", status.Timestamp.ToLocalTime());

        if (status.BrightnessPercent is { } brightness)
        {
            PowerDetailText += Format(" · {0}% brightness", brightness);
        }

        if (status.ForegroundProcess is { Length: > 0 } process)
        {
            PowerDetailText += $" · {process}";
        }
    }

    private static string FormatPercent(int? percent) => percent is null ? "—" : $"{percent}%";

    private static Brush ThemeBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    public void RefreshTheme()
    {
        if (_startupError is not null) SetStartupFailure(_startupError);
        else if (_service.Latest is { } status) ApplyStatus(status);
        else
        {
            PowerBrush = GreenBrush;
            StateBrush = BlueBrush;
        }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record SessionRow(
    string Start,
    string End,
    string State,
    string Duration,
    string Battery,
    string Energy,
    string Average);

public sealed record AppRow(string Name, string Cpu, string Energy);

