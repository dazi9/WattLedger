using Avalonia;
using WattLedger.Linux;

return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .LogToTrace();
