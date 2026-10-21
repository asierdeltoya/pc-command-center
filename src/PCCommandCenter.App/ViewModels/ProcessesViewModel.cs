using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PCCommandCenter.App.Services;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.ViewModels;

public sealed partial class ProcessRow : ObservableObject
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string? _path;
    [ObservableProperty] private string? _user;
    [ObservableProperty] private string? _company;
    [ObservableProperty] private double _cpu;
    [ObservableProperty] private long _ram;
    [ObservableProperty] private double _disk;
    [ObservableProperty] private int _connections;
    [ObservableProperty] private int _threads;
    [ObservableProperty] private int _handles;
    [ObservableProperty] private bool _isCritical;
    [ObservableProperty] private ImageSource? _icon;
    public DateTime? StartTime { get; set; }
    public int ParentPid { get; set; }

    public string CpuText => Cpu < 0.05 ? "0%" : Fmt.Percent(Cpu, Cpu < 10 ? 1 : 0);
    public string RamText => Fmt.Bytes(Ram);
    public string DiskText => Disk < 1 ? "0 B/s" : Fmt.DiskRate(Disk);
    partial void OnCpuChanged(double value) => OnPropertyChanged(nameof(CpuText));
    partial void OnRamChanged(long value) => OnPropertyChanged(nameof(RamText));
    partial void OnDiskChanged(double value) => OnPropertyChanged(nameof(DiskText));
}

public sealed partial class ProcessesViewModel : PageViewModel
{
    private static readonly ConcurrentDictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly IProcessService _processes;
    private readonly DialogService _dialogs;
    private readonly INotificationService _notify;
    private readonly NavigationService _nav;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<int, ProcessRow> _byPid = [];
    private bool _refreshing;
    private CancellationTokenSource? _detailsCts;

