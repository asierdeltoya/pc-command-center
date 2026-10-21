using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using PCCommandCenter.AI;
using PCCommandCenter.App.Services;
using PCCommandCenter.App.ViewModels;
using PCCommandCenter.App.Views;
using PCCommandCenter.Core;
using PCCommandCenter.Diagnostics;
using PCCommandCenter.Diagnostics.Logs;
using PCCommandCenter.Hardware;
using PCCommandCenter.Network;
using PCCommandCenter.Processes;
using PCCommandCenter.Settings;
using PCCommandCenter.Storage;
using PCCommandCenter.WindowsTools;

namespace PCCommandCenter.App;

public partial class App : Application
{
    private const string MutexName = @"Local\PCCommandCenter.SingleInstance";
    private const string ActivateEventName = @"Local\PCCommandCenter.Activate";
    private const string ShutdownEventName = @"Local\PCCommandCenter.Shutdown";
    private EventWaitHandle? _shutdownEvent;
    private bool _primary;

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private ServiceProvider? _services;
    private MainWindow? _window;
    private TrayService? _tray;
    private TrayFlyout? _flyout;
    private bool _exiting;
    private int _unhandledCount;

    public static IServiceProvider Services => ((App)Current)._services!;

    public bool IsExiting => _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterGlobalExceptionHandlers();

        bool elevatedRestart = e.Args.Contains("--elevated-restart");
        _instanceMutex = new Mutex(true, MutexName, out bool createdNew);

        // "--shutdown" asks a running instance to exit cleanly (used by the uninstaller and scripts).
        if (e.Args.Contains("--shutdown"))
        {
            if (!createdNew)
            {
                try
                {
                    EventWaitHandle.OpenExisting(ShutdownEventName).Set();
                    _instanceMutex.WaitOne(TimeSpan.FromSeconds(10));
                }
                catch (AbandonedMutexException) { }
                catch { }
            }
            Shutdown();
            return;
        }

