using Avalonia;
using Avalonia.Headless;
using Avalonia.Skia;

[assembly: AvaloniaTestApplication(typeof(WattLedger.Linux.Tests.TestAppBuilder))]

namespace WattLedger.Linux.Tests;

public sealed class TestAppBuilder : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestAppBuilder>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
