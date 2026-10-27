# Desktop iteration status and next scope

Updated 2026-10-27. The next scope below records the user's latest requests; it is separate from the current Wayland/documentation closeout.

## Delivered work

| Area | Implementation and evidence |
|---|---|
| Chart navigation | [PR #4](https://github.com/dazi9/WattLedger/pull/4): default Pan mode, Select mode, horizontal-wheel panning, follow-live/paused ranges and shared chart calculations. |
| Appearance | [PR #5](https://github.com/dazi9/WattLedger/pull/5): persistent System / Light / Dark on Linux and Windows. |
| Windows parity | [PR #6](https://github.com/dazi9/WattLedger/pull/6): shared chart analysis, signed power, background reports/exports and startup/exit behavior. |
| Follow-up correctness | [PR #8](https://github.com/dazi9/WattLedger/pull/8) and [PR #9](https://github.com/dazi9/WattLedger/pull/9): review fixes, local-day report boundaries and a shared chart interaction state machine used by both desktops. |
| Native Linux backend | [PR #10](https://github.com/dazi9/WattLedger/pull/10): Avalonia 12.1.4, explicit auto/Wayland/X11 selection, dependencies and actionable startup errors. Native Wayland startup and continued sampling were checked on Ubuntu 24.04.5 GNOME with `DISPLAY` removed. |
| Documentation | Updated English README and a complete, independent `README.zh-CN.md`. A Chinese README does not imply a Chinese application UI is already implemented. |

Build/test CI on Windows, Ubuntu 24.04 and Debian 12/13 does not certify all hardware, compositor, GPU, portal or gesture combinations. Real suspend/resume, controlled battery rundown, broader native Windows interaction and additional Linux distributions remain distinct validation work.

## Architecture retained

Keep the .NET measurement/storage core and the UI-free `WattLedger.Desktop` presentation sources. Linux uses Avalonia; Windows retains its WPF shell. Shared chart math and state transitions belong in the shared sources, while platform controls translate native input and render the results.

The selected Linux path is Avalonia's native Wayland backend rather than a separate Qt frontend. Upstream still labels it experimental. Auto mode can use `UseWaylandWithFallback()` when an X11 display is available; explicit `UseWayland()` does not silently change protocols. Keep the explicit X11 compatibility entry point. Direct Wayland protocol support does not imply Qt/GTK native widgets. See [backend configuration](linux-display-backends.md).

## Next iteration: touchpad zoom and application language

The user reports that Linux chart zoom does not work as expected on a touchpad and asks for the Windows behavior to be checked too. Treat this as an unresolved input issue. Existing mouse-wheel handlers, horizontal-scroll handlers and synthetic pointer tests are not proof of pinch/precision-touchpad support.

Branches follow `<github-username>/<topic>` and may proceed in parallel where changes are independent:

| Proposed branch | Scope and concrete acceptance |
|---|---|
| `dazi9/touchpad-zoom-linux` | Inspect actual events from two-finger scrolling, Ctrl+scroll and pinch on native Wayland and the X11 compatibility backend. Implement the appropriate zoom gesture path with pointer anchoring and bounded deltas; preserve horizontal pan and explicit Select mode. Verify that a real touchpad changes the visible time range, ordinary clicks keep following, actual navigation pauses it, and a gesture is not processed twice. Record the desktop/backend actually checked. |
| `dazi9/touchpad-zoom-windows` | Check WPF precision-touchpad input and pinch/scroll translation on Windows. Match the shared range, anchoring, boundaries and follow/pause rules; keep platform-specific input translation in the WPF adapter. Confirm the physical input path on Windows rather than inferring it from a cross-build. |
| `dazi9/app-localization` | Add System / English / 简体中文 language selection to both desktop applications, with a saved preference that preserves the existing theme setting. Move visible labels, chart tools/statistics, tray menus, startup/lease errors and export dialogs/status into resources with English fallback. Check clipping and live updates after language changes. Keep energy units, stored sessions and machine-readable CSV/JSON field names stable; localized numbers/dates must not make manual time entry ambiguous. |

The application UI is currently mainly English. None of the gesture fixes or application-language settings above is claimed implemented in the current closeout.

Use focused input/resource checks and the required builds. Avoid a broad repeat of already-passing measurement regressions unless a change reaches those paths. Small unrelated defects can remain documented in [known issues](KNOWN-ISSUES.md).

## Later work

Historical/multi-day chart navigation, additional distribution packages and architectures, wider GNOME/KDE/driver coverage, per-app estimated attribution, alerts and standardized battery-rundown validation remain candidates. They are not features promised by the existing v0.2.x version number.
