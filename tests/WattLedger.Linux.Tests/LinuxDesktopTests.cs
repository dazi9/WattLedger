using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Diagnostics;
using WattLedger.Core.Engine;
using WattLedger.Linux;

namespace WattLedger.Linux.Tests;

public sealed class LinuxDesktopTests
{
    [Fact]
    public void DesktopExecEscaping_round_trips_special_characters()
    {
        var first = "a path/with spaces, \"quotes\", $cash, `ticks`, \\slashes and 50%";
        var second = "--db";
        var third = "/tmp/a database.sqlite";
        var desktopEntry = StartupRegistration.BuildDesktopEntry(["/opt/Watt Ledger/wattledger", "--tray", second, third, first]);
        var execLine = Assert.Single(desktopEntry.Split('\n'), line => line.StartsWith("Exec=", StringComparison.Ordinal));

        Assert.Equal(
            ["/opt/Watt Ledger/wattledger", "--tray", second, third, first],
            DecodeExecValue(execLine["Exec=".Length..]));
    }

    [Theory]
    [InlineData("path\nwith newline")]
    [InlineData("path\rwith carriage return")]
    public void DesktopExecEscaping_rejects_line_breaks(string argument)
    {
        Assert.Throws<ArgumentException>(() => StartupRegistration.DesktopQuote(argument));
    }

    [Fact]
    public void Options_accept_db_override_and_tray_flag()
    {
        var options = AppOptions.Parse(["--tray", "--db", "/tmp/a database.sqlite"]);

        Assert.True(options.StartInTray);
        Assert.Equal(Path.GetFullPath("/tmp/a database.sqlite"), options.DatabasePath);
    }

    [Theory]
    [InlineData("--db")]
    [InlineData("--db=")]
    public void Options_normalize_relative_database_paths_once(string option)
    {
        var options = option == "--db"
            ? AppOptions.Parse([option, Path.Combine("relative-folder", "ledger.sqlite")])
            : AppOptions.Parse([$"{option}{Path.Combine("relative-folder", "ledger.sqlite")}"]);

        Assert.Equal(Path.GetFullPath(Path.Combine("relative-folder", "ledger.sqlite")), options.DatabasePath);
    }

    [Fact]
    public void Report_refresh_gate_defers_hidden_work_and_refreshes_when_shown()
    {
        var gate = new ReportRefreshGate();

        Assert.False(gate.TryConsumeRefresh());
        Assert.False(gate.MarkDirty());
        Assert.True(gate.SetVisible(true));
        Assert.True(gate.TryConsumeRefresh());
        Assert.False(gate.TryConsumeRefresh());

        Assert.False(gate.SetVisible(false));
        Assert.False(gate.MarkDirty());
        Assert.True(gate.IsDirty);
        Assert.True(gate.SetVisible(true));
        Assert.True(gate.TryConsumeRefresh());
    }

    [Fact]
    public void Tray_host_state_tracks_watcher_owner_acquisition_and_loss()
    {
        Assert.Null(TrayHostState.ApplyOwnerChanged("org.example.OtherService", ":1.42"));
        Assert.True(TrayHostState.ApplyOwnerChanged(TrayHostState.WatcherName, ":1.42"));
        Assert.False(TrayHostState.ApplyOwnerChanged(TrayHostState.WatcherName, ""));
        Assert.False(TrayHostState.ConnectionLost());
    }

    [Fact]
    public async Task Tray_host_detector_dispose_is_idempotent()
    {
        var detector = new TrayHostDetector();

        await detector.DisposeAsync();
        await detector.DisposeAsync();
    }

