#!/usr/bin/env python3
"""Create a reproducible demo database for WattLedger screenshots and manual UI testing.

This is a developer tool, not part of the application. It writes synthetic samples
and sessions for a single day; do not point it at your real WattLedger database.

Example:
    python tools/seed-demo.py --db demo.db
    dotnet run --project src/WattLedger.App -- --db demo.db
"""
from __future__ import annotations

import argparse
import math
import sqlite3
from datetime import datetime, timedelta, timezone
from pathlib import Path

SCHEMA = """
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;

CREATE TABLE IF NOT EXISTS samples (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts INTEGER NOT NULL,
    power_state TEXT NOT NULL,
    battery_percent INTEGER NULL,
    remaining_mwh INTEGER NULL,
    full_charge_mwh INTEGER NULL,
    design_mwh INTEGER NULL,
    voltage_mv INTEGER NULL,
    discharge_mw INTEGER NULL,
    charge_mw INTEGER NULL,
    is_ac INTEGER NOT NULL,
    is_charging INTEGER NOT NULL,
    is_discharging INTEGER NOT NULL,
    rapl_pkg_w REAL NULL,
    rapl_cpu_w REAL NULL,
    rapl_gpu_w REAL NULL,
    rapl_dram_w REAL NULL,
    brightness_pct INTEGER NULL,
    foreground_process TEXT NULL,
    cycle_count INTEGER NULL,
    device_name TEXT NULL
);
CREATE INDEX IF NOT EXISTS ix_samples_ts ON samples(ts);

CREATE TABLE IF NOT EXISTS sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    start_ts INTEGER NOT NULL UNIQUE,
    end_ts INTEGER NOT NULL,
    type TEXT NOT NULL,
    note TEXT NULL,
    start_percent INTEGER NULL,
    end_percent INTEGER NULL,
    start_remaining_mwh INTEGER NULL,
    end_remaining_mwh INTEGER NULL,
    energy_wh REAL NOT NULL,
    avg_watts REAL NOT NULL,
    peak_watts REAL NOT NULL,
    duration_seconds INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_sessions_start ON sessions(start_ts);

CREATE TABLE IF NOT EXISTS battery_health (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts INTEGER NOT NULL,
    design_mwh INTEGER NULL,
    full_charge_mwh INTEGER NULL,
    cycle_count INTEGER NULL,
    health_pct REAL NULL
);
CREATE INDEX IF NOT EXISTS ix_health_ts ON battery_health(ts);
"""

PROCESSES = ["msedge", "Code", "ChatGPT", "explorer", "WindowsTerminal", "WattLedger"]


def parse_offset(value: str) -> timezone:
    sign = 1 if value.startswith("+") else -1
    value = value.lstrip("+-")
    hours, minutes = (int(part) for part in value.split(":"))
    return timezone(sign * timedelta(hours=hours, minutes=minutes))


