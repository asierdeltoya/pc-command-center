using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PCCommandCenter.App.Services;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.ViewModels;

public sealed record SizeOption(long Bytes, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class SpeedTestViewModel : PageViewModel
{
    private readonly IInternetSpeedTest _internet;
    private readonly IDiskBenchmark _disk;
    private readonly IStorageAnalyzer _storage;
    private readonly DialogService _dialogs;
    private readonly INotificationService _notify;
    private readonly NavigationService _nav;
    private CancellationTokenSource? _cts;
    private readonly List<double> _live = [];

    public SpeedTestViewModel(IInternetSpeedTest internet, IDiskBenchmark disk, IStorageAnalyzer storage, DialogService dialogs, INotificationService notify, NavigationService nav)
    {
        _internet = internet;
        _disk = disk;
        _storage = storage;
        _dialogs = dialogs;
        _notify = notify;
        _nav = nav;
    }

    public string Provider => _internet.Provider;
    public ObservableCollection<VolumeInfo> Drives { get; } = [];
    public static IReadOnlyList<SizeOption> Sizes { get; } = [new(256L << 20, "256 MB"), new(1L << 30, "1 GB"), new(4L << 30, "4 GB")];

    // Internet
    [ObservableProperty] private bool _netRunning;
    [ObservableProperty] private string _netPhase = "";
    [ObservableProperty] private double _netProgress;
    [ObservableProperty] private double _gaugeValue;
    [ObservableProperty] private string _liveSpeed = "";
    [ObservableProperty] private double[] _liveHistory = [];
    [ObservableProperty] private InternetSpeedResult? _netResult;
    [ObservableProperty] private string _download = "—";
    [ObservableProperty] private string _upload = "—";
    [ObservableProperty] private string _latency = "—";
    [ObservableProperty] private string _jitter = "—";
    [ObservableProperty] private string _loadedLatency = "—";
    [ObservableProperty] private string _netVerdict = "";
    [ObservableProperty] private string _netServer = "";

    // Disk
    [ObservableProperty] private VolumeInfo? _selectedDrive;
    [ObservableProperty] private SizeOption _selectedSize = Sizes[1];
    [ObservableProperty] private bool _diskRunning;
    [ObservableProperty] private string _diskPhase = "";
    [ObservableProperty] private double _diskProgress;
    [ObservableProperty] private DiskBenchmarkResult? _diskResult;
    [ObservableProperty] private string _seqRead = "—";
    [ObservableProperty] private string _seqWrite = "—";
    [ObservableProperty] private string _rndRead = "—";
    [ObservableProperty] private string _rndWrite = "—";
    [ObservableProperty] private string _diskVerdict = "";
    [ObservableProperty] private double _seqReadBar;
    [ObservableProperty] private double _seqWriteBar;
    [ObservableProperty] private double _rndReadBar;
    [ObservableProperty] private double _rndWriteBar;

    public bool AnyRunning => NetRunning || DiskRunning;
    partial void OnNetRunningChanged(bool value) => OnPropertyChanged(nameof(AnyRunning));
    partial void OnDiskRunningChanged(bool value) => OnPropertyChanged(nameof(AnyRunning));

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        if (Drives.Count == 0)
        {
            foreach (var d in _storage.GetDrives()) Drives.Add(d);
            SelectedDrive = Drives.FirstOrDefault(d => d.IsSystem) ?? Drives.FirstOrDefault();
        }
        if (parameter is "internet") _ = RunInternetAsync();
    }

    [RelayCommand]
    private async Task RunInternetAsync()
    {
        if (AnyRunning) return;
        if (!await _dialogs.ConfirmAsync(Loc.T("Run an internet speed test?"),
                Loc.F("The test downloads and uploads data to {0} for about 20 seconds. On very fast connections it can use 1–2 GB.", Provider),
                Loc.T("Start test"), DialogKind.Info, Loc.T("Close downloads and video calls first for an accurate result."))) return;
        NetRunning = true;
        NetResult = null;
        Download = Upload = Latency = Jitter = LoadedLatency = "—";
        NetVerdict = "";
        _live.Clear();
        LiveHistory = [];
        _cts = new CancellationTokenSource();
        var progress = new Progress<SpeedProgress>(p =>
        {
            NetPhase = p.Phase;
            NetProgress = p.Fraction;
            if (p.CurrentMbps is { } mbps)
            {
                LiveSpeed = $"{mbps:0.0} Mbps";
                GaugeValue = Math.Min(100, Math.Log10(Math.Max(1, mbps)) / 3 * 100); // log scale up to 1 Gbps
                _live.Add(mbps);
                LiveHistory = _live.TakeLast(120).ToArray();
            }
        });
        try
        {
            var r = await _internet.RunAsync(progress, _cts.Token);
            NetResult = r;
            if (r.Error != null)
            {
                NetVerdict = r.Error;
                _notify.Notify(NotificationLevel.Error, Loc.T("Speed test failed"), r.Error);
                return;
            }
            Download = r.DownloadMbps is { } d ? $"{d:0.0}" : "—";
            Upload = r.UploadMbps is { } u ? $"{u:0.0}" : "—";
            Latency = Fmt.Ms(r.LatencyMs);
            Jitter = Fmt.Ms(r.JitterMs);
            LoadedLatency = Fmt.Ms(r.LoadedLatencyMs);
            NetServer = r.Colo != null ? $"{r.Server} · {r.Colo}" : r.Server;
            NetVerdict = Verdict(r);
            GaugeValue = r.DownloadMbps is { } dd ? Math.Min(100, Math.Log10(Math.Max(1, dd)) / 3 * 100) : 0;
            LiveSpeed = "";
            _notify.Notify(NotificationLevel.Success, Loc.T("Speed test completed"), $"↓ {Download} Mbps · ↑ {Upload} Mbps · {Latency}");
        }
        catch (OperationCanceledException) { NetVerdict = Loc.T("Test stopped"); }
        finally { NetRunning = false; NetPhase = ""; }
    }

    private static string Verdict(InternetSpeedResult r)
    {
        var parts = new List<string>();
        var d = r.DownloadMbps ?? 0;
        parts.Add(d >= 300 ? Loc.T("Excellent: enough for several 4K streams, large downloads and cloud gaming at the same time.")
                : d >= 100 ? Loc.T("Very good: 4K streaming and fast downloads on several devices.")
                : d >= 25 ? Loc.T("Good: HD/4K streaming and video calls work well.")
                : d >= 10 ? Loc.T("Basic: HD streaming on one device; large downloads are slow.")
                : Loc.T("Slow: video calls and streaming may stutter."));
        if (r.LoadedLatencyMs is { } loaded && r.LatencyMs is { } idle && loaded - idle > 100)
            parts.Add(Loc.F("Latency rises by {0} under load (bufferbloat): games and calls may lag while someone downloads. Enabling SQM/QoS on the router helps.", Fmt.Ms(loaded - idle)));
        if (r.JitterMs is > 30) parts.Add(Loc.T("High jitter: the connection is unstable, which affects calls and games."));
        return string.Join(" ", parts);
    }

    [RelayCommand]
    private async Task RunDiskAsync()
    {
        if (AnyRunning || SelectedDrive == null) return;
        if (!await _dialogs.ConfirmAsync(Loc.F("Benchmark drive {0}?", SelectedDrive.Name),
                Loc.F("A temporary {0} file is written and read on the drive, then deleted. It takes about 30 seconds and adds wear equivalent to copying that file.", SelectedSize.Label),
                Loc.T("Start benchmark"), DialogKind.Info, Loc.T("Close programs that use the drive heavily for an accurate result."))) return;
        DiskRunning = true;
        DiskResult = null;
        SeqRead = SeqWrite = RndRead = RndWrite = "—";
        SeqReadBar = SeqWriteBar = RndReadBar = RndWriteBar = 0;
        DiskVerdict = "";
        _cts = new CancellationTokenSource();
        var progress = new Progress<SpeedProgress>(p => { DiskPhase = p.Phase; DiskProgress = p.Fraction; });
        try
        {
            var r = await _disk.RunAsync(SelectedDrive.Name, SelectedSize.Bytes, progress, _cts.Token);
            DiskResult = r;
            if (r.Error != null)
            {
                DiskVerdict = r.Error;
                _notify.Notify(NotificationLevel.Error, Loc.T("Benchmark failed"), r.Error);
                return;
            }
            SeqRead = $"{r.SeqReadMBps:0}";
            SeqWrite = $"{r.SeqWriteMBps:0}";
            RndRead = $"{r.RandReadMBps:0.0}";
            RndWrite = $"{r.RandWriteMBps:0.0}";
            // Bars on a log scale: 10 MB/s … 10 GB/s for sequential, 0.1 … 200 MB/s for 4K.
            SeqReadBar = Scale(r.SeqReadMBps, 10, 10000);
            SeqWriteBar = Scale(r.SeqWriteMBps, 10, 10000);
            RndReadBar = Scale(r.RandReadMBps, 0.1, 200);
            RndWriteBar = Scale(r.RandWriteMBps, 0.1, 200);
            var seq = r.SeqReadMBps ?? 0;
            DiskVerdict = seq >= 2500 ? Loc.T("NVMe SSD class performance: excellent for games, editing and large projects.")
                        : seq >= 1000 ? Loc.T("Fast NVMe SSD performance.")
                        : seq >= 350 ? Loc.T("SATA SSD class performance: Windows and apps load quickly.")
                        : seq >= 80 ? Loc.T("Hard-drive class performance: fine for storage and media, slow for Windows and games.")
                        : Loc.T("Very slow: the drive may be failing, nearly full, or connected through a slow port.");
            _notify.Notify(NotificationLevel.Success, Loc.T("Benchmark completed"), Loc.F("{0} read {1} MB/s · write {2} MB/s", r.Drive, SeqRead, SeqWrite));
        }
        catch (OperationCanceledException) { DiskVerdict = Loc.T("Test stopped"); }
        finally { DiskRunning = false; DiskPhase = ""; }
    }

    private static double Scale(double? value, double min, double max) =>
        value is { } v && v > 0 ? Math.Clamp((Math.Log10(v) - Math.Log10(min)) / (Math.Log10(max) - Math.Log10(min)) * 100, 2, 100) : 0;

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void AskAi()
    {
        var lines = new List<string>();
        if (NetResult is { Error: null } n)
            lines.Add($"Internet ({n.Server} {n.Colo}): download {n.DownloadMbps:0.0} Mbps, upload {n.UploadMbps:0.0} Mbps, idle latency {n.LatencyMs:0.0} ms, jitter {n.JitterMs:0.0} ms, latency under load {n.LoadedLatencyMs:0.0} ms");
        if (DiskResult is { Error: null } d)
            lines.Add($"Disk {d.Drive} ({Fmt.Bytes(d.TestSizeBytes)} test): SEQ read {d.SeqReadMBps:0} MB/s, SEQ write {d.SeqWriteMBps:0} MB/s, RND4K Q1T1 read {d.RandReadIops:0} IOPS ({d.RandReadMBps:0.0} MB/s), write {d.RandWriteIops:0} IOPS ({d.RandWriteMBps:0.0} MB/s)");
        if (lines.Count == 0) return;
        WeakReferenceMessenger.Default.Send(new AiAttachmentMessage(Loc.T("Speed test results"), string.Join("\n", lines), Loc.T("Are these speed results normal for my hardware and connection?")));
        _nav.Navigate(PageKey.Ai);
    }
}
