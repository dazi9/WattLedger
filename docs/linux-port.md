# Linux port: design and migration plan

This is the integration contract and status record for the initial Linux desktop port. It targets x64 GNOME on Ubuntu 24.04 and Debian 12/13. Local native startup, private-D-Bus no-host `--tray` visible-window fallback, tray-quit/socket cleanup, and second-instance activation checks passed; the local Core and Linux desktop test suites passed. CI builds and publishes on the listed baselines but does not run a GNOME session or validate laptop sensors. Real suspend, hardware, and distro-specific validation remain pending; no tested laptop model is claimed.

## Design

- Keep the Windows solution and WPF application intact. `WattLedger.Linux.slnx` selects the shared Core, CLI, Linux desktop, and Core tests without pulling WPF into a Linux build.
- Keep sensor and operating-system integration in Core's platform implementation. The Linux desktop is an Avalonia client and accepts `--db <path>` and `--tray`.
- Read battery capacity and related values from readable sysfs battery entries. Linux exports energy in µWh, power in µW, and voltage in µV; Core converts them to internal mWh, mW, and mV. Missing or unreadable values remain unavailable. Charge-only devices may provide a percentage without energy capacity. When a usable discharge rate is unavailable, estimate from capacity deltas and label the result as an estimate. Battery quantization can hide small short-term changes.
- Keep Windows `ModernStandby` records and its threshold of more than 60 seconds unchanged. Linux uses `SleepGap` for any positive gap when logind suspend and resume events are both observed, including gaps of 60 seconds or less, and records that confirmation in the session note. Unconfirmed gaps require more than 60 seconds and are marked inferred. If the system bus is unavailable (including across a container/host boundary), use the inferred fallback. Neither logind nor the gap supplies battery measurements during the interval. When either endpoint lacks battery energy capacity, the existing schema stores numeric zero for the gap energy but marks the session note as `battery energy unavailable`; report summaries identify the standby-energy total as partial.
- Default Linux data lives in `$XDG_DATA_HOME/wattledger` or `~/.local/share/wattledger`; `--db` overrides the database path. Basic battery access should not need root.
- The GNOME desktop requires XWayland for an X11-backed Avalonia window in a Wayland session. Tray hosting is optional and depends on a GNOME AppIndicator extension. Without an available tray host, the app window must remain visible for continuous use. XDG autostart is user-scoped.
- Linux initially has no RAPL power breakdown, brightness reader, or foreground-process reader. Do not imply those Windows capabilities work on Linux.

## Build, release, and package

`WattLedger.slnx` remains the Windows solution and retains the WPF app. `WattLedger.Linux.slnx` is the Linux build/test boundary, including Core tests and headless Avalonia desktop tests. The CI workflow keeps a Windows full-solution build, Core tests, and Windows CLI/desktop publishes, and adds Linux-solution build/tests/publish on Ubuntu 24.04 and Debian 12/13 baselines. Debian runs in matching containers. Linux CI artifacts are self-contained `linux-x64` CLI and desktop publish directories; Debian packages can be built with:

```sh
tools/package-linux.sh <cli-publish-dir> <desktop-publish-dir> <output.deb>
```

The script reads the package version from `Directory.Build.props`, installs the CLI as `wattledger` and the desktop as `wattledger-desktop`, and does not require root. The `.deb` declares GTK 3, X11, fontconfig, ICU, OpenSSL, and XWayland runtime packages, including the package-name alternatives used by Ubuntu 24.04 and Debian 12/13. The optional GNOME AppIndicator extension is a desktop-environment choice and is not a hard package dependency.

![WattLedger running as a native Linux desktop app](linux-screenshot.png)

This capture is from the actual GNOME session and includes live battery readings. The displayed historical/demo chart data is synthetic; it is not a battery rundown or suspend/resume result. The original Windows screenshot at [`docs/screenshot.png`](screenshot.png) is unchanged.

## Migration milestones and verification

| Milestone | Scope | Verification / status |
|---|---|---|
| 1. Shared engine | Core/CLI target `net10.0`; keep platform-specific readers separated | Local Core and Linux desktop test suites passed; remote CI matrix still pending |
| 2. Linux desktop | Avalonia app, `--db`, optional `--tray`, logind pause/resume, XDG autostart | Native startup, private-D-Bus no-host `--tray` visible-window fallback, tray quit/socket cleanup, and second-instance activation passed locally. Remote CI result is pending; no real suspend/resume was performed |
| 3. Release integration | Linux solution, Ubuntu/Debian CI, self-contained x64 artifacts, Debian package | Self-contained CLI/desktop published locally; `.deb` built (~66 MB), extracted, metadata inspected, and CLI `--help` passed. Remote CI and real Debian desktop/install validation remain pending |
| 4. Hardware validation | Battery sysfs units and permissions, suspend/resume accounting, GNOME/XWayland and tray behavior | Screenshot shows live battery readings but is not a controlled hardware test. No laptop model or Debian desktop validation is claimed; real rundown and suspend testing remain pending |

CI can establish that the selected projects compile, tests pass, and Linux publish outputs are produced on the configured OS baselines. Local native UI checks are separate from CI and do not establish that a particular laptop exposes expected sysfs values, that sleep events match every firmware behavior, or that tray extensions are installed. Record those only after hands-on validation.
