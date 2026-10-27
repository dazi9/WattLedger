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

On Linux, battery entries are read from `/sys/class/power_supply`. Linux sysfs exports `energy_now`, `energy_full`, and `energy_full_design` in µWh; WattLedger divides by 1,000 to store mWh. `power_now` (µW) and `voltage_now` (µV) are converted to mW and mV. If only `current_now` (µA) and voltage are available, their product is converted to mW. Values that are absent, unreadable, unsupported, or invalid remain unavailable; they are not replaced with invented measurements. Some devices expose charge in µAh rather than energy, so energy capacity can remain unavailable even when a percentage is available.

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
| `ModernStandby` | historical Windows classification for a sampling gap longer than 60 seconds |
| `SleepGap` | Linux positive sampling gap confirmed by observed logind suspend/resume events, including gaps of 60 seconds or less; unconfirmed gaps require more than 60 seconds and are marked inferred |

A session closes when the type changes or when the sampler detects a gap. The interval at a transition is assigned to the previous state, which is conservative for AC/battery switches.

On Linux, `status=Charging` is evidence of external power and takes precedence over an AC/USB supply reporting `online=0` or no readable `online` value. Both the device online flag and aggregate AC state reflect this, so charging samples form `Charging` sessions and count toward AC time.

Linux session tracking deliberately requires a present system battery and evidence of charging, discharging, or external power. If firmware reports `Full`, `Not charging`, or `Unknown` without a readable online supply indicating external power, the power source is ambiguous. WattLedger stores an `Unknown` sample, closes the current session at the last known sample, and clears session continuity. When the state becomes known again, a new session starts; no active, AC, or sleep energy is inferred across the unknown interval. The raw battery percentage and available capacities are still recorded. `Full` or `Unknown` with `online=1` continues an `AC` session. This conservative policy can leave gaps in session history on firmware that omits both power-source signals.

### Standby accounting

On Windows, Modern Standby can suspend the app process. When sampling resumes, WattLedger sees a time gap and preserves the existing `ModernStandby` session type and threshold of more than 60 seconds. On Linux, an event-confirmed positive gap uses `SleepGap` even when it is 60 seconds or less. If both logind suspend and resume events are received, the session note records the event-confirmed pair; if the system bus is unavailable or an event is missed, only gaps longer than 60 seconds are classified as inferred `SleepGap` sessions. Timestamps alone do not prove the machine suspended. Gap energy is computed from remaining-capacity deltas, not guessed idle wattage:

```
E_standby = (capacity_before - capacity_after) / 1000
```

This is why standby drain is reported separately and is not mixed into active-use averages. If the machine was fully off and the battery did not change, the session reports approximately zero.

The sampler must have capacity readings before and after a gap to calculate a capacity delta. The current session schema stores gap energy as a numeric value: missing capacity is represented as `0 Wh`, the same value used for an unchanged capacity. Therefore `0 Wh` does not prove there was no drain; consult the nullable start/end capacity fields and the session note (`battery energy unavailable`). Report summaries flag standby energy as partial if one or more included standby sessions have missing capacity. A logind event pair confirms a suspend/resume transition but cannot recover battery measurements during the interval.

## 5. Reports

- **Active Wh** — positive energy from `Battery` sessions.
- **Standby Wh** — positive energy from Windows `ModernStandby` and Linux `SleepGap` sessions. Linux session notes distinguish an observed logind suspend/resume pair from an inferred timestamp gap. If gap capacity is missing, the report marks standby energy as partial.
- **Charged Wh** — absolute value of negative energy from `Charging` sessions.
- **Average active watts** — active Wh / active hours.
- **Average standby watts** — standby Wh / standby hours.
- **Battery health** — `FullChargedCapacity / DesignedCapacity`, if both are known.

Daily reports use the local calendar day as a half-open interval `[local midnight, next local midnight)`. Sessions that cross midnight are clipped to each day and their duration, energy and capacity values are prorated; a session is never counted in full on two days. The next midnight is resolved independently from the current one so daylight-saving days are 23 or 25 hours long. The session ledger still stores each underlying sample/session once; only the day view is split.

Timestamps are stored as UTC milliseconds. The UI and Markdown report convert to local time; CSV exports both `timestamp_local` and `timestamp_utc`.

### Interactive chart range estimates

The Linux chart supports dragging between recorded points or entering two local times to select a range. Calculations use the original timestamped power readings, independent of the reduced points drawn at the current zoom level. Readings with duplicate timestamps use the last value while preserving any sleep-gap boundary.

For each continuous pair of readings, power at the selected boundaries is linearly interpolated and energy is integrated by the trapezoidal rule. Positive power contributes to discharged Wh; negative power contributes to charged Wh. A segment that crosses zero is split at that crossing so charging and discharging are calculated separately. Net Wh is discharged Wh minus charged Wh.

Intervals with missing or non-finite power at either endpoint, gaps longer than 60 seconds, and recorded sleep-gap boundaries (including event-confirmed gaps of 60 seconds or less) are excluded. The chart does not extrapolate outside recorded data. **Coverage** is the included duration divided by the selected duration; **average watts** is net Wh divided by the included hours. With no included interval, average watts is unavailable; zero accumulated Wh does not establish that no energy was used.

These are power-rate estimates. They do not apply the ledger's capacity-delta fallback or standby accounting, so selected-range energy can differ from ledger totals for the same times.

## 6. Known error sources

1. **Firmware rate is not a lab shunt.** Some laptops report rate in coarse steps, some report zero while charging, and some use a sign convention that differs from the Windows documentation. WattLedger uses the power-state flags and falls back to capacity deltas.
2. **Capacity is quantized.** Many batteries update `RemainingCapacity` in 1% steps. Over a 5-second interval this can look like zero change; over minutes it is useful for calibration.
3. **RAPL is package-only.** It does not include the display, storage, network, or VRM losses. Use it for breakdown and trends, not as the total.
4. **Sampling gaps can be ambiguous.** A gap over 60 seconds retains Windows `ModernStandby` for compatibility. Linux notes a confirmed suspend/resume only when both logind events are observed and accepts any positive gap, including 60 seconds or less; unconfirmed gaps require more than 60 seconds and remain inferred. A paused process or scheduling delay can also cause an inferred gap. If capacity is missing, the existing session schema stores 0 Wh, but the note and report mark energy as unavailable/partial.
5. **Per-app attribution is not implemented yet.** A future implementation would estimate CPU/GPU shares and label them as estimates; it is not a feature of the current v0.2.x release.

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

A possible future approach, not implemented in the current release:

1. Sample each process's CPU time delta (`GetProcessTimes`) at the same cadence as the system sampler.
2. Attribute CPU package power (`PP0`) proportionally to CPU time.
3. Attribute GPU power using per-process GPU Engine counters where available.
4. Keep display, storage, network, and platform power in an "unattributed" bucket.
5. Label the result **estimated** everywhere; never present it as a measured per-app wattage.

This is deliberately conservative: a useful estimate with an honest error bar is better than a fake precise number.
