using System.Runtime.InteropServices;
using WattLedger.Core.Models;
using WattLedger.Core.Native;
using WattLedger.Core.Storage;

namespace WattLedger.Core.Engine;

public sealed record DiagnosticsReport
{
    public string AppVersion { get; init; } = "0.1.0";
    public string OsDescription { get; init; } = "";
    public string RuntimeVersion { get; init; } = "";
    public string ProcessArchitecture { get; init; } = "";
    public string DatabasePath { get; init; } = "";
    public long SampleCount { get; init; }
    public long SessionCount { get; init; }
    public IReadOnlyList<BatteryDeviceSnapshot> BatteryDevices { get; init; } = Array.Empty<BatteryDeviceSnapshot>();
    public IReadOnlyDictionary<string, double> RaplMilliwatts { get; init; } = new Dictionary<string, double>();
    public IReadOnlyList<EnergyDomainSnapshot> EnergyDomains { get; init; } = Array.Empty<EnergyDomainSnapshot>();
    public int? BrightnessPercent { get; init; }
    public HealthRecord? Health { get; init; }
}

public static class DiagnosticsService
{
    public static DiagnosticsReport Collect(WattLedgerService service)
    {
        var database = service.Database;
        return new DiagnosticsReport
        {
            AppVersion = typeof(DiagnosticsService).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            OsDescription = RuntimeInformation.OSDescription,
            RuntimeVersion = Environment.Version.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            DatabasePath = database.DatabasePath,
            SampleCount = database.GetSampleCount(),
            SessionCount = database.GetSessionCount(),
            BatteryDevices = service.ReadBatteryDevices(),
            RaplMilliwatts = service.ReadRawEnergyMilliwatts(),
            EnergyDomains = service.ReadEnergyDomains(),
            BrightnessPercent = service.ReadBrightnessForDiagnostics(),
            Health = database.GetLatestHealth()
        };
    }
}
