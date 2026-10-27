# Chart touchpad input

The Linux chart preserves fractional two-finger scrolling, supports Ctrl+scroll,
consumes native touchpad magnification, and recognizes multi-contact touchscreen
pinch. Native Wayland touchpad pinch requires the patched backend shipped in
`third_party/Avalonia.Wayland` and a compositor that advertises
`zwp_pointer_gestures_v1`. Put the cursor inside the chart's plot before pinching.
Use `WattLedger.Linux --backend=wayland` to select this path explicitly. The
X11/XWayland compatibility backend does not implement native pinch in Avalonia
12.1.4; scrolling, Ctrl+scroll, buttons and keyboard controls remain available.

## Why a backend patch is necessary

The upstream dependency is pinned to Avalonia **12.1.4**, commit
[`2d79ba55fae1b1e71c69f93a668080c9029e6a31`](https://github.com/AvaloniaUI/Avalonia/tree/2d79ba55fae1b1e71c69f93a668080c9029e6a31).
Its [Wayland global bindings](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Wayland/Server/Transient/WaylandGlobals.cs)
and [pointer dispatcher](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Wayland/Server/Transient/WaylandInputDispatcher.cs)
handle `wl_pointer` scrolling but never bind the pointer-gestures protocol.
Adding a chart event handler alone cannot fix physical touchpad pinch.

The reviewable [protocol patch](patches/avalonia-12.1.4-wayland-pinch.patch) uses
the generated protocol classes already present in **NWayland 0.11.0**; it needs no
new native library, input-device access, administrator permissions, reflection,
or private binary hooks. The vendored source keeps the original backend assembly
identity, public signing key and license; the remaining Avalonia assemblies use
the official packages. See `third_party/Avalonia.Wayland/README.md` for the
reproduction and refresh procedure. Do not update the Avalonia packages without
refreshing the pinned backend and reviewing the patch.

The added event chain is:

1. Bind `zwp_pointer_gestures_v1` at an available protocol version from 1 to 3.
2. Create one `zwp_pointer_gesture_pinch_v1` for each seat's pointer, including
   pointers announced before initial globals finish binding or added later.
3. On begin, retain the event's target surface and the cursor position. Subsequent
   packets stay with that surface and anchor instead of following another window.
4. Convert Wayland's cumulative scale into per-packet magnification:
   `magnification = currentScale / previousScale - 1`.
5. Marshal it through the generated surface-event proxy to
   `RawPointerGestureEventArgs(Magnify)`, then the chart's
   `PointerTouchPadGestureMagnify` handler. The chart changes the visible time span
   by `1 / (1 + magnification)` around the pointer anchor.
6. End, cancellation, pointer leave, surface destruction, pointer-capability loss
   and disposal stop the sequence and clear its baseline. Cancellation retains
   the last navigation position; it does not commit a selection or data change.
   Axis packets received during native pinch are ignored to avoid double zoom.

Wayland defines the scale as cumulative from gesture begin; see the
[protocol specification](https://gitlab.freedesktop.org/wayland/wayland-protocols/-/blob/main/unstable/pointer-gestures/pointer-gestures-unstable-v1.xml).
Avalonia's native magnify event uses an incremental delta, as shown by its
[macOS implementation](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/native/Avalonia.Native/src/OSX/AvnView.mm)
and [mouse gesture dispatch](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Base/Input/MouseDevice.cs).
These scale contracts must not be interchanged. The
[touchscreen pinch recognizer](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Base/Input/GestureRecognizers/PinchGestureRecognizer.cs)
uses cumulative scale and actual Touch/Pen contacts; it cannot synthesize physical
touchpad contacts.

## Input behavior

| Input over the plot | Behavior |
| --- | --- |
| Vertical wheel / two-finger vertical scroll | Zoom around the pointer, retaining fractional deltas |
| Predominantly horizontal two-finger scroll | Pan the visible time range |
| Ctrl+scroll, including a horizontal-only packet | Zoom around the pointer |
| Native touchpad pinch on the patched Wayland backend | Continuous zoom around the cursor position at gesture begin |
| Two touchscreen contacts moving apart/together | Continuous zoom around the recognizer's origin |
| Single-contact drag, selection and middle/Shift drag | Existing selection/pan behavior |

Zoom and pan continue to use the shared chart bounds, minimum span and follow-live
state. Zooming pauses following live; End / Follow live restores it. Pinch does not
extend the existing custom calculation selection. Two contacts do not also pan or
select the chart.

## Local physical-device result

On 2026-10-27, the user confirmed that two-finger spread/pinch changes the chart time range in integrated build `d0f6e8b` on the local Ubuntu 24.04 GNOME session. The process ran with `--backend=wayland` and `DISPLAY` removed. Opt-in diagnostics recorded native `magnify` events; sampling continued after the update. Diagnostic tracing was disabled after this check. This confirms the local native input path, not every device, compositor or checklist condition below. Windows physical input remains unverified.

## Focused verification and device checklist

Automated checks exercise fractional scrolling, Ctrl+horizontal scroll, pointer
anchoring, same-timestamp magnify/wheel deduplication, actual headless Touch
contacts, cumulative protocol scale conversion, invalid packets and end/reset.
They verify the chart and protocol state conversion. Building the patched backend
verifies its protocol listener and cross-thread sink APIs. These checks do not
substitute for a physical touchpad and compositor test.

For a local device check, run the native backend with optional chart diagnostics:

```sh
WATTLEDGER_INPUT_TRACE=1 WattLedger.Linux --backend=wayland
```

The trace goes to stderr and includes wheel deltas, magnification and visible time
span. It is disabled by default and does not write sample contents to the trace.

- [ ] Spread and pinch two fingers with the cursor in the plot: the time span
      changes smoothly in both directions without Ctrl.
- [ ] Lift the fingers, start another pinch: no jump from the previous scale.
- [ ] Move the cursor out of the window or close the window during a gesture:
      another window does not inherit its remaining updates.
- [ ] Scroll vertically/horizontally after ending or cancelling pinch: zoom/pan
      resumes normally without double changes.
- [ ] Zoom near either data boundary and the minimum span: the range stays valid.
- [ ] Select an interval, pinch, then resume live: the selection remains usable
      and follow/pause controls agree with the viewport.
- [ ] Repeat on the target compositor and physical touchpad; record compositor,
      backend, device and observed input path before marking hardware verified.

A compositor that does not expose the pointer-gestures protocol cannot deliver
native pinch to this backend. Missing hardware/compositor support remains a
conditional compatibility limit; the application does not read raw input devices
or install a gesture daemon as a workaround.
