using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using PCCommandCenter.Core;
using PCCommandCenter.Hardware.Cpu;
using PCCommandCenter.Hardware.Gpu;
using PCCommandCenter.Hardware.Native;
using PCCommandCenter.Hardware.Net;
using PCCommandCenter.Hardware.Storage;

namespace PCCommandCenter.Hardware;

/// <summary>
/// Samples all hardware telemetry on a dedicated low-priority background thread and publishes
/// immutable <see cref="SystemSnapshot"/> objects. Each sub-sampler fails independently.
/// </summary>
public sealed class HardwareMonitor : IHardwareMonitor
{
    private readonly IConnectivityMonitor? _connectivity;
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private CpuSampler? _cpu;
    private GpuSampler? _gpu;
    private DiskSampler? _disk;
    private NetSampler? _net;
    private TimeSpan _interval = TimeSpan.FromSeconds(1);
    private IReadOnlyList<PhysicalDiskInfo>? _physicalDisks;
    private DateTime _physicalDisksAt;
    private readonly SemaphoreSlim _physicalLock = new(1, 1);
    private bool _gpuFailed;

    public HardwareMonitor(IConnectivityMonitor? connectivity = null)
    {
        _connectivity = connectivity;
    }

    public event EventHandler<SystemSnapshot>? SnapshotUpdated;

    public SystemSnapshot? Latest { get; private set; }

    public MetricHistory History { get; } = new();

    public CpuInfo? Cpu => _cpu?.Info;