def ms(value: datetime) -> int:
    return int(value.timestamp() * 1000)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--db", default="demo.db", help="demo database path (default: demo.db)")
    parser.add_argument("--date", default=datetime.now().strftime("%Y-%m-%d"), help="day to seed (default: today)")
    parser.add_argument("--utc-offset", default="+08:00", help="local UTC offset for the demo timeline (default: +08:00)")
    parser.add_argument("--force", action="store_true", help="allow writing to a database named wattledger.db")
    args = parser.parse_args()

    db_path = Path(args.db).resolve()
    if db_path.name.lower() == "wattledger.db" and not args.force:
        raise SystemExit("Refusing to modify the default database. Use a demo path or pass --force.")

    day = datetime.strptime(args.date, "%Y-%m-%d").replace(tzinfo=parse_offset(args.utc_offset))
    day_end = day + timedelta(days=1)

    def at(hour: int, minute: int) -> datetime:
        return day.replace(hour=hour, minute=minute, second=0, microsecond=0)

    conn = sqlite3.connect(db_path)
    cur = conn.cursor()
    cur.executescript(SCHEMA)

    # Make the demo idempotent without touching other days.
    cur.execute("DELETE FROM samples WHERE ts >= ? AND ts < ?", (ms(day), ms(day_end)))
    cur.execute("DELETE FROM sessions WHERE start_ts >= ? AND start_ts < ?", (ms(day), ms(day_end)))
    cur.execute("DELETE FROM battery_health WHERE ts >= ? AND ts < ?", (ms(day), ms(day_end)))

    samples: list[tuple] = []

    def add_sample(t: datetime, state: str, pct: float, discharge_w: float | None,
                   charge_w: float | None, ac: bool, charging: bool, discharging: bool,
                   index: int) -> None:
        remaining = int(round(max(0.0, min(100.0, pct)) / 100.0 * 60_000))
        soc_power = (discharge_w or 0.0) if discharge_w else (charge_w or 0.0) * 0.5
        samples.append((
            ms(t), state, int(round(pct)), remaining, 60_000, 60_000, 11_610,
            int(discharge_w * 1000) if discharge_w else None,
            int(charge_w * 1000) if charge_w else None,
            1 if ac else 0, 1 if charging else 0, 1 if discharging else 0,
            round(soc_power * 0.55, 3), round(soc_power * 0.30, 3),
            round(soc_power * 0.05, 3), round(soc_power * 0.02, 3),
            40, PROCESSES[index % len(PROCESSES)], 57, "SR Real Battery",
        ))

    def battery_segment(start: datetime, end: datetime, p0: float, p1: float,
                        w0: float, w1: float) -> None:
        span = (end - start).total_seconds()
        t, index = start, 0
        while t <= end:
            frac = (t - start).total_seconds() / span
            pct = p0 + (p1 - p0) * frac
            power = max(4.0, w0 + (w1 - w0) * frac + 1.1 * math.sin(index * 0.9) + (2.2 if index % 17 == 0 else 0))
            add_sample(t, "Battery", pct, power, None, False, False, True, index)
            index += 1
            t += timedelta(minutes=5)

    def charging_segment(start: datetime, end: datetime, p0: float, p1: float) -> None:
        span = (end - start).total_seconds()
        t, index = start, 0
        while t <= end:
            frac = (t - start).total_seconds() / span
            pct = p0 + (p1 - p0) * frac
            charge = max(5.0, 45.0 - 40.0 * frac + 1.5 * math.sin(index * 0.8))
            add_sample(t, "Charging", pct, None, charge, True, True, False, index)
            index += 1
            t += timedelta(minutes=5)

    def ac_segment(start: datetime, end: datetime) -> None:
        t, index = start, 0
        while t <= end:
            add_sample(t, "AC", 100, None, None, True, False, False, index)
            index += 1
            t += timedelta(minutes=5)

    battery_segment(at(7, 54), at(9, 40), 100, 73, 9.0, 12.0)
    battery_segment(at(13, 47), at(14, 37), 70, 54, 10.0, 13.0)
    battery_segment(at(14, 47), at(16, 15), 53, 21, 12.0, 18.0)
    charging_segment(at(16, 15), at(21, 30), 21, 100)
    ac_segment(at(21, 30), at(22, 7))

    cur.executemany("""
        INSERT INTO samples (ts, power_state, battery_percent, remaining_mwh, full_charge_mwh,
                             design_mwh, voltage_mv, discharge_mw, charge_mw, is_ac, is_charging,
                             is_discharging, rapl_pkg_w, rapl_cpu_w, rapl_gpu_w, rapl_dram_w,
                             brightness_pct, foreground_process, cycle_count, device_name)
        VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
    """, samples)

    sessions = [
        (at(7, 54), at(9, 40), "Battery", "Battery", 100, 73, 60_000, 43_800, 16.2, 9.17, 15.0),
        (at(9, 40), at(13, 47), "ModernStandby", "Battery", 73, 70, 43_800, 42_000, 1.8, 0.44, 1.2),
        (at(13, 47), at(14, 37), "Battery", "Battery", 70, 54, 42_000, 32_400, 9.6, 11.52, 16.5),
        (at(14, 37), at(14, 47), "ModernStandby", "Battery", 54, 53, 32_400, 31_800, 0.6, 3.6, 4.0),
        (at(14, 47), at(16, 15), "Battery", "Battery", 53, 21, 31_800, 12_600, 19.2, 13.09, 22.0),
        (at(16, 15), at(21, 30), "Charging", "AC", 21, 100, 12_600, 60_000, -50.0, -8.2, 45.0),
        (at(21, 30), at(22, 7), "AC", "AC", 100, 100, 60_000, 60_000, 0.0, 0.0, 0.0),
    ]
    for start, end, kind, note, sp, ep, sr, er, energy, avg, peak in sessions:
        cur.execute("""
            INSERT OR REPLACE INTO sessions (start_ts, end_ts, type, note, start_percent,
                                             end_percent, start_remaining_mwh, end_remaining_mwh,
                                             energy_wh, avg_watts, peak_watts, duration_seconds)
            VALUES (?,?,?,?,?,?,?,?,?,?,?,?)
        """, (ms(start), ms(end), kind, note, sp, ep, sr, er, energy, avg, peak,
              int((end - start).total_seconds())))

    cur.execute("""
        INSERT INTO battery_health (ts, design_mwh, full_charge_mwh, cycle_count, health_pct)
        VALUES (?, 60000, 60000, 57, 100.0)
    """, (ms(at(16, 15)),))

    conn.commit()
    print(f"Seeded {db_path} with {len(samples)} samples and {len(sessions)} sessions.")
    print(f"Open it with: dotnet run --project src/WattLedger.App -- --db \"{db_path}\"")
    conn.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
