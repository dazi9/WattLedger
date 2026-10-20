# Changelog

All notable changes to WattLedger are documented here. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-10-20

### Added

- Native Windows battery reader via SetupAPI and battery IOCTLs:
  - remaining/full/design capacity in mWh;
  - charge/discharge rate in mW;
  - voltage, power state, and cycle count when exposed.
- Windows `Energy Meter` / RAPL reader for package, CPU, GPU, and DRAM power.
- Energy accounting engine:
  - trapezoidal integration of net battery power;
  - coulomb-counter fallback;
  - AC / charging / battery / Modern Standby session state machine;
  - standby energy from capacity delta across sleep gaps.
- SQLite storage in WAL mode with sample, session, and battery-health tables.
- CLI:
  - `status`, `watch`, `today`, `report`, `export` (CSV/JSON/Markdown), `diag`, `init`;
  - `--db` to use an alternate database.
- WPF tray application:
  - live power, battery, time-left and today cards;
  - daily power/battery chart;
  - session ledger;
  - CSV and Markdown export;
  - open data folder;
  - optional start with Windows;
  - dark title bar and tray icon.
- Documentation:
  - methodology and error sources;
  - privacy statement;
  - supported hardware list;
  - contributing guide, code of conduct, security policy;
  - synthetic demo-data generator (`tools/seed-demo.py`).
- Unit tests for energy integration and session transitions.
- GitHub Actions build/test workflow.

### Known limitations

- System-level accounting only; per-app attribution is planned for v0.2.
- Standby sessions are only reconstructed if WattLedger was running before and after the sleep.
- Some laptop firmware exposes no battery rate or cycle count.
- RAPL / `Energy Meter` availability varies by CPU and firmware.
- Windows x64 only in v0.1.
