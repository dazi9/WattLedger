# Supported hardware

WattLedger reads the standard Windows battery interface and Windows `Energy Meter` counters, or Linux battery data through kernel sysfs. Support therefore depends on the laptop firmware and CPU, not on the OEM brand.

The initial Linux port targets x64 GNOME on Ubuntu 24.04 and Debian 12/13. Local native GNOME checks covered startup, tray quit/socket cleanup, and second-instance activation; actual battery rundown, suspend/resume, hardware coverage, and distro-specific install behavior remain unverified. Do not treat CI images as tested hardware or as evidence that every laptop exposes equivalent sysfs readings.

## Tested

| Laptop | CPU | Windows | Battery | RAPL / Energy Meter | Notes |
|---|---|---|---|---|---|
| MECHREVO WUJIE | Intel Core Ultra 5 226V | 11 26200 | SR Real Battery, 60 Wh | Package / CPU / GPU / DRAM | Primary development machine. Cycle count is not exposed. |

## What “supported” means

- **Battery**: the device appears under `GUID_DEVCLASS_BATTERY` and answers the standard IOCTLs. Nearly all laptops do.
- **Rate**: the battery reports a discharge/charge rate in mW. If it reports zero, WattLedger falls back to remaining-capacity deltas.
- **Health**: design capacity and full-charge capacity are exposed. Cycle count is optional.
- **RAPL**: the `Energy Meter` counter set exists and exposes `RAPL_Package0_*` instances. Intel and many AMD machines do; some firmware hides it.
- **Modern Standby**: detected from sampling gaps and power events. No special firmware support is required.

## Diagnostics

Run:

```powershell
dotnet run --project src/WattLedger.Cli -- diag
```

Look for:

- battery devices with design/full/remaining capacities;
- a non-empty `RAPL / Energy Meter` table;
- brightness and health values.

If a value is missing, open an issue with the output (redact serial numbers) and your laptop model. The most useful reports are:

- battery rate reports 0 while discharging;
- two batteries;
- ARM64 / Snapdragon;
- Modern Standby drain not detected;
- RAPL missing or mis-scaled.

## Adding a report

Add a row to the table above in a pull request, including:

- laptop model;
- CPU;
- Windows build (`winver`);
- battery name and capacity;
- whether RAPL counters are present.

Please do not include serial numbers or personal data.
