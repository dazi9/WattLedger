using System.Globalization;
using System.Text;
using System.Text.Json;
using WattLedger.Core.Models;
using WattLedger.Core.Storage;

namespace WattLedger.Core.Reporting;

public sealed record DayReport
{
    public DateOnly Date { get; init; }
    public IReadOnlyList<SessionRecord> Sessions { get; init; } = Array.Empty<SessionRecord>();
    public double DischargeWh { get; init; }
    public double ChargeWh { get; init; }
    public double ActiveSeconds { get; init; }
    public double StandbySeconds { get; init; }
    public double AcSeconds { get; init; }
    public double AverageActiveWatts { get; init; }
    public double AverageStandbyWatts { get; init; }
    public HealthRecord? Health { get; init; }
    public BatterySnapshot? Battery { get; init; }
}

public static class ReportBuilder
{
    public static DayReport BuildDay(LedgerDatabase database, DateOnly date)
    {
        var localStart = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        var start = new DateTimeOffset(localStart);
        var end = start.AddDays(1);

        var sessions = database.GetSessions(start, end)
            .Where(s => s.DurationSeconds > 0)
            .ToArray();
        var activeSessions = sessions.Where(s => s.Type == "Battery").ToArray();
        var standbySessions = sessions.Where(s => s.Type == "ModernStandby").ToArray();
        var acSessions = sessions.Where(s => s.Type is "AC" or "Charging").ToArray();

        var activeWh = activeSessions.Sum(s => Math.Max(0, s.EnergyWh));
        var standbyWh = standbySessions.Sum(s => Math.Max(0, s.EnergyWh));
        var chargeWh = sessions.Sum(s => Math.Max(0, -s.EnergyWh));
        var activeSeconds = activeSessions.Sum(s => s.DurationSeconds);
        var standbySeconds = standbySessions.Sum(s => s.DurationSeconds);
        var acSeconds = acSessions.Sum(s => s.DurationSeconds);

        var latest = database.GetLatestSample(start, end);

        return new DayReport
        {
            Date = date,
            Sessions = sessions,
            DischargeWh = activeWh + standbyWh,
            ChargeWh = chargeWh,
            ActiveSeconds = activeSeconds,
            StandbySeconds = standbySeconds,
            AcSeconds = acSeconds,
            AverageActiveWatts = activeSeconds > 0 ? activeWh / (activeSeconds / 3600.0) : 0,
            AverageStandbyWatts = standbySeconds > 0 ? standbyWh / (standbySeconds / 3600.0) : 0,
            Health = database.GetLatestHealth(),
            Battery = latest is null
                ? null
                : new BatterySnapshot
                {
                    Timestamp = latest.Timestamp,
                    Percent = latest.BatteryPercent,
                    RemainingCapacityMwh = latest.RemainingMwh,
                    FullChargeCapacityMwh = latest.FullChargeMwh,
                    DesignCapacityMwh = latest.DesignMwh,
                    CycleCount = latest.CycleCount,
                    DeviceName = latest.DeviceName,
                    IsAcOnline = latest.IsAcOnline,
                    IsCharging = latest.IsCharging,
                    IsDischarging = latest.IsDischarging
                }
        };
    }

    public static void ExportCsv(IEnumerable<SampleRecord> samples, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("timestamp_local,timestamp_utc,power_state,battery_percent,remaining_mwh,full_charge_mwh,voltage_mv,discharge_mw,charge_mw,ac_online,charging,discharging,rapl_pkg_w,rapl_cpu_w,rapl_gpu_w,rapl_dram_w,brightness_pct,foreground_process,cycle_count,device_name");
        foreach (var sample in samples)
        {
            writer.WriteLine(string.Join(",",
                Csv(sample.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                Csv(sample.Timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
                Csv(sample.PowerState),
                Csv(sample.BatteryPercent),
                Csv(sample.RemainingMwh),
                Csv(sample.FullChargeMwh),
                Csv(sample.VoltageMv),
                Csv(sample.DischargeMw),
                Csv(sample.ChargeMw),
                Csv(sample.IsAcOnline),
                Csv(sample.IsCharging),
                Csv(sample.IsDischarging),
                Csv(sample.RaplPackageWatts),
                Csv(sample.RaplCpuWatts),
                Csv(sample.RaplGpuWatts),
                Csv(sample.RaplDramWatts),
                Csv(sample.BrightnessPercent),
                Csv(sample.ForegroundProcess),
                Csv(sample.CycleCount),
                Csv(sample.DeviceName)));
        }
    }

    public static void ExportJson(DayReport report, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    public static void ExportMarkdown(DayReport report, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new StringBuilder();
        builder.AppendLine($"# WattLedger — {report.Date:yyyy-MM-dd}");
        builder.AppendLine();
        builder.AppendLine($"- Battery energy used: **{report.DischargeWh:0.00} Wh**");
        builder.AppendLine($"- Battery energy charged: **{report.ChargeWh:0.00} Wh**");
        builder.AppendLine($"- Active on battery: **{FormatDuration(report.ActiveSeconds)}**");
        builder.AppendLine($"- Modern Standby: **{FormatDuration(report.StandbySeconds)}**");
        builder.AppendLine($"- Average active power: **{report.AverageActiveWatts:0.0} W**");
        builder.AppendLine($"- Average standby power: **{report.AverageStandbyWatts:0.0} W**");
        if (report.Health?.HealthPercent is { } health)
        {
            builder.AppendLine($"- Battery health: **{health:0.0}%**");
        }

        builder.AppendLine();
        builder.AppendLine("| Start | End | State | Duration | Battery | Energy | Avg W |");
        builder.AppendLine("|---|---|---|---|---:|---:|---:|");
        foreach (var session in report.Sessions)
        {
            builder.AppendLine(
                $"| {session.Start.ToLocalTime():HH:mm:ss} | {session.End.ToLocalTime():HH:mm:ss} | {StateLabel(session.Type)} | {FormatDuration(session.DurationSeconds)} | {FormatPercent(session.StartPercent)} → {FormatPercent(session.EndPercent)} | {session.EnergyWh:0.00} Wh | {session.AverageWatts:0.0} W |");
        }

        builder.AppendLine();
        builder.AppendLine("_Energy is integrated from the battery's discharge rate and calibrated against its remaining-capacity counter. Standby energy is measured from the capacity delta across the sleep gap._");

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    public static string FormatDuration(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}h {value.Minutes}m"
            : $"{value.Minutes}m {value.Seconds}s";
    }

    public static string StateLabel(string type) => type switch
    {
        "Battery" => "Battery · 清醒",
        "ModernStandby" => "Modern Standby · 待机",
        "AC" => "AC · 插电",
        "Charging" => "Charging · 充电",
        _ => type
    };

    private static string FormatPercent(int? value) => value is null ? "?" : $"{value}%";

    private static string Csv(object? value)
    {
        if (value is null)
        {
            return "";
        }

        var text = value switch
        {
            bool b => b ? "1" : "0",
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };

        return text.Contains('"') || text.Contains(',') || text.Contains('\n')
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }
}