    [Fact]
    public void Autostart_path_uses_absolute_xdg_config_home_and_ignores_relative_values()
    {
        var priorConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "/tmp/wattledger-config");
            Assert.Equal("/tmp/wattledger-config/autostart/wattledger.desktop", StartupRegistration.EntryPath);

            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "relative-config");
            Assert.Equal(Path.Combine(home, ".config", "autostart", "wattledger.desktop"), StartupRegistration.EntryPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", priorConfigHome);
        }
    }

    [Fact]
    public void Second_instance_requests_activation_and_owner_removes_socket()
    {
        var runtimeDirectory = Path.Combine(Path.GetTempPath(), $"wattledger-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runtimeDirectory);
        var priorRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtimeDirectory);
        try
        {
            using var activated = new ManualResetEventSlim();
            using var owner = Assert.IsType<SingleInstance>(SingleInstance.TryStart(activated.Set));
            var endpoint = Path.Combine(runtimeDirectory, "wattledger-linux.sock");

            Assert.Null(SingleInstance.TryStart(() => { }));
            Assert.True(activated.Wait(TimeSpan.FromSeconds(2)));

            owner.Dispose();
            Assert.False(File.Exists(endpoint));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", priorRuntime);
            Directory.Delete(runtimeDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Native_second_launch_exits_cleanly_while_first_instance_is_alive()
    {
        if (!OperatingSystem.IsLinux() ||
            Environment.GetEnvironmentVariable("WATTLEDGER_NATIVE_GUI_TESTS") != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"wattledger-native-{Guid.NewGuid():N}");
        var runtime = Path.Combine(root, "runtime");
        Directory.CreateDirectory(runtime);
        File.SetUnixFileMode(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var primary = StartDesktopApp(runtime, Path.Combine(root, "primary.sqlite"));
        Process? second = null;
        try
        {
            var endpoint = Path.Combine(runtime, "wattledger-linux.sock");
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while (!File.Exists(endpoint) && !primary.HasExited)
            {
                await Task.Delay(100, startupTimeout.Token);
            }

            Assert.True(File.Exists(endpoint), primary.HasExited
                ? $"First app exited early with code {primary.ExitCode}."
                : "First app did not create its single-instance socket in time.");
            second = StartDesktopApp(runtime, Path.Combine(root, "primary.sqlite"));
            using var secondTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await second.WaitForExitAsync(secondTimeout.Token);

            Assert.Equal(0, second.ExitCode);
            Assert.False(primary.HasExited, "The second launch must leave the original window process running.");
        }
        finally
        {
            if (second is { HasExited: false }) second.Kill(entireProcessTree: true);
            if (!primary.HasExited) primary.Kill(entireProcessTree: true);
            try { await primary.WaitForExitAsync(); } catch (InvalidOperationException) { }
            if (second is not null)
            {
                try { await second.WaitForExitAsync(); } catch (InvalidOperationException) { }
                second.Dispose();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private static Process StartDesktopApp(string runtimeDirectory, string databasePath)
    {
        var appAssembly = typeof(App).Assembly.Location;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add(appAssembly);
        start.ArgumentList.Add("--db");
        start.ArgumentList.Add(databasePath);
        start.Environment["XDG_RUNTIME_DIR"] = runtimeDirectory;
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(runtimeDirectory, "config");
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the Linux desktop test process.");
    }

    [AvaloniaFact]
    public void Power_chart_renders_headlessly()
    {
        var start = DateTimeOffset.Now.Date;
        var chart = new PowerChart
        {
            Width = 440,
            Height = 160,
            Samples =
            [
                new PowerPoint(start.AddMinutes(1), 4.2),
                new PowerPoint(start.AddMinutes(2), null),
                new PowerPoint(start.AddMinutes(35), 7.1),
                new PowerPoint(start.AddMinutes(36), -1.3)
            ]
        };
        chart.Measure(new Size(440, 160));
        chart.Arrange(new Rect(0, 0, 440, 160));
        using var bitmap = new RenderTargetBitmap(new PixelSize(440, 160));

        bitmap.Render(chart);

        Assert.Equal(440, bitmap.PixelSize.Width);
        Assert.Equal(160, bitmap.PixelSize.Height);
    }

    [AvaloniaFact]
    public async Task Hidden_window_skips_report_work_and_show_refreshes_once()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wattledger-window-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var service = new WattLedgerService(Path.Combine(root, "ledger.sqlite"));
        service.Database.Initialize();
        var trayDetector = new TrayHostDetector();
        var window = new MainWindow(
            service,
            startInTray: false,
            shutdown: () => { },
            monitorTrayHost: false,
            trayHostDetector: trayDetector);

        try
        {
            window.Show();
            await window.ReportRefreshTask;
            Assert.Empty(window.ChartSamples);

            var originalSamples = window.ChartSamples;
            window.Hide();
            var sample = new WattLedger.Core.Models.SampleRecord
            {
                Timestamp = DateTimeOffset.Now,
                PowerState = "Battery",
                BatteryPercent = 61,
                DischargeMw = 3200,
                IsDischarging = true
            };
            service.Database.InsertSample(sample);
            window.OnSampleRecorded(service, sample);
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.Same(originalSamples, window.ChartSamples);
            Assert.Empty(window.ChartSamples);

            window.ShowWindow();
            await window.ReportRefreshTask;
            var point = Assert.Single(window.ChartSamples);
            Assert.Equal(sample.Timestamp.ToUnixTimeMilliseconds(), point.Timestamp.ToUnixTimeMilliseconds());
            Assert.Equal(61, point.BatteryPercent);
            Assert.Equal(3.2, point.Watts);

            window.WindowState = WindowState.Minimized;
            var shownSamples = window.ChartSamples;
            var next = sample with { Timestamp = sample.Timestamp.AddMilliseconds(1), BatteryPercent = 60 };
            service.Database.InsertSample(next);
            window.OnSampleRecorded(service, next);
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.Same(shownSamples, window.ChartSamples);
            window.WindowState = WindowState.Normal;
            await window.ReportRefreshTask;
            Assert.Equal(2, window.ChartSamples.Count);

            window.Hide();
            trayDetector.SetAvailability(true);
            await Dispatcher.UIThread.InvokeAsync(() => { });
            trayDetector.NotifyConnectionLost();
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.True(window.IsVisible);

            window.Hide();
            window.SetStartupError("Could not start sampling: another sampler owns the database.");
            await Dispatcher.UIThread.InvokeAsync(() => { });
            await window.ReportRefreshTask;
            Assert.True(window.IsVisible);
            Assert.Contains(Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<TextBlock>(),
                text => text.Text == "NOT RECORDING");
        }
        finally
        {
            await window.DisposeAsync();
            window.Hide();
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Chart_uses_all_samples_and_accepts_custom_range()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wattledger-chart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var service = new WattLedgerService(Path.Combine(root, "ledger.sqlite"));
        service.Database.Initialize();
        var start = new DateTimeOffset(DateTime.Today);
        // Use a fixed, fully elapsed range, including when the test runs just after midnight.
        var step = Math.Min(5, Math.Max(.001, (DateTimeOffset.Now - start).TotalSeconds / 400));
        for (var i = 0; i < 361; i++)
            service.Database.InsertSample(new WattLedger.Core.Models.SampleRecord
            {
                Timestamp = start.AddSeconds(i * step), PowerState = "Battery", IsDischarging = true,
                DischargeMw = 6000 + (long)(2000 * Math.Sin(i / 12d)), BatteryPercent = 80 - i / 30
            });
        var window = new MainWindow(service, false, () => { }, monitorTrayHost: false);
        try
        {
            window.Show();
            await window.ReportRefreshTask;
            Assert.Equal(361, window.ChartSamples.Count);
            var controls = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).ToArray();
            var from = Assert.Single(controls.OfType<TextBox>(), box => box.Name == "SelectionStart");
            var to = Assert.Single(controls.OfType<TextBox>(), box => box.Name == "SelectionEnd");
            var calculate = Assert.Single(controls.OfType<Button>(), button => Equals(button.Content, "Calculate"));
            from.Text = "00:00:00";
            to.Text = "00:15:00";
            calculate.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var summary = Assert.Single(controls.OfType<TextBlock>(), text => text.Name == "SelectionSummary");
            Assert.Contains("Coverage", summary.Text);
            Assert.Contains("used", summary.Text);
            from.Text = "not a time";
            calculate.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("Enter two times", summary.Text);

            from.Text = "00:00:00";
            calculate.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            if (Environment.GetEnvironmentVariable("WATTLEDGER_PREVIEW_PATH") is { Length: > 0 } path)
            {
                window.Height = 1050;
                Dispatcher.UIThread.RunJobs();
                using var bitmap = window.CaptureRenderedFrame();
                Assert.NotNull(bitmap);
                bitmap.Save(path);
            }
        }
        finally
        {
            await window.DisposeAsync();
            window.Hide();
            Directory.Delete(root, recursive: true);
        }
    }

    private static IReadOnlyList<string> DecodeExecValue(string value)
    {
        var desktopDecoded = new System.Text.StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\')
            {
                Assert.True(index + 1 < value.Length);
                Assert.Equal('\\', value[index + 1]);
                desktopDecoded.Append('\\');
                index++;
            }
            else
            {
                desktopDecoded.Append(value[index]);
            }
        }

        var exec = desktopDecoded.ToString();
        var arguments = new List<string>();
        for (var index = 0; index < exec.Length;)
        {
            Assert.Equal('"', exec[index++]);
            var argument = new System.Text.StringBuilder();
            while (index < exec.Length && exec[index] != '"')
            {
                if (exec[index] == '\\')
                {
                    Assert.True(index + 1 < exec.Length);
                    argument.Append(exec[index + 1]);
                    index += 2;
                }
                else if (exec[index] == '%' && index + 1 < exec.Length && exec[index + 1] == '%')
                {
                    argument.Append('%');
                    index += 2;
                }
                else
                {
                    argument.Append(exec[index++]);
                }
            }

            Assert.True(index < exec.Length);
            index++;
            arguments.Add(argument.ToString());
            if (index < exec.Length)
            {
                Assert.Equal(' ', exec[index++]);
            }
        }

        return arguments;
    }
}
