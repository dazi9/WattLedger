# Changelog

All notable changes to WattLedger are documented here. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.2.0] - 2026-10-24

### Added

- Initial Linux CLI and GNOME desktop packaging, build, and migration documentation.
- Linux battery readings from sysfs with unit conversion and `SleepGap` compatibility; session notes distinguish observed logind suspend/resume pairs from timestamp-inferred gaps, and reports flag unavailable standby energy. See the Linux port plan for scope and validation status.
- Interactive power chart analysis on Linux and Windows: pan, zoom, hover inspection, click/drag selection with statistics, follow-live viewport, keyboard navigation and horizontal-wheel panning; chart math and the interaction state machine are shared in `WattLedger.Desktop` as UI-free code with platform-independent unit tests.
- Shared light, dark and system-following themes across the Linux and Windows desktops.
- Windows sampler startup tests, including tray lease conflict scenarios.
- CI/CD: required `dotnet format` lint gate on `main`, test-only CI jobs per platform, a tag-triggered package workflow (win-x64 CLI/desktop, linux-x64 CLI/desktop, Debian package, SHA-256 checksums) and draft releases with manual publishing.

### Changed

- Repository normalized to `.editorconfig` formatting (LF line endings, no BOM) and pinned with `.gitattributes`.
- CI no longer publishes artifacts on every push; publishing moved to the package workflow (tags or manual dispatch).

### Fixed

- Sampler state and tray startup failures on Windows; Linux chart analysis corrections.
- Report refresh and CSV export are guarded against running during window shutdown.

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
