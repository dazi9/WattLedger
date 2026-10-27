using WattLedger.Desktop;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WattLedger.Core.Engine;

namespace WattLedger.Linux;

public sealed partial class App : Application
{
    private WattLedgerService? _service;
    private SingleInstance? _singleInstance;
    private LogindMonitor? _logind;
    private MainWindow? _window;
    private CancellationTokenSource? _lifetime;
    private Task? _startupTask;
    private bool _shutdownPrepared;
    private bool _shutdownInProgress;

    public override void Initialize()
    {
        AppText.Initialize();
        AvaloniaXamlLoader.Load(this);
        InitializeTheme();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var options = AppOptions.Parse(desktop.Args ?? Array.Empty<string>());
            _singleInstance = SingleInstance.TryStart(ShowExistingInstance);
            if (_singleInstance is null)
            {
                base.OnFrameworkInitializationCompleted();
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => desktop.Shutdown(), Avalonia.Threading.DispatcherPriority.Normal);
                return;
            }

            _service = new WattLedgerService(options.DatabasePath);
            _window = new MainWindow(_service, options.StartInTray, Shutdown);
            desktop.MainWindow = _window;
            desktop.ShutdownRequested += OnShutdownRequested;
            desktop.Exit += OnExit;

            _logind = new LogindMonitor(_service);
            _logind.StatusChanged += (_, status) => _window.SetPlatformStatus(status);
            _lifetime = new CancellationTokenSource();
            _startupTask = StartServicesAsync(_lifetime.Token);
            _window.Show();
            _window.Activate();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ShowExistingInstance()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            {
                if (window is MainWindow mainWindow) mainWindow.ShowWindow();
                else
                {
                    window.Show();
                    window.WindowState = Avalonia.Controls.WindowState.Normal;
                    window.Activate();
                }
            }
        });
    }

    private void Shutdown() =>
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

    private async Task StartServicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _service!.StartAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            _window?.SetStartupError(ex.Message);
        }

        if (cancellationToken.IsCancellationRequested) return;
        try
        {
            await _logind!.StartAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _window?.SetPlatformError("Suspend/resume notifications unavailable; sleep gaps will be inferred. {0}", ex.Message);
        }
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs args)
    {
        if (_shutdownPrepared) return;
        args.Cancel = true;
        if (_shutdownInProgress) return;
        _shutdownInProgress = true;
        _lifetime?.Cancel();
        _ = PrepareShutdownAsync();
    }

    private async Task PrepareShutdownAsync()
    {
        if (_startupTask is not null)
        {
            try { await _startupTask; }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError("WattLedger startup cleanup issue: {0}", ex); }
        }

        if (_window is not null)
        {
            try { await _window.DisposeAsync(); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError("WattLedger window cleanup issue: {0}", ex); }
        }

        if (_logind is not null)
        {
            try { await _logind.DisposeAsync(); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError("WattLedger logind cleanup issue: {0}", ex); }
        }

        try
        {
            if (_service is not null) await _service.StopAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("WattLedger sampler stop issue: {0}", ex);
        }

        try { _service?.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("WattLedger service cleanup issue: {0}", ex); }
        try { _singleInstance?.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("WattLedger instance cleanup issue: {0}", ex); }
        try { _lifetime?.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("WattLedger lifetime cleanup issue: {0}", ex); }
        _shutdownPrepared = true;
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs args)
    {
        if (_shutdownPrepared) return;
        _lifetime?.Cancel();
        _logind?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _service?.Dispose();
        _singleInstance?.Dispose();
        _lifetime?.Dispose();
    }
}

internal sealed record AppOptions(string? DatabasePath, bool StartInTray)
{
    public static AppOptions Parse(string[] args)
    {
        string? databasePath = null;
        var startInTray = false;

        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--tray")
            {
                startInTray = true;
            }
            else if (args[index] == "--db" && index + 1 < args.Length)
            {
                databasePath = args[++index];
            }
            else if (args[index].StartsWith("--db=", StringComparison.Ordinal))
            {
                databasePath = args[index][5..];
            }
        }

        if (!string.IsNullOrWhiteSpace(databasePath))
        {
            databasePath = Path.GetFullPath(databasePath);
        }

        return new AppOptions(databasePath, startInTray);
    }
}
