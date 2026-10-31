using WattLedger.Desktop;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WattLedger.Core.Engine;
using WattLedger.Core.Models;
using WattLedger.Core.Reporting;
using WattLedger.Core.Time;

namespace WattLedger.Linux;

internal sealed class MainWindow : Window
{
    private readonly WattLedgerService _service;
    private readonly bool _startInTray;
    private readonly bool _monitorTrayHost;
    private readonly Action _shutdown;
    private readonly ReportRefreshGate _reportRefresh = new();
    private readonly DashboardDataLoader _dataLoader;
    private readonly TextBlock _power = ValueText("—", 34, "GreenBrush");
    private readonly TextBlock _powerDetail = DetailText("Waiting for the first sample");
    private readonly TextBlock _battery = ValueText("—", 30, "TextBrush");
    private readonly TextBlock _batteryDetail = DetailText("Battery state unavailable");
    private readonly TextBlock _remaining = ValueText("—", 30, "BlueBrush");
    private readonly TextBlock _remainingDetail = DetailText("Estimated from recent samples");
    private readonly TextBlock _today = ValueText("0.0 Wh", 30, "GreenBrush");
    private readonly TextBlock _todayDetail = DetailText("No recorded battery use yet");
    private readonly TextBlock _status = DetailText("Starting local sampler…");
    private readonly TextBlock _platformStatus = DetailText("");
    private readonly TextBlock _powerState = DetailText("STARTING");
    private readonly Border _powerStateDot = new()
    {
        Width = 8,
        Height = 8,
        CornerRadius = new CornerRadius(4),
        Background = Brush("BlueBrush"),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _sampleCount = DetailText("No samples yet");
    private readonly StackPanel _ledgerRows = new() { Spacing = 0 };
    private readonly StackPanel _appsRows = new() { Spacing = 0 };
    private readonly PowerChart _chart = new();
    private ComboBox? _chartModeSelector;
    private ComboBox? _languageSelector;
    private ComboBox? _themeSelector;
    private Button? _followLiveButton;
    private MenuItem? _earlierMenuItem;
    private MenuItem? _laterMenuItem;
    private readonly TextBlock _chartNavigationHint = DetailText("Pan mode: drag left/right to explore today's data.");
    private readonly TextBlock _chartRange = DetailText("Waiting for samples");
    private readonly TextBlock _selectionSummary = DetailText("Choose Select and drag across the chart, or enter two times to calculate a range.");
    private readonly TextBox _selectionStart = new() { PlaceholderText = "HH:mm:ss", Width = 98, FontSize = 11 };
    private readonly TextBox _selectionEnd = new() { PlaceholderText = "HH:mm:ss", Width = 98, FontSize = 11 };
    private readonly MenuItem _startupMenuItem;
    private readonly TrayIcon _trayIcon;
    private readonly DispatcherTimer _reportRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly TrayHostDetector _trayHostDetector;
    private bool _trayHostAvailable;
    private bool _trayRegistered;
    private bool _allowClose;
    private bool _disposed;
    private bool _checkingClose;
    private LedgerStatus? _latestStatus;
    private Task? _disposeTask;
    private Task _reportRefreshTask = Task.CompletedTask;
    private Task _exportTask = Task.CompletedTask;
    private bool _exportInProgress;
    private string? _startupError;
    private DashboardSnapshot? _lastSnapshot;
    private string _platformTemplate = "";
    private object?[] _platformArguments = [];
    private DateTimeOffset _lastReportRefreshUtc;
    private DateTimeOffset _lastRecordedTimestamp;
    internal IReadOnlyList<PowerPoint> ChartSamples => _chart.Samples;
    internal Task ReportRefreshTask => _reportRefreshTask;

    public MainWindow(
        WattLedgerService service,
        bool startInTray,
        Action shutdown,
        bool monitorTrayHost = true,
        TrayHostDetector? trayHostDetector = null)
    {
        _service = service;
        _dataLoader = new DashboardDataLoader(service);
        _startInTray = startInTray;
        _monitorTrayHost = monitorTrayHost;
        _trayHostDetector = trayHostDetector ?? new TrayHostDetector();
        _shutdown = shutdown;
        _startupMenuItem = ActionMenuItem(StartupRegistration.IsEnabled ? "Disable start at login" : "Start at login", ToggleStartup);
        Title = "WattLedger";
        Width = 1120;
        Height = 800;
        MinWidth = 740;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = LedgerIcon.CreateWindowIcon();
        Content = CreateContent();
        _trayIcon = CreateTrayIcon();

        _service.StatusUpdated += OnStatusUpdated;
        _service.SampleRecorded += OnSampleRecorded;
        _service.Error += OnError;
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
        _reportRefreshTimer.Tick += OnReportRefresh;
        _trayHostDetector.AvailabilityChanged += OnTrayHostAvailabilityChanged;
        AppText.LanguageChanged += OnLanguageChanged;
        if (Application.Current is App app) app.ThemeChanged += OnThemeChanged;
        _chart.SelectionChanged += (_, _) => UpdateSelectionSummary();
        _chart.ViewChanged += (_, _) => UpdateChartRange();
        PropertyChanged += (_, args) =>
        {
            if (args.Property != WindowStateProperty && args.Property != IsVisibleProperty) return;
            if (!CanRefresh)
            {
                _reportRefresh.SetVisible(false);
                _reportRefreshTimer.Stop();
            }
            else if (_reportRefresh.SetVisible(true)) RefreshReportNow();
        };
    }

    private bool CanRefresh => IsVisible && WindowState != WindowState.Minimized && !_disposed;

    public void SetStartupError(string message) => Post(() =>
    {
        _startupError = message;
        _status.Text = AppText.Format("Could not start sampling: {0}", message);
        _powerState.Text = AppText.Get("NOT RECORDING");
        _powerState.Foreground = Brush("OrangeBrush");
        _powerStateDot.Background = Brush("OrangeBrush");
        _trayIcon.ToolTipText = AppText.Get("WattLedger · not recording");
        ShowWindow();
    });

    public void SetPlatformStatus(string message)
    {
        const string suspendError = "Could not update suspend state: ";
        if (message.StartsWith(suspendError, StringComparison.Ordinal))
            SetPlatformError("Could not update suspend state: {0}", message[suspendError.Length..]);
        else Post(() => SetPlatformMessage(message));
    }

    public void SetPlatformError(string template, params object?[] arguments) =>
        Post(() => SetPlatformMessage(template, arguments));

    private void SetPlatformMessage(string template, params object?[] arguments)
    {
        _platformTemplate = template;
        _platformArguments = arguments;
        _platformStatus.Text = AppText.Format(template, arguments);
    }

    private void OnThemeChanged(object? sender, EventArgs args) => Post(() =>
    {
        if (Application.Current is App app && _themeSelector is not null && !Equals(_themeSelector.SelectedItem, app.CurrentThemeMode))
            _themeSelector.SelectedItem = app.CurrentThemeMode;
    });

    private void OnLanguageChanged(object? sender, EventArgs args) => Post(() =>
    {
        _trayIcon.Menu = CreateTrayMenu();
        if (_languageSelector is not null && !Equals(_languageSelector.SelectedItem, AppText.CurrentLanguage))
            _languageSelector.SelectedItem = AppText.CurrentLanguage;
        _startupMenuItem.Header = AppText.Get(StartupRegistration.IsEnabled ? "Disable start at login" : "Start at login");
        _platformStatus.Text = AppText.Format(_platformTemplate, _platformArguments);
        if (_lastSnapshot is not null) ApplyReport(_lastSnapshot, updateSamples: false);
        if (_startupError is not null)
        {
            _status.Text = AppText.Format("Could not start sampling: {0}", _startupError);
            _powerState.Text = AppText.Get("NOT RECORDING");
            _trayIcon.ToolTipText = AppText.Get("WattLedger · not recording");
        }
        else if (_latestStatus is not null) ApplyStatus(_latestStatus);
        _chart.RefreshLanguage();
        UpdateChartRange();
        UpdateSelectionSummary();
    });

    private Control CreateContent()
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Spacing = 18,
                Margin = new Thickness(26, 22, 26, 20),
                Children =
                {
                    CreateHeader(),
                    CreateMetricGrid(),
                    CreateChartPanel(),
                    CreateLedgerPanel(),
                    CreateAppsPanel(),
                    CreateFooter()
                }
            }
        };
        return scroll;
    }

    private Control CreateHeader()
    {
        var title = new TextBlock
        {
            [!TextBlock.TextProperty] = Localized("WattLedger"),
            FontSize = 25,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextBrush")
        };
        var sub = new TextBlock
        {
            [!TextBlock.TextProperty] = Localized("Local-first battery energy journal"),
            FontSize = 12,
            Foreground = Brush("MutedBrush"),
            Margin = new Thickness(0, 3, 0, 0)
        };
        var titleStack = new StackPanel { Children = { title, sub } };
        _powerState.FontSize = 11;
        _powerState.FontWeight = FontWeight.SemiBold;
        _powerState.Foreground = Brush("BlueBrush");
        _powerState.HorizontalAlignment = HorizontalAlignment.Right;
        _powerState.VerticalAlignment = VerticalAlignment.Center;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(titleStack);
        var stateContents = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Children = { _powerStateDot, _powerState }
        };
        var statePill = new Border
        {
            Background = Brush("CardBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(13, 7),
            Child = stateContents,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(statePill, 1);
        header.Children.Add(statePill);
        return header;
    }

    private Control CreateMetricGrid()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
            ColumnSpacing = 12
        };
        AddMetric(grid, 0, "LIVE POWER", _power, _powerDetail);
        AddMetric(grid, 1, "BATTERY", _battery, _batteryDetail);
        AddMetric(grid, 2, "TIME LEFT", _remaining, _remainingDetail);
        AddMetric(grid, 3, "TODAY", _today, _todayDetail);
        return grid;
    }

    private static void AddMetric(Grid grid, int column, string label, TextBlock value, TextBlock detail)
    {
        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(new TextBlock
        {
            [!TextBlock.TextProperty] = Localized(label),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("MutedBrush")
        });
        stack.Children.Add(value);
        stack.Children.Add(detail);
        var card = new Border
        {
            Background = Brush("CardBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15),
            MinHeight = 128,
            Padding = new Thickness(15, 13),
            Child = stack
        };
        Grid.SetColumn(card, column);
        grid.Children.Add(card);
    }

    private Control CreateChartPanel()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var heading = PanelHeading("Power & battery · today");
        heading.FontSize = 15;
        heading.Foreground = Brush("TextBrush");
        header.Children.Add(heading);
        var legend = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 18 };
        legend.Children.Add(LegendItem("Power (+ discharge / − charge)", "GreenBrush"));
        legend.Children.Add(LegendItem("Battery", "BlueBrush"));
        Grid.SetColumn(legend, 1);
        header.Children.Add(legend);
        var tools = new WrapPanel { Name = "ChartToolbar" };
        _chartModeSelector = new ComboBox
        {
            Name = "ChartModeSelector",
            ItemsSource = Enum.GetValues<ChartInteractionMode>(),
            ItemTemplate = new FuncDataTemplate<ChartInteractionMode>((mode, _) => DetailText(mode.ToString())),
            SelectedItem = _chart.InteractionMode,
            MinWidth = 110,
            Margin = new Thickness(0, 0, 8, 8)
        };
        _chartModeSelector.Bind(Avalonia.Automation.AutomationProperties.NameProperty, Localized("Chart interaction mode"));
        _chartModeSelector.SelectionChanged += (_, _) =>
        {
            if (_chartModeSelector.SelectedItem is ChartInteractionMode mode && mode != _chart.InteractionMode)
                _chart.InteractionMode = mode;
        };
        _chartModeSelector.Bind(ToolTip.TipProperty, new Binding(nameof(TextBlock.Text)) { Source = _chartNavigationHint });
        _earlierMenuItem = ActionMenuItem("← Earlier", () => _chart.Pan(-_chart.VisibleRange.Seconds * .2));
        _laterMenuItem = ActionMenuItem("Later →", () => _chart.Pan(_chart.VisibleRange.Seconds * .2));
        _followLiveButton = ActionButton("Following live ✓", _chart.FollowLive);
        var zoomOut = ActionButton("−", () => _chart.Zoom(2));
        zoomOut.Bind(ToolTip.TipProperty, Localized("Zoom −"));
        zoomOut.Bind(Avalonia.Automation.AutomationProperties.NameProperty, Localized("Zoom −"));
        var zoomIn = ActionButton("+", () => _chart.Zoom(.5));
        zoomIn.Bind(ToolTip.TipProperty, Localized("Zoom +"));
        zoomIn.Bind(Avalonia.Automation.AutomationProperties.NameProperty, Localized("Zoom +"));
        tools.Children.Add(_chartModeSelector);
        tools.Children.Add(zoomOut);
        tools.Children.Add(zoomIn);
        tools.Children.Add(_followLiveButton);
        tools.Children.Add(ActionMenu("More…",
            _earlierMenuItem,
            _laterMenuItem,
            new Separator(),
            ActionMenuItem("Fit today", _chart.ResetView),
            ActionMenuItem("Fit selection", _chart.ZoomToSelection),
            ActionMenuItem("Clear selection", _chart.ClearSelection)));
        var selection = new WrapPanel { Orientation = Orientation.Horizontal };
        selection.Children.Add(new TextBlock { [!TextBlock.TextProperty] = Localized("From"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 8), FontSize = 11 });
        _selectionStart.Name = "SelectionStart";
        _selectionStart.Margin = new Thickness(0, 0, 8, 8);
        _selectionStart.Bind(Avalonia.Automation.AutomationProperties.NameProperty, Localized("Selection start time"));
        selection.Children.Add(_selectionStart);
        selection.Children.Add(new TextBlock { [!TextBlock.TextProperty] = Localized("to"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 8), FontSize = 11 });
        _selectionEnd.Name = "SelectionEnd";
        _selectionEnd.Margin = new Thickness(0, 0, 8, 8);
        _selectionEnd.Bind(Avalonia.Automation.AutomationProperties.NameProperty, Localized("Selection end time"));
        selection.Children.Add(_selectionEnd);
        selection.Children.Add(ActionButton("Calculate", SelectEnteredRange));
        _selectionSummary.Name = "SelectionSummary";
        UpdateChartRange();
        return PanelBorder(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                header,
                tools,
                _chartRange,
                new Border
                {
                    Height = 240,
                    Background = Brush("CardBrush"),
                    CornerRadius = new CornerRadius(9),
                    Child = _chart
                },
                _selectionSummary,
                new Expander
                {
                    Name = "SelectionAnalysis",
                    [!Expander.HeaderProperty] = Localized("Selection analysis"),
                    IsExpanded = false,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            selection,
                            DetailText("Range estimates use recorded power rates; missing readings and sleep gaps are excluded. Ledger totals also use capacity calibration."),
                            _chartNavigationHint,
                            DetailText("Horizontal scroll: pan · Vertical / Ctrl+wheel: zoom · Shift/middle drag: pan · Double-click / Home: fit today · End: follow live")
                        }
                    }
                }
            }
        });
    }

    private void UpdateChartRange()
    {
        var range = _chart.VisibleRange;
        var hasSamples = _chart.Samples.Count > 0;
        var fullRange = !_chart.CanPanEarlier && !_chart.CanPanLater;
        var boundary = fullRange ? AppText.Get("All today's data fits; zoom in to pan") :
            !_chart.CanPanEarlier ? AppText.Get("At the earliest recorded data") : !_chart.CanPanLater ? AppText.Get("At the latest recorded data") : AppText.Get("Earlier and later data available");
        _chartRange.Text = !hasSamples ? AppText.Get("No recorded samples today") :
            AppText.Format("{0} · {1:HH:mm:ss} – {2:HH:mm:ss} · {3:N0} points · {4}", AppText.Get(_chart.IsFollowingLive ? "Following live" : "View paused"), range.Start.ToLocalTime(), range.End.ToLocalTime(), _chart.Samples.Count, boundary);
        _chartNavigationHint.Text = _chart.InteractionMode == ChartInteractionMode.Pan
            ? AppText.Get("Pan mode: drag left/right to move through today's recorded data. Choose Select to measure a range.")
            : AppText.Get("Select mode: drag between two recorded points to calculate a range. Choose Pan to move the view.");
        if (_chartModeSelector is not null && !Equals(_chartModeSelector.SelectedItem, _chart.InteractionMode))
            _chartModeSelector.SelectedItem = _chart.InteractionMode;
        if (_earlierMenuItem is not null) _earlierMenuItem.IsEnabled = hasSamples && _chart.CanPanEarlier;
        if (_laterMenuItem is not null) _laterMenuItem.IsEnabled = hasSamples && _chart.CanPanLater;
        if (_followLiveButton is not null)
        {
            _followLiveButton.Content = _chart.IsFollowingLive ? AppText.Get("Following live ✓") : AppText.Get("Follow live");
            _followLiveButton.IsEnabled = hasSamples && !_chart.IsFollowingLive;
        }
    }

    private void UpdateSelectionSummary()
    {
        var stats = _chart.Selection;
        if (stats is null)
        {
            _selectionStart.Text = _selectionEnd.Text = AppText.Get("");
            _selectionSummary.Text = AppText.Get("Choose Select and drag across the chart, or enter two times to calculate a range.");
            return;
        }
        if (!_selectionStart.IsFocused && !_selectionEnd.IsFocused)
        {
            _selectionStart.Text = stats.Start.ToLocalTime().ToString("HH:mm:ss");
            _selectionEnd.Text = stats.End.ToLocalTime().ToString("HH:mm:ss");
        }
        var energy = stats.CoveredSeconds > 0
            ? AppText.Format("net {0:+0.000;-0.000;0.000} Wh · used {1:0.000} Wh · charged {2:0.000} Wh · avg {3:0.00} W", stats.NetEnergyWh, stats.DischargeEnergyWh, stats.ChargeEnergyWh, stats.AverageWatts)
            : AppText.Get("No continuous power readings in this range");
        _selectionSummary.Text = AppText.Format("{0:N0} points · {1} · {2}\nCoverage {3:P1} ({4})", stats.SampleCount, ReportBuilder.FormatDuration(stats.DurationSeconds), energy, stats.Coverage, ReportBuilder.FormatDuration(stats.CoveredSeconds)) +
                                 (stats.Coverage < 1 ? AppText.Get(" · partial") : "") +
                                 (stats.MinimumWatts is { } min && stats.MaximumWatts is { } max ? AppText.Format(" · min {0:0.00} / max {1:0.00} W", min, max) : "");
    }

    private void SelectEnteredRange()
    {
        string[] formats = ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss"];
        if (!TimeOnly.TryParseExact(_selectionStart.Text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !TimeOnly.TryParseExact(_selectionEnd.Text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) || end <= start)
        {
            _selectionSummary.Text = AppText.Get("Enter two times as HH:mm or HH:mm:ss, with the end after the start. Both times must stay within the same day; ranges crossing midnight are not supported.");
            return;
        }
        var date = _chart.Samples.Count > 0 ? DateOnly.FromDateTime(_chart.Samples[0].Timestamp.LocalDateTime) : DateOnly.FromDateTime(DateTime.Today);
        var from = new DateTimeOffset(date.ToDateTime(start, DateTimeKind.Local));
        var to = new DateTimeOffset(date.ToDateTime(end, DateTimeKind.Local));
        if (_chart.Samples.Count == 0 || to < _chart.Samples[0].Timestamp || from > _chart.Samples[^1].Timestamp)
        {
            _selectionSummary.Text = AppText.Get("Choose a range that overlaps today's recorded samples.");
            return;
        }
        _chart.SelectRange(from, to);
    }

    private static Control LegendItem(string label, string color)
    {
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(new Border
        {
            Width = 16,
            Height = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brush(color)
        });
        row.Children.Add(new TextBlock { [!TextBlock.TextProperty] = Localized(label), FontSize = 11, Foreground = Brush("MutedBrush") });
        return row;
    }

    private static Border PanelBorder(Control child) => new()
    {
        Background = Brush("CardBrush"),
        BorderBrush = Brush("BorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(15),
        Padding = new Thickness(18, 16),
        Child = child
    };

    private Control CreateLedgerPanel()
    {
        var panel = new StackPanel { Spacing = 10 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(PanelHeading("TODAY’S LEDGER"));
        _sampleCount.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_sampleCount, 1);
        header.Children.Add(_sampleCount);
        panel.Children.Add(header);

        var ledgerContent = new StackPanel { MinWidth = 650 };
        ledgerContent.Children.Add(LedgerHeader());
        ledgerContent.Children.Add(_ledgerRows);
        var ledger = new ScrollViewer
        {
            MaxHeight = 230,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = ledgerContent
        };
        panel.Children.Add(ledger);
        return PanelBorder(panel);
    }

    private Control CreateAppsPanel()
    {
        var panel = new StackPanel { Spacing = 10 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var heading = PanelHeading("Apps today");
        heading.FontSize = 15;
        heading.Foreground = Brush("TextBrush");
        header.Children.Add(heading);
        var note = DetailText("Resource usage; energy values are model estimates.");
        note.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(note, 1);
        header.Children.Add(note);
        panel.Children.Add(header);

        var content = new StackPanel { MinWidth = 650 };
        content.Children.Add(AppsHeader());
        content.Children.Add(_appsRows);
        panel.Children.Add(content);
        return PanelBorder(panel);
    }

    private static Control AppsHeader()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("2*,1*,1.4*"),
            Margin = new Thickness(12, 0, 12, 5)
        };
        string[] headings = ["APP", "CPU time", "Est. energy"];
        for (var index = 0; index < headings.Length; index++)
        {
            var label = new TextBlock { [!TextBlock.TextProperty] = Localized(headings[index]), FontSize = 9, Foreground = Brush("MutedBrush") };
            Grid.SetColumn(label, index);
            grid.Children.Add(label);
        }
        return grid;
    }

    private Control CreateFooter()
    {
        var bar = new WrapPanel();
        bar.Children.Add(ActionMenu("Application",
            ActionMenuItem("Export CSV", () => ExportAsync("csv")),
            ActionMenuItem("Export Markdown", () => ExportAsync("md")),
            ActionMenuItem("Export JSON", () => ExportAsync("json")),
            new Separator(),
            ActionMenuItem("Open data folder", OpenDataFolder),
            _startupMenuItem,
            new Separator(),
            ActionMenuItem("Hide to tray", HideToTrayAsync),
            ActionMenuItem("Quit", Quit)));
        bar.Children.Add(CreateThemeSelector());
        bar.Children.Add(CreateLanguageSelector());
        var footer = new StackPanel { Spacing = 9 };
        footer.Children.Add(bar);
        footer.Children.Add(_status);
        footer.Children.Add(_platformStatus);
        return footer;
    }

    private Control CreateThemeSelector()
    {
        var selector = _themeSelector = new ComboBox
        {
            ItemsSource = Enum.GetValues<ThemeMode>(),
            ItemTemplate = new FuncDataTemplate<ThemeMode>((mode, _) => DetailText(mode.ToString())),
            SelectedItem = (Application.Current as App)?.CurrentThemeMode ?? ThemeMode.System,
            Width = 112,
            Margin = new Thickness(0, 0, 8, 8)
        };
        selector.Bind(Avalonia.Automation.AutomationProperties.NameProperty, Localized("Color theme"));
        selector.Bind(ToolTip.TipProperty, Localized("Color theme: System follows the desktop appearance"));
        selector.SelectionChanged += (_, _) =>
        {
            if (Application.Current is not App app || selector.SelectedItem is not ThemeMode mode || mode == app.CurrentThemeMode) return;
            try { app.SetThemeMode(mode); }
            catch (Exception ex)
            {
                SetPlatformMessage("Could not save theme preference: {0}", ex.Message);
                selector.SelectedItem = app.CurrentThemeMode;
            }
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { [!TextBlock.TextProperty] = Localized("Theme"), Foreground = Brush("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 8) },
                selector
            }
        };
    }

    private Control CreateLanguageSelector()
    {
        var selector = _languageSelector = new ComboBox
        {
            ItemsSource = Enum.GetValues<AppLanguage>(),
            SelectedItem = AppText.CurrentLanguage,
            Width = 132,
            Margin = new Thickness(0, 0, 8, 8),
            ItemTemplate = new FuncDataTemplate<AppLanguage>((language, _) =>
                DetailText(language == AppLanguage.System ? "System" : language == AppLanguage.English ? "English" : "简体中文"))
        };
        selector.Bind(Avalonia.Automation.AutomationProperties.NameProperty, Localized("Language"));
        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedItem is not AppLanguage language || language == AppText.CurrentLanguage) return;
            try { AppText.SetLanguage(language); }
            catch (Exception ex)
            {
                SetPlatformMessage("Could not save language preference: {0}", ex.Message);
                selector.SelectedItem = AppText.CurrentLanguage;
            }
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { [!TextBlock.TextProperty] = Localized("Language"), Foreground = Brush("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 8) },
                selector
            }
        };
    }

    private static Binding Localized(string text) => new(nameof(AppText.Revision))
    {
        Source = AppText.Instance,
        Converter = new FuncValueConverter<int, string>(_ => AppText.Get(text))
    };

    private static TextBlock PanelHeading(string value) => new()
    {
        [!TextBlock.TextProperty] = Localized(value),
        FontSize = 10,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brush("MutedBrush")
    };

    private static MenuItem ActionMenuItem(string text, Action action)
    {
        var item = new MenuItem { [!MenuItem.HeaderProperty] = Localized(text) };
        item.Click += (_, _) => action();
        return item;
    }

    private static Menu ActionMenu(string text, params Control[] items) => new()
    {
        Margin = new Thickness(0, 0, 8, 8),
        Background = Brush("ButtonBrush"),
        Foreground = Brush("TextBrush"),
        ItemsSource = new[]
        {
            new MenuItem
            {
                [!MenuItem.HeaderProperty] = Localized(text),
                Padding = new Thickness(12, 7),
                FontSize = 11,
                ItemsSource = items
            }
        }
    };

    private static Button ActionButton(string text, Action action)
    {
        var button = new Button
        {
            [!Button.ContentProperty] = Localized(text),
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(12, 7),
            FontSize = 11,
            Background = Brush("ButtonBrush"),
            Foreground = Brush("TextBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private TrayIcon CreateTrayIcon()
    {
        var tray = new TrayIcon
        {
            Icon = LedgerIcon.CreateWindowIcon(),
            ToolTipText = "WattLedger",
            IsVisible = true,
            Menu = CreateTrayMenu()
        };
        tray.Clicked += (_, _) => ShowWindow();
        try
        {
            TrayIcon.SetIcons(Application.Current!, new TrayIcons { tray });
            _trayRegistered = true;
        }
        catch (Exception ex)
        {
            SetPlatformMessage("System tray unavailable: {0}", ex.Message);
        }

        return tray;
    }

    private NativeMenu CreateTrayMenu()
    {
        var menu = new NativeMenu();
        var show = new NativeMenuItem(AppText.Get("Open WattLedger"));
        show.Click += (_, _) => ShowWindow();
        var data = new NativeMenuItem(AppText.Get("Open data folder"));
        data.Click += (_, _) => OpenDataFolder();
        var exit = new NativeMenuItem(AppText.Get("Quit"));
        exit.Click += (_, _) => Quit();
        menu.Add(show);
        menu.Add(data);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(exit);
        return menu;
    }

    private async void OnOpened(object? sender, EventArgs args)
    {
        _reportRefresh.SetVisible(true);
        RefreshReportNow();
        if (!_monitorTrayHost) return;
        try
        {
            await _trayHostDetector.StartAsync();
            _trayHostDetectorStarted = true;
            _trayHostAvailable = _trayRegistered && _trayHostDetector.IsAvailable == true;
            if (_startInTray && _trayHostAvailable && _startupError is null) HideToTray();
        }
        catch (Exception ex)
        {
            _trayHostAvailable = false;
            SetPlatformMessage("Could not confirm a tray host; window stays available. {0}", ex.Message);
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        if (_checkingClose) return;
        _checkingClose = true;
        _ = CheckCloseDestinationAsync();
    }

    private async Task CheckCloseDestinationAsync()
    {
        var available = _trayRegistered && _trayHostDetector.IsAvailable == true;
        if (!_trayHostDetectorStarted)
        {
            try
            {
                await _trayHostDetector.StartAsync();
                _trayHostDetectorStarted = true;
                _trayHostAvailable = _trayRegistered && _trayHostDetector.IsAvailable == true;
                available = _trayHostAvailable;
            }
            catch { available = false; }
        }
        _checkingClose = false;
        if (available)
        {
            HideToTray();
            SetPlatformMessage("WattLedger is still running in the system tray.");
        }
        else
        {
            SetPlatformMessage("No StatusNotifier host is available; closing WattLedger.");
            _allowClose = true;
            _shutdown();
        }
    }

    private void HideToTrayAsync()
    {
        if (_trayRegistered && _trayHostAvailable) HideToTray();
        else SetPlatformMessage("No StatusNotifier host is available; the window stays open.");
    }

    private void HideToTray()
    {
        _reportRefresh.SetVisible(false);
        _reportRefreshTimer.Stop();
        Hide();
        SetPlatformMessage("WattLedger is still running in the system tray.");
    }

    private bool _trayHostDetectorStarted;

    internal void OnTrayHostAvailabilityChanged(object? sender, bool available) => Post(() =>
    {
        available &= _trayRegistered;
        var disappeared = _trayHostAvailable && !available;
        _trayHostAvailable = available;
        if (available && !IsVisible && _startInTray) return;
        if (!available && !IsVisible && disappeared)
        {
            ShowWindow();
            SetPlatformMessage("System tray host disappeared; WattLedger window restored.");
        }
        else if (!available && IsVisible)
        {
            SetPlatformMessage("No StatusNotifier host found. WattLedger stays available in this window.");
        }
    });

    private void OnClosed(object? sender, EventArgs args)
    {
        _ = DisposeAsync();
    }

    public ValueTask DisposeAsync()
    {
        _disposeTask ??= DisposeCoreAsync();
        return new ValueTask(_disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        AppText.LanguageChanged -= OnLanguageChanged;
        if (Application.Current is App app) app.ThemeChanged -= OnThemeChanged;
        _reportRefreshTimer.Stop();
        _reportRefreshTimer.Tick -= OnReportRefresh;
        _trayHostDetector.AvailabilityChanged -= OnTrayHostAvailabilityChanged;
        _service.StatusUpdated -= OnStatusUpdated;
        _service.SampleRecorded -= OnSampleRecorded;
        _service.Error -= OnError;
        await _reportRefreshTask;
        try { await _exportTask; }
        catch (Exception ex) { Trace.TraceError("WattLedger export cleanup issue: {0}", ex); }
        await _trayHostDetector.DisposeAsync();
        _trayIcon.Dispose();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons());
    }

    private void OnStatusUpdated(object? sender, LedgerStatus status) => Post(() =>
    {
        _latestStatus = status;
        if (CanRefresh) ApplyStatus(status);
        else UpdateTrayTooltip(status);
    });

    internal void OnSampleRecorded(object? sender, SampleRecord sample) => Post(() =>
    {
        if (sample.Timestamp < _lastRecordedTimestamp ||
            (_chart.Samples.Count > 0 && sample.Timestamp < _chart.Samples[^1].Timestamp))
            _dataLoader.Invalidate();
        _lastRecordedTimestamp = sample.Timestamp;
        var becameVisible = _reportRefresh.SetVisible(CanRefresh);
        if (!CanRefresh)
        {
            _reportRefreshTimer.Stop();
        }

        if (becameVisible)
        {
            _reportRefresh.MarkDirty();
            RefreshReportNow();
            return;
        }

        if (_reportRefresh.MarkDirty())
        {
            ScheduleReportRefresh();
        }
    });

    private void OnError(object? sender, string message) => Post(() =>
    {
        if (IsVisible) _status.Text = AppText.Format("Sampling error: {0}", message);
        else _trayIcon.ToolTipText = AppText.Get("WattLedger · sampling error");
    });

    private void ApplyStatus(LedgerStatus status)
    {
        var battery = status.Battery;
        var power = battery.IsDischarging ? battery.DischargeWatts : battery.IsCharging ? battery.ChargeWatts : null;
        var estimatedPower = false;
        if (power is null && battery.IsDischarging && status.RecentAverageWatts is > 0)
        {
            power = status.RecentAverageWatts;
            estimatedPower = true;
        }
        else if (power is null && battery.IsCharging && status.RecentAverageWatts is < -0.1)
        {
            power = Math.Abs(status.RecentAverageWatts.Value);
            estimatedPower = true;
        }
        _power.Text = battery.IsDischarging && power is { } discharge
            ? $"+{discharge:0.0} W"
            : battery.IsCharging && power is { } charge
                ? $"−{charge:0.0} W"
                : "—";
        _power.Foreground = Brush(battery.IsCharging ? "BlueBrush" : battery.IsDischarging ? "GreenBrush" : "MutedBrush");
        _powerDetail.Text = status.Rapl.PackageWatts is { } package
            ? $"SoC {package:0.0} W · CPU {status.Rapl.CpuWatts.GetValueOrDefault():0.0} W"
            : power is not null ? estimatedPower ? AppText.Get("Estimated from capacity change") : battery.IsCharging ? AppText.Get("Battery charging") : AppText.Get("Battery discharge") : AppText.Get("Power rate unavailable");
        _battery.Text = battery.Percent is { } percent ? $"{percent}%" : "—";
        var health = battery.HealthPercent is { } h ? AppText.Format(" · {0:0}% health", h) : "";
        if (battery.PercentIsEstimated) health += AppText.Get(" · estimated");
        var capacity = battery.RemainingCapacityMwh is { } remaining && battery.FullChargeCapacityMwh is { } full
            ? $"{remaining / 1000d:0.0} / {full / 1000d:0.0} Wh"
            : battery.DeviceName ?? AppText.Get("Capacity unavailable");
        _batteryDetail.Text = capacity + health;

        if (battery.IsDischarging && status.EstimatedRemainingHours is { } hours)
        {
            _remaining.Text = $"{hours:0.0} h";
            _remainingDetail.Text = status.RecentAverageWatts is { } average ? AppText.Format("recent average {0:0.0} W", average) : AppText.Get("estimated remaining");
        }
        else if (battery.IsCharging && battery.ChargeWatts is > 0.1 && battery.FullChargeCapacityMwh is { } fullCharge && battery.RemainingCapacityMwh is { } remainingCharge)
        {
            _remaining.Text = $"{Math.Max(0, (fullCharge - remainingCharge) / 1000d / battery.ChargeWatts.Value):0.0} h";
            _remainingDetail.Text = AppText.Get("until full");
        }
        else
        {
            _remaining.Text = battery.IsAcOnline ? AppText.Get("AC") : "—";
            _remainingDetail.Text = battery.IsAcOnline ? AppText.Get("plugged in") : AppText.Get("waiting for discharge data");
        }

        _powerState.Text = battery.BatteryCount == 0 ? AppText.Get("NO BATTERY")
            : battery.IsCharging ? AppText.Get("CHARGING")
            : battery.IsDischarging ? AppText.Get("ON BATTERY")
            : battery.IsAcOnline ? AppText.Get("AC POWER") : AppText.Get("STATE UNKNOWN");
        var stateColor = battery.IsCharging || battery.IsAcOnline ? "BlueBrush" : battery.IsDischarging ? "GreenBrush" : "MutedBrush";
        _powerState.Foreground = Brush(stateColor);
        _powerStateDot.Background = Brush(stateColor);
        _status.Text = battery.IsCharging || battery.IsDischarging || battery.IsAcOnline
            ? AppText.Format("Recording locally · last sample {0:HH:mm:ss}", status.Timestamp.ToLocalTime())
            : AppText.Format("State unavailable · session tracking paused · last reading {0:HH:mm:ss}", status.Timestamp.ToLocalTime());
        _trayIcon.ToolTipText = $"WattLedger · {_battery.Text} · {_power.Text}";
    }

    private void OnReportRefresh(object? sender, EventArgs args)
    {
        _reportRefreshTimer.Stop();
        RefreshReportNow();
    }

    private void ScheduleReportRefresh()
    {
        var now = DateTimeOffset.UtcNow;
        var delay = TimeSpan.FromMilliseconds(350);
        if (_lastReportRefreshUtc != default)
        {
            var earliest = _lastReportRefreshUtc.AddSeconds(20) - now;
            if (earliest > delay) delay = earliest;
        }

        _reportRefreshTimer.Stop();
        _reportRefreshTimer.Interval = delay;
        _reportRefreshTimer.Start();
    }

    private void RefreshReportNow()
    {
        _reportRefresh.SetVisible(CanRefresh);
        if (!CanRefresh)
        {
            _reportRefreshTimer.Stop();
            return;
        }

        if (!_reportRefreshTask.IsCompleted || !_reportRefresh.TryConsumeRefresh()) return;
        _lastReportRefreshUtc = DateTimeOffset.UtcNow;
        _reportRefreshTask = LoadReportAsync();
    }

    private async Task LoadReportAsync()
    {
        try
        {
            var snapshot = await Task.Run(() => _dataLoader.Load(DateTimeOffset.Now));
            if (!CanRefresh)
            {
                _reportRefresh.MarkDirty();
                return;
            }
            _lastSnapshot = snapshot;
            ApplyReport(snapshot);
        }
        catch (Exception ex)
        {
            _reportRefresh.MarkDirty();
            if (!_disposed && _startupError is null) _status.Text = AppText.Format("Report unavailable: {0}", ex.Message);
        }
        finally
        {
            if (CanRefresh && _reportRefresh.IsDirty) ScheduleReportRefresh();
        }
    }

    private void ApplyReport(DashboardSnapshot snapshot, bool updateSamples = true)
    {
        var report = snapshot.Report;
        _today.Text = $"{report.DischargeWh:0.0} Wh";
        _todayDetail.Text = AppText.Format("active {0} · standby {1}", ReportBuilder.FormatDuration(report.ActiveSeconds), ReportBuilder.FormatDuration(report.StandbySeconds)) +
                            (report.StandbyEnergyIncomplete ? AppText.Get(" · some sleep-gap energy unavailable") : "");

        if (updateSamples) _chart.Samples = snapshot.Samples;
        UpdateChartRange();
        _sampleCount.Text = AppText.Format("{0:N0} samples", snapshot.Samples.Count);
        _ledgerRows.Children.Clear();
        foreach (var session in report.Sessions.Where(session => session.DurationSeconds > 0)
                     .OrderByDescending(session => session.Start).Take(40))
        {
            _ledgerRows.Children.Add(LedgerRow(session));
        }

        if (report.Sessions.Count == 0)
        {
            _ledgerRows.Children.Add(new TextBlock
            {
                [!TextBlock.TextProperty] = Localized("Ledger entries appear as battery and charging sessions are recorded."),
                Foreground = Brush("MutedBrush"),
                FontSize = 11,
                Margin = new Thickness(12, 10)
            });
        }

        _appsRows.Children.Clear();
        foreach (var app in snapshot.Apps)
        {
            _appsRows.Children.Add(AppsRow(app));
        }

        if (snapshot.Apps.Count == 0)
        {
            _appsRows.Children.Add(new TextBlock
            {
                [!TextBlock.TextProperty] = Localized("No application usage recorded yet."),
                Foreground = Brush("MutedBrush"),
                FontSize = 11,
                Margin = new Thickness(12, 10)
            });
        }
    }

    private static Control AppsRow(AppResourceRow app)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("2*,1*,1.4*"),
            Margin = new Thickness(12, 8, 12, 8)
        };
        var fields = new[]
        {
            app.Name,
            app.Cpu,
            string.IsNullOrEmpty(app.Energy) ? "—" : app.Energy
        };
        for (var index = 0; index < fields.Length; index++)
        {
            var text = new TextBlock { Text = fields[index], FontSize = 10, Foreground = Brush("TextBrush") };
            Grid.SetColumn(text, index);
            grid.Children.Add(text);
        }

        return new Border
        {
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid
        };
    }

    private static Control LedgerHeader()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.15*,1.15*,1.1*,1.1*,1*,1*,.8*"),
            Margin = new Thickness(12, 0, 12, 5)
        };
        string[] headings = ["START", "END", "STATE", "DURATION", "BATTERY", "ENERGY", "AVG W"];
        for (var index = 0; index < headings.Length; index++)
        {
            var label = new TextBlock { [!TextBlock.TextProperty] = Localized(headings[index]), FontSize = 9, Foreground = Brush("MutedBrush") };
            Grid.SetColumn(label, index);
            grid.Children.Add(label);
        }
        return grid;
    }

    private static Control LedgerRow(SessionRecord session)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.15*,1.15*,1.1*,1.1*,1*,1*,.8*"),
            Margin = new Thickness(12, 8, 12, 8)
        };
        var fields = new[]
        {
            session.Start.ToLocalTime().ToString("HH:mm:ss"),
            session.End.ToLocalTime().ToString("HH:mm:ss"),
            SessionStateLabel(session),
            ReportBuilder.FormatDuration(session.DurationSeconds),
            $"{FormatPercent(session.StartPercent)} → {FormatPercent(session.EndPercent)}",
            (session.Type is "SleepGap" or "ModernStandby") &&
            session.Note?.Contains("energy unavailable", StringComparison.OrdinalIgnoreCase) == true
                ? AppText.Get("Unavailable")
                : $"{session.EnergyWh:0.00} Wh",
            FormatAverageWatts(session)
        };
        for (var index = 0; index < fields.Length; index++)
        {
            var text = new TextBlock { Text = fields[index], FontSize = 10, Foreground = Brush("TextBrush") };
            Grid.SetColumn(text, index);
            grid.Children.Add(text);
        }

        return new Border
        {
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid
        };
    }

    private static string SessionStateLabel(SessionRecord session)
    {
        if (session.Type == "ModernStandby")
        {
            return AppText.Get("Modern Standby");
        }

        if (session.Type == "SleepGap")
        {
            var confirmed = session.Note?.Contains("Suspend/resume event confirmed", StringComparison.Ordinal) == true;
            return confirmed ? AppText.Get("Sleep gap (confirmed)") : AppText.Get("Sleep gap (inferred)");
        }

        return AppText.SessionState(session.Type);
    }

    private static string FormatAverageWatts(SessionRecord session)
    {
        var energyUnavailable = (session.Type is "SleepGap" or "ModernStandby") &&
                                session.Note?.Contains("energy unavailable", StringComparison.OrdinalIgnoreCase) == true;
        return energyUnavailable || session.DurationSeconds <= 0
            ? "—"
            : $"{session.AverageWatts:0.0} W";
    }

    private async void ExportAsync(string format)
    {
        if (_exportInProgress || _disposed) return;
        _exportInProgress = true;
        try
        {
            var extension = format == "md" ? "md" : format;
            var type = format switch
            {
                "csv" => new FilePickerFileType("CSV") { Patterns = ["*.csv"] },
                "md" => new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
                _ => new FilePickerFileType("JSON") { Patterns = ["*.json"] }
            };
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = AppText.Format("Export today as {0}", format.ToUpperInvariant()),
                SuggestedFileName = $"wattledger-{DateTime.Today:yyyy-MM-dd}.{extension}",
                FileTypeChoices = [type],
                DefaultExtension = extension
            });
            var path = file?.TryGetLocalPath();
            if (path is null || _disposed) return;

            _status.Text = AppText.Get("Exporting…");
            var date = DateOnly.FromDateTime(DateTime.Today);
            var now = DateTimeOffset.Now;
            _exportTask = Task.Run(() =>
            {
                switch (format)
                {
                    case "csv":
                        var start = LocalDay.Start(date);
                        ReportBuilder.ExportCsv(_service.GetSamples(start, now), path);
                        break;
                    case "md":
                        ReportBuilder.ExportMarkdown(_service.GetReport(date), path);
                        break;
                    default:
                        ReportBuilder.ExportJson(_service.GetReport(date), path);
                        break;
                }
            });
            await _exportTask;

            if (!_disposed) _status.Text = AppText.Format("Exported to {0}", path);
        }
        catch (Exception ex)
        {
            if (!_disposed) _status.Text = AppText.Format("Export failed: {0}", ex.Message);
        }
        finally
        {
            _exportInProgress = false;
        }
    }

    private void OpenDataFolder()
    {
        try
        {
            var directory = Path.GetDirectoryName(_service.DatabasePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("The database folder is not available.");
            }

            Directory.CreateDirectory(directory);
            var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(directory);
            Process.Start(start);
        }
        catch (Exception ex)
        {
            _status.Text = AppText.Format("Could not open data folder: {0}", ex.Message);
        }
    }

    private void ToggleStartup()
    {
        try
        {
            StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled, _service.DatabasePath);
            _startupMenuItem.Header = AppText.Get(StartupRegistration.IsEnabled ? "Disable start at login" : "Start at login");
            _status.Text = AppText.Get(StartupRegistration.IsEnabled ? "WattLedger will start at login." : "Start at login disabled.");
        }
        catch (Exception ex)
        {
            _status.Text = AppText.Format("Could not update start-at-login setting: {0}", ex.Message);
        }
    }

    public void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_reportRefresh.SetVisible(CanRefresh)) RefreshReportNow();
        if (_latestStatus is not null) ApplyStatus(_latestStatus);
    }

    private void UpdateTrayTooltip(LedgerStatus status)
    {
        var battery = status.Battery.Percent is { } percent ? $"{percent}%" : "—";
        var rate = status.Battery.IsDischarging && status.Battery.DischargeWatts is { } discharge
            ? $"+{discharge:0.0} W"
            : status.Battery.IsCharging && status.Battery.ChargeWatts is { } charge
                ? $"−{charge:0.0} W"
                : "—";
        _trayIcon.ToolTipText = $"WattLedger · {battery} · {rate}";
    }

    private void Quit()
    {
        _allowClose = true;
        _shutdown();
    }

    private void Post(Action action)
    {
        if (_disposed) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed) action();
        });
    }

    private static string FormatPercent(int? percent) => percent is null ? "—" : $"{percent}%";

    private static TextBlock ValueText(string value, double size, string color) => new()
    {
        [!TextBlock.TextProperty] = Localized(value),
        FontSize = size,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brush(color)
    };

    private static TextBlock DetailText(string value) => new()
    {
        [!TextBlock.TextProperty] = Localized(value),
        FontSize = 10,
        Foreground = Brush("MutedBrush"),
        TextWrapping = TextWrapping.Wrap
    };

    private static SolidColorBrush Brush(string key) => ThemeResources.Brush(key);
}
