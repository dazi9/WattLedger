# Contributing to WattLedger

Thanks for helping. WattLedger is a small, focused tool: an energy journal for laptops, not a general hardware dashboard.

## Ground rules

- **No kernel drivers.** Use documented Windows APIs on Windows and standard readable interfaces such as Linux sysfs and logind on Linux.
- **No administrator requirement** for basic battery readings.
- **No network / telemetry.** Local-only is a feature.
- **Label estimates.** If a number is modelled rather than measured, say so in the UI and docs.
- **Keep the core UI-free.** `WattLedger.Core` must not reference WPF.

## Build and test

```powershell
dotnet restore WattLedger.slnx
dotnet build WattLedger.slnx -c Release
dotnet test tests/WattLedger.Core.Tests/WattLedger.Core.Tests.csproj -c Release
```

The Windows solution includes the WPF application. For Linux, build the platform solution:

```sh
dotnet restore WattLedger.Linux.slnx
dotnet build WattLedger.Linux.slnx -c Release
dotnet test tests/WattLedger.Core.Tests/WattLedger.Core.Tests.csproj -c Release
```

Run the CLI:

```powershell
dotnet run --project src/WattLedger.Cli -- status
dotnet run --project src/WattLedger.Cli -- watch
dotnet run --project src/WattLedger.Cli -- today
dotnet run --project src/WattLedger.Cli -- diag
```

Run the desktop app:

```powershell
dotnet run --project src/WattLedger.App
```

## Architecture

```
src/WattLedger.Core/
  Native/       Windows battery IOCTL / Energy Meter and Linux sysfs / logind readers
  Engine/       energy integration, session tracker, service loop, diagnostics
  Storage/      SQLite schema and queries
  Reporting/    day reports and CSV/JSON/Markdown exporters
  Models/       immutable records

src/WattLedger.Desktop/ UI-free presentation logic shared by both desktops: chart math and
                  series, the chart interaction state machine, dashboard loading, refresh
                  gating and the theme preference. Both desktop projects compile these
                  files in via <Compile Include>; add every new file to both
                  WattLedger.App.csproj and WattLedger.Linux.csproj.

src/WattLedger.Cli/   status / watch / today / export / diag
src/WattLedger.App/   WPF tray app + dashboard
src/WattLedger.Linux/ Avalonia desktop app for Linux
tests/                unit tests for the energy and session engines
```

## Pull requests

1. Open an issue first for non-trivial changes (new sensors, schema changes, UI redesign).
2. Name your branch `<github-username>/<topic>` (for example `dazi9/chart-navigation`) so each contributor's work is grouped under their own name.
3. Keep PRs focused. One concern per PR.
4. Add tests for energy math and session-state changes.
5. Update `docs/METHODOLOGY.md` if you change a measurement or formula.
6. Include a screenshot for UI changes.
7. Run `dotnet build` and `dotnet test` before pushing.

## Hardware reports

Tested hardware is listed in the README. If your laptop behaves differently:

- run `wattledger diag`;
- note the model, Windows build, CPU, and whether the battery exposes rate/health/cycle count;
- open an issue with a short description and the diagnostics (redact serial numbers).

Useful cases:

- battery rate reports 0 while discharging;
- two batteries;
- ARM64 / Snapdragon;
- Modern Standby drain that is not detected;
- RAPL counters missing or mis-scaled.

## Good first issues

- Add a `--json` output mode to `wattledger status`.
- Add a tray tooltip setting.
- Add a “delete all data” confirmation in the app.
- Add more unit tests around clock changes and negative gaps.
- Improve the chart when the sample count is small.
- Add a `docs/supported-hardware.md` table.

## Code style

- C# 13 / .NET 10, nullable enabled.
- 4-space indentation, file-scoped namespaces, `var` when the type is obvious.
- Prefer immutable records for data crossing layers.
- Keep platform interop and sensor access in `Core/Native`; do not leak platform APIs into UI code.
- New chart range/selection/interaction logic belongs in `src/WattLedger.Desktop` as UI-free code with platform-independent unit tests. The platform `PowerChart` controls stay thin event and render adapters and must not re-implement those rules.
- Use `DateTimeOffset` in the core, UTC milliseconds in SQLite, local time only at the edges.

## License

By contributing, you agree that your contribution is licensed under the MIT License.
