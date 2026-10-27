# Desktop appearance

Both desktop apps offer **System**, **Light**, and **Dark** in the footer's Theme selector. The default is System. A choice takes effect immediately, including chart labels, fills, hover cards, controls, and the Windows title bar, and survives app restarts. It does not change the database or sampling settings.

System follows Avalonia's effective desktop appearance on Linux and the Windows `AppsUseLightTheme` preference on Windows. The Windows app observes system preference changes while running. Linux follows the appearance supplied by the desktop/platform integration; if that integration does not expose a preference, Avalonia's default applies. Choosing Light or Dark overrides the system setting.

Preferences are saved to:

- Linux: `$XDG_CONFIG_HOME/wattledger/preferences.json`, falling back to `~/.config/wattledger/preferences.json`.
- Windows: `%LocalAppData%/WattLedger/preferences.json`.

The `theme` value is `System`, `Light`, or `Dark`. Missing, malformed, or unrecognized settings fall back to System. Other JSON properties are preserved when saving. Failure to save is reported and the existing theme remains selected.

## Implementation contract

`src/WattLedger.Desktop/ThemePreference.cs` is linked into both desktop projects. It contains `ThemeMode`, preference persistence, and the shared semantic palette. Each App exposes `CurrentThemeMode`, `IsDarkTheme`, `SetThemeMode(ThemeMode)`, and `ThemeChanged`.

Avalonia installs Light/Dark theme dictionaries; XAML uses DynamicResource, while code-built controls share live brushes that are updated on actual theme changes. The custom chart discards cached labels/layout when its effective theme changes. WPF updates dynamic resources and notifies its view model and chart; it does not use WPF's experimental ThemeMode API.

Chart resource keys: `ChartGridBrush`, `ChartZeroBrush`, `ChartLabelBrush`, `ChartPowerBrush`, `ChartBatteryBrush`, `ChartSelectionBrush`, `ChartHoverBrush`, `ChartAreaTopColor`, and `ChartAreaBottomColor`. The last two are Color values; the others are brushes. General resources include `WindowBackgroundBrush`, `CardBrush`, `CardAltBrush`, `BorderBrush`, `TextBrush`, `MutedBrush`, `GreenBrush`, `OrangeBrush`, `BlueBrush`, `RedBrush`, `ButtonBrush`, `ButtonHoverBrush`, `ButtonPressedBrush`, `SelectionBrush`, and `RowHoverBrush`.

## Focused validation

Build the Linux desktop and cross-build WPF with `-p:EnableWindowsTargeting=true`. Run the `ThemePreferenceTests` and `ThemeTests` filters to check persistence and render the same chart in both palettes. Linux rendering and Windows compilation do not substitute for a Windows desktop check of the theme picker, title bar, and live system-theme switching.

## Chart previews

The same synthetic charging/discharging data is rendered with each palette:

![Light chart](theme-light.png)

![Dark chart](theme-dark.png)

Set `WATTLEDGER_THEME_SCREENSHOT_DIR` to an output directory when running `ThemeTests` to reproduce these previews.