    public ProcessesViewModel(IProcessService processes, DialogService dialogs, INotificationService notify, NavigationService nav)
    {
        _processes = processes;
        _dialogs = dialogs;
        _notify = notify;
        _nav = nav;
        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        View.Filter = Filter;
        View.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.Cpu), ListSortDirection.Descending));
        View.IsLiveSorting = true;
        foreach (var p in new[] { nameof(ProcessRow.Cpu), nameof(ProcessRow.Ram), nameof(ProcessRow.Disk), nameof(ProcessRow.Connections), nameof(ProcessRow.Threads), nameof(ProcessRow.Handles) })
            View.LiveSortingProperties.Add(p);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public ObservableCollection<ProcessRow> Rows { get; } = [];
    public ListCollectionView View { get; }

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private ProcessRow? _selected;
    [ObservableProperty] private ProcessDetails? _details;
    [ObservableProperty] private bool _detailsOpen;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _paused;

    partial void OnSearchChanged(string value) => View.Refresh();

    partial void OnSelectedChanged(ProcessRow? value)
    {
        if (DetailsOpen && value != null) _ = LoadDetailsAsync(value);
    }

    private bool Filter(object o)
    {
        if (string.IsNullOrWhiteSpace(Search)) return true;
        var r = (ProcessRow)o;
        var q = Search.Trim();
        return r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Pid.ToString() == q
            || (r.Path?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (r.User?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        if (parameter is string query) Search = query;
        _ = RefreshAsync();
        _timer.Start();
    }

    public override void OnNavigatedFrom()
    {
        base.OnNavigatedFrom();
        _timer.Stop();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_refreshing || Paused || !WindowVisible) return;
        _refreshing = true;
        try
        {
            if (Rows.Count == 0) IsBusy = true;
            var list = await _processes.GetProcessesAsync();
            var seen = new HashSet<int>();
            {
                foreach (var p in list)
                {
                    seen.Add(p.Pid);
                    if (!_byPid.TryGetValue(p.Pid, out var row) || row.Name != p.Name)
                    {
                        if (row != null) Rows.Remove(row);
                        row = new ProcessRow { Pid = p.Pid, Name = p.Name };
                        _byPid[p.Pid] = row;
                        Rows.Add(row);
                        if (p.Path != null) LoadIcon(row, p.Path);
                    }
                    row.DisplayName = p.Description is { Length: > 0 } d && !d.Equals(p.Name, StringComparison.OrdinalIgnoreCase) ? d : p.Name;
                    row.Path = p.Path;
                    row.User = p.User;
                    row.Company = p.Company;
                    row.Cpu = p.CpuPercent;
                    row.Ram = p.WorkingSet;
                    row.Disk = p.DiskBps;
                    row.Connections = p.TcpConnections;
                    row.Threads = p.Threads;
                    row.Handles = p.Handles;
                    row.IsCritical = p.IsCritical;
                    row.StartTime = p.StartTime;
                    row.ParentPid = p.ParentPid;
                }
                foreach (var gone in _byPid.Keys.Where(k => !seen.Contains(k)).ToList())
                {
                    Rows.Remove(_byPid[gone]);
                    _byPid.Remove(gone);
                }
            }
            Summary = Loc.F("{0} processes · CPU {1} · RAM {2}", list.Count, Fmt.Percent(Math.Min(100, list.Sum(p => p.CpuPercent))), Fmt.Bytes(list.Sum(p => p.WorkingSet)));
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            AppLog.Error("Process refresh failed", ex);
            ErrorMessage = Loc.T("Unable to read the process list");
        }
        finally
        {
            IsBusy = false;
            _refreshing = false;
        }
    }

    private void LoadIcon(ProcessRow row, string path)
    {
        if (IconCache.TryGetValue(path, out var cached)) { row.Icon = cached; return; }
        Task.Run(() =>
        {
            ImageSource? img = null;
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                    src.Freeze();
                    img = src;
                }
            }
            catch { }
            IconCache[path] = img;
            if (img != null) Ui(() => row.Icon = img);
        });
    }

    [RelayCommand]
    private async Task KillAsync(ProcessRow? row) => await KillCoreAsync(row, false);

    [RelayCommand]
    private async Task KillTreeAsync(ProcessRow? row) => await KillCoreAsync(row, true);

    private async Task KillCoreAsync(ProcessRow? row, bool tree)
    {
        row ??= Selected;
        if (row == null) return;
        bool critical = row.IsCritical || _processes.IsCriticalProcess(row.Name, row.Pid);
        var dialog = critical
            ? new ConfirmDialog
            {
                Title = Loc.F("End critical process {0}?", row.Name),
                Message = Loc.T("This is a core Windows process. Ending it can make Windows unstable, log you out, or cause an immediate restart and data loss."),
                Details = $"PID {row.Pid}\n{row.Path}",
                ConfirmText = Loc.T("End process anyway"),
                Kind = DialogKind.Danger,
                Acknowledge = Loc.T("I understand this may crash or restart Windows"),
            }
            : new ConfirmDialog
            {
                Title = Loc.F(tree ? "End {0} and its child processes?" : "End {0}?", row.DisplayName),
                Message = Loc.T("Unsaved data in this application will be lost."),
                Details = $"{row.Name} · PID {row.Pid}\n{row.Path}",
                ConfirmText = Loc.T("End process"),
                Kind = DialogKind.Warning,
            };
        if (!await _dialogs.ConfirmAsync(dialog)) return;
        var result = await _processes.KillAsync(row.Pid, tree);
        _notify.Notify(result.Success ? NotificationLevel.Success : NotificationLevel.Error,
            result.Success ? Loc.T("Process ended") : Loc.T("Unable to end process"), result.Message);
        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenLocation(ProcessRow? row)
    {
        row ??= Selected;
        if (row?.Path is { } p) _processes.OpenFileLocation(p);
        else _notify.Notify(NotificationLevel.Info, Loc.T("Location not available"), Loc.T("Windows does not expose the executable path of this process to non-elevated applications."));
    }

    [RelayCommand]
    private void Copy(string what)
    {
        var row = Selected;
        if (row == null) return;
        var text = what switch
        {
            "name" => row.Name,
            "pid" => row.Pid.ToString(),
            "path" => row.Path ?? "",
            _ => Describe(row),
        };
        try { Clipboard.SetText(text); _notify.Notify(NotificationLevel.Success, Loc.T("Copied to clipboard"), text.Length > 80 ? text[..80] + "…" : text); }
        catch (Exception ex) { AppLog.Warn("Clipboard failed", ex); }
    }

    private static string Describe(ProcessRow r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Name: {r.Name}");
        sb.AppendLine($"Description: {r.DisplayName}");
        sb.AppendLine($"PID: {r.Pid} (parent {r.ParentPid})");
        sb.AppendLine($"CPU: {r.CpuText}");
        sb.AppendLine($"Memory (private working set): {r.RamText}");
        sb.AppendLine($"Disk I/O: {r.DiskText}");
        sb.AppendLine($"TCP connections: {r.Connections}");
        sb.AppendLine($"Threads: {r.Threads}  Handles: {r.Handles}");
        sb.AppendLine($"User: {r.User ?? "Not available"}");
        sb.AppendLine($"Company: {r.Company ?? "Not available"}");
        sb.AppendLine($"Path: {r.Path ?? "Not available"}");
        if (r.StartTime is { } st) sb.AppendLine($"Started: {st:g}");
        return sb.ToString();
    }

    [RelayCommand]
    private async Task ShowDetailsAsync(ProcessRow? row)
    {
        row ??= Selected;
        if (row == null) return;
        Selected = row;
        DetailsOpen = true;
        await LoadDetailsAsync(row);
    }

    private async Task LoadDetailsAsync(ProcessRow row)
    {
        _detailsCts?.Cancel();
        _detailsCts = new CancellationTokenSource();
        var token = _detailsCts.Token;
        try
        {
            var d = await _processes.GetDetailsAsync(row.Pid, token);
            if (!token.IsCancellationRequested) Details = d;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn("Process details failed", ex); }
    }

    [RelayCommand] private void CloseDetails() => DetailsOpen = false;

    [RelayCommand]
    private void AskAi(ProcessRow? row)
    {
        row ??= Selected;
        if (row == null) return;
        WeakReferenceMessenger.Default.Send(new AiAttachmentMessage(Loc.F("Process {0}", row.Name), Describe(row),
            Loc.F("What is {0} and is its resource usage normal?", row.Name)));
        _nav.Navigate(PageKey.Ai);
    }
}
