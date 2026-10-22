# Privacy

WattLedger is designed to work entirely on your machine.

## What it stores

- Battery samples: timestamp, percentage, remaining/full/design capacity, voltage, charge/discharge rate, AC state, optional RAPL power values, brightness, foreground process name, and battery device name. Values unavailable from the platform or hardware remain unavailable rather than fabricated.
- Session summaries: AC/charging/battery/standby segments, duration, energy, average/peak watts.
- Battery-health snapshots: design capacity, full-charge capacity, cycle count, health percentage.

On Windows, data is stored in:

```
%LocalAppData%\WattLedger\wattledger.db
```

On Linux the default is `$XDG_DATA_HOME/wattledger/wattledger.db`, falling back to `~/.local/share/wattledger/wattledger.db`. The desktop app and CLI share that platform's database. An explicit `--db` path overrides the default. Unsupported Linux fields such as RAPL, brightness, and foreground process remain unavailable.

## What it does not do

- No network connections.
- No accounts, cloud sync, telemetry, analytics, or crash reporting.
- No kernel driver.
- No reading of documents, browsing history, or personal files.
- No uploading of data anywhere.

On Windows, the optional process context is the **foreground process name** (for example, `msedge`) at each sample. It is stored locally and is not tied to window titles, URLs, or document names. The Linux port does not collect foreground-process information.

## Deleting your data

Close WattLedger, then delete:

```
%LocalAppData%\WattLedger\
```

That removes the database, WAL files, and local logs. Exported CSV/JSON/Markdown files are separate and must be deleted by you.

On Linux, remove `$XDG_DATA_HOME/wattledger/` or `~/.local/share/wattledger/` after closing WattLedger. An explicitly selected `--db` path must be removed separately.

## Diagnostics

`wattledger diag` prints hardware and database information to the terminal. It can include:

- laptop battery device name and manufacturer; Windows diagnostics may also include a serial number if exposed by firmware;
- RAPL counter values;
- database path and row counts.

Review the output before pasting it into a public issue. You can redact serial numbers and device paths.

## Third-party components

- `Microsoft.Data.Sqlite` — MIT
- `Hardcodet.NotifyIcon.Wpf` — MIT
- `Spectre.Console` — MIT
- `System.Management` — MIT
- `Avalonia` — MIT
- `Tmds.DBus.Protocol` — MIT

WattLedger does not bundle HWiNFO, MSI Afterburner, or any proprietary sensor SDK.
