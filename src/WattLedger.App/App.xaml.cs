using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using WattLedger.Core.Engine;
using WattLedger.Core.Models;

namespace WattLedger.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private WattLedgerService? _service;
    private MainWindow? _window;
    private TaskbarIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, @"Local\WattLedger.SingleInstance", out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            MessageBox.Show(
                "WattLedger is already running. Look for it in the notification area.",
                "WattLedger",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _service = new WattLedgerService(GetOption(e.Args, "--db"));
        _service.Initialize();
        _service.StatusUpdated += OnStatusUpdated;

        _window = new MainWindow(_service);
        MainWindow = _window;
        _tray = CreateTrayIcon();

        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            _window.Show();
        }

        _ = _service.StartAsync();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public void ShowMainWindow()
    {
        _window?.ShowFromTray();
    }

    public void ExitApplication()
    {
        _window?.AllowClose();
        _window?.Close();
        Shutdown();
    }

    private TaskbarIcon CreateTrayIcon()
    {
        var tray = new TaskbarIcon
        {
            Icon = TrayIconFactory.Create(),
            ToolTipText = "WattLedger",
            Visibility = Visibility.Visible
        };

        tray.TrayMouseDoubleClick += (_, _) => ShowMainWindow();

        var menu = new ContextMenu();
        var openItem = new MenuItem { Header = "Open WattLedger" };
        openItem.Click += (_, _) => ShowMainWindow();
        var hideItem = new MenuItem { Header = "Hide window" };
        hideItem.Click += (_, _) => _window?.HideToTray();
        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => ExitApplication();

        menu.Items.Add(openItem);
        menu.Items.Add(hideItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        tray.ContextMenu = menu;

        return tray;
    }

    private void OnStatusUpdated(object? sender, LedgerStatus status)
    {
        _tray?.Dispatcher.BeginInvoke(() =>
        {
            if (_tray is null)
            {
                return;
            }

            var battery = status.Battery;
            _tray.ToolTipText = battery switch
            {
                { IsDischarging: true, DischargeWatts: { } watts } => $"-{watts:0.0} W · {battery.Percent}% · WattLedger",
                { IsCharging: true, ChargeWatts: { } watts } => $"+{watts:0.0} W · {battery.Percent}% · WattLedger",
                _ => $"AC · {battery.Percent}% · WattLedger"
            };
        });
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                _service?.NotifySuspend();
                break;
            case PowerModes.Resume:
                _service?.NotifyResume();
                break;
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }

        return null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _service?.Dispose();
        _tray?.Dispose();

        if (_ownsMutex)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch
            {
                // Ignore shutdown races.
            }
        }

        _mutex?.Dispose();
        base.OnExit(e);
    }
}
