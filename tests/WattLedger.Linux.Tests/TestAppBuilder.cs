using Avalonia;
using Avalonia.Headless;
using Avalonia.Skia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(WattLedger.Linux.Tests.TestAppBuilder))]

namespace WattLedger.Linux.Tests;

public sealed class TestAppBuilder : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new Style(selector => selector.OfType<Window>())
        {
            Setters =
            {
                new Setter(Window.BackgroundProperty, new SolidColorBrush(Color.Parse("#0B0D10"))),
                new Setter(Window.ForegroundProperty, new SolidColorBrush(Color.Parse("#E6EDF3")))
            }
        });
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestAppBuilder>()
            .UseSkia()
            .UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