        if (!createdNew)
        {
            // A previous instance is closing (elevation restart) — wait for it; otherwise activate it and exit.
            bool acquired = false;
            if (elevatedRestart)
            {
                try { acquired = _instanceMutex.WaitOne(TimeSpan.FromSeconds(15)); }
                catch (AbandonedMutexException) { acquired = true; }
            }
            if (!acquired)
            {
                try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); } catch { }
                Shutdown();
                return;
            }
        }

        _primary = true;
        // Windows sign-out, shutdown or the installer's Restart Manager: exit instead of hiding to the tray.
        SessionEnding += (_, _) => ExitApplication();
        AppLog.Info($"──── {AppInfo.Name} {AppInfo.Version} starting (elevated={AppInfo.IsElevated}, args='{string.Join(' ', e.Args)}') ────");
        var sw = Stopwatch.StartNew();

        // WPF formats bound dates/numbers with en-US unless told otherwise; use the user's regional settings.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag)));

        _services = ConfigureServices();
        var settings = _services.GetRequiredService<ISettingsService>();
        var cfg = settings.Current;

        Loc.SetLanguage(cfg.Language);
        Fmt.TemperatureUnit = cfg.TemperatureUnit;
        Fmt.NetworkInBits = cfg.NetworkInBits;
        var theme = _services.GetRequiredService<ThemeService>();
        theme.Apply(cfg.Theme, cfg.Accent);

        var connectivity = _services.GetRequiredService<IConnectivityMonitor>();
        connectivity.Target = cfg.LatencyTarget;
        connectivity.Interval = TimeSpan.FromSeconds(cfg.LatencyIntervalSeconds);
        connectivity.Start();
        var hardware = _services.GetRequiredService<IHardwareMonitor>();
        hardware.Interval = TimeSpan.FromSeconds(cfg.RefreshIntervalSeconds);
        hardware.Start();
        _services.GetRequiredService<AlertMonitor>();
        _services.GetRequiredService<IMetricsHistory>(); // starts recording one-minute history

        var main = _services.GetRequiredService<MainViewModel>();
        Loc.Instance.PropertyChanged += (_, _) => main.RefreshTitles();

        _window = new MainWindow(main, settings, theme);
        _window.ExitRequested += ExitApplication;
        SetupTray(hardware, settings);
        StartActivationListener();

        bool startHidden = e.Args.Contains(AutoStart.TrayArgument) || (cfg.StartMinimized && cfg.FirstRunCompleted);
        main.Start();
        if (startHidden && cfg.MinimizeToTray)
        {
            main.OnWindowVisibilityChanged(false);
            AppLog.Info("Started hidden in the notification area");
        }
        else
        {
            _window.Show();
        }
        AppLog.Info($"Startup completed in {sw.ElapsedMilliseconds} ms (render tier {System.Windows.Media.RenderCapability.Tier >> 16})");

        var qa = Array.IndexOf(e.Args, "--qa-capture");
        if (qa >= 0 && qa + 1 < e.Args.Length) _ = RunQaAsync(e.Args[qa + 1], e.Args.Skip(qa + 2).ToList(), main);
    }

    private async Task RunQaAsync(string folder, IReadOnlyList<string> extra, MainViewModel main)
    {
        if (_window == null) return;
        try
        {
            _window.Show();
            Directory.CreateDirectory(folder);
            await Task.Delay(1500);
            if (main.Welcome != WelcomeStage.None)
            {
                QaCapture.Save((FrameworkElement)_window.Content, Path.Combine(folder, "00-Welcome.png"));
                main.SkipWelcomeCommand.Execute(null);
            }
            await QaCapture.RunAsync(_window, (FrameworkElement)_window.Content, _services!.GetRequiredService<NavigationService>(), main, folder, extra);
        }
        catch (Exception ex) { AppLog.Error("QA run failed", ex); }
        if (!extra.Contains("stay")) ExitApplication();
    }

    private static ServiceProvider ConfigureServices()
    {
        var sc = new ServiceCollection();
        // Settings
        sc.AddSingleton<ISettingsService, JsonSettingsService>();
        sc.AddSingleton<ISecretStore, DpapiSecretStore>();
        // System modules
        sc.AddSingleton<IConnectivityMonitor, ConnectivityMonitor>();
        sc.AddSingleton<IHardwareMonitor>(sp => new HardwareMonitor(sp.GetRequiredService<IConnectivityMonitor>()));
        sc.AddSingleton<INetworkTools, NetworkTools>();
        sc.AddSingleton<IProcessService>(sp => new ProcessService(sp.GetRequiredService<INetworkTools>()));
        sc.AddSingleton<IStorageAnalyzer, StorageAnalyzer>();
        sc.AddSingleton<IWindowsToolsService, WindowsToolsService>();
        sc.AddSingleton<IStartupService, StartupService>();
        sc.AddSingleton<IShellService, ShellService>();
        sc.AddSingleton<IHealthService, HealthService>();
        sc.AddSingleton<ILogAnalyzer, LogAnalyzer>();
        sc.AddSingleton<IAiAssistant, AnthropicAssistant>();
        sc.AddSingleton<IQuickFixService, QuickFixService>();
        sc.AddSingleton<ISecurityService, SecurityService>();
        sc.AddSingleton<IInstalledAppsService, InstalledAppsService>();
        sc.AddSingleton<IPowerService, PowerService>();
        sc.AddSingleton<IInternetSpeedTest, InternetSpeedTest>();
        sc.AddSingleton<IDiskBenchmark, DiskBenchmark>();
        sc.AddSingleton<IMetricsHistory, MetricsHistory>();
        sc.AddSingleton<IReportService, ReportService>();
        // UI services
        sc.AddSingleton<NavigationService>();
        sc.AddSingleton<NotificationCenter>();
        sc.AddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationCenter>());
        sc.AddSingleton<DialogService>();
        sc.AddSingleton<ThemeService>();
        sc.AddSingleton<AiContextBuilder>();
        sc.AddSingleton<AlertMonitor>();
        // View models
        sc.AddSingleton<MainViewModel>();
        sc.AddSingleton<DashboardViewModel>();
        sc.AddSingleton<HealthViewModel>();
        sc.AddSingleton<ProcessesViewModel>();
        sc.AddSingleton<NetworkViewModel>();
        sc.AddSingleton<StorageViewModel>();
        sc.AddSingleton<StartupViewModel>();
        sc.AddSingleton<WindowsViewModel>();
        sc.AddSingleton<LogAnalyzerViewModel>();
        sc.AddSingleton<AiAssistantViewModel>();
        sc.AddSingleton<TerminalViewModel>();
        sc.AddSingleton<SettingsViewModel>();
        sc.AddSingleton<AboutViewModel>();
        sc.AddSingleton<HistoryViewModel>();
        sc.AddSingleton<SecurityViewModel>();
        sc.AddSingleton<AppsViewModel>();
        sc.AddSingleton<QuickFixesViewModel>();
        sc.AddSingleton<SpeedTestViewModel>();
        return sc.BuildServiceProvider();
    }

    private void SetupTray(IHardwareMonitor hardware, ISettingsService settings)
    {
        _tray = new TrayService(hardware);
        _tray.OpenRequested += () => Dispatcher.BeginInvoke(ShowMainWindow);
        _tray.QuickStatusRequested += () => Dispatcher.BeginInvoke(ShowFlyout);
        _tray.DiagnosticRequested += () => Dispatcher.BeginInvoke(() =>
        {
            ShowMainWindow();
            _services!.GetRequiredService<NavigationService>().Navigate(PageKey.Health, "scan");
        });
        _tray.ExitRequested += () => Dispatcher.BeginInvoke(ExitApplication);

        var notifications = _services!.GetRequiredService<NotificationCenter>();
        notifications.Raised += item =>
        {
            if (_window is { IsVisible: false } && settings.Current.TrayBalloons && settings.Current.NotificationsEnabled && item.Level >= NotificationLevel.Warning)
                _tray.ShowBalloon(item.Title, item.Message, item.Level);
        };
    }

    private void ShowFlyout()
    {
        if (_services == null) return;
        if (_flyout is { IsVisible: true }) { _flyout.Close(); return; }
        _flyout = new TrayFlyout(_services.GetRequiredService<IHardwareMonitor>(), _services.GetRequiredService<IHealthService>());
        _flyout.OpenRequested += ShowMainWindow;
        _flyout.DiagnosticRequested += () =>
        {
            ShowMainWindow();
            _services.GetRequiredService<NavigationService>().Navigate(PageKey.Health, "scan");
        };
        _flyout.ExitRequested += ExitApplication;
        _flyout.Show();
        _flyout.Activate();
    }

    public void ShowMainWindow()
    {
        if (_window == null) return;
        _flyout?.Close();
        _window.ShowFromTray();
    }

    private void StartActivationListener()
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
        var thread = new Thread(() =>
        {
            try
            {
                var handles = new WaitHandle[] { _activateEvent, _shutdownEvent };
                while (!_exiting)
                {
                    int signaled = WaitHandle.WaitAny(handles, TimeSpan.FromSeconds(1));
                    if (_exiting) break;
                    if (signaled == 0) Dispatcher.BeginInvoke(ShowMainWindow);
                    else if (signaled == 1) { AppLog.Info("Shutdown requested by another process"); Dispatcher.BeginInvoke(ExitApplication); }
                }
            }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true, Name = "Instance activation" };
        thread.Start();
    }

    public void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        AppLog.Info("Exit requested");
        try { _window?.SaveWindowState(); } catch { }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        try
        {
            _flyout?.Close();
            _tray?.Dispose();
            _services?.GetService<AlertMonitor>()?.Dispose();
            (_services?.GetService<ISettingsService>() as IDisposable)?.Dispose();
            _services?.Dispose();
        }
        catch (Exception ex) { AppLog.Error("Error during shutdown", ex); }
        if (_primary) AppLog.Info("Exited cleanly");
        AppLog.Shutdown();
        try { _activateEvent?.Dispose(); } catch { }
        try { _shutdownEvent?.Dispose(); } catch { }
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error($"Fatal unhandled exception (terminating={e.IsTerminating})", e.ExceptionObject as Exception);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Never crash because a single feature failed: log it and show a friendly message.
        AppLog.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        if (Interlocked.Increment(ref _unhandledCount) > 50) return; // avoid notification storms
        try
        {
            _services?.GetService<INotificationService>()?.Notify(NotificationLevel.Error, Loc.T("Something went wrong"),
                Loc.F("{0} The details were written to app.log.", OperationResult.FriendlyError(e.Exception)), "unhandled-" + e.Exception.GetType().Name, TimeSpan.FromSeconds(30));
        }
        catch { }
    }
}
