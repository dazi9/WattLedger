# Windows chart touchpad and touch input

The WPF chart now has explicit adapters for three input paths. They all change the shared `ChartInteractionState` viewport; they do not transform the rendered bitmap or change sample/energy data.

| Input delivered by Windows | Chart handling |
|---|---|
| Ordinary or fractional `MouseWheel` | Pointer-anchored zoom; fractional wheel steps remain fractional. Shift + wheel pans. |
| Native `WM_MOUSEWHEEL` / `WM_MOUSEHWHEEL` with `MK_CONTROL` | Zoom, with Ctrl taking priority over Shift/horizontal pan. Read the modifier from `wParam`, rather than assuming WPF global keyboard state reflects a synthesized touchpad Ctrl press. The hook consumes the message so WPF cannot apply it a second time. |
| Native `WM_MOUSEHWHEEL` without Ctrl | Horizontal pan, retaining the Windows scroll-right sign and bounded deltas. |
| `WM_GESTURE` / `GID_ZOOM`, when delivered | Use the gesture's physical-screen center (converted to WPF device-independent coordinates) and incremental distance ratios. The initial packet establishes a baseline without moving or pausing the chart. Only owned zoom handles are closed; unhandled gestures and generic begin/end messages continue to the default window procedure. |
| WPF Windows Touch manipulation, when delivered | Two-contact scale gestures zoom around the manipulation origin using incremental scale. A one-contact press does not start a scale manipulation. Inertial scale packets are completed rather than continuing to zoom after release. |

The chart retains ordinary Pan/Select mouse behavior, Shift/middle dragging, keyboard controls, bounds and follow-live rules. Actual zoom changes pause following; initial/identity/invalid scale packets do not. Native and WPF touch gesture ownership is mutually exclusive, and wheel packets promoted while a gesture is active are ignored. Hiding/unloading the chart, window focus loss and cancellation reset input ownership; HWND hooks are removed on unload.

## What this does and does not establish

Microsoft documents that applications which do not use Direct Manipulation or InteractionTracker typically receive Precision Touchpad gestures through system-generated mouse-wheel messages. WPF touch manipulation is a Windows Touch path (for example, a touchscreen); it is not proof that every Precision Touchpad emits WPF touch contacts. Likewise, `WM_GESTURE` availability depends on the window/input stack. This implementation does not register the entire window for raw touchpad pointer input or require new preview Precision Touchpad APIs.

Local validation on Linux covers shared scale math, native packet/coordinate decoding, native structure layout, gesture ownership and cumulative-to-incremental transitions. A Windows-targeted Release cross-build verifies the WPF/Win32 adapter compiles. These checks do **not** establish that a physical Windows touchpad or touchscreen produced these events. A real Windows device must still verify Ctrl+pinch/wheel, native/WPF pinch where available, horizontal pan, DPI/multi-monitor anchoring, focus cancellation and no duplicate zoom.

## Optional input diagnostics

Set `WATTLEDGER_INPUT_TRACE=1` before starting the desktop and capture its standard error from a terminal/redirected launch. The opt-in trace names the input path, modifier flags, wheel steps or gesture scale/distance; it is off by default and does not include database contents or transmit anything. If a device never delivers a zoom input path, retain the zoom buttons and +/- keys while reporting the Windows build, device type and observed trace.

References: [Precision Touchpad input delivery](https://learn.microsoft.com/en-us/windows/win32/input-precisiontouchpad/precision-touchpad-portal), [WM_GESTURE ownership and zoom arguments](https://learn.microsoft.com/en-us/windows/win32/wintouch/wm-gesture), [GESTUREINFO and physical coordinates](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-gestureinfo), [WPF Windows Touch manipulation](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/walkthrough-creating-your-first-touch-application).
