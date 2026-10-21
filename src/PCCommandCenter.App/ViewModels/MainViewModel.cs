using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PCCommandCenter.App.Services;
using PCCommandCenter.Core;
using PCCommandCenter.Settings;

namespace PCCommandCenter.App.ViewModels;

public sealed partial class NavItem : ObservableObject
{
    public required PageInfo Info { get; init; }
    public string Title => Info.Title;
    public string Glyph => Info.Glyph;
    public bool StartsGroup { get; init; }
    public string Group => Info.Group;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string? _badge;

    /// <summary>Re-evaluates translated bindings after a language change.</summary>
    public void RefreshText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Group));
    }
}

public enum WelcomeStage { None, Intro, Scanning, Ready }

/// <summary>Application shell: navigation, overlays (palette, dialogs, notifications) and first-run experience.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly NavigationService _nav;
    private readonly ISettingsService _settings;
    private readonly IHealthService _health;
    private readonly IHardwareMonitor _hardware;
    private readonly IWindowsToolsService _windows;
    private readonly Dictionary<PageKey, PageViewModel> _pages = [];
    private PageViewModel? _active;
    private DateTime _lastStatus;

    public MainViewModel(IServiceProvider services, NavigationService nav, ISettingsService settings, NotificationCenter notifications,
                         DialogService dialogs, IHealthService health, IHardwareMonitor hardware, IWindowsToolsService windows, IProcessService processes)
    {
        _services = services;
        _nav = nav;
        _settings = settings;
        _health = health;
        _hardware = hardware;
        _windows = windows;
        Notifications = notifications;
        Dialogs = dialogs;
        Palette = new CommandPaletteViewModel(nav, windows, processes, () => [.. ExtraCommands(), .. FixCommands()]);
        SidebarCollapsed = settings.Current.SidebarCollapsed;

        string? lastGroup = null;
        foreach (var p in NavigationService.Pages)
        {
            NavItems.Add(new NavItem { Info = p, StartsGroup = p.Group != lastGroup });
            lastGroup = p.Group;
        }

        _nav.Navigated += OnNavigated;
        _health.ReportUpdated += (_, r) => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var problems = r.Issues.Count(i => i.Severity >= IssueSeverity.Warning);
            NavItems.First(n => n.Info.Key == PageKey.Health).Badge = problems > 0 ? problems.ToString() : null;
        });
        _hardware.SnapshotUpdated += (_, s) =>
        {
            if (DateTime.Now - _lastStatus < TimeSpan.FromSeconds(2) || !PageViewModel.WindowVisible) return;
            _lastStatus = DateTime.Now;
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                StatusCpu = s.Cpu.Usage;
                StatusRam = s.Memory.Percent;
                StatusText = $"CPU {Fmt.Percent(s.Cpu.Usage)} · RAM {Fmt.Percent(s.Memory.Percent)}";
            });
        };
    }

    public ObservableCollection<NavItem> NavItems { get; } = [];
    public NotificationCenter Notifications { get; }
    public DialogService Dialogs { get; }
    public CommandPaletteViewModel Palette { get; }

    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private string _pageTitle = "";
    [ObservableProperty] private string _pageSubtitle = "";
    [ObservableProperty] private bool _sidebarCollapsed;
    [ObservableProperty] private bool _notificationsOpen;
    [ObservableProperty] private WelcomeStage _welcome = WelcomeStage.None;
    [ObservableProperty] private double _welcomeProgress;
    [ObservableProperty] private string _welcomeStep = "";
    [ObservableProperty] private int? _welcomeScore;
    [ObservableProperty] private string _welcomeSummary = "";
    [ObservableProperty] private double _statusCpu;
    [ObservableProperty] private double _statusRam;
    [ObservableProperty] private string _statusText = "";

    public bool IsElevated => AppInfo.IsElevated;
    public string Version => "v" + AppInfo.Version;

    partial void OnSidebarCollapsedChanged(bool value) => _settings.Update(s => s.SidebarCollapsed = value);

    partial void OnNotificationsOpenChanged(bool value) { if (value) Notifications.MarkRead(); }

    public void Start()
    {
        if (!_settings.Current.FirstRunCompleted) Welcome = WelcomeStage.Intro;
        _nav.Navigate(PageKey.Dashboard);
    }

    private void OnNavigated(PageKey key, object? parameter)
    {
        var vm = GetPage(key);
        if (_active != vm) _active?.OnNavigatedFrom();
        _active = vm;
        var info = NavigationService.Info(key);
        PageTitle = Loc.T(info.Title);
        PageSubtitle = Loc.T(info.Subtitle);
        foreach (var n in NavItems) n.IsSelected = n.Info.Key == key;
        CurrentPage = vm;
        vm.OnNavigatedTo(parameter);
    }

    /// <summary>Called when the language changes so the header text is refreshed.</summary>
    public void RefreshTitles()
    {
        var info = NavigationService.Info(_nav.Current);
        PageTitle = Loc.T(info.Title);
        PageSubtitle = Loc.T(info.Subtitle);
        foreach (var n in NavItems) n.RefreshText();
        // Re-create the visible page so every converter-based translation is evaluated again.
        var page = CurrentPage;
        CurrentPage = null;
        CurrentPage = page;
    }

    private PageViewModel GetPage(PageKey key)
    {
        if (_pages.TryGetValue(key, out var vm)) return vm;
        vm = key switch
        {
            PageKey.Dashboard => _services.GetRequiredService<DashboardViewModel>(),
            PageKey.Health => _services.GetRequiredService<HealthViewModel>(),
            PageKey.Processes => _services.GetRequiredService<ProcessesViewModel>(),
            PageKey.Network => _services.GetRequiredService<NetworkViewModel>(),
            PageKey.Storage => _services.GetRequiredService<StorageViewModel>(),
            PageKey.Startup => _services.GetRequiredService<StartupViewModel>(),
            PageKey.Windows => _services.GetRequiredService<WindowsViewModel>(),
            PageKey.Logs => _services.GetRequiredService<LogAnalyzerViewModel>(),
            PageKey.Ai => _services.GetRequiredService<AiAssistantViewModel>(),
            PageKey.Terminal => _services.GetRequiredService<TerminalViewModel>(),
            PageKey.Settings => _services.GetRequiredService<SettingsViewModel>(),
            PageKey.History => _services.GetRequiredService<HistoryViewModel>(),
            PageKey.Security => _services.GetRequiredService<SecurityViewModel>(),
            PageKey.Apps => _services.GetRequiredService<AppsViewModel>(),
            PageKey.Fixes => _services.GetRequiredService<QuickFixesViewModel>(),
            PageKey.SpeedTest => _services.GetRequiredService<SpeedTestViewModel>(),
            _ => _services.GetRequiredService<AboutViewModel>(),
        };
        _pages[key] = vm;
        return vm;
    }

    /// <summary>Notify the visible page that the window was hidden/shown so it can pause timers.</summary>
    public void OnWindowVisibilityChanged(bool visible)
    {
        PageViewModel.WindowVisible = visible;
        if (_active == null) return;
        if (visible) _active.OnNavigatedTo(null);
        else _active.OnNavigatedFrom();
    }

    [RelayCommand] private void Navigate(PageKey key) => _nav.Navigate(key);
    [RelayCommand] private void NavigateItem(NavItem item) => _nav.Navigate(item.Info.Key);
    [RelayCommand] private void ToggleSidebar() => SidebarCollapsed = !SidebarCollapsed;
    [RelayCommand] private void OpenPalette() => Palette.Open();
    [RelayCommand] private void ToggleNotifications() => NotificationsOpen = !NotificationsOpen;
    [RelayCommand] private void DismissNotification(NotificationItem item) => Notifications.Dismiss(item);
    [RelayCommand] private void DismissToast(NotificationItem item) => Notifications.DismissToast(item);
    [RelayCommand] private void ClearNotifications() => Notifications.ClearAll();

    [RelayCommand]
    private void RestartElevated()
    {
        var r = _windows.RestartAsAdministrator();
        if (r.Success) ((App)System.Windows.Application.Current).ExitApplication();
        else Notifications.Notify(NotificationLevel.Warning, Loc.T("Not restarted"), r.Message);
    }

    // First run

    [RelayCommand]
    private async Task StartWelcomeScanAsync()
    {
        Welcome = WelcomeStage.Scanning;
        WelcomeStep = Loc.T("Scanning your system...");
        try
        {
            var progress = new Progress<HealthProgress>(p => { WelcomeProgress = p.Fraction; WelcomeStep = p.Step; });
            var report = await _health.RunScanAsync(progress);
            WelcomeScore = report.Score;
            WelcomeSummary = Loc.F("{0} checks completed · {1} finding(s)", report.Categories.Sum(c => c.Checks.Count), report.Issues.Count);
        }
        catch (Exception ex)
        {
            AppLog.Error("Initial scan failed", ex);
            WelcomeSummary = Loc.T("The initial scan could not be completed. You can run it later from System Health.");
        }
        WelcomeProgress = 1;
        Welcome = WelcomeStage.Ready;
    }

    [RelayCommand]
    private void FinishWelcome()
    {
        _settings.Update(s => s.FirstRunCompleted = true);
        Welcome = WelcomeStage.None;
    }

    [RelayCommand]
    private void SkipWelcome()
    {
        _settings.Update(s => s.FirstRunCompleted = true);
        Welcome = WelcomeStage.None;
    }

    /// <summary>Every repair and diagnostic is reachable from Ctrl+K (it opens the card; running still needs a click).</summary>
    private IEnumerable<PaletteCommand> FixCommands() =>
        ((IQuickFixService)_services.GetService(typeof(IQuickFixService))!).Fixes.Select(f => new PaletteCommand
        {
            Title = Loc.T(f.Title), Subtitle = Loc.T(f.Symptoms), Glyph = f.Glyph,
            Group = f.Risk == FixRisk.ReadOnly ? Loc.T("Diagnostics") : Loc.T("Repairs"),
            Keywords = $"{f.Id} {f.Title} {f.CommandPreview} fix repair diagnose", Execute = () => _nav.Navigate(PageKey.Fixes, f.Id),
        });

    private IReadOnlyList<PaletteCommand> ExtraCommands() =>
    [
        new()
        {
            Title = Loc.T("Toggle sidebar"), Subtitle = Loc.T("Collapse or expand the navigation"), Glyph = ((char)0xE700).ToString(), Group = Loc.T("Actions"),
            Keywords = "sidebar menu collapse expand", Execute = ToggleSidebar,
        },
        new()
        {
            Title = Loc.T("Show notifications"), Subtitle = Loc.T("Open the notification center"), Glyph = ((char)0xEA8F).ToString(), Group = Loc.T("Actions"),
            Keywords = "notifications alerts bell", Execute = () => NotificationsOpen = true,
        },
        new()
        {
            Title = Loc.T("Clear notifications"), Subtitle = Loc.T("Dismiss all notifications"), Glyph = ((char)0xE74D).ToString(), Group = Loc.T("Actions"),
            Keywords = "clear notifications dismiss", Execute = ClearNotifications,
        },
        new()
        {
            Title = Loc.T("Restart as administrator"), Subtitle = Loc.T("Unlock SMART data, services and system-wide startup entries"), Glyph = ((char)0xEA18).ToString(), Group = Loc.T("Actions"),
            Keywords = "admin elevate administrator uac", Execute = RestartElevated,
        },
        new()
        {
            Title = Loc.T("Open application log"), Subtitle = AppInfo.LogFile, Glyph = ((char)0xE9F9).ToString(), Group = Loc.T("Actions"),
            Keywords = "log app.log debug", Execute = () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppInfo.LogFile) { UseShellExecute = true }); } catch { } },
        },
    ];
}
