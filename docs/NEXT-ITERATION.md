# Next iteration: native Linux desktop and Windows parity

Agreed scope for the next iteration, 2026-10-24. These changes are separate from the current sampler/chart fixes.

## Architecture decision

Use Avalonia 12.1's native Wayland backend as the first implementation path. Keep the existing .NET measurement/storage core and extract shared presentation behavior where useful. This minimizes duplicated measurement logic and allows Windows and Linux to share chart calculations and interaction rules. Windows can retain its WPF shell while adopting the same behavior.

Avalonia's [12.1 release notes](https://avaloniaui.net/blog/release-12-1) and [Linux documentation](https://docs.avaloniaui.net/docs/platform-specific-guides/linux) describe a direct Wayland backend, but explicitly mark it experimental. `UseWayland()` has no automatic X11 fallback. The implementation must select backends deliberately and provide an explicit X11 compatibility option. A native protocol backend does not imply Qt/GTK native widgets.

The first PR is a validation gate: confirm the package/API versions, native Wayland startup without XWayland, input, scaling, window restoration, tray behavior, and renderer stability before making it the default. Keep the existing working backend available until this passes. Revisit Qt 6/QML only if this gate reveals blocking desktop integration or reliability gaps; a Qt rewrite would also require a maintained boundary to the .NET core.

## Separate branches and PRs

| Order | Proposed branch | Deliverable and acceptance criteria |
|---|---|---|
| 1 | `codex/native-wayland` | Upgrade/validate the backend; isolate desktop integration; explicit Wayland/X11 selection with actionable startup errors. Validate GNOME and KDE on representative Debian/Ubuntu and Fedora systems, including fractional scaling, clipboard, tray availability and restore. Record tested distribution versions and native dependencies; provide reproducible packages and an X11 compatibility path. |
| 2 | `codex/chart-navigation` | Resolve the reported inability to slide the chart left/right. Provide discoverable pan/select modes, ordinary mouse dragging and horizontal touchpad scrolling; keep zoom anchored to the pointer and permit navigating available history. Add follow-live/reset controls and clear range limits. Share range/selection calculations and specify identical mouse, keyboard and touchpad behavior for both desktop clients. Verify real compositor input as well as automated gestures. |
| 3 | `codex/theme-modes` | Light, dark and system modes with persisted preference and live system-theme updates. Replace fixed colors with semantic resources, including chart grid, axes, selection and tooltips; verify contrast and exports in both themes. |
| 4 | `codex/windows-behavior-parity` | Apply the shared chart interaction, selection statistics, theme settings and error/status behavior to Windows. Align startup/lease conflict handling, tray restore, export and missing/sleep-data presentation. Preserve platform-specific sensor capabilities and test the WPF tray path on Windows. |

Each implementation PR should include its own focused tests, screenshots where applicable, and an updated validation record. Use shared presentation contracts rather than adding UI dependencies to the measurement core. Do not claim support for untested distributions or native Windows interactions based only on cross-compilation.

## Current limitation carried forward

Horizontal navigation remains a user-reported issue. The current chart implements panning only after zooming, using Shift + left drag or middle drag; a normal left drag selects points, and horizontal touchpad scrolling is not implemented. This is not considered resolved by the current headless gesture tests. PR 2 must validate the user's ordinary mouse/touchpad workflow and make the interaction discoverable.
