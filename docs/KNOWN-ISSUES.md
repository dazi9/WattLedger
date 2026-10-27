# Non-blocking follow-ups

Updated 2026-10-27. These items are deferred from the sampler and interactive-chart fixes. Power-state classification, writer-lease failures, missing-data handling, and shutdown safety are covered by the fixes and regression tests.

| Item | Effect and current workaround |
|---|---|
| Whole-second time fields | Drag selections keep the original timestamps, including milliseconds, but From / To display whole seconds. Clicking Calculate then uses those displayed seconds. Keep the drag selection when sub-second endpoints matter. |
| Repeated local times during daylight-saving changes | Manually entered wall-clock times cannot specify which UTC offset to use in a repeated hour. Drag the original points instead; their timestamps and elapsed-time calculations remain unambiguous. |
| Manually entered selection ranges crossing midnight | Both From / To times apply to the loaded day, so a range whose end is before its start (for example 23:00–01:00) is rejected; the message explains the same-day limit. Drag the recorded points instead, or calculate each day separately. |
| Sparse-data hover | Hover inspects the nearest original point in the visible range, which can be some distance from the pointer around a sampling gap. Read the timestamp in the tooltip; gaps are excluded from integration. |
| Live cards after a window-manager restore | Restoring a minimized window refreshes the chart/report immediately, but live cards can wait for the next sample (normally up to 30 seconds on AC). Opening through the tray applies the latest live reading immediately. Sampling and stored history continue throughout. |
| History navigation | The interactive Linux chart currently covers today. Previous-day selection, multi-day comparison, and selection export are future UI work; the CLI and existing full-day exports remain available. |
| Touchpad zoom / pinch and physical input | Linux touchpad zoom is user-reported as not working as expected. Horizontal scrolling and mouse-wheel zoom handlers exist, but pinch/precision-touchpad support is unresolved; Windows input parity also needs checking next iteration. Use zoom buttons or +/- keys for now. See [the next scope](NEXT-ITERATION.md) and [chart navigation](chart-navigation.md). |
| Application language | The GUI is currently mainly English. A complete Chinese README is available; Simplified Chinese application UI and saved language selection are next-iteration work. |

## Validation still requiring hardware

Native Windows tray/dialog interaction, real Linux suspend/resume accounting, controlled battery rundown, and native Debian GNOME interaction are separate from automated fixtures, headless UI tests, and cross-compilation. See [the validation record](linux-port.md) for checks actually performed.
