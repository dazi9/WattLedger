using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(WattLedger.Linux.Tests.TestAppBuilder))]

namespace WattLedger.Linux.Tests;

public sealed class TestAppBuilder : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestAppBuilder>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
