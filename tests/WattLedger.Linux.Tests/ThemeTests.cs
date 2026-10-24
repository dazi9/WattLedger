using WattLedger.Desktop;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using WattLedger.Linux;

namespace WattLedger.Linux.Tests;

public sealed class ThemeTests
{
    [AvaloniaFact]
    public void ThemeChangesUpdateLiveBrushesAndChartRendering()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        app.Resources.ThemeDictionaries[ThemeVariant.Light] = ThemeResources.CreateDictionary(false);
        app.Resources.ThemeDictionaries[ThemeVariant.Dark] = ThemeResources.CreateDictionary(true);
        var first = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.FromHours(8));
        var chart = new PowerChart
        {
            Width = 480,
            Height = 200,
            Samples = [new(first, 5, 60), new(first.AddSeconds(30), -3, 61), new(first.AddSeconds(60), 8, 61)]
        };
        var background = ThemeResources.Brush("CardBrush");
        var label = ThemeResources.Brush("ChartLabelBrush");
        var panel = new Border { Width = 480, Height = 200, Background = background, Child = chart };
        var window = new Window { Content = panel };
        try
        {
            window.Show();
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                app.RequestedThemeVariant = variant;
                ThemeResources.Refresh(app);
                window.UpdateLayout();
                Assert.Equal(variant == ThemeVariant.Light ? Color.Parse("#FFFFFF") : Color.Parse("#14181F"), background.Color);
                Assert.Same(label, ThemeResources.Brush("ChartLabelBrush"));
                panel.Measure(new Size(480, 200));
                panel.Arrange(new Rect(0, 0, 480, 200));
                using var bitmap = new RenderTargetBitmap(new PixelSize(480, 200));
                bitmap.Render(panel);
                if (Environment.GetEnvironmentVariable("WATTLEDGER_THEME_SCREENSHOT_DIR") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    bitmap.Save(Path.Combine(directory, $"theme-{variant.Key.ToString()!.ToLowerInvariant()}.png"));
                }
            }
        }
        finally
        {
            window.Close();
            app.RequestedThemeVariant = original;
            ThemeResources.Refresh(app);
        }
    }
}
