# Linux display backends

WattLedger uses Avalonia 12.1.4 with native Wayland and X11 support. Native Wayland is experimental upstream; X11 remains available for compatibility. Display selection is independent of battery sampling, storage, and desktop theme.

## Choosing a backend

```sh
wattledger-desktop --backend=auto
wattledger-desktop --backend=wayland
wattledger-desktop --backend=x11
WATTLEDGER_BACKEND=x11 wattledger-desktop
```

`--backend auto|wayland|x11` also accepts a separate argument. The command line overrides `WATTLEDGER_BACKEND`; the default is `auto`. Other options, including `--db` and `--tray`, keep their existing behavior. `--help` works without a graphical session. Enabling start at login saves the chosen preference in the XDG autostart entry.

- **auto:** Prefer Wayland when `WAYLAND_DISPLAY` or a Wayland `XDG_SESSION_TYPE` identifies the session and the named compositor socket exists. If an X11 `DISPLAY` is also present, Avalonia's `UseWaylandWithFallback()` checks compositor usability and can retain the X11 backend. Otherwise use the available X11 session. No display produces an error with headless CLI alternatives.
- **wayland:** Require a local compositor socket and initialize native Wayland directly. Relative display names are resolved under an absolute `XDG_RUNTIME_DIR`; absolute `WAYLAND_DISPLAY` paths are supported. If no name is supplied, `wayland-0` is used. Startup failures identify the selected backend and suggest checking the compositor/EGL drivers or using X11. The explicit mode does not silently switch protocols.
- **x11:** Require `DISPLAY` and use Avalonia's X11 backend, including its normal software rendering fallback. In a Wayland session this needs XWayland. This mode also supports an X11 desktop or a forwarded X11 display.

Both backends identify the application as `wattledger` (`WM_CLASS` on X11 and `app_id` on Wayland), matching the installed desktop entry. Choosing a backend applies when starting a new process; a second instance activates the already-running instance.

## Runtime and distribution requirements

The self-contained `linux-x64` publish directory can be used independently of Debian packaging. It still needs the distribution's native graphics and font libraries: fontconfig, FreeType, ICU, OpenSSL, and the selected display stack. Wayland needs libwayland-client, libwayland-cursor, libwayland-egl, libxkbcommon, and a working EGL/OpenGL driver. X11 needs the existing X11 client libraries. Install an appropriate desktop portal for native file-picker integration; the application retains the GTK fallback where Avalonia provides it. A StatusNotifier/AppIndicator tray host is optional; without one the window remains available.

The `.deb` declares the Wayland/EGL libraries alongside the existing X11 libraries. XWayland is a recommendation rather than a mandatory dependency, so a native Wayland installation can omit it. Package names differ on other distributions; use their package manager to satisfy the shared-library requirements. The packaging script still targets Debian/Ubuntu `amd64`; this change does not add RPM/Arch packages or claim other CPU architectures have been validated.

## Verification and limitations

Focused backend tests cover argument/environment precedence, missing display errors, socket selection, stale socket fallback to X11, explicit-mode behavior, and preserving application arguments. The initial targeted validation passed 17 backend, headless-rendering, and tray-cleanup cases. After rebasing onto the latest main (including the day-boundary and shared chart-state fixes), 30 focused backend, shared chart-state and theme cases passed. The application builds against Avalonia 12.1.4; test helpers still emit Bitmap.Save deprecation and xUnit cancellation-token analyzer warnings, deferred as non-blocking cleanup. A process-level check confirmed that `--help` works without a display and all three modes return actionable errors when their display is absent. Build and runtime results for this revision belong in the PR validation record. Headless tests validate selection logic, not a compositor or GPU driver.

Upstream currently labels native Wayland experimental and documents missing KDE-specific integrations, including global menus, window icons, and blur-behind. Changing a backend cannot provide a missing tray host. Physical suspend/resume and battery measurement validation are separate concerns. Native GNOME/KDE sessions, multiple distributions, fractional scaling, portals, and tray extensions must be tested explicitly before claiming compatibility for those combinations.

References: [Avalonia Linux backend documentation](https://docs.avaloniaui.net/docs/platform-specific-guides/linux), [Avalonia 12 migration notes](https://docs.avaloniaui.net/docs/avalonia12-breaking-changes), [Avalonia.Wayland 12.1.4 package](https://www.nuget.org/packages/Avalonia.Wayland/12.1.4). The package's XML API documentation additionally describes `UseWaylandWithFallback()` and `WaylandPlatformOptions.AppId`.
