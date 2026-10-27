# WattLedger

**A local-first energy journal for Windows and Linux laptops.**

English · [简体中文](README.zh-CN.md)

WattLedger records how much energy your battery actually delivers, separates active use from sleep, breaks system power down into package / CPU / GPU / DRAM where the hardware exposes it, and keeps a long-term battery-health history. No kernel driver, no administrator rights for basic battery readings, no network, no telemetry.

Current source: **0.2.1** · [Changelog](CHANGELOG.md) · [Releases](https://github.com/dazi9/WattLedger/releases)

![WattLedger dashboard with synthetic demonstration data](docs/screenshot.png)

*Synthetic demonstration data.*

## Why another battery tool?

Windows shows a percentage. `powercfg /batteryreport` gives a static snapshot. HWiNFO and MSI Afterburner are excellent sensor dashboards, but they are not battery journals: they do not answer "where did today's watt-hours go?" or "how much did standby drain overnight?"

WattLedger is deliberately narrow:

- **Live power:** battery discharge/charge watts, voltage, remaining energy, time left.
- **Energy accounting:** trapezoidal integration of battery power, calibrated against the battery's remaining-capacity counter.
- **Session ledger:** AC, charging, battery and Modern Standby segments with start/end percentage, duration, Wh and average watts.
- **Hardware breakdown:** Windows Energy Meter (RAPL) counters for package, CPU, GPU and DRAM power on supported Intel/AMD machines.
- **Battery health:** design capacity, full-charge capacity, cycle count and stored health history.
- **Long-term history:** SQLite, local-only, exportable to CSV, JSON or Markdown.
- **Desktop app + CLI:** a WPF dashboard on Windows, an Avalonia desktop app on Linux, and a scriptable command-line interface.
- **English and Simplified Chinese UI:** choose System, English or 简体中文 in the footer; the choice persists across restarts.

### How it compares

| Tool | Live watts | Long-term journal | Standby accounting | Per-app energy | No driver/admin |
|---|---|---|---|---|---|
| Windows Settings / battery report | partial | no | partial | newer builds | yes |
| HWiNFO64 | yes | CSV only | manual | no | partial |
| MSI Afterburner | GPU-centric | no | no | no | yes |
| Powir | no | battery report | no | no | yes |
| Watt's Left | yes | 30 min | no | no | yes |
| **WattLedger** | **yes** | **yes** | **yes** | planned | **yes** |

Third-party rows summarize public documentation; check current versions before relying on them.

## Requirements

- Windows 10 or 11, x64.
- Ubuntu 24.04 or Debian 12/13, x64. GNOME is the tested desktop; other desktops need the native libraries listed in [Linux display backends](docs/linux-display-backends.md).
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build from source.
- A laptop battery with a supported Windows battery interface or Linux sysfs battery entries.

The Linux desktop supports native Wayland (experimental) and X11. Automatic selection prefers Wayland in a Wayland session; use `--backend=x11` for X11/XWayland compatibility. The tray icon is optional and needs a StatusNotifier/AppIndicator host; without one, keep the app window open.

Basic battery readings do not require administrator rights or a kernel driver. Linux sensor availability depends on readable kernel sysfs interfaces.

## Quick start

On Windows, in PowerShell or a terminal:

```sh
git clone https://github.com/dazi9/WattLedger.git
cd WattLedger

dotnet build WattLedger.slnx -c Release

# One-shot status
dotnet run --project src/WattLedger.Cli -- status

# Record continuously (Ctrl+C to stop)
dotnet run --project src/WattLedger.Cli -- watch

# Today's ledger
dotnet run --project src/WattLedger.Cli -- today

# JSON / Markdown / CSV export
dotnet run --project src/WattLedger.Cli -- export --format md --output report.md
dotnet run --project src/WattLedger.Cli -- export --format csv --output samples.csv

# Hardware / database diagnostics
dotnet run --project src/WattLedger.Cli -- diag

# Desktop app (tray + dashboard)
dotnet run --project src/WattLedger.App
```

On Linux, use the platform solution and desktop project:

```sh
dotnet build WattLedger.Linux.slnx -c Release
dotnet run --project src/WattLedger.Cli -- status
dotnet run --project src/WattLedger.Linux
```

The Linux desktop accepts `--db <path>`, `--tray` and `--backend auto|wayland|x11`. `WATTLEDGER_BACKEND` supplies a default when the command-line option is omitted. See [Linux display backends](docs/linux-display-backends.md) for native-library requirements, fallback behavior and current limitations. Data defaults to `$XDG_DATA_HOME/wattledger` (or `~/.local/share/wattledger`).

`wattledger status` is a sensor-only read: it does not create the database, record a sample, or take the sampler's writer lease, and remains usable while the desktop or `watch` records. Desktop `--db` paths are made absolute at launch so enabling start-at-login keeps the same database. If another sampler owns the database, the Windows app reports the startup failure even in `--tray` mode and displays `NOT RECORDING`.

## Using the chart

The chart spans actual recorded timestamps, with green signed power and a blue dashed battery-percentage curve. Positive power means discharge; negative power means charging, including in the live card and tray tooltip.

- **Zoom:** scroll over the plot, pinch on a precision touchpad, use Ctrl+scroll, the **+/−** buttons or keys, and use **Fit today**, double-click or Home to fit today's loaded range.
- **Pan:** in **Pan** mode, left-drag the plot. Shift-drag or middle-drag pans temporarily; arrow keys and horizontal scroll also pan.
- **Select:** switch to **Select** and drag between recorded points, or enter local From / To times (`HH:mm:ss`) and click **Calculate**. **Fit selection** zooms to the range; Escape clears it.
- **Follow live:** press End or **Follow live**. Dragging or navigating pauses following.
- **Inspect:** hover for the original sample's timestamp, watts and battery percentage.

Native Wayland pinch uses the bundled patched Avalonia backend and needs a compositor that exposes pointer gestures. X11/XWayland keeps wheel, Ctrl+scroll, button and keyboard zoom.

Chart drawing reduces points to the available pixels but keeps each column's extrema; range calculations always use the original samples, not the reduced curve.

Range statistics are power-rate estimates, separate from the capacity-calibrated ledger totals. Missing rates, intervals longer than 60 seconds, and recorded sleep gaps (including confirmed short suspends) are excluded from integration; average power uses only covered seconds. An interval with no usable power is shown as unavailable.

Reports and exports run in the background. Chart history loads incrementally, and report/chart refresh is deferred while hidden or minimized and catches up when restored.

## Linux battery values

Linux battery values are read from kernel sysfs and converted from its micro-units to WattLedger's internal mWh, mW and mV units. Missing sysfs values remain unavailable. A missing or zero `power_now` falls back to usable `current_now` and voltage. When a battery reports no usable rate, WattLedger estimates energy from capacity changes; quantization and firmware update cadence can make short intervals appear as zero.

Linux does not provide RAPL, brightness or foreground-process readings. A `SleepGap` is timestamp-detected; when logind suspend and resume events are both observed, its note records that confirmation, otherwise it remains explicitly inferred. Missing capacity is marked as unavailable, and report summaries flag standby energy as partial.

Basic live readings and UI startup have been checked on Ubuntu 24.04.5 GNOME. Per-model sensor coverage and suspend behavior are tracked in [supported hardware](docs/supported-hardware.md), and the backend design is documented in [Linux display backends](docs/linux-display-backends.md).

## Where your data lives

The Windows app and CLI share the same database:

```text
%LocalAppData%\WattLedger\wattledger.db
```

On Linux, the default database is `$XDG_DATA_HOME/wattledger/wattledger.db`, falling back to `~/.local/share/wattledger/wattledger.db`. Both platforms accept `--db` for an explicit location.

Quit the desktop and `watch` before copying or removing the database. Back up the data directory, including any `-wal` and `-shm` files, or use SQLite's backup API for a live backup. Delete the file to reset history; export first if you want to keep it.

## Demo data

The screenshot in this README is generated from synthetic data, not from a real user's history:

```sh
python tools/seed-demo.py --db demo.db
dotnet run --project src/WattLedger.App -- --db demo.db
```

The script refuses to touch a database named `wattledger.db` unless you pass `--force`.

## Self-contained builds

Linux x64:

```sh
dotnet publish src/WattLedger.Cli -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/cli-linux-x64
dotnet publish src/WattLedger.Linux -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/desktop-linux-x64
bash tools/package-linux.sh artifacts/cli-linux-x64 artifacts/desktop-linux-x64 artifacts/wattledger-linux-x64.deb
```

Windows x64, in PowerShell:

```powershell
dotnet publish src/WattLedger.Cli -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/cli-win-x64
dotnet publish src/WattLedger.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/desktop-win-x64
```

The Debian script needs `dpkg-deb` and Python 3 and produces an `amd64` package. Linux builds still need the native graphics, font and display libraries described in [Linux display backends](docs/linux-display-backends.md).

## How it works

```text
Windows battery IOCTL + Energy Meter/RAPL ─┐
Linux sysfs battery + logind events ───────┼─> sampler / session tracker
Windows power events ──────────────────────┘            │
                                                       ▼
                                            SQLite (local WAL database)
                                                       │
                                  ┌────────────────────┴───────────────────┐
                                  ▼                                        ▼
                          CLI reports/exports                    WPF / Avalonia desktops
```

- **Battery:** SetupAPI enumeration plus `IOCTL_BATTERY_QUERY_STATUS` / `IOCTL_BATTERY_QUERY_INFORMATION` for mWh, mW, mV, design capacity, full-charge capacity and cycle count on Windows; sysfs on Linux.
- **System power:** the battery's discharge rate is the ground truth. On Intel/AMD machines, the Windows Energy Meter performance counter set exposes package, CPU, GPU and DRAM power without a driver.
- **Energy:** trapezoidal integration of net battery power over each sample interval, with a remaining-capacity fallback when firmware reports no rate.
- **Sleep gaps:** a sampling gap longer than 60 seconds remains `ModernStandby` on Windows for historical compatibility and is labeled `SleepGap` on Linux. A Linux session note records a confirmed suspend/resume only when both logind events were observed; otherwise the gap remains inferred. Gap energy uses remaining-capacity deltas when available; unavailable energy retains the existing numeric zero in the schema but is marked in the note and report as incomplete.
- **Storage:** SQLite in WAL mode, one row per sample, one row per session, one health snapshot per day.

See [docs/METHODOLOGY.md](docs/METHODOLOGY.md) for the formulas and edge cases.

## Project layout

```text
src/WattLedger.Core/      Native readers, energy engine, SQLite, reports
src/WattLedger.Desktop/   Shared UI-free chart math/state, dashboard loading, theme preferences
src/WattLedger.Cli/       Scriptable CLI (status / watch / today / export / diag)
src/WattLedger.App/       WPF tray app and dashboard
src/WattLedger.Linux/     Avalonia desktop app for Linux
tests/                    Unit tests for integration and session state
docs/                     Methodology, privacy, hardware and platform notes
```

The core library has no UI dependency; the CLI and both desktop apps are thin clients over the same engine. Shared chart math and interaction state live in `WattLedger.Desktop`.

## Privacy

WattLedger is local-first by design:

- no network connections;
- no accounts or telemetry;
- no background cloud sync.

Local data includes the SQLite database, preferences, SQLite WAL/SHM files, logs, the start-at-login entry and the .NET extraction cache. Document contents, window titles and URLs are not collected. Windows records the foreground process name with samples; Linux does not. `wattledger diag` can include device names, paths, row counts and firmware serial numbers; review it before sharing.

See [docs/PRIVACY.md](docs/PRIVACY.md).

## Roadmap

- **0.1:** Windows energy journal, tray app, CLI, CSV/JSON/Markdown export, battery health.
- **0.2.x:** shared chart navigation and statistics, themes, day-boundary-safe reports, native Wayland backend, precision-touchpad pinch, English/Simplified Chinese UI.
- **Next:** broader hardware, compositor and device coverage.
- **Later:** historical/multi-day chart navigation, per-app estimated attribution, standby alerts, additional packaging and architectures, standardized battery-rundown validation.

## Limitations

- Battery firmware sampling is coarse: some machines update capacity in 1% steps and report rate noisily. WattLedger shows what the hardware exposes and labels measured versus estimated values.
- Some laptops expose no discharge rate; the remaining-capacity fallback is used.
- RAPL / Energy Meter availability varies by CPU and firmware. `wattledger diag` shows what your machine exposes.
- Native Wayland pinch depends on the compositor exposing pointer gestures; X11/XWayland keeps wheel, Ctrl+scroll, button and keyboard zoom.
- Windows precision-touchpad gestures depend on what the device and Windows deliver; wheel, Ctrl+scroll, buttons and keyboard zoom remain available.
- Per-app energy attribution is not implemented. Windows does not provide a precise public per-app energy API; a later version will use CPU/GPU time-share estimates and label them as estimates.
- If the app is not running during a sleep period, that interval cannot be reconstructed. A Linux `SleepGap` is not proof that the machine slept. Keep the app running for continuous history; Linux supports user-scoped XDG autostart.
- Windows testing is primarily on a MECHREVO WUJIE (Intel Core Ultra 5 226V, Windows 11 build 26200). Linux startup has been checked on Ubuntu 24.04.5 GNOME. See [supported hardware](docs/supported-hardware.md); community reports for other laptops are welcome.

## Contributing

Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md). For bug reports, include the output of:

```sh
dotnet run --project src/WattLedger.Cli -- diag
```

Also include your laptop model and Windows build. Do not attach personal files from the database unless you have reviewed them.

## License

MIT. See [LICENSE](LICENSE).
