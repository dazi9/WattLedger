using Avalonia;
using WattLedger.Linux;

DesktopBackend? backend = null;
try
{
    var options = DesktopBackendOptions.Parse(args,
        Environment.GetEnvironmentVariable(DesktopBackendOptions.EnvironmentVariable));
    if (options.ShowHelp)
    {
        Console.WriteLine("Usage: WattLedger.Linux [--db <path>] [--tray] [--backend auto|wayland|x11]");
        Console.WriteLine("auto prefers a local Wayland compositor, then X11. Native Wayland is experimental.");
        Console.WriteLine("WATTLEDGER_BACKEND sets the default; --backend overrides it. No display: use wattledger watch.");
        return 0;
    }
    var display = DesktopDisplayEnvironment.Read();
    backend = display.Resolve(options.Preference);
    var allowFallback = options.Preference == DesktopBackend.Auto && !string.IsNullOrWhiteSpace(display.Display);
    DesktopBackendOptions.StartupPreference = options.Preference;
    Console.Error.WriteLine(backend == DesktopBackend.Wayland
        ? allowFallback
            ? "WattLedger: preferring native Wayland (experimental), with X11 fallback."
            : "WattLedger: using native Wayland (experimental). Use --backend=x11 for compatibility."
        : "WattLedger: using X11.");
    return BuildAvaloniaApp(backend.Value, allowFallback).StartWithClassicDesktopLifetime(options.AppArguments);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"WattLedger desktop could not start{(backend is null ? "" : $" using {backend}")}: {ex.Message}");
    if (backend == DesktopBackend.Wayland)
        Console.Error.WriteLine("Check the Wayland compositor and EGL/OpenGL drivers. Try --backend=x11 with XWayland enabled if native Wayland is unsupported on this desktop.");
    else if (backend == DesktopBackend.X11)
        Console.Error.WriteLine("Check DISPLAY and the X11/XWayland server. On a Wayland desktop, try --backend=wayland.");
    return 1;
}

static AppBuilder BuildAvaloniaApp(DesktopBackend backend, bool allowFallback)
{
    var builder = AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new X11PlatformOptions { WmClass = "wattledger" })
        .With(new WaylandPlatformOptions { AppId = "wattledger" })
        .LogToTrace();
    if (backend != DesktopBackend.Wayland) return builder;
    return allowFallback ? builder.UseWaylandWithFallback() : builder.UseWayland();
}
