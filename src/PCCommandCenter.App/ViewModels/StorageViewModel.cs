using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCCommandCenter.App.Controls;
using PCCommandCenter.App.Services;
using PCCommandCenter.Core;
using PCCommandCenter.Settings;

namespace PCCommandCenter.App.ViewModels;

public sealed class DriveItem
{
    public required VolumeInfo Info { get; init; }
    public string Name => Info.Name.TrimEnd('\\');
    public string Label => string.IsNullOrEmpty(Info.Label) ? Loc.T("Local disk") : Info.Label;
    public double Percent => Info.Percent;
    public string Usage => Loc.F("{0} free of {1}", Fmt.Bytes(Info.Free), Fmt.Bytes(Info.Total));
    public bool IsLow => Info.FreePercent < 10;
}

public sealed class FolderItem
{
    public required FolderNode Node { get; init; }
    public double Fraction { get; init; }
    public string Name => Node.Name;
    public string Size => Fmt.Bytes(Node.Size);
    public string Meta => Loc.F("{0:N0} files · {1:N0} folders", Node.FileCount, Node.FolderCount);
    public string Percent => Fmt.Percent(Fraction * 100, 1);
    public bool HasChildren => Node.Children.Count > 0;
}

public sealed class FileItem
{
    public required FileEntry Entry { get; init; }
    public string Name => System.IO.Path.GetFileName(Entry.Path);
    public string Folder => System.IO.Path.GetDirectoryName(Entry.Path) ?? "";
    public string Size => Fmt.Bytes(Entry.Size);
    public string Modified => Entry.Modified.ToString("d");
}

public sealed class ExtensionItem
{
    public required ExtensionStat Stat { get; init; }
    public required Brush Brush { get; init; }
    public double Fraction { get; init; }
    public string Name => Stat.Extension;
    public string Size => Fmt.Bytes(Stat.Size);
    public string Count => Loc.F("{0:N0} files", Stat.Count);
    public string Percent => Fmt.Percent(Fraction * 100, 1);
}

public sealed class CleanupItem
{
    public required CleanupTarget Target { get; init; }
    public string Name => Target.Name;
    public string Description => Target.Description;
    public string Size => Fmt.Bytes(Target.Size);
    public string Items => Loc.F("{0:N0} items", Target.Items);
    public bool CanClean => Target.Size > 0 && !Target.RequiresAdmin;
}

public sealed partial class StorageViewModel : PageViewModel
{
    private static readonly string[] PaletteKeys = ["Brush.Chart.Cpu", "Brush.Chart.Gpu", "Brush.Chart.Ram", "Brush.Chart.Disk", "Brush.Chart.Rx", "Brush.Chart.Tx", "Brush.Chart.Latency", "Brush.Info", "Brush.TextMuted"];

    private readonly IStorageAnalyzer _storage;
    private readonly DialogService _dialogs;
    private readonly INotificationService _notify;
    private readonly ISettingsService _settings;
    private CancellationTokenSource? _cts;
    private readonly Stopwatch _scanClock = new();
    private readonly DispatcherTimer _clockTimer;

    public StorageViewModel(IStorageAnalyzer storage, DialogService dialogs, INotificationService notify, ISettingsService settings)
    {
        _storage = storage;
        _dialogs = dialogs;
        _notify = notify;
        _settings = settings;
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clockTimer.Tick += (_, _) => ScanElapsed = Fmt.Duration(_scanClock.Elapsed);
    }

    public ObservableCollection<DriveItem> Drives { get; } = [];
    public ObservableCollection<FolderNode> Breadcrumbs { get; } = [];
    public ObservableCollection<FolderItem> Folders { get; } = [];
    public ObservableCollection<FileItem> Files { get; } = [];
    public ObservableCollection<ExtensionItem> Extensions { get; } = [];
    public ObservableCollection<CleanupItem> Cleanup { get; } = [];

