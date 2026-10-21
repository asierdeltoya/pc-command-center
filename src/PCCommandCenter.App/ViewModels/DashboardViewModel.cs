using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCCommandCenter.App.Services;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.ViewModels;

public sealed partial class CoreLoad : ObservableObject
{
    public int Index { get; init; }
    public string Label => $"#{Index}";
    [ObservableProperty] private double _value;
}

public sealed partial class VolumeItem : ObservableObject
{
    public string Name { get; init; } = "";
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _usage = "";
    [ObservableProperty] private string _free = "";
    [ObservableProperty] private bool _isLow;
    public string Format { get; init; } = "";
    public bool IsSystem { get; init; }
}

public sealed class PhysicalDiskItem
{
    public required PhysicalDiskInfo Info { get; init; }
    public string Title => Info.Model;
    public string Subtitle => $"{Info.MediaType} · {Info.BusType} · {Fmt.Bytes(Info.Size)}";
    public string Health => Info.HealthStatus;
    public string Temperature => Fmt.Temp(Info.TemperatureC);
    public string Smart => Info.PredictFailure switch { true => Loc.T("Failure predicted"), false => Loc.T("OK"), null => Loc.T("Not available") };
    public string? Note => Info.SmartNote is { } n ? Loc.T(n) : null;
    public string Extra
    {
        get
        {
            var parts = new List<string>();
            if (Info.PowerOnHours is { } h) parts.Add(Loc.F("{0:N0} h powered on", h));
            if (Info.WearPercent is { } w) parts.Add(Loc.F("wear {0}", Fmt.Percent(w)));
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Live dashboard: all values come from <see cref="IHardwareMonitor"/> snapshots.</summary>
public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly IHardwareMonitor _hardware;
    private readonly IHealthService _health;
    private readonly NavigationService _nav;
    private int _pending;

    public DashboardViewModel(IHardwareMonitor hardware, IHealthService health, NavigationService nav)
    {
        _hardware = hardware;
        _health = health;
        _nav = nav;
        _hardware.SnapshotUpdated += OnSnapshot;
        _health.ReportUpdated += (_, r) => Ui(() => ApplyHealth(r));
        if (_health.LastReport != null) ApplyHealth(_health.LastReport);
    }

    // General
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private int? _healthScore;
    [ObservableProperty] private string _healthLabel = Loc.T("Not scanned yet");
    [ObservableProperty] private string _greeting = "";

    // CPU
    [ObservableProperty] private double _cpuUsage;
    [ObservableProperty] private string _cpuName = "";
    [ObservableProperty] private string _cpuTopology = "";
    [ObservableProperty] private string _cpuFrequency = "";
    [ObservableProperty] private string _cpuBaseClock = "";
    [ObservableProperty] private string _cpuTemperature = "";
    [ObservableProperty] private string _cpuTemperatureSource = "";
    [ObservableProperty] private string _cpuPower = "";
    [ObservableProperty] private string _cpuThrottle = "";
    [ObservableProperty] private HealthStatus _cpuThrottleStatus = HealthStatus.NotAvailable;
    [ObservableProperty] private string _cpuQueue = "";
    [ObservableProperty] private string _cpuCounts = "";
    [ObservableProperty] private string _uptime = "";
    [ObservableProperty] private double[] _cpuHistory = [];
    [ObservableProperty] private double[] _cpuTempHistory = [];
    public ObservableCollection<CoreLoad> Cores { get; } = [];

    // GPU
    [ObservableProperty] private bool _gpuAvailable;
    [ObservableProperty] private double _gpuUsage;
    [ObservableProperty] private string _gpuName = "";
    [ObservableProperty] private string _gpuTemperature = "";
    [ObservableProperty] private string _gpuVram = "";
    [ObservableProperty] private double _gpuVramPercent;
    [ObservableProperty] private string _gpuClock = "";
    [ObservableProperty] private string _gpuPower = "";
    [ObservableProperty] private string _gpuFan = "";
    [ObservableProperty] private string _gpuEngines = "";
    [ObservableProperty] private string _gpuSource = "";
    [ObservableProperty] private string _gpuThrottle = "";
    [ObservableProperty] private string _otherGpus = "";
    [ObservableProperty] private double[] _gpuHistory = [];

    // Memory
    [ObservableProperty] private double _memPercent;
    [ObservableProperty] private string _memUsed = "";
    [ObservableProperty] private string _memTotal = "";
    [ObservableProperty] private string _memAvailable = "";
    [ObservableProperty] private string _memCommit = "";
    [ObservableProperty] private double _memCommitPercent;
    [ObservableProperty] private string _memCached = "";
    [ObservableProperty] private string _memModules = "";
    [ObservableProperty] private double[] _memHistory = [];

    // Disk
    public ObservableCollection<VolumeItem> Volumes { get; } = [];
    public ObservableCollection<PhysicalDiskItem> PhysicalDisks { get; } = [];
    [ObservableProperty] private string _diskRead = "";
    [ObservableProperty] private string _diskWrite = "";
    [ObservableProperty] private double _diskActive;
    [ObservableProperty] private double[] _diskReadHistory = [];
    [ObservableProperty] private double[] _diskWriteHistory = [];
    [ObservableProperty] private bool _loadingDisks;

    // Network
    [ObservableProperty] private ConnectionState _netState;
    [ObservableProperty] private string _netStateText = "";
    [ObservableProperty] private string _adapter = "";
    [ObservableProperty] private string _adapterType = "";
    [ObservableProperty] private string _localIp = "";
    [ObservableProperty] private string _gateway = "";
    [ObservableProperty] private string _linkSpeed = "";
    [ObservableProperty] private string _rx = "";
    [ObservableProperty] private string _tx = "";
    [ObservableProperty] private string _latency = "";
    [ObservableProperty] private string _gatewayLatency = "";
    [ObservableProperty] private string _packetLoss = "";
    [ObservableProperty] private double _latencyGauge;
    [ObservableProperty] private double[] _rxHistory = [];
    [ObservableProperty] private double[] _txHistory = [];
    [ObservableProperty] private double[] _latencyHistory = [];

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        UpdateGreeting();
        if (_hardware.Latest is { } s) Apply(s);
        if (PhysicalDisks.Count == 0) _ = LoadPhysicalDisksAsync(false);
    }

    private void UpdateGreeting()
    {
        var h = DateTime.Now.Hour;
        Greeting = h < 6 ? Loc.T("Good night") : h < 12 ? Loc.T("Good morning") : h < 20 ? Loc.T("Good afternoon") : Loc.T("Good evening");
    }

    private void OnSnapshot(object? sender, SystemSnapshot s)
    {
        if (!IsActive || !WindowVisible) return;
        // Coalesce: never queue more than one pending UI update.
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _pending, 0);
            Apply(s);
        }, DispatcherPriority.Background);
    }

    private void Apply(SystemSnapshot s)
    {
        var hist = _hardware.History;
        HasData = true;

        // CPU
        var cpu = s.Cpu;
        CpuUsage = cpu.Usage;
        CpuName = cpu.Name;
        CpuTopology = Loc.F("{0} cores · {1} threads", cpu.Cores, cpu.LogicalProcessors);
        CpuFrequency = Fmt.Frequency(cpu.FrequencyMhz);
        CpuBaseClock = Fmt.Frequency(cpu.BaseClockMhz);
        CpuTemperature = Fmt.Temp(cpu.TemperatureC);
        CpuTemperatureSource = cpu.TemperatureC.HasValue ? Loc.T("ACPI thermal zone") : Loc.T("Requires a hardware sensor driver");
        CpuPower = Fmt.Watts(cpu.PackagePowerW);
        CpuThrottleStatus = cpu.IsThrottled switch { true => HealthStatus.Warning, false => HealthStatus.Good, null => HealthStatus.NotAvailable };
        CpuThrottle = cpu.IsThrottled switch
        {
            true => Loc.F("Throttled · {0} of nominal", Fmt.Percent(cpu.PerformanceLimitPercent)),
            false => cpu.PerformanceLimitPercent is { } pl ? Loc.F("Not throttled · limit {0}", Fmt.Percent(pl)) : Loc.T("Not throttled"),
            null => Loc.T("Throttling: not available"),
        };
        CpuQueue = Fmt.Number(cpu.QueueLength);
        CpuCounts = Loc.F("{0} processes · {1:N0} threads · {2:N0} handles", cpu.ProcessCount, cpu.ThreadCount, cpu.HandleCount);
        Uptime = Fmt.Duration(cpu.Uptime);
        CpuHistory = hist.Cpu.ToArray();
        CpuTempHistory = hist.CpuTemp.ToArray();
        if (Cores.Count != cpu.PerCore.Length)
        {
            Cores.Clear();
            for (int i = 0; i < cpu.PerCore.Length; i++) Cores.Add(new CoreLoad { Index = i });
        }
        for (int i = 0; i < cpu.PerCore.Length; i++) Cores[i].Value = cpu.PerCore[i];

        // GPU
        var g = s.PrimaryGpu;
        GpuAvailable = g != null;
        if (g != null)
        {
            GpuName = g.Name;
            GpuUsage = g.Usage ?? 0;
            GpuTemperature = Fmt.Temp(g.TemperatureC);
            GpuVram = g.DedicatedTotal.HasValue ? $"{Fmt.Bytes(g.DedicatedUsed)} / {Fmt.Bytes(g.DedicatedTotal)}" : Fmt.Bytes(g.DedicatedUsed);
            GpuVramPercent = g is { DedicatedUsed: { } u, DedicatedTotal: { } t } && t > 0 ? u * 100.0 / t : 0;
            GpuClock = Fmt.Frequency(g.CoreClockMhz);
            GpuPower = g.PowerW.HasValue ? (g.PowerLimitW.HasValue ? $"{Fmt.Watts(g.PowerW)} / {Fmt.Watts(g.PowerLimitW)}" : Fmt.Watts(g.PowerW)) : Fmt.NA;
            GpuFan = Fmt.Percent(g.FanPercent);
            GpuEngines = string.Join("  ·  ", g.EngineUsage.Where(e => e.Value >= 1).OrderByDescending(e => e.Value).Take(4).Select(e => $"{e.Key} {e.Value:0}%"));
            GpuSource = g.SensorSource != null ? Loc.F("Sensors: {0}", g.SensorSource) : Loc.T("Temperature, clock and power require vendor sensor support (available for NVIDIA)");
            GpuThrottle = g.ThrottleReasons is { } tr ? Loc.T(tr) : Fmt.NA;
            OtherGpus = string.Join("  ·  ", s.Gpus.Where(x => x != g).Select(x => $"{x.Name} {Fmt.Percent(x.Usage)}"));
        }
        GpuHistory = hist.Gpu.ToArray();

        // Memory
        var m = s.Memory;
        MemPercent = m.Percent;
        MemUsed = Fmt.Bytes(m.Used);
        MemTotal = Fmt.Bytes(m.Total);
        MemAvailable = Fmt.Bytes(m.Available);
        MemCommit = $"{Fmt.Bytes(m.CommitUsed)} / {Fmt.Bytes(m.CommitLimit)}";
        MemCommitPercent = m.CommitPercent;
        MemCached = Fmt.Bytes(m.Cached);
        MemModules = _hardware.MemoryModules is { Modules: > 0 } mi
            ? $"{mi.Modules} × {mi.Type ?? "RAM"}{(mi.SpeedMhz is { } sp ? $" @ {sp:0} MT/s" : "")}{(mi.FormFactor != null ? " · " + mi.FormFactor : "")}"
            : Fmt.NA;
        MemHistory = hist.Memory.ToArray();

        // Disk
        DiskRead = Fmt.DiskRate(s.Disk.TotalReadBps);
        DiskWrite = Fmt.DiskRate(s.Disk.TotalWriteBps);
        DiskActive = s.Disk.MaxActivePercent ?? 0;
        DiskReadHistory = hist.DiskRead.ToArray();
        DiskWriteHistory = hist.DiskWrite.ToArray();
        SyncVolumes(s.Disk.Volumes);

        // Network
        var n = s.Network;
        NetState = n.State;
        NetStateText = n.State switch
        {
            ConnectionState.Connected => Loc.T("Connected"),
            ConnectionState.NoInternet => Loc.T("No internet"),
            ConnectionState.Disconnected => Loc.T("Disconnected"),
            _ => Loc.T("Checking…"),
        };
        Adapter = string.IsNullOrEmpty(n.AdapterName) ? Fmt.NA : n.AdapterName;
        AdapterType = n.AdapterType;
        LocalIp = n.IPv4 ?? Fmt.NA;
        Gateway = n.Gateway ?? Fmt.NA;
        LinkSpeed = Fmt.LinkSpeed(n.LinkSpeedBps);
        Rx = Fmt.NetRate(n.RxBps);
        Tx = Fmt.NetRate(n.TxBps);
        Latency = Fmt.Ms(n.LatencyMs);
        GatewayLatency = Fmt.Ms(n.GatewayLatencyMs);
        PacketLoss = Fmt.Percent(n.PacketLossPercent);
        LatencyGauge = n.LatencyMs is { } l ? Math.Min(100, l / 2) : 0;
        RxHistory = hist.NetRx.ToArray();
        TxHistory = hist.NetTx.ToArray();
        LatencyHistory = hist.Latency.ToArray();
    }

    private void SyncVolumes(IReadOnlyList<VolumeInfo> volumes)
    {
        if (Volumes.Count != volumes.Count || Volumes.Select(v => v.Name).Except(volumes.Select(v => v.Name)).Any())
        {
            Volumes.Clear();
            foreach (var v in volumes) Volumes.Add(new VolumeItem { Name = v.Name, Format = v.Format, IsSystem = v.IsSystem });
        }
        foreach (var v in volumes)
        {
            var item = Volumes.First(x => x.Name == v.Name);
            item.Label = string.IsNullOrEmpty(v.Label) ? Loc.T("Local disk") : v.Label;
            item.Percent = v.Percent;
            item.Usage = Loc.F("{0} of {1}", Fmt.Bytes(v.Used), Fmt.Bytes(v.Total));
            item.Free = Loc.F("{0} free", Fmt.Bytes(v.Free));
            item.IsLow = v.FreePercent < 10;
        }
    }

    [RelayCommand]
    private Task RefreshDisksAsync() => LoadPhysicalDisksAsync(true);

    private async Task LoadPhysicalDisksAsync(bool force)
    {
        LoadingDisks = true;
        try
        {
            var disks = await _hardware.GetPhysicalDisksAsync(force);
            PhysicalDisks.Clear();
            foreach (var d in disks) PhysicalDisks.Add(new PhysicalDiskItem { Info = d });
        }
        catch (Exception ex) { AppLog.Warn("Physical disk query failed", ex); }
        finally { LoadingDisks = false; }
    }

    private void ApplyHealth(HealthReport r)
    {
        HealthScore = r.Score;
        HealthLabel = Loc.T(r.ScoreLabel);
    }

    [RelayCommand] private void RunScan() => _nav.Navigate(PageKey.Health, "scan");

    [RelayCommand] private void Open(string page) { if (Enum.TryParse<PageKey>(page, out var key)) _nav.Navigate(key); }
}
