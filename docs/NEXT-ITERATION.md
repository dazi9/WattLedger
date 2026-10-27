# Desktop iteration status and next scope

Updated 2026-10-27. The current iteration adds touchpad input, application language and a smaller desktop toolbar.

## Delivered work

| Area | Implementation and evidence |
|---|---|
| Chart navigation | [PR #4](https://github.com/dazi9/WattLedger/pull/4): default Pan mode, Select mode, horizontal-wheel panning, follow-live/paused ranges and shared chart calculations. |
| Appearance | [PR #5](https://github.com/dazi9/WattLedger/pull/5): persistent System / Light / Dark on Linux and Windows. |
| Windows parity | [PR #6](https://github.com/dazi9/WattLedger/pull/6): shared chart analysis, signed power, background reports/exports and startup/exit behavior. |
| Follow-up correctness | [PR #8](https://github.com/dazi9/WattLedger/pull/8) and [PR #9](https://github.com/dazi9/WattLedger/pull/9): review fixes, local-day report boundaries and a shared chart interaction state machine used by both desktops. |
| Native Linux backend | [PR #10](https://github.com/dazi9/WattLedger/pull/10): Avalonia 12.1.4, explicit auto/Wayland/X11 selection, dependencies and actionable startup errors. Native Wayland startup and continued sampling were checked on Ubuntu 24.04.5 GNOME with `DISPLAY` removed. |
| Documentation | Updated English README and a complete, independent `README.zh-CN.md`. Both desktop applications now also have saved System / English / 简体中文 interface selection. |

Build/test CI on Windows, Ubuntu 24.04 and Debian 12/13 does not certify all hardware, compositor, GPU, portal or gesture combinations. Real suspend/resume, controlled battery rundown, broader native Windows interaction and additional Linux distributions remain distinct validation work.

## Architecture retained

Keep the .NET measurement/storage core and the UI-free `WattLedger.Desktop` presentation sources. Linux uses Avalonia; Windows retains its WPF shell. Shared chart math and state transitions belong in the shared sources, while platform controls translate native input and render the results.

The selected Linux path is Avalonia's native Wayland backend rather than a separate Qt frontend. Upstream still labels it experimental. Auto mode can use `UseWaylandWithFallback()` when an X11 display is available; explicit `UseWayland()` does not silently change protocols. Keep the explicit X11 compatibility entry point. Direct Wayland protocol support does not imply Qt/GTK native widgets. See [backend configuration](linux-display-backends.md).

## Current iteration: touchpad input, language and UI simplification

The reported Linux gesture is a two-finger pinch. Avalonia's stock Wayland backend does not deliver that gesture, so this iteration includes a pinned, source-built backend protocol patch. Automated protocol/control checks are separate from physical touchpad verification.

Branches follow `<github-username>/<topic>` and may proceed in parallel where changes are independent:

| Branch | Implementation and remaining boundary |
|---|---|
| `dazi9/touchpad-zoom-linux` | Shared bounded gesture scaling, fractional/Ctrl-axis scroll, anchored touchpad magnification and touchscreen pinch. The pinned Avalonia Wayland backend binds compositor pointer gestures and forwards pinch to the chart; X11 remains a compatibility path without native pinch. See [implementation/provenance](touchpad-input.md). Physical devices still require confirmation. |
| `dazi9/touchpad-zoom-windows` | WPF native Ctrl+wheel and zoom messages plus Windows Touch manipulation; shared range/anchor/follow rules and duplicate suppression. Targeted decoder/state checks and cross-build pass; physical Windows input remains unverified. See [Windows input](windows-touchpad.md). |
| `dazi9/app-localization` | Saved System / English / 简体中文 on both desktops, live label/chart/tray/error updates, stable ledger/export types, and English fallback. Compact chart toolbar, a More menu, collapsible manual selection and an Application menu retain existing functions with fewer visible buttons. See [language and interface](app-localization.md). |


Use focused input/resource checks and the required builds. Avoid a broad repeat of already-passing measurement regressions unless a change reaches those paths. Small unrelated defects can remain documented in [known issues](KNOWN-ISSUES.md).

## Later work

Historical/multi-day chart navigation, additional distribution packages and architectures, wider GNOME/KDE/driver coverage, per-app estimated attribution, alerts and standardized battery-rundown validation remain candidates. They are not features promised by the existing v0.2.x version number.
