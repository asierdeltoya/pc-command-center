using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PCCommandCenter.App.Services;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.ViewModels;

public sealed class ServiceItem
{
    public required ServiceStatusInfo Info { get; init; }
    public string DisplayName => Info.DisplayName;
    public string Purpose => Loc.T(Info.Purpose);
    public string Status => Info.Status;
    public string StatusText => Loc.T(Info.Status);
    public string StartType => Loc.T(Info.StartType);
    public bool IsRunning => Info.IsRunning;
    public bool IsProblem => Info.ShouldBeRunning && !Info.IsRunning;
}

public sealed class EventItem
{
    public required EventEntry Entry { get; init; }
    public string Time => Entry.Time.ToString("g");
    public string Level => Entry.Level;
    public string LevelText => Loc.T(Entry.Level);
    public string Source => $"{Entry.Source} · {Entry.EventId}";
    public string Message => Entry.Message.Length > 400 ? Entry.Message[..400] + "…" : Entry.Message;
    public string ShortMessage => Entry.Message.Split('\n')[0].Trim();
}

public sealed partial class WindowsViewModel : PageViewModel
{
    private readonly IWindowsToolsService _windows;
    private readonly DialogService _dialogs;
    private readonly INotificationService _notify;
    private readonly NavigationService _nav;
    private readonly IPowerService _power;
    private bool _loaded;
    private bool _applyingPlan;

    public WindowsViewModel(IWindowsToolsService windows, DialogService dialogs, INotificationService notify, NavigationService nav, IPowerService power)
    {
        _windows = windows;
        _dialogs = dialogs;
        _notify = notify;
        _nav = nav;
        _power = power;
    }

    // ── Power & battery
    public ObservableCollection<PowerPlan> PowerPlans { get; } = [];
    [ObservableProperty] private PowerPlan? _activePlan;
    [ObservableProperty] private BatteryInfo? _battery;
    [ObservableProperty] private string _batterySummary = "";
    [ObservableProperty] private string _batteryHealth = "";
    [ObservableProperty] private double _batteryHealthPercent;
    [ObservableProperty] private string _powerSource = "";

    partial void OnActivePlanChanged(PowerPlan? oldValue, PowerPlan? newValue)
    {
        if (_applyingPlan || newValue == null || oldValue == null || newValue.IsActive) return;
        _ = ChangePlanAsync(newValue);
    }

    private async Task ChangePlanAsync(PowerPlan plan)
    {
        var r = await _power.SetActivePlanAsync(plan);
        _notify.Notify(r.Success ? NotificationLevel.Success : NotificationLevel.Error, Loc.T("Power plan"), r.Message);
        await LoadPowerAsync();
    }

    private async Task LoadPowerAsync()
    {
        try
        {
            var plans = await _power.GetPlansAsync();
            _applyingPlan = true;
            PowerPlans.Clear();
            foreach (var p in plans) PowerPlans.Add(p);
            ActivePlan = PowerPlans.FirstOrDefault(p => p.IsActive);
            _applyingPlan = false;

            var b = await _power.GetBatteryAsync();
            Battery = b;
            PowerSource = b.OnAcPower switch { true => Loc.T("Plugged in"), false => Loc.T("On battery"), null => Loc.T("Not available") };
            if (b.Present)
            {
                BatterySummary = $"{Fmt.Percent(b.ChargePercent)} · {Loc.T(b.Status)}" + (b.EstimatedRuntime is { } rt ? $" · {Fmt.Duration(rt)}" : "");
                BatteryHealth = b.HealthPercent is { } h
                    ? Loc.F("{0} of original capacity ({1:N0} of {2:N0} mWh)", Fmt.Percent(h), b.FullChargeCapacityMWh, b.DesignCapacityMWh)
                    : Loc.T("Capacity data not available (may require administrator)");
                BatteryHealthPercent = b.HealthPercent ?? 0;
            }
            else BatterySummary = Loc.T("No battery (desktop PC)");
        }
        catch (Exception ex) { AppLog.Warn("Power info failed", ex); }
        finally { _applyingPlan = false; }
    }

    [RelayCommand]
    private async Task BatteryReportAsync()
    {
        var r = await _power.CreateBatteryReportAsync();
        _notify.Notify(r.Success ? NotificationLevel.Success : NotificationLevel.Warning, Loc.T("Battery report"), r.Message);
    }

