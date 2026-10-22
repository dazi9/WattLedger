using Avalonia;
using WattLedger.Linux;

return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .With(new X11PlatformOptions { WmClass = "wattledger" })
    .LogToTrace();
