# Methodology

WattLedger is an energy accountant, not a lab power analyzer. This document explains exactly what is measured, what is estimated, and where the error bars are.

## 1. What is measured

### Battery

WattLedger enumerates battery devices through SetupAPI (`GUID_DEVCLASS_BATTERY`) and opens each device with `CreateFile`. It then uses the standard Windows battery IOCTLs:

- `IOCTL_BATTERY_QUERY_TAG`
- `IOCTL_BATTERY_QUERY_INFORMATION` (`BatteryInformation`)
- `IOCTL_BATTERY_QUERY_STATUS`

The relevant fields are:

| Field | Unit | Meaning |
|---|---:|---|
| `Capacity` | mWh | remaining energy reported by the battery |
| `FullChargedCapacity` | mWh | current full-charge capacity |
| `DesignedCapacity` | mWh | design capacity |
| `Voltage` | mV | battery voltage |
| `Rate` | mW | charge/discharge rate (sign and availability vary by firmware) |
| `PowerState` | flags | online / charging / discharging |
| `CycleCount` | count | if the firmware exposes it |

`GetSystemPowerStatus` supplies the AC line state and a percentage fallback.

### System power and RAPL

The battery discharge rate is the ground truth for total system power. On machines that expose it, the Windows **Energy Meter** performance counter set provides CPU package energy counters (RAPL):

- `RAPL_Package0_PKG` — package power
- `RAPL_Package0_PP0` — CPU cores
- `RAPL_Package0_PP1` — integrated GPU
- `RAPL_Package0_DRAM` — DRAM

The counters report **milliwatts**; WattLedger converts them to watts. Uncore is derived as:

```
uncore = max(0, package - cpu - gpu - dram)
```

RAPL is a hardware energy counter, not a model. It still has limitations: it covers the CPU package, not the display, SSD, Wi-Fi, VRM losses, or the rest of the platform. That is why the battery discharge rate remains the primary total.

### Display brightness and foreground process

Brightness is read from `root\wmi` `WmiMonitorBrightness` at most once per minute. The foreground process is read from `GetForegroundWindow` / `GetWindowThreadProcessId`. Neither is used for energy accounting in v0.1; they are context for later per-app estimates.

## 2. Sampling

- **5 seconds** while on battery.
- **30 seconds** while on AC.
- A sample is written to SQLite immediately; sessions are upserted on every sample so a crash does not lose the whole session.
- The sampler is event-driven at transitions: AC plug/unplug changes the next interval, and the WPF app receives `PowerModeChanged` suspend/resume notifications.

## 3. Energy integration

For consecutive samples `i-1` and `i`:

```
P_i = net battery discharge power in watts
    = +discharge_watts, if discharging
    = -charge_watts,    if charging

E_interval = ((P_(i-1) + P_i) / 2) * Δt_hours
```

This is trapezoidal integration of the instantaneous rate. It is more responsive than using only the battery percentage, and it captures short load spikes that a 1% capacity step would miss.

### Calibration and fallback

Battery firmware is noisy. WattLedger keeps the coulomb counter as a cross-check:

```
E_capacity = (RemainingCapacity_(i-1) - RemainingCapacity_i) / 1000
```

- If the integrated power is non-zero, it is used for the session.
- If the firmware reports no rate (common on some laptops), the capacity delta is used.
- At the end of a session, the reported energy can be compared with the capacity delta; large disagreements usually mean the firmware’s rate is stale or scaled incorrectly.

The current release does not silently apply a fudge factor. The raw integrated value is stored; the methodology is visible and testable.

## 4. Session state machine

Each sample belongs to one of:

| Type | Meaning |
|---|---|
| `AC` | plugged in, not charging (battery full or charge paused) |
| `Charging` | plugged in and charging |
| `Battery` | on battery, actively sampling |
| `ModernStandby` | a gap in sampling longer than 60 seconds while the process was suspended or the machine slept |

A session closes when the type changes or when the sampler detects a gap. The interval at a transition is assigned to the previous state, which is conservative for AC/battery switches.

### Standby accounting

Modern Standby can suspend the app process. When sampling resumes, WattLedger sees a time gap. It creates a `ModernStandby` session spanning the gap and computes its energy from the remaining-capacity delta, not from a guessed idle wattage:

```
E_standby = (capacity_before - capacity_after) / 1000
```

This is why standby drain is reported separately and is not mixed into active-use averages. If the machine was fully off and the battery did not change, the session reports approximately zero.

The current release only sees a standby gap if the sampler was running before and after it. If WattLedger is not running during the sleep, that session is not reconstructed.

## 5. Reports

- **Active Wh** — positive energy from `Battery` sessions.
- **Standby Wh** — positive energy from `ModernStandby` sessions.
- **Charged Wh** — absolute value of negative energy from `Charging` sessions.
- **Average active watts** — active Wh / active hours.
- **Average standby watts** — standby Wh / standby hours.
- **Battery health** — `FullChargedCapacity / DesignedCapacity`, if both are known.

Timestamps are stored as UTC milliseconds. The UI and Markdown report convert to local time; CSV exports both `timestamp_local` and `timestamp_utc`.

## 6. Known error sources

1. **Firmware rate is not a lab shunt.** Some laptops report rate in coarse steps, some report zero while charging, and some use a sign convention that differs from the Windows documentation. WattLedger uses the power-state flags and falls back to capacity deltas.
2. **Capacity is quantized.** Many batteries update `RemainingCapacity` in 1% steps. Over a 5-second interval this can look like zero change; over minutes it is useful for calibration.
3. **RAPL is package-only.** It does not include the display, storage, network, or VRM losses. Use it for breakdown and trends, not as the total.
4. **Standby gaps are inferred.** A 60-second gap is treated as Modern Standby. A paused debugger or a heavily loaded machine could also create a gap; the report labels the session type honestly.
5. **Per-app attribution is not in v0.1.** Windows does not expose a precise public per-app energy API. v0.2 will estimate CPU/GPU shares and label them as estimates.

## 7. Validation

The project's test suite covers:

- trapezoidal integration, including one-sided samples and charging;
- capacity-delta fallback;
- session transitions between AC, charging, battery, and standby;
- close/flush behavior.

For hardware validation, compare a controlled run against:

- `powercfg /batteryreport` capacity change over the same interval;
- a USB-C power meter on the charger input (for charging power);
- a fixed workload (video loop, fixed brightness) over 30–60 minutes.

If your laptop reports strange numbers, run:

```powershell
dotnet run --project src/WattLedger.Cli -- diag
```

and open an issue with the output (review it for personal data first).

## 8. Future per-app attribution

The planned v0.2 approach:

1. Sample each process's CPU time delta (`GetProcessTimes`) at the same cadence as the system sampler.
2. Attribute CPU package power (`PP0`) proportionally to CPU time.
3. Attribute GPU power using per-process GPU Engine counters where available.
4. Keep display, storage, network, and platform power in an "unattributed" bucket.
5. Label the result **estimated** everywhere; never present it as a measured per-app wattage.

This is deliberately conservative: a useful estimate with an honest error bar is better than a fake precise number.
