# Third-party notices

WattLedger is MIT-licensed. It depends on the following open-source packages. The versions in the project files are the authoritative list.

| Component | License | Use |
|---|---|---|
| Microsoft.Data.Sqlite / SQLitePCLRaw | MIT / Apache-2.0 | Local SQLite storage |
| Spectre.Console | MIT | CLI rendering |
| Hardcodet.NotifyIcon.Wpf | MIT | System tray icon |
| Avalonia | MIT | Linux desktop UI |
| Avalonia.Desktop / Avalonia.Skia / Avalonia.HarfBuzz | MIT | Linux desktop host, rendering, and text shaping |
| Avalonia.Wayland / NWayland | MIT | Native Wayland desktop protocol integration; Avalonia.Wayland 12.1.4 is built from pinned, patched source |
| Tmds.DBus.Protocol | MIT | Linux logind suspend/resume and desktop tray-host D-Bus integration |
| System.Management | MIT | Optional WMI brightness query |
| .NET / WPF | MIT | Runtime and desktop framework |

The vendored Avalonia.Wayland source, minimal upstream source generators, public signing key, upstream MIT license, and provenance manifest are in [`third_party/Avalonia.Wayland`](third_party/Avalonia.Wayland/README.md). Its pointer-gestures patch is recorded in [`docs/patches/avalonia-12.1.4-wayland-pinch.patch`](docs/patches/avalonia-12.1.4-wayland-pinch.patch). It uses official matching Avalonia and NWayland packages for the remaining assemblies.

SQLite itself is in the public domain. The `SQLitePCLRaw.bundle_e_sqlite3` package embeds a native SQLite build; see the package's notices for details.

No proprietary sensor SDK, HWiNFO component, or MSI Afterburner component is bundled or required.