    [RelayCommand]
    private void OpenPowerSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:powersleep") { UseShellExecute = true }); } catch { }
    }

    public IReadOnlyList<WindowsTool> Tools => _windows.Tools;
    public ObservableCollection<ServiceItem> Services { get; } = [];
    public ObservableCollection<EventItem> Events { get; } = [];

    [ObservableProperty] private SystemInfo? _info;
    [ObservableProperty] private string _infoUptime = "";
    [ObservableProperty] private string _pendingReboot = "";
    [ObservableProperty] private bool _loadingServices;
    [ObservableProperty] private bool _loadingEvents;
    [ObservableProperty] private bool _includeWarnings;
    [ObservableProperty] private EventItem? _selectedEvent;
    [ObservableProperty] private string _eventSummary = "";
    public bool IsElevated => AppInfo.IsElevated;

    partial void OnIncludeWarningsChanged(bool value) => _ = LoadEventsAsync();

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        if (_loaded) return;
        _loaded = true;
        _ = LoadInfoAsync();
        _ = LoadServicesAsync();
        _ = LoadEventsAsync();
        _ = LoadPowerAsync();
    }

    [RelayCommand]
    private void Launch(WindowsTool? tool)
    {
        if (tool == null) return;
        var r = _windows.Launch(tool);
        if (!r.Success) _notify.Notify(NotificationLevel.Error, Loc.F("Unable to open {0}", Loc.T(tool.Name)), r.Message);
    }

    private async Task LoadInfoAsync()
    {
        try
        {
            Info = await _windows.GetSystemInfoAsync();
            InfoUptime = Fmt.Duration(Info.Uptime);
            PendingReboot = Info.PendingReboot switch
            {
                true => Loc.F("Yes ({0})", Loc.T(Info.PendingRebootReason ?? "")),
                false => Loc.T("No"),
                null => Loc.T("Not available"),
            };
        }
        catch (Exception ex) { AppLog.Warn("System info failed", ex); }
    }

    [RelayCommand]
    private async Task LoadServicesAsync()
    {
        LoadingServices = true;
        try
        {
            var list = await _windows.GetImportantServicesAsync();
            Services.Clear();
            foreach (var s in list.OrderByDescending(s => s.ShouldBeRunning && !s.IsRunning).ThenBy(s => s.DisplayName)) Services.Add(new ServiceItem { Info = s });
        }
        catch (Exception ex) { AppLog.Warn("Services failed", ex); }
        finally { LoadingServices = false; }
    }

    [RelayCommand]
    private async Task LoadEventsAsync()
    {
        LoadingEvents = true;
        try
        {
            var list = await _windows.GetRecentEventsAsync(TimeSpan.FromHours(24), 150, IncludeWarnings);
            Events.Clear();
            foreach (var e in list) Events.Add(new EventItem { Entry = e });
            EventSummary = Loc.F("{0} critical · {1} errors · {2} warnings (last 24 h)",
                list.Count(e => e.Level == "Critical"), list.Count(e => e.Level == "Error"), list.Count(e => e.Level == "Warning"));
        }
        catch (Exception ex) { AppLog.Warn("Events failed", ex); }
        finally { LoadingEvents = false; }
    }

    private async Task ControlAsync(ServiceItem? item, ServiceAction action)
    {
        if (item == null) return;
        if (!AppInfo.IsElevated)
        {
            var restart = await _dialogs.ConfirmAsync(Loc.T("Administrator privileges required"),
                Loc.T("Windows only allows administrators to start or stop services. Restart PC Command Center as administrator?"),
                Loc.T("Restart as administrator"), DialogKind.Info);
            if (restart) RestartElevated();
            return;
        }
        var ok = await _dialogs.ConfirmAsync(Loc.F("{0} service '{1}'?", Loc.T(action.ToString()), item.DisplayName),
            action == ServiceAction.Stop ? Loc.T("Stopping a system service can break features that depend on it until it is started again.")
                                         : Loc.T("The service state will be changed immediately."),
            Loc.T(action.ToString()), action == ServiceAction.Stop ? DialogKind.Danger : DialogKind.Warning, $"{item.Info.Name} · {item.Purpose}");
        if (!ok) return;
        var r = await _windows.ControlServiceAsync(item.Info.Name, action);
        _notify.Notify(r.Success ? NotificationLevel.Success : NotificationLevel.Error, r.Success ? Loc.T("Service updated") : Loc.T("Service change failed"), r.Message);
        await LoadServicesAsync();
    }

    [RelayCommand] private Task StartServiceAsync(ServiceItem? item) => ControlAsync(item, ServiceAction.Start);
    [RelayCommand] private Task StopServiceAsync(ServiceItem? item) => ControlAsync(item, ServiceAction.Stop);
    [RelayCommand] private Task RestartServiceAsync(ServiceItem? item) => ControlAsync(item, ServiceAction.Restart);

    [RelayCommand]
    private void RestartElevated()
    {
        var r = _windows.RestartAsAdministrator();
        if (r.Success) ((App)System.Windows.Application.Current).ExitApplication();
        else _notify.Notify(NotificationLevel.Warning, Loc.T("Not restarted"), r.Message);
    }

    [RelayCommand]
    private void AskAiEvent(EventItem? item)
    {
        item ??= SelectedEvent;
        if (item == null) return;
        var e = item.Entry;
        WeakReferenceMessenger.Default.Send(new AiAttachmentMessage(Loc.F("Event {0} ({1})", e.EventId, e.Source),
            $"Log: {e.Log}\nLevel: {e.Level}\nTime: {e.Time:g}\nSource: {e.Source}\nEvent ID: {e.EventId}\nMessage: {e.Message}",
            Loc.T("What does this Windows event mean and should I worry about it?")));
        _nav.Navigate(PageKey.Ai);
    }

    [RelayCommand]
    private void AskAiAllEvents()
    {
        if (Events.Count == 0) return;
        var text = string.Join("\n", Events.Take(60).Select(e => $"{e.Entry.Time:g} [{e.Entry.Level}] {e.Entry.Log}/{e.Entry.Source} {e.Entry.EventId}: {e.ShortMessage}"));
        WeakReferenceMessenger.Default.Send(new AiAttachmentMessage(Loc.T("Recent Windows events"), text, Loc.T("Analyze these recent Windows errors. Which ones matter?")));
        _nav.Navigate(PageKey.Ai);
    }
}
