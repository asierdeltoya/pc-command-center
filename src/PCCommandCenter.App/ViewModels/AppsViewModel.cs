using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCCommandCenter.App.Services;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.ViewModels;

public sealed partial class AppItem : ObservableObject
{
    public required InstalledApp App { get; init; }
    public string Name => App.Name;
    public string Publisher => App.Publisher ?? Loc.T("Unknown publisher");
    public string Version => App.Version ?? "";
    public long Size => App.SizeBytes ?? 0;
    public string SizeText => App.SizeBytes is { } s ? Fmt.Bytes(s) : "—";
    public DateTime Installed => App.InstallDate ?? DateTime.MinValue;
    public string InstalledText => App.InstallDate?.ToString("d") ?? "—";
    public string Scope => Loc.T(App.Scope);
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private AppUpdate? _update;
    public bool HasUpdate => Update != null;
    partial void OnUpdateChanged(AppUpdate? value) => OnPropertyChanged(nameof(HasUpdate));
}

public sealed partial class AppsViewModel : PageViewModel
{
    private readonly IInstalledAppsService _apps;
    private readonly DialogService _dialogs;
    private readonly INotificationService _notify;
    private bool _loaded;

    public AppsViewModel(IInstalledAppsService apps, DialogService dialogs, INotificationService notify)
    {
        _apps = apps;
        _dialogs = dialogs;
        _notify = notify;
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = Filter;
        View.SortDescriptions.Add(new SortDescription(nameof(AppItem.Name), ListSortDirection.Ascending));
    }

    public ObservableCollection<AppItem> Items { get; } = [];
    public ObservableCollection<AppUpdate> Updates { get; } = [];
    public ICollectionView View { get; }

    public static IReadOnlyList<string> Sorts { get; } = ["Name", "Size", "Install date", "Publisher"];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _sort = "Name";
    [ObservableProperty] private bool _onlyUpdates;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _checkingUpdates;
    [ObservableProperty] private string _updatesStatus = "";
    [ObservableProperty] private bool _wingetAvailable = true;
    [ObservableProperty] private AppItem? _selected;

    partial void OnSearchChanged(string value) => View.Refresh();
    partial void OnOnlyUpdatesChanged(bool value) => View.Refresh();
    partial void OnSortChanged(string value)
    {
        View.SortDescriptions.Clear();
        View.SortDescriptions.Add(value switch
        {
            "Size" => new SortDescription(nameof(AppItem.Size), ListSortDirection.Descending),
            "Install date" => new SortDescription(nameof(AppItem.Installed), ListSortDirection.Descending),
            "Publisher" => new SortDescription(nameof(AppItem.Publisher), ListSortDirection.Ascending),
            _ => new SortDescription(nameof(AppItem.Name), ListSortDirection.Ascending),
        });
    }

    private bool Filter(object o)
    {
        var a = (AppItem)o;
        if (OnlyUpdates && !a.HasUpdate) return false;
        if (string.IsNullOrWhiteSpace(Search)) return true;
        return a.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || a.Publisher.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        if (_loaded) return;
        _loaded = true;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RunBusyAsync(async () =>
        {
            var list = await _apps.GetAppsAsync();
            Items.Clear();
            foreach (var a in list)
            {
                var item = new AppItem { App = a };
                Items.Add(item);
                if (a.IconPath is { } icon && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) LoadIcon(item, icon);
            }
            Summary = Loc.F("{0} programs · {1} on disk", list.Count, Fmt.Bytes(list.Sum(a => a.SizeBytes ?? 0)));
        }, "Unable to read installed programs");
        await CheckUpdatesAsync();
    }

    private void LoadIcon(AppItem item, string path) => Task.Run(() =>
    {
        try
        {
            if (!File.Exists(path)) return;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return;
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            src.Freeze();
            Ui(() => item.Icon = src);
        }
        catch { }
    });

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        CheckingUpdates = true;
        UpdatesStatus = Loc.T("Checking for updates with winget…");
        try
        {
            var r = await _apps.GetUpdatesAsync();
            WingetAvailable = r.WingetAvailable;
            Updates.Clear();
            foreach (var u in r.Updates) Updates.Add(u);
            foreach (var item in Items)
                item.Update = r.Updates.FirstOrDefault(u =>
                {
                    // winget truncates long names with "…": match exactly, or by a long enough prefix.
                    var n = u.Name.TrimEnd('…', '.', ' ');
                    return item.Name.Equals(n, StringComparison.OrdinalIgnoreCase)
                           || (n.Length >= 10 && item.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase));
                });
            UpdatesStatus = r.Error ?? (r.Updates.Count == 0 ? Loc.T("All apps known to winget are up to date.") : Loc.F("{0} updates available", r.Updates.Count));
            View.Refresh();
        }
        finally { CheckingUpdates = false; }
    }

    [RelayCommand]
    private async Task UpgradeAsync(AppUpdate? update)
    {
        if (update == null) return;
        if (!await _dialogs.ConfirmAsync(Loc.F("Update {0}?", update.Name), Loc.F("winget will download and install version {0} from {1}. The installer may ask for permission.", update.Available, update.Source),
                Loc.T("Update"), DialogKind.Info, $"{update.Id}: {update.Version} → {update.Available}")) return;
        var r = _apps.Upgrade(update);
        _notify.Notify(r.Success ? NotificationLevel.Info : NotificationLevel.Error, Loc.T("App update"), r.Message);
    }

    [RelayCommand]
    private async Task UpgradeAllAsync()
    {
        if (Updates.Count == 0) return;
        if (!await _dialogs.ConfirmAsync(Loc.F("Update {0} apps?", Updates.Count), Loc.T("winget will update every listed app one after another in a separate window. Close the apps being updated first."),
                Loc.T("Update all"), DialogKind.Warning, string.Join("\n", Updates.Select(u => $"{u.Name}: {u.Version} → {u.Available}")))) return;
        var r = _apps.UpgradeAll();
        _notify.Notify(r.Success ? NotificationLevel.Info : NotificationLevel.Error, Loc.T("App update"), r.Message);
    }

    [RelayCommand]
    private void UpgradeItem(AppItem? item)
    {
        if (item?.Update is { } u) _ = UpgradeAsync(u);
    }

    [RelayCommand]
    private async Task UninstallAsync(AppItem? item)
    {
        item ??= Selected;
        if (item == null) return;
        if (!await _dialogs.ConfirmAsync(Loc.F("Uninstall {0}?", item.Name), Loc.T("The program's own uninstaller will open. Follow its steps to remove it."),
                Loc.T("Open uninstaller"), DialogKind.Warning, $"{item.Publisher} · {item.Version}\n{item.App.UninstallString}")) return;
        var r = _apps.Uninstall(item.App);
        _notify.Notify(r.Success ? NotificationLevel.Info : NotificationLevel.Error, Loc.T("Uninstall"), r.Message);
    }

    [RelayCommand]
    private void OpenLocation(AppItem? item)
    {
        item ??= Selected;
        var dir = item?.App.InstallLocation;
        if (string.IsNullOrEmpty(dir) && item?.App.IconPath is { } icon) dir = Path.GetDirectoryName(icon);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\"");
        else _notify.Notify(NotificationLevel.Info, Loc.T("Location not available"), Loc.T("This program does not register its install folder."));
    }

    [RelayCommand]
    private void SearchWeb(AppItem? item)
    {
        item ??= Selected;
        if (item == null) return;
        try { Process.Start(new ProcessStartInfo($"https://www.bing.com/search?q={Uri.EscapeDataString(item.Name + " " + item.Publisher)}") { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void OpenWindowsApps()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true }); } catch { }
    }
}
