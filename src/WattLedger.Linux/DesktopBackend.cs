namespace WattLedger.Linux;

internal enum DesktopBackend { Auto, Wayland, X11 }

internal sealed record DesktopBackendOptions(DesktopBackend Preference, string[] AppArguments, bool ShowHelp = false)
{
    internal const string EnvironmentVariable = "WATTLEDGER_BACKEND";
    internal static DesktopBackend StartupPreference { get; set; }

    public static DesktopBackendOptions Parse(string[] args, string? environmentValue)
    {
        string? requested = null;
        var remaining = new List<string>();
        var showHelp = false;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            // A database filename is data, even when it resembles a backend option.
            if (argument == "--db" && index + 1 < args.Length)
            {
                remaining.Add(argument);
                remaining.Add(args[++index]);
            }
            else if (argument == "--backend")
            {
                if (index + 1 == args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("--backend requires auto, wayland, or x11.");
                requested = args[++index];
            }
            else if (argument.StartsWith("--backend=", StringComparison.Ordinal))
                requested = argument["--backend=".Length..];
            else if (argument is "--help" or "-h") showHelp = true;
            else remaining.Add(argument);
        }

        var value = requested ?? (string.IsNullOrWhiteSpace(environmentValue) ? "auto" : environmentValue);
        var preference = value.ToLowerInvariant() switch
        {
            "auto" => DesktopBackend.Auto,
            "wayland" => DesktopBackend.Wayland,
            "x11" => DesktopBackend.X11,
            _ => throw new ArgumentException($"Unknown desktop backend '{value}'. Choose auto, wayland, or x11.")
        };
        return new(preference, remaining.ToArray(), showHelp);
    }
}

internal sealed record DesktopDisplayEnvironment(string? Display, string? WaylandDisplay, string? RuntimeDirectory, string? SessionType = null)
{
    public static DesktopDisplayEnvironment Read() => new(
        Environment.GetEnvironmentVariable("DISPLAY"),
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"));

    public string? WaylandSocketPath
    {
        get
        {
            var display = string.IsNullOrWhiteSpace(WaylandDisplay) ? "wayland-0" : WaylandDisplay;
            if (Path.IsPathFullyQualified(display)) return display;
            return !string.IsNullOrWhiteSpace(RuntimeDirectory) && Path.IsPathFullyQualified(RuntimeDirectory)
                ? Path.Combine(RuntimeDirectory, display) : null;
        }
    }

    public DesktopBackend Resolve(DesktopBackend preference, Func<string, bool>? socketExists = null)
    {
        socketExists ??= File.Exists;
        var waylandAvailable = WaylandSocketPath is { } socket && socketExists(socket);
        var x11Available = !string.IsNullOrWhiteSpace(Display);
        if (preference == DesktopBackend.Wayland)
        {
            if (!waylandAvailable)
                throw new InvalidOperationException("No Wayland compositor socket was found. Start WattLedger inside a Wayland desktop and check WAYLAND_DISPLAY / XDG_RUNTIME_DIR, or use --backend=x11 when DISPLAY is available.");
            return DesktopBackend.Wayland;
        }
        if (preference == DesktopBackend.X11)
        {
            if (!x11Available)
                throw new InvalidOperationException("The X11 backend requires DISPLAY. Start WattLedger inside an X11 desktop, enable XWayland, or use --backend=wayland inside a Wayland session.");
            return DesktopBackend.X11;
        }
        var waylandSession = !string.IsNullOrWhiteSpace(WaylandDisplay) ||
                             string.Equals(SessionType, "wayland", StringComparison.OrdinalIgnoreCase);
        if (waylandSession && waylandAvailable) return DesktopBackend.Wayland;
        if (x11Available) return DesktopBackend.X11;
        throw new InvalidOperationException("No graphical display is available. Start WattLedger inside a Wayland or X11 desktop; for a headless machine use wattledger watch or wattledger status.");
    }
}
