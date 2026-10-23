# Windows desktop behavior parity

The WPF client now uses the same `WattLedger.Desktop` source files as Linux for original samples, signed power, missing/sleep gaps, chart layout, range integration and incremental dashboard reads. The shared files have no UI-framework dependency.

- Live cards, tray values and chart use positive watts for discharge and negative watts for charging. Unknown power is not replaced by a measured zero; unknown battery state is explicit.
- Charts initially follow the latest 30 minutes. Pan is the default left-drag mode; Select points enables endpoint selection. Horizontal wheel messages, Shift+wheel, arrow keys and Earlier/Later buttons pan; vertical wheel and +/- zoom. Home/Fit today pauses on the current full extent; End/Follow live resumes tracking. A paused viewport stays fixed as data arrives.
- Manual time fields, Fit selection, clear selection and original-point hover use the same data as Linux. Selection summaries show net/discharged/charged Wh, weighted average, coverage and min/max. Display reduction does not change the calculation.
- Dashboard reads run off the UI thread, are serialized, and are skipped while the window is hidden/minimized. Restore refreshes the latest live sample and report immediately. Resume invalidates the incremental cache.
- CSV, Markdown and JSON exports run in the background. Explicit application exit waits for active readers/exports before disposing the database service.
- Theme resources are resolved by the chart and its cache is invalidated on theme changes, preserving Light/Dark/System behavior from the theme PR.

Validation: Windows Release cross-build on Linux. Shared calculation and Linux interaction checks are recorded in the chart-navigation PR; they are not repeated here. A focused independent code review found no additional blocking lifecycle/data-loss issue. Actual WPF rendering, Windows tray/dialog interaction and physical precision-touchpad gestures still need a Windows machine; cross-compilation is not a substitute for that check.

The chart currently covers today's recorded samples. Previous-day navigation, sub-second manual fields and repeated local-time ambiguity remain in the documented follow-up scope.