    public MemoryInfo? MemoryModules { get; private set; }

    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            _interval = TimeSpan.FromMilliseconds(Math.Clamp(value.TotalMilliseconds, 250, 10_000));
            _wake.Set();
        }
    }

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Hardware monitor", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void Run()
    {
        var sw = new Stopwatch();
        try
        {
            // Initialization runs here so the UI thread never waits on WMI/DXGI.
            _cpu = Safe(() => new CpuSampler(), "CPU sampler");
            _gpu = Safe(() => new GpuSampler(), "GPU sampler");
            _disk = new DiskSampler();
            _net = new NetSampler();
            MemoryModules = Safe(ReadMemoryModules, "Memory module info");
        }
        catch (Exception ex)
        {
            AppLog.Error("Hardware monitor initialization failed", ex);
        }

        while (!_cts.IsCancellationRequested)
        {
            sw.Restart();
            try
            {
                var snapshot = TakeSnapshot();
                Latest = snapshot;
                AppendHistory(snapshot);
                SnapshotUpdated?.Invoke(this, snapshot);
            }
            catch (Exception ex)
            {
                AppLog.Error("Telemetry sample failed", ex);
            }
            var remaining = _interval - sw.Elapsed;
            if (remaining < TimeSpan.FromMilliseconds(50)) remaining = TimeSpan.FromMilliseconds(50);
            WaitHandle.WaitAny([_wake, _cts.Token.WaitHandle], remaining);
        }
    }

    private SystemSnapshot TakeSnapshot()
    {
        CpuSnapshot cpu = new();
        try { if (_cpu != null) cpu = _cpu.Sample(); } catch (Exception ex) { AppLog.Debug("CPU sample: " + ex.Message); }

        IReadOnlyList<GpuSnapshot> gpus = [];
        if (_gpu != null && !_gpuFailed)
        {
            try { gpus = _gpu.Sample(); }
            catch (Exception ex) { _gpuFailed = true; AppLog.Error("Unable to retrieve GPU telemetry", ex); }
        }

        DiskSnapshot disk = new();
        try { if (_disk != null) disk = _disk.Sample(); } catch (Exception ex) { AppLog.Debug("Disk sample: " + ex.Message); }

        NetworkSnapshot net = new();
        try { if (_net != null) net = _net.Sample(_connectivity); } catch (Exception ex) { AppLog.Debug("Network sample: " + ex.Message); }

        return new SystemSnapshot
        {
            Timestamp = DateTime.Now,
            Cpu = cpu,
            Gpus = gpus,
            Memory = SampleMemory(),
            Disk = disk,
            Network = net,
        };
    }

    private void AppendHistory(SystemSnapshot s)
    {
        History.Cpu.Add(s.Cpu.Usage);
        History.CpuTemp.Add(s.Cpu.TemperatureC ?? double.NaN);
        History.CpuPower.Add(s.Cpu.PackagePowerW ?? double.NaN);
        var gpu = s.PrimaryGpu;
        History.Gpu.Add(gpu?.Usage ?? double.NaN);
        History.GpuTemp.Add(gpu?.TemperatureC ?? double.NaN);
        History.GpuMemory.Add(gpu is { DedicatedUsed: { } u, DedicatedTotal: { } t } && t > 0 ? u * 100.0 / t : double.NaN);
        History.Memory.Add(s.Memory.Percent);
        History.DiskRead.Add(s.Disk.TotalReadBps);
        History.DiskWrite.Add(s.Disk.TotalWriteBps);
        History.DiskActive.Add(s.Disk.MaxActivePercent ?? double.NaN);
        History.NetRx.Add(s.Network.RxBps);
        History.NetTx.Add(s.Network.TxBps);
        History.Latency.Add(s.Network.LatencyMs ?? double.NaN);
    }

    private static MemorySnapshot SampleMemory()
    {
        var status = new HwNative.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<HwNative.MEMORYSTATUSEX>() };
        if (!HwNative.GlobalMemoryStatusEx(ref status)) return new MemorySnapshot();
        long commitUsed = 0, commitLimit = 0, cache = 0, paged = 0, nonPaged = 0;
        if (HwNative.GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<HwNative.PERFORMANCE_INFORMATION>()))
        {
            long page = (long)pi.PageSize;
            commitUsed = (long)pi.CommitTotal * page;
            commitLimit = (long)pi.CommitLimit * page;
            cache = (long)pi.SystemCache * page;
            paged = (long)pi.KernelPaged * page;
            nonPaged = (long)pi.KernelNonpaged * page;
        }
        return new MemorySnapshot
        {
            Total = (long)status.ullTotalPhys,
            Available = (long)status.ullAvailPhys,
            CommitUsed = commitUsed,
            CommitLimit = commitLimit,
            Cached = cache,
            PagedPool = paged,
            NonPagedPool = nonPaged,
        };
    }

    private static MemoryInfo ReadMemoryModules()
    {
        int modules = 0;
        long installed = 0;
        double? speed = null;
        string? type = null, form = null;
        using var searcher = new ManagementObjectSearcher("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, FormFactor FROM Win32_PhysicalMemory");
        foreach (var o in searcher.Get())
        {
            using (o)
            {
                modules++;
                installed += Convert.ToInt64(o["Capacity"] ?? 0L);
                var s = Convert.ToDouble(o["ConfiguredClockSpeed"] ?? o["Speed"] ?? 0);
                if (s > 0) speed = Math.Max(speed ?? 0, s);
                type ??= Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0) switch
                {
                    20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 35 => "LPDDR5", 30 => "LPDDR4", _ => null
                };
                form ??= Convert.ToInt32(o["FormFactor"] ?? 0) switch { 8 => "DIMM", 12 => "SO-DIMM", _ => null };
            }
        }
        return new MemoryInfo { Modules = modules, InstalledBytes = installed, SpeedMhz = speed, Type = type, FormFactor = form };
    }

    public async Task<IReadOnlyList<PhysicalDiskInfo>> GetPhysicalDisksAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        await _physicalLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _physicalDisks != null && DateTime.UtcNow - _physicalDisksAt < TimeSpan.FromMinutes(2))
                return _physicalDisks;
            _physicalDisks = await Task.Run(DiskSampler.ReadPhysicalDisks, ct).ConfigureAwait(false);
            _physicalDisksAt = DateTime.UtcNow;
            return _physicalDisks;
        }
        finally { _physicalLock.Release(); }
    }

    private static T? Safe<T>(Func<T> factory, string what) where T : class
    {
        try { return factory(); }
        catch (Exception ex) { AppLog.Error($"{what} unavailable", ex); return null; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _gpu?.Dispose();
        _wake.Dispose();
    }
}
