using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCCommandCenter.App.Services;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.ViewModels;

public sealed partial class StartupItem : ObservableObject
{
    public required StartupEntry Entry { get; init; }
    public string Name => Entry.Name;
    public string Command => Entry.Command;
    public string Publisher => Entry.Publisher ?? Loc.T("Unknown publisher");
    public string Location => Entry.Location;
    public string Scope => Loc.T(Entry.Scope);
    public bool CanToggle => Entry.CanToggle;
    public string? ToggleNote => Entry.ToggleNote is { } n ? Loc.T(n) : null;
    public bool Missing => Entry.ExecutablePath != null && Path.IsPathRooted(Entry.ExecutablePath) && !Entry.FileExists;
    public string Impact => Loc.T("Not available");
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private ImageSource? _icon;
    public string StatusText => Enabled ? Loc.T("Enabled") : Loc.T("Disabled");
    partial void OnEnabledChanged(bool value) => OnPropertyChanged(nameof(StatusText));
}

public sealed partial class StartupViewModel : PageViewModel
{
    private readonly IStartupService _startup;
    private readonly IProcessService _processes;
    private readonly DialogService _dialogs;
    private readonly INotificationService _notify;
    private readonly IWindowsToolsService _windows;

    public StartupViewModel(IStartupService startup, IProcessService processes, DialogService dialogs, INotificationService notify, IWindowsToolsService windows)
    {
        _startup = startup;
        _processes = processes;
        _dialogs = dialogs;
        _notify = notify;
        _windows = windows;
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => string.IsNullOrWhiteSpace(Search) || ((StartupItem)o).Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
                           || ((StartupItem)o).Publisher.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }

    public ObservableCollection<StartupItem> Items { get; } = [];
    public ICollectionView View { get; }
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private int _enabledCount;
    [ObservableProperty] private int _disabledCount;
    public bool IsElevated => AppInfo.IsElevated;
    partial void OnSearchChanged(string value) => View.Refresh();

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        _ = RefreshAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => RunBusyAsync(async () =>
    {
        var entries = await _startup.GetEntriesAsync();
        Items.Clear();
        foreach (var e in entries)
        {
            var item = new StartupItem { Entry = e, Enabled = e.Enabled };
            Items.Add(item);
            if (e.ExecutablePath != null && e.FileExists) LoadIcon(item, e.ExecutablePath);
        }
        UpdateCounts();
    }, "Unable to read startup entries");

    private void UpdateCounts()
    {
        EnabledCount = Items.Count(i => i.Enabled);
        DisabledCount = Items.Count - EnabledCount;
        Summary = Loc.F("{0} entries · {1} enabled · {2} disabled", Items.Count, EnabledCount, DisabledCount);
    }

    private void LoadIcon(StartupItem item, string path) => Task.Run(() =>
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return;
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromWidthAndHeight(32, 32));
            src.Freeze();
            Ui(() => item.Icon = src);
        }
        catch { }
    });

    /// <summary>Invoked by the toggle switch. The toggle is reverted unless the user confirms and the change succeeds.</summary>
    [RelayCommand]
    private async Task ToggleAsync(StartupItem? item)
    {
        if (item == null) return;
        bool requested = item.Enabled;      // the switch already flipped visually
        item.Enabled = !requested;          // revert until confirmed
        if (!item.CanToggle)
        {
            _notify.Notify(NotificationLevel.Info, Loc.T("This entry cannot be changed"), item.ToggleNote ?? "");
            return;
        }
        var ok = await _dialogs.ConfirmAsync(
            requested ? Loc.F("Enable {0} at startup?", item.Name) : Loc.F("Disable {0} at startup?", item.Name),
            requested ? Loc.T("The program will start automatically the next time you sign in.")
                      : Loc.T("The program will no longer start automatically. The entry is kept and can be re-enabled at any time."),
            requested ? Loc.T("Enable") : Loc.T("Disable"), DialogKind.Warning, $"{item.Location}\n{item.Command}");
        if (!ok) return;
        var result = await _startup.SetEnabledAsync(item.Entry, requested);
        if (result.Success) item.Enabled = requested;
        _notify.Notify(result.Success ? NotificationLevel.Success : NotificationLevel.Error, result.Success ? Loc.T("Startup updated") : Loc.T("Unable to update startup"), result.Message);
        UpdateCounts();
    }

    [RelayCommand]
    private void OpenLocation(StartupItem? item)
    {
        if (item?.Entry.ExecutablePath is { } p) _processes.OpenFileLocation(p);
    }

    [RelayCommand]
    private void OpenTaskManagerStartup()
    {
        var tool = _windows.Tools.First(t => t.Id == "startup");
        _windows.Launch(tool);
    }

    [RelayCommand]
    private void RestartElevated()
    {
        var r = _windows.RestartAsAdministrator();
        if (r.Success) ((App)System.Windows.Application.Current).ExitApplication();
        else _notify.Notify(NotificationLevel.Warning, Loc.T("Not restarted"), r.Message);
    }
}
