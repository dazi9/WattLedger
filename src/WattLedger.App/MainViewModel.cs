using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using WattLedger.Core.Engine;
using WattLedger.Core.Models;
using WattLedger.Core.Reporting;

namespace WattLedger.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly Brush GreenBrush = Freeze(Color.FromRgb(61, 220, 151));
    private static readonly Brush OrangeBrush = Freeze(Color.FromRgb(255, 180, 84));
    private static readonly Brush BlueBrush = Freeze(Color.FromRgb(90, 169, 255));
    private static readonly Brush MutedBrush = Freeze(Color.FromRgb(139, 152, 169));
    private static readonly Brush RedBrush = Freeze(Color.FromRgb(255, 107, 107));

    private readonly WattLedgerService _service;
    private string _powerText = "—";
    private string _powerDetailText = "Waiting for the first sample";
    private string _batteryText = "—";
    private string _batteryDetailText = "—";
    private string _timeLeftText = "—";
    private string _timeLeftDetailText = "—";
    private string _todayEnergyText = "—";
    private string _todayDetailText = "—";
    private string _stateText = "STARTING";
    private string _statusText = "Collecting the first sample…";
    private Brush _powerBrush = GreenBrush;
    private Brush _stateBrush = BlueBrush;
    private IReadOnlyList<ChartSample> _chartSamples = Array.Empty<ChartSample>();

    public MainViewModel(WattLedgerService service)
    {
        _service = service;
        _service.Error += (_, message) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                StatusText = $"Sampling error: {message}";
            }
            else
            {
                dispatcher.BeginInvoke(() => StatusText = $"Sampling error: {message}");
            }
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ChartUpdated;

    public ObservableCollection<SessionRow> Sessions { get; } = new();

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

    public IReadOnlyList<ChartSample> ChartSamples
    {
        get => _chartSamples;
        private set => Set(ref _chartSamples, value);
    }

    public void RefreshLive()
    {
        try
        {
            var status = _service.Latest ?? _service.SampleOnce();
            ApplyStatus(status);
        }
        catch (Exception ex)
        {
            StatusText = $"Sampling error: {ex.Message}";
        }
    }

    public void RefreshReport()
    {
        try
        {
            var report = _service.GetTodayReport();
            TodayEnergyText = $"{report.DischargeWh:0.0} Wh";
            TodayDetailText = $"active {ReportBuilder.FormatDuration(report.ActiveSeconds)} · standby {ReportBuilder.FormatDuration(report.StandbySeconds)}";

            Sessions.Clear();
            foreach (var session in report.Sessions
                         .Where(s => s.DurationSeconds > 0)
                         .OrderByDescending(s => s.Start)
                         .Take(60))
            {
                Sessions.Add(new SessionRow(
                    session.Start.ToLocalTime().ToString("HH:mm:ss"),
                    session.End.ToLocalTime().ToString("HH:mm:ss"),
                    ReportBuilder.StateLabel(session.Type),
                    ReportBuilder.FormatDuration(session.DurationSeconds),
                    $"{FormatPercent(session.StartPercent)} → {FormatPercent(session.EndPercent)}",
                    $"{session.EnergyWh:0.00} Wh",
                    $"{session.AverageWatts:0.0} W"));
            }

            var now = DateTimeOffset.Now;
            var todayStart = new DateTimeOffset(DateTime.Today);
            ChartSamples = _service.GetSamples(todayStart, now, 240)
                .Select(s => new ChartSample(
                    s.Timestamp,
                    Math.Max(0, s.NetDischargeWatts ?? 0),
                    s.BatteryPercent))
                .ToArray();

            StatusText = $"Recording locally · last sample {DateTime.Now:HH:mm:ss}";
            ChartUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusText = $"Report error: {ex.Message}";
        }
    }

    public void RefreshAll()
    {
        RefreshLive();
        RefreshReport();
    }

    private void ApplyStatus(LedgerStatus status)
    {
        var battery = status.Battery;
        var discharge = battery.DischargeWatts
                        ?? (battery.IsDischarging ? status.RecentAverageWatts : null);
        var charge = battery.ChargeWatts
                     ?? (battery.IsCharging && status.RecentAverageWatts is < 0 ? -status.RecentAverageWatts : null);
        var powerEstimated = (battery.IsDischarging && battery.DischargeWatts is null && discharge is not null) ||
                             (battery.IsCharging && battery.ChargeWatts is null && charge is not null);

        if (battery.IsDischarging && discharge is { } dischargeWatts)
        {
            PowerText = $"-{dischargeWatts:0.0} W";
            PowerBrush = GreenBrush;
        }
        else if (battery.IsCharging && charge is { } chargeWattsValue)
        {
            PowerText = $"+{chargeWattsValue:0.0} W";
            PowerBrush = BlueBrush;
        }
        else
        {
            PowerText = "0.0 W";
            PowerBrush = MutedBrush;
        }

        PowerDetailText = status.Rapl.PackageWatts is { } package
            ? $"SoC {package:0.0} W · CPU {status.Rapl.CpuWatts.GetValueOrDefault():0.0} W · GPU {status.Rapl.GpuWatts.GetValueOrDefault():0.0} W"
            : "RAPL / Energy Meter unavailable";

        if (powerEstimated)
        {
            PowerDetailText += " · estimated from capacity";
        }

        BatteryText = FormatPercent(battery.Percent);

        var health = battery.HealthPercent is { } h ? $"{h:0.0}% health" : "health unknown";
        var cycles = battery.CycleCount is { } c ? $" · {c} cycles" : "";
        BatteryDetailText = battery.RemainingCapacityMwh is { } remaining && battery.FullChargeCapacityMwh is { } full
            ? $"{remaining / 1000.0:0.0} / {full / 1000.0:0.0} Wh · {health}{cycles}"
            : $"{health}{cycles}";

        if (battery.IsDischarging && status.EstimatedRemainingHours is { } hours)
        {
            TimeLeftText = $"{hours:0.0} h";
            TimeLeftDetailText = status.RecentAverageWatts is { } average
                ? $"recent average {average:0.0} W"
                : "integrating recent samples";
        }
        else if (battery.IsCharging &&
                 battery.ChargeWatts is { } chargeWatts &&
                 battery.FullChargeCapacityMwh is { } fullCharge &&
                 battery.RemainingCapacityMwh is { } remainingCharge &&
                 chargeWatts > 0.1)
        {
            var hoursToFull = (fullCharge - remainingCharge) / 1000.0 / chargeWatts;
            TimeLeftText = $"{Math.Max(0, hoursToFull):0.0} h";
            TimeLeftDetailText = $"to full · charging at {chargeWatts:0.0} W";
        }
        else
        {
            TimeLeftText = battery.IsAcOnline ? "AC" : "—";
            TimeLeftDetailText = battery.IsAcOnline ? "plugged in" : "waiting for power data";
        }

        if (battery.IsAcOnline)
        {
            StateText = battery.IsCharging ? "CHARGING" : "AC POWER";
            StateBrush = battery.IsCharging ? OrangeBrush : BlueBrush;
        }
        else
        {
            StateText = "ON BATTERY";
            StateBrush = GreenBrush;
        }

        if (status.BrightnessPercent is { } brightness)
        {
            PowerDetailText += $" · {brightness}% brightness";
        }

        if (status.ForegroundProcess is { Length: > 0 } process)
        {
            PowerDetailText += $" · {process}";
        }
    }

    private static string FormatPercent(int? percent) => percent is null ? "—" : $"{percent}%";

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
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

public sealed record ChartSample(
    DateTimeOffset Timestamp,
    double PowerWatts,
    int? BatteryPercent);
