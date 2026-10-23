namespace WattLedger.Linux.Tests;

public sealed class DesktopBackendTests
{
    [Theory]
    [InlineData("auto", "Auto")]
    [InlineData("wayland", "Wayland")]
    [InlineData("X11", "X11")]
    public void CommandLineOverridesEnvironmentAndPreservesAppArguments(string value, string expected)
    {
        var options = DesktopBackendOptions.Parse(["--tray", "--backend", value, "--db", "/tmp/a ledger.db"], "invalid-env");
        Assert.Equal(expected, options.Preference.ToString());
        Assert.Equal(["--tray", "--db", "/tmp/a ledger.db"], options.AppArguments);
    }

    [Fact]
    public void EqualsOptionAndEnvironmentAreSupported()
    {
        Assert.Equal(DesktopBackend.X11, DesktopBackendOptions.Parse(["--backend=x11"], "wayland").Preference);
        Assert.Equal(DesktopBackend.Wayland, DesktopBackendOptions.Parse([], "wayland").Preference);
        Assert.Equal(DesktopBackend.Auto, DesktopBackendOptions.Parse([], null).Preference);
    }

    [Theory]
    [InlineData("--backend")]
    [InlineData("--backend=")]
    [InlineData("--backend=vulkan")]
    public void InvalidOptionGivesActionableError(string option)
    {
        var error = Assert.Throws<ArgumentException>(() => DesktopBackendOptions.Parse([option], null));
        Assert.Contains("auto", error.Message);
        Assert.Contains("wayland", error.Message);
        Assert.Contains("x11", error.Message);
    }

    [Fact]
    public void BackendOptionDoesNotConsumeDatabaseFilenameOrFollowingFlag()
    {
        var options = DesktopBackendOptions.Parse(["--db", "--backend=wayland"], "x11");
        Assert.Equal(DesktopBackend.X11, options.Preference);
        Assert.Equal(["--db", "--backend=wayland"], options.AppArguments);
        Assert.Throws<ArgumentException>(() => DesktopBackendOptions.Parse(["--backend", "--tray"], null));
        Assert.False(DesktopBackendOptions.Parse(["--db", "--help"], null).ShowHelp);
        Assert.True(DesktopBackendOptions.Parse(["--help"], null).ShowHelp);
    }

    [Fact]
    public void AutoPrefersNativeWaylandWhenSessionSocketExists()
    {
        var environment = new DesktopDisplayEnvironment(":0", "wayland-1", "/run/user/1000");
        Assert.Equal(DesktopBackend.Wayland, environment.Resolve(DesktopBackend.Auto, path => path == "/run/user/1000/wayland-1"));
        Assert.Equal(DesktopBackend.X11, environment.Resolve(DesktopBackend.X11, _ => true));
    }

    [Fact]
    public void AutoFallsBackToX11ForMissingWaylandSocket()
    {
        var environment = new DesktopDisplayEnvironment(":0", "wayland-stale", "/run/user/1000");
        Assert.Equal(DesktopBackend.X11, environment.Resolve(DesktopBackend.Auto, _ => false));
        var error = Assert.Throws<InvalidOperationException>(() => environment.Resolve(DesktopBackend.Wayland, _ => false));
        Assert.Contains("--backend=x11", error.Message);
        Assert.Contains("WAYLAND_DISPLAY", error.Message);
    }

    [Fact]
    public void AutoDoesNotSelectUnrelatedCompositorInX11Session()
    {
        var environment = new DesktopDisplayEnvironment(":0", null, "/run/user/1000", "x11");
        Assert.Equal(DesktopBackend.X11, environment.Resolve(DesktopBackend.Auto, _ => true));
        Assert.Equal(DesktopBackend.Wayland, environment.Resolve(DesktopBackend.Wayland, _ => true));
    }

    [Fact]
    public void WaylandSessionCanUseDefaultDisplayName()
    {
        var environment = new DesktopDisplayEnvironment(null, null, "/run/user/1000", "wayland");
        Assert.Equal(DesktopBackend.Wayland, environment.Resolve(DesktopBackend.Auto, path => path.EndsWith("/wayland-0")));
    }

    [Fact]
    public void AbsoluteWaylandDisplayDoesNotRequireRuntimeDirectory()
    {
        var environment = new DesktopDisplayEnvironment(null, "/tmp/custom-wayland", null);
        Assert.Equal(DesktopBackend.Wayland, environment.Resolve(DesktopBackend.Auto, path => path == "/tmp/custom-wayland"));
    }

    [Fact]
    public void RelativeRuntimeDirectoryIsNotUsedForWaylandSocket()
    {
        var environment = new DesktopDisplayEnvironment(null, "wayland-0", "relative");
        Assert.Null(environment.WaylandSocketPath);
        Assert.Throws<InvalidOperationException>(() => environment.Resolve(DesktopBackend.Wayland, _ => true));
    }

    [Fact]
    public void HeadlessAndExplicitX11FailuresGiveRecoveryInstructions()
    {
        var environment = new DesktopDisplayEnvironment(null, null, null);
        var headless = Assert.Throws<InvalidOperationException>(() => environment.Resolve(DesktopBackend.Auto));
        Assert.Contains("wattledger watch", headless.Message);
        var x11 = Assert.Throws<InvalidOperationException>(() => environment.Resolve(DesktopBackend.X11));
        Assert.Contains("DISPLAY", x11.Message);
    }
}
