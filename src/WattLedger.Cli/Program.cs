using System.Globalization;
using System.Text;
using Spectre.Console;
using WattLedger.Core.Engine;
using WattLedger.Core.Models;
using WattLedger.Core.Reporting;
using WattLedger.Core.Time;

namespace WattLedger.Cli;

internal static class Program
{
    private static readonly object ConsoleGate = new();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length > 0 && args[0] is "--help" or "-h" or "help")
        {
            PrintHelp();
            return 0;
        }

        var command = args.Length > 0 && !args[0].StartsWith('-')
            ? args[0].ToLowerInvariant()
            : "status";
        var optionStart = args.Length > 0 && !args[0].StartsWith('-') ? 1 : 0;
        var options = ParseOptions(args, optionStart);

        if (command is "help" or "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        var databasePath = options.TryGetValue("db", out var db) ? Path.GetFullPath(db) : null;

        try
        {
            using var service = new WattLedgerService(databasePath);
            if (command == "status")
            {
                return RunStatus(service);
            }

            service.Initialize();

            return command switch
            {
                "watch" => await RunWatchAsync(service),
                "today" => RunReport(service, DateOnly.FromDateTime(DateTime.Now)),
                "report" => RunReport(service, ParseDate(options.GetValueOrDefault("date"))),
                "export" => RunExport(service, options),
                "diag" => RunDiagnostics(service),
                "init" => RunInit(service),
                _ => UnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static int RunStatus(WattLedgerService service)
    {
        var status = service.ReadStatus();
        var battery = status.Battery;

        AnsiConsole.Write(new Rule("[bold]WattLedger[/]").LeftJustified());
        AnsiConsole.MarkupLine($"[grey]Database:[/] {Markup.Escape(service.DatabasePath)}");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Metric");
        table.AddColumn("Value");
        table.AddRow("Battery", $"{FormatPercent(battery.Percent)}{(battery.PercentIsEstimated ? " (estimated)" : "")}  ({FormatEnergy(battery.RemainingCapacityMwh)} / {FormatEnergy(battery.FullChargeCapacityMwh)})");
        table.AddRow("State", battery.IsAcOnline
            ? (battery.IsCharging ? "AC · charging" : "AC · idle")
            : battery.IsCharging
                ? "Charging · AC state unknown"
                : battery.IsDischarging
                    ? "Battery · discharging"
                    : battery.BatteryCount > 0 || battery.Percent is not null
                        ? "Battery state unknown"
                        : "Battery unavailable");
        var discharge = battery.DischargeWatts
                        ?? (battery.IsDischarging ? status.RecentAverageWatts : null);
        var charge = battery.ChargeWatts
                     ?? (battery.IsCharging && status.RecentAverageWatts is < 0 ? -status.RecentAverageWatts : null);
        var estimated = (battery.IsDischarging && battery.DischargeWatts is null && discharge is not null) ||
                        (battery.IsCharging && battery.ChargeWatts is null && charge is not null);
        var powerText = battery.IsDischarging
            ? $"{FormatWatts(discharge)} out{(estimated ? " (estimated)" : "")}"
            : battery.IsCharging
                ? $"{FormatWatts(charge)} in{(estimated ? " (estimated)" : "")}"
                : "—";

        table.AddRow("Battery power", powerText);
        table.AddRow("Voltage", battery.VoltageMv is { } v ? $"{v / 1000.0:0.000} V" : "—");
        table.AddRow("Health", battery.HealthPercent is { } h ? $"{h:0.0}%" : "—");
        table.AddRow("Cycles", battery.CycleCount?.ToString(CultureInfo.InvariantCulture) ?? "—");
        table.AddRow("Device", battery.DeviceName ?? "—");
        table.AddRow("Brightness", status.BrightnessPercent is { } b ? $"{b}%" : "—");
        table.AddRow("Foreground", status.ForegroundProcess ?? "—");
        table.AddRow("RAPL package", status.Rapl.PackageWatts is { } p ? $"{p:0.00} W" : "unavailable");
        table.AddRow("RAPL CPU", status.Rapl.CpuWatts is { } c ? $"{c:0.00} W" : "—");
        table.AddRow("RAPL GPU", status.Rapl.GpuWatts is { } g ? $"{g:0.00} W" : "—");
        table.AddRow("RAPL DRAM", status.Rapl.DramWatts is { } d ? $"{d:0.00} W" : "—");
        table.AddRow("Recent average", status.RecentAverageWatts is { } avg ? $"{avg:0.0} W" : "—");
        table.AddRow("Estimated left (instant rate)", status.EstimatedRemainingHours is { } hours ? $"{hours:0.0} h" : "—");
        AnsiConsole.Write(table);

        return 0;
    }

    private static async Task<int> RunWatchAsync(WattLedgerService service)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        AnsiConsole.Write(new Rule("[bold]WattLedger watch[/]").LeftJustified());
        AnsiConsole.MarkupLine("[grey]Sampling every 5 s on battery / 30 s on AC. Press Ctrl+C to stop.[/]");
        AnsiConsole.MarkupLine("[grey]TIME      STATE     BAT   POWER    RAPL_PKG  PROCESS[/]");

        service.SampleRecorded += (_, sample) =>
        {
            lock (ConsoleGate)
            {
                var power = sample.NetDischargeWatts is { } watts
                    ? $"{watts,6:0.00} W"
                    : "     —";
                var rapl = sample.RaplPackageWatts is { } package
                    ? $"{package,6:0.00} W"
                    : "     —";
                var process = Truncate(sample.ForegroundProcess ?? "—", 28);
                Console.WriteLine(
                    $"{sample.Timestamp.ToLocalTime():HH:mm:ss}  {sample.PowerState,-8}  {FormatPercent(sample.BatteryPercent),4}  {power}  {rapl}  {process}");
            }
        };

        service.Error += (_, message) =>
        {
            lock (ConsoleGate)
            {
                AnsiConsole.MarkupLine($"[red]Sampling error:[/] {Markup.Escape(message)}");
            }
        };

        await service.StartAsync(cts.Token);
        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C.
        }

        await service.StopAsync();
        return 0;
    }

    private static int RunReport(WattLedgerService service, DateOnly date)
    {
        var report = service.GetReport(date);
        PrintReport(report);
        return 0;
    }

    private static int RunExport(WattLedgerService service, IReadOnlyDictionary<string, string> options)
    {
        var date = ParseDate(options.GetValueOrDefault("date"));
        var format = options.GetValueOrDefault("format", "csv").ToLowerInvariant();
        var extension = format switch
        {
            "csv" => "csv",
            "json" => "json",
            "md" or "markdown" => "md",
            _ => throw new ArgumentException("--format must be csv, json, or md")
        };
        var output = options.TryGetValue("output", out var path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath($"wattledger-{date:yyyy-MM-dd}.{extension}");

        var (localStart, localEnd) = LocalDay.Range(date);

        switch (format)
        {
            case "csv":
                var samples = service.GetSamples(localStart, localEnd.AddTicks(-1));
                ReportBuilder.ExportCsv(samples, output);
                break;
            case "json":
                ReportBuilder.ExportJson(service.GetReport(date), output);
                break;
            default:
                ReportBuilder.ExportMarkdown(service.GetReport(date), output);
                break;
        }

        AnsiConsole.MarkupLine($"[green]Exported[/] {Markup.Escape(output)}");
        return 0;
    }

    private static int RunDiagnostics(WattLedgerService service)
    {
        var diagnostics = DiagnosticsService.Collect(service);

        AnsiConsole.Write(new Rule("[bold]WattLedger diagnostics[/]").LeftJustified());
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Item");
        table.AddColumn("Value");
        table.AddRow("Version", diagnostics.AppVersion);
        table.AddRow("OS", diagnostics.OsDescription);
        table.AddRow("Runtime", $"{diagnostics.RuntimeVersion} ({diagnostics.ProcessArchitecture})");
        table.AddRow("Database", diagnostics.DatabasePath);
        table.AddRow("Samples", diagnostics.SampleCount.ToString("N0", CultureInfo.InvariantCulture));
        table.AddRow("Sessions", diagnostics.SessionCount.ToString("N0", CultureInfo.InvariantCulture));
        table.AddRow("Brightness", diagnostics.BrightnessPercent is { } b ? $"{b}%" : "—");
        table.AddRow("Health", diagnostics.Health?.HealthPercent is { } h ? $"{h:0.0}%" : "—");
        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Battery devices[/]");
        if (diagnostics.BatteryDevices.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No battery devices reported by the platform.[/]");
        }
        else
        {
            var devices = new Table().Border(TableBorder.Rounded);
            devices.AddColumn("Name");
            devices.AddColumn("Manufacturer");
            devices.AddColumn("Design");
            devices.AddColumn("Full");
            devices.AddColumn("Remaining");
            devices.AddColumn("Rate");
            devices.AddColumn("State");
            foreach (var device in diagnostics.BatteryDevices)
            {
                devices.AddRow(
                    Markup.Escape(device.DeviceName ?? "—"),
                    Markup.Escape(device.Manufacturer ?? "—"),
                    FormatEnergy(device.DesignedCapacityMwh),
                    FormatEnergy(device.FullChargeCapacityMwh),
                    FormatEnergy(device.RemainingCapacityMwh),
                    device.RateMw is { } rate ? $"{rate / 1000.0:0.00} W" : "—",
                    $"0x{device.PowerState:X}");
            }

            AnsiConsole.Write(devices);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]RAPL / Energy Meter (raw milliwatts)[/]");
        if (diagnostics.RaplMilliwatts.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Energy Meter counters unavailable on this machine.[/]");
        }
        else
        {
            var rapl = new Table().Border(TableBorder.Rounded);
            rapl.AddColumn("Component");
            rapl.AddColumn("mW");
            rapl.AddColumn("W");
            foreach (var (name, value) in diagnostics.RaplMilliwatts.OrderBy(kv => kv.Key))
            {
                rapl.AddRow(Markup.Escape(name), $"{value:0}", $"{value / 1000.0:0.000}");
            }

            AnsiConsole.Write(rapl);
        }

        return 0;
    }

    private static int RunInit(WattLedgerService service)
    {
        AnsiConsole.MarkupLine($"[green]Database ready:[/] {Markup.Escape(service.DatabasePath)}");
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        AnsiConsole.MarkupLine($"[red]Unknown command:[/] {Markup.Escape(command)}");
        PrintHelp();
        return 1;
    }

    private static void PrintReport(DayReport report)
    {
        AnsiConsole.Write(new Rule($"[bold]WattLedger — {report.Date:yyyy-MM-dd}[/]").LeftJustified());

        var summary = new Table().Border(TableBorder.Rounded);
        summary.AddColumn("Metric");
        summary.AddColumn("Value");
        summary.AddRow(report.StandbyEnergyIncomplete ? "Battery energy used (partial)" : "Battery energy used", $"{report.DischargeWh:0.00} Wh");
        summary.AddRow("Battery energy charged", $"{report.ChargeWh:0.00} Wh");
        summary.AddRow("Active on battery", $"{ReportBuilder.FormatDuration(report.ActiveSeconds)}  (avg {report.AverageActiveWatts:0.0} W)");
        summary.AddRow("Sleep / standby", $"{ReportBuilder.FormatDuration(report.StandbySeconds)}  (avg {(report.StandbyEnergyIncomplete ? "incomplete" : $"{report.AverageStandbyWatts:0.0} W")})");
        summary.AddRow("AC / charging", ReportBuilder.FormatDuration(report.AcSeconds));
        if (report.Health?.HealthPercent is { } health)
        {
            summary.AddRow("Battery health", $"{health:0.0}%");
        }

        AnsiConsole.Write(summary);
        AnsiConsole.WriteLine();

        if (report.Sessions.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No sessions recorded for this day. Run `wattledger watch` to start logging.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Start");
        table.AddColumn("End");
        table.AddColumn("State");
        table.AddColumn("Duration");
        table.AddColumn("Battery");
        table.AddColumn("Energy");
        table.AddColumn("Avg W");
        table.AddColumn("Note");

        foreach (var session in report.Sessions)
        {
            table.AddRow(
                session.Start.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                session.End.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                Markup.Escape(ReportBuilder.StateLabel(session.Type)),
                ReportBuilder.FormatDuration(session.DurationSeconds),
                $"{FormatPercent(session.StartPercent)} → {FormatPercent(session.EndPercent)}",
                $"{session.EnergyWh:0.00} Wh",
                $"{session.AverageWatts:0.0} W",
                Markup.Escape(session.Note ?? ""));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]Sleep-gap energy is inferred from capacity changes where available; missing battery energy is marked unavailable.[/]");
    }

    private static Dictionary<string, string> ParseOptions(string[] args, int start)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = start; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg[2..];
            if (key.Length == 0)
            {
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[key] = args[++i];
            }
            else
            {
                options[key] = "true";
            }
        }

        return options;
    }

    private static DateOnly ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DateOnly.FromDateTime(DateTime.Now);
        }

        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        throw new ArgumentException("--date must be in yyyy-MM-dd format");
    }

    private static string FormatEnergy(long? mwh) =>
        mwh is null ? "—" : $"{mwh.Value / 1000.0:0.0} Wh";

    private static string FormatPercent(int? percent) => percent is null ? "—" : $"{percent}%";

    private static string FormatWatts(double? watts) => watts is null ? "—" : $"{watts:0.0} W";

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..(length - 1)] + "…";

    private static void PrintHelp()
    {
        AnsiConsole.Write(new Rule("[bold]WattLedger[/]").LeftJustified());
        AnsiConsole.MarkupLine("A local-first energy journal for laptops.");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Usage[/]");
        AnsiConsole.WriteLine("  wattledger status                 One-shot live status (default)");
        AnsiConsole.WriteLine("  wattledger watch                  Record continuously and print samples");
        AnsiConsole.WriteLine("  wattledger today                  Show today's energy ledger");
        AnsiConsole.WriteLine("  wattledger report --date 2026-10-20");
        AnsiConsole.WriteLine("  wattledger export --format csv|json|md --output report.csv [--date yyyy-MM-dd]");
        AnsiConsole.WriteLine("  wattledger diag                   Hardware and database diagnostics");
        AnsiConsole.WriteLine("  wattledger init                   Create/initialize the database");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Options: --db <path>  Use a custom SQLite database path.[/]");
    }
}
