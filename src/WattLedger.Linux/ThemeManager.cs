using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using WattLedger.Desktop;

namespace WattLedger.Linux;

public sealed partial class App
{
    private readonly ThemePreference _themePreference = new();
    public ThemeMode CurrentThemeMode { get; private set; } = ThemeMode.System;
    public bool IsDarkTheme => ActualThemeVariant == ThemeVariant.Dark;
    public event EventHandler? ThemeChanged;

    private void InitializeTheme()
    {
        Resources.ThemeDictionaries[ThemeVariant.Light] = ThemeResources.CreateDictionary(false);
        Resources.ThemeDictionaries[ThemeVariant.Dark] = ThemeResources.CreateDictionary(true);
        ActualThemeVariantChanged += (_, _) => RefreshTheme();
        CurrentThemeMode = _themePreference.Load();
        ApplyThemeMode();
    }

    public void SetThemeMode(ThemeMode mode)
    {
        _themePreference.Save(mode);
        CurrentThemeMode = mode;
        ApplyThemeMode();
    }

    private void ApplyThemeMode()
    {
        RequestedThemeVariant = CurrentThemeMode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
        RefreshTheme();
    }

    private void RefreshTheme()
    {
        ThemeResources.Refresh(this);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }
}

// Code-built controls share live brushes. XAML uses the same theme dictionaries
// through DynamicResource, while these brush instances update when the OS theme changes.
internal static class ThemeResources
{
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();
    internal static readonly LinearGradientBrush ChartAreaBrush = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = [new GradientStop(default, 0), new GradientStop(default, 1)]
    };

    internal static ResourceDictionary CreateDictionary(bool dark)
    {
        var resources = new ResourceDictionary();
        foreach (var (key, value) in ThemePalette.Colors(dark))
        {
            var color = Color.Parse(value);
            resources[key] = key.EndsWith("Color", StringComparison.Ordinal) ? color : new SolidColorBrush(color);
        }
        return resources;
    }

    internal static SolidColorBrush Brush(string key)
    {
        if (!Brushes.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(Resolve(key));
            Brushes[key] = brush;
        }
        return brush;
    }

    internal static void Refresh(Application app)
    {
        foreach (var (key, brush) in Brushes) brush.Color = Resolve(key);
        ChartAreaBrush.GradientStops[0].Color = Resolve("ChartAreaTopColor");
        ChartAreaBrush.GradientStops[1].Color = Resolve("ChartAreaBottomColor");
    }

    private static Color Resolve(string key)
    {
        var app = Application.Current;
        if (app?.TryGetResource(key, app.ActualThemeVariant, out var resource) == true)
        {
            if (resource is ISolidColorBrush brush) return brush.Color;
            if (resource is Color color) return color;
        }
        return Color.Parse(ThemePalette.Colors(app?.ActualThemeVariant == ThemeVariant.Dark)[key]);
    }
}
