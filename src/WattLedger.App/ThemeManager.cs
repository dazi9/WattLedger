using DesktopThemeMode = WattLedger.Desktop.ThemeMode;
using System.Security;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using WattLedger.Desktop;

namespace WattLedger.App;

public partial class App
{
    private readonly ThemePreference _themePreference = new();
    private bool _themeStopped;
    public DesktopThemeMode CurrentThemeMode { get; private set; } = DesktopThemeMode.System;
    public bool IsDarkTheme { get; private set; }
    public event EventHandler? ThemeChanged;

    private void InitializeTheme()
    {
        CurrentThemeMode = _themePreference.Load();
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;
    }

    public void SetThemeMode(DesktopThemeMode mode)
    {
        _themePreference.Save(mode);
        CurrentThemeMode = mode;
        ApplyTheme();
    }

    private void OnSystemThemeChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (_themeStopped || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_themeStopped && CurrentThemeMode == DesktopThemeMode.System) ApplyTheme();
        });
    }

    private void ApplyTheme()
    {
        IsDarkTheme = CurrentThemeMode == DesktopThemeMode.Dark ||
            (CurrentThemeMode == DesktopThemeMode.System && SystemUsesDarkTheme());
        foreach (var (key, value) in ThemePalette.Colors(IsDarkTheme))
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            if (key.EndsWith("Color", StringComparison.Ordinal)) Resources[key] = color;
            else
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                Resources[key] = brush;
            }
        }
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    private void ShutdownTheme()
    {
        _themeStopped = true;
        SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged;
    }
}
