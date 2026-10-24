# Chart navigation

The Linux chart opens in **Pan** mode, following the latest 30 minutes of today's recorded data. A shorter recording uses its available extent, with a minimum 60-second display span. With more than 30 minutes recorded, ordinary dragging can reveal earlier samples immediately.

| Action | Result |
|---|---|
| Left-button drag in **Pan** mode | Move through the recorded time range. Drag right to reveal earlier data. |
| **Earlier** / **Later**, or Left / Right keys | Move by 20% of the visible duration; buttons disable at the corresponding boundary. |
| Horizontal touchpad scroll | Pan in either mode; the dominant scroll axis wins to avoid accidental zoom from small diagonal motion. |
| Vertical wheel, with or without Ctrl | Zoom around the pointer. Zoom buttons and + / − keys zoom around the center. |
| **Select**, then left-button drag | Select two original sample timestamps and calculate their interval. From / To inputs still accept custom times. |
| Shift + left-button drag, or middle-button drag | Pan temporarily, including in Select mode. |
| **Fit today**, double-click, or Home | Fit all currently loaded data and pause live following. |
| **Follow live**, or End | Resume following the newest sample. Keep the current visible duration; when the whole extent is visible, use the latest 30 minutes instead. |
| **Fit selection** / **Clear selection**, or Escape to clear | Fit the selected interval or remove the selection without changing recorded data. |

Panning, zooming, or starting a drag pauses live following. New samples continue to be recorded and loaded while the view stays in place. The chart labels its following/paused state and whether it is at the earliest or latest data. When all recorded data fits, it explicitly says to zoom in before panning. Changing days resets the view to follow the new day's latest data.

Navigation is limited to the **currently loaded local calendar day**, from its first to last recorded sample (plus display padding for recordings shorter than one minute). No previous-day or multi-day database browser is added here. Empty intervals inside that range can be navigated, but do not fabricate readings or selectable points.

## Shared implementation and validation

`src/WattLedger.Desktop` contains the framework-independent sample model, selection integration, layout, incremental dashboard loading, and report-refresh gate. Linux compile-links these sources; Windows can use the same files without a UI-framework dependency. Integration formulas and gap handling are unchanged: selection estimates use original samples, and missing rates and confirmed or timestamp-detected sleep gaps remain excluded.

Focused headless tests exercise ordinary left dragging without a preceding zoom, horizontal scrolling, mode switching, bounds, and follow/pause behavior, alongside the existing interval-math and shared-loader checks. Physical touchpad behavior still needs confirmation on the user's desktop session; headless pointer tests do not establish native Wayland or cross-distribution compatibility.
