using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PCCommandCenter.App.Services;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.ViewModels;

public sealed class SecurityItem
{
    public required SecurityCheck Check { get; init; }
    public string Title => Check.Title;
    public string Value => Check.Value;
    public string Detail => Check.Detail;
    public string Recommendation => Check.Recommendation;
    public HealthStatus Status => Check.Status;
    public string Glyph => Check.Glyph;
    public bool CanOpen => Check.OpenTarget != null;
    public string StatusText => Check.Status switch
    {
        HealthStatus.Good => Loc.T("GOOD"), HealthStatus.Warning => Loc.T("WARNING"),
        HealthStatus.Critical => Loc.T("CRITICAL"), _ => Loc.T("NOT AVAILABLE"),
    };
}

public sealed partial class SecurityViewModel : PageViewModel
{
    private readonly ISecurityService _security;
    private readonly DialogService _dialogs;
    private readonly INotificationService _notify;
    private readonly NavigationService _nav;
    private bool _loaded;

    public SecurityViewModel(ISecurityService security, DialogService dialogs, INotificationService notify, NavigationService nav)
    {
        _security = security;
        _dialogs = dialogs;
        _notify = notify;
        _nav = nav;
    }

    public ObservableCollection<SecurityItem> Checks { get; } = [];
    [ObservableProperty] private HealthStatus _overall = HealthStatus.NotAvailable;
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _subline = "";
    [ObservableProperty] private int _protectedCount;
    [ObservableProperty] private int _attentionCount;

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        if (!_loaded) { _loaded = true; _ = RefreshAsync(); }
    }

    [RelayCommand]
    private Task RefreshAsync() => RunBusyAsync(async () =>
    {
        var list = await _security.GetChecksAsync();
        Checks.Clear();
        foreach (var c in list.OrderBy(c => c.Status switch { HealthStatus.Critical => 0, HealthStatus.Warning => 1, HealthStatus.Good => 2, _ => 3 }))
            Checks.Add(new SecurityItem { Check = c });
        AttentionCount = list.Count(c => c.Status is HealthStatus.Warning or HealthStatus.Critical);
        ProtectedCount = list.Count(c => c.Status == HealthStatus.Good);
        Overall = list.Any(c => c.Status == HealthStatus.Critical) ? HealthStatus.Critical
                : list.Any(c => c.Status == HealthStatus.Warning) ? HealthStatus.Warning : HealthStatus.Good;
        Headline = Overall switch
        {
            HealthStatus.Critical => Loc.T("Your PC needs protection"),
            HealthStatus.Warning => Loc.T("Mostly protected, some items need attention"),
            _ => Loc.T("Your PC is protected"),
        };
        Subline = Loc.F("{0} checks OK · {1} need attention", ProtectedCount, AttentionCount);
    }, "Unable to read the security status");

    [RelayCommand]
    private void Open(SecurityItem? item)
    {
        if (item?.Check.OpenTarget is not { } target) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { _notify.Notify(NotificationLevel.Error, Loc.T("Unable to open"), OperationResult.FriendlyError(ex)); }
    }

    [RelayCommand]
    private async Task QuickScanAsync()
    {
        if (!await _dialogs.ConfirmAsync(Loc.T("Run a Microsoft Defender quick scan?"), Loc.T("The scan runs in the background for a few minutes. Windows Security notifies you if it finds a threat."),
                Loc.T("Start scan"), DialogKind.Info)) return;
        var r = await _security.StartDefenderQuickScanAsync();
        _notify.Notify(r.Success ? NotificationLevel.Success : NotificationLevel.Warning, Loc.T("Quick scan"), r.Message);
    }

    [RelayCommand]
    private void OpenWindowsSecurity()
    {
        try { Process.Start(new ProcessStartInfo("windowsdefender:") { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void AskAi()
    {
        var text = string.Join("\n", Checks.Select(c => $"{c.Title}: {c.Value} [{c.Status}] {c.Detail}"));
        WeakReferenceMessenger.Default.Send(new AiAttachmentMessage(Loc.T("Security status"), text, Loc.T("Review my PC security status. What should I improve first?")));
        _nav.Navigate(PageKey.Ai);
    }
}