    [ObservableProperty] private DriveItem? _selectedDrive;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _progressFiles = "";
    [ObservableProperty] private string _progressBytes = "";
    [ObservableProperty] private string _progressPath = "";
    [ObservableProperty] private string _scanElapsed = "";
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private string _scannedSize = "";
    [ObservableProperty] private string _freeSpace = "";
    [ObservableProperty] private string _usedSpace = "";
    [ObservableProperty] private double _usedPercent;
    [ObservableProperty] private string _fileCount = "";
    [ObservableProperty] private string _inaccessible = "";
    [ObservableProperty] private FolderNode? _currentNode;
    [ObservableProperty] private IReadOnlyList<TreeMapItem> _treeMapItems = [];
    [ObservableProperty] private IReadOnlyList<ChartSegment> _extensionSegments = [];
    [ObservableProperty] private bool _loadingCleanup;
    [ObservableProperty] private string _scanRoot = "";

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        if (Drives.Count == 0) RefreshDrives();
        if (Cleanup.Count == 0) _ = LoadCleanupAsync();
        if (parameter is string drive && Drives.FirstOrDefault(d => d.Info.Name.StartsWith(drive, StringComparison.OrdinalIgnoreCase)) is { } d2)
        {
            SelectedDrive = d2;
            _ = ScanAsync();
        }
    }

    [RelayCommand]
    private void RefreshDrives()
    {
        var current = SelectedDrive?.Info.Name ?? _settings.Current.LastStorageDrive;
        Drives.Clear();
        foreach (var d in _storage.GetDrives()) Drives.Add(new DriveItem { Info = d });
        SelectedDrive = Drives.FirstOrDefault(d => d.Info.Name == current) ?? Drives.FirstOrDefault(d => d.Info.IsSystem) ?? Drives.FirstOrDefault();
    }

    [RelayCommand]
    private Task ScanAsync() => SelectedDrive == null ? Task.CompletedTask : ScanPathAsync(SelectedDrive.Info.Name);

    [RelayCommand]
    private async Task ScanFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Choose a folder to analyze") };
        if (dialog.ShowDialog() == true) await ScanPathAsync(dialog.FolderName);
    }

    private async Task ScanPathAsync(string root)
    {
        if (IsScanning) return;
        _settings.Update(s => s.LastStorageDrive = SelectedDrive?.Info.Name);
        IsScanning = true;
        ScanRoot = root;
        _cts = new CancellationTokenSource();
        _scanClock.Restart();
        _clockTimer.Start();
        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressFiles = Loc.F("{0:N0} files · {1:N0} folders", p.Files, p.Folders);
            ProgressBytes = Fmt.Bytes(p.Bytes);
            ProgressPath = p.CurrentPath;
        });
        try
        {
            var result = await _storage.ScanAsync(root, progress, _cts.Token);
            ApplyResult(result);
            if (!result.Cancelled)
                _notify.Notify(NotificationLevel.Success, Loc.T("Storage analysis completed"),
                    Loc.F("{0} analyzed in {1}", Fmt.Bytes(result.TotalBytes), Fmt.Duration(result.Elapsed)));
        }
        catch (Exception ex)
        {
            AppLog.Error("Storage scan failed", ex);
            _notify.Notify(NotificationLevel.Error, Loc.T("Storage analysis failed"), OperationResult.FriendlyError(ex));
        }
        finally
        {
            IsScanning = false;
            _clockTimer.Stop();
            _scanClock.Stop();
        }
    }

    [RelayCommand] private void CancelScan() => _cts?.Cancel();

    private void ApplyResult(StorageScanResult r)
    {
        HasResult = true;
        ScannedSize = Fmt.Bytes(r.TotalBytes);
        FreeSpace = Fmt.Bytes(r.VolumeFree);
        UsedSpace = Fmt.Bytes(r.VolumeTotal - r.VolumeFree);
        UsedPercent = r.VolumeTotal > 0 ? (r.VolumeTotal - r.VolumeFree) * 100.0 / r.VolumeTotal : 0;
        FileCount = Loc.F("{0:N0} files · {1:N0} folders", r.TotalFiles, r.TotalFolders);
        Inaccessible = r.InaccessibleFolders > 0 ? Loc.F("{0:N0} folders could not be read (access denied)", r.InaccessibleFolders) : "";
        ResultSummary = (r.Cancelled ? Loc.T("Partial result (scan cancelled)") + " · " : "") + Loc.F("Scanned in {0}", Fmt.Duration(r.Elapsed));

        Files.Clear();
        foreach (var f in r.LargestFiles.Take(50)) Files.Add(new FileItem { Entry = f });

        Extensions.Clear();
        var top = r.Extensions.Take(8).ToList();
        long other = r.Extensions.Skip(8).Sum(e => e.Size);
        long total = Math.Max(1, r.TotalBytes);
        var segments = new List<ChartSegment>();
        for (int i = 0; i < top.Count; i++)
        {
            var brush = PaletteBrush(i);
            Extensions.Add(new ExtensionItem { Stat = top[i], Brush = brush, Fraction = top[i].Size / (double)total });
            segments.Add(new ChartSegment { Label = top[i].Extension, Value = top[i].Size, Brush = brush, Detail = Fmt.Bytes(top[i].Size) });
        }
        if (other > 0)
        {
            var brush = PaletteBrush(8);
            var stat = new ExtensionStat(Loc.T("Other"), r.Extensions.Skip(8).Sum(e => e.Count), other);
            Extensions.Add(new ExtensionItem { Stat = stat, Brush = brush, Fraction = other / (double)total });
            segments.Add(new ChartSegment { Label = stat.Extension, Value = other, Brush = brush, Detail = Fmt.Bytes(other) });
        }
        ExtensionSegments = segments;

        Breadcrumbs.Clear();
        ShowNode(r.RootNode);
    }

    private static Brush PaletteBrush(int i) => Application.Current.TryFindResource(PaletteKeys[i % PaletteKeys.Length]) as Brush ?? Brushes.SlateBlue;

    private void ShowNode(FolderNode node)
    {
        CurrentNode = node;
        Breadcrumbs.Clear();
        for (var n = node; n != null; n = n.Parent) Breadcrumbs.Insert(0, n);
        Folders.Clear();
        double total = Math.Max(1, node.Size);
        foreach (var c in node.Children.Take(60)) Folders.Add(new FolderItem { Node = c, Fraction = c.Size / total });
        var items = node.Children.Take(40).Select(c => new TreeMapItem { Label = c.Name, Value = c.Size, Tag = c, Detail = Fmt.Bytes(c.Size) }).ToList();
        if (node.OwnFilesSize > 0) items.Add(new TreeMapItem { Label = Loc.T("(files in this folder)"), Value = node.OwnFilesSize, Detail = Fmt.Bytes(node.OwnFilesSize) });
        TreeMapItems = items;
    }

    [RelayCommand]
    private void OpenNode(object? node)
    {
        var target = node switch { FolderNode n => n, FolderItem fi => fi.Node, _ => null };
        if (target is { Children.Count: > 0 }) ShowNode(target);
    }

    [RelayCommand]
    private void Up()
    {
        if (CurrentNode?.Parent is { } p) ShowNode(p);
    }

    [RelayCommand]
    private void Reveal(object? item)
    {
        var path = item switch { FileItem f => f.Entry.Path, FolderItem fo => fo.Node.FullPath, FolderNode n => n.FullPath, string s => s, _ => null };
        if (path != null) _storage.RevealInExplorer(path);
    }

    [RelayCommand]
    private async Task RecycleAsync(FileItem? file)
    {
        if (file == null) return;
        var ok = await _dialogs.ConfirmAsync(Loc.T("Move file to the Recycle Bin?"),
            Loc.T("The file will be moved to the Recycle Bin. You can restore it from there."),
            Loc.T("Move to Recycle Bin"), DialogKind.Warning, $"{file.Entry.Path}\n{file.Size}");
        if (!ok) return;
        var result = await Task.Run(() => _storage.MoveToRecycleBin(file.Entry.Path));
        _notify.Notify(result.Success ? NotificationLevel.Success : NotificationLevel.Error, result.Success ? Loc.T("File moved") : Loc.T("Unable to move file"), result.Message);
        if (result.Success) Files.Remove(file);
    }

    [RelayCommand]
    private async Task LoadCleanupAsync()
    {
        LoadingCleanup = true;
        try
        {
            var targets = await _storage.GetCleanupTargetsAsync(CancellationToken.None);
            Cleanup.Clear();
            foreach (var t in targets) Cleanup.Add(new CleanupItem { Target = t });
        }
        catch (Exception ex) { AppLog.Warn("Cleanup targets failed", ex); }
        finally { LoadingCleanup = false; }
    }

    [RelayCommand]
    private async Task CleanAsync(CleanupItem? item)
    {
        if (item == null || !item.CanClean) return;
        bool permanent = item.Target.Id == "recycle-bin";
        var ok = await _dialogs.ConfirmAsync(new ConfirmDialog
        {
            Title = Loc.F("Clean {0}?", item.Name),
            Message = permanent
                ? Loc.T("All items in the Recycle Bin will be permanently deleted. This cannot be undone.")
                : Loc.T("Temporary files older than 24 hours will be permanently deleted. Files currently in use are skipped."),
            Details = $"{item.Target.Path ?? item.Name}\n{item.Items} · {item.Size}",
            ConfirmText = Loc.T("Delete permanently"),
            Kind = DialogKind.Danger,
        });
        if (!ok) return;
        LoadingCleanup = true;
        var result = await _storage.CleanAsync(item.Target, CancellationToken.None);
        _notify.Notify(result.Success ? NotificationLevel.Success : NotificationLevel.Error, result.Success ? Loc.T("Cleanup completed") : Loc.T("Cleanup failed"), result.Message);
        await LoadCleanupAsync();
        RefreshDrives();
    }
}
