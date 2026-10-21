namespace PCCommandCenter.Core;

// All nullable members mean "not available on this system" and must be shown as such, never guessed.

public sealed class CpuInfo
{
    public string Name { get; init; } = "Unknown CPU";
    public string Vendor { get; init; } = "";
    public int Cores { get; init; }
    public int LogicalProcessors { get; init; }
    public double BaseClockMhz { get; init; }
    public string? Socket { get; init; }
    public long? L2CacheKb { get; init; }
    public long? L3CacheKb { get; init; }
}

public sealed class CpuSnapshot
{
    public string Name { get; init; } = "";
    public int Cores { get; init; }
    public int LogicalProcessors { get; init; }
    public double Usage { get; init; }
    public double[] PerCore { get; init; } = [];
    public double? FrequencyMhz { get; init; }
    public double BaseClockMhz { get; init; }
    public double? TemperatureC { get; init; }
    public string? TemperatureSource { get; init; }
    public double? PackagePowerW { get; init; }
    /// <summary>Performance the processor can currently deliver, as a percentage of nominal (Windows "% Performance Limit").</summary>
    public double? PerformanceLimitPercent { get; init; }
    public bool? IsThrottled { get; init; }
    public double? QueueLength { get; init; }
    public int ProcessCount { get; init; }
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    public TimeSpan Uptime { get; init; }
}

public sealed class GpuSnapshot
{
    public string Name { get; init; } = "";
    public string Vendor { get; init; } = "";
    public long Luid { get; init; }
    public double? Usage { get; init; }
    public IReadOnlyDictionary<string, double> EngineUsage { get; init; } = new Dictionary<string, double>();
    public long? DedicatedUsed { get; init; }
    public long? DedicatedTotal { get; init; }
    public long? SharedUsed { get; init; }
    public double? TemperatureC { get; init; }
    public double? CoreClockMhz { get; init; }
    public double? MemoryClockMhz { get; init; }
    public double? PowerW { get; init; }
    public double? PowerLimitW { get; init; }
    public double? FanPercent { get; init; }
    public string? ThrottleReasons { get; init; }
    public string? DriverVersion { get; init; }
    /// <summary>Where the extended sensors came from, e.g. "NVIDIA NVML".</summary>
    public string? SensorSource { get; init; }
    public bool IsIntegrated { get; init; }
}

public sealed class MemoryInfo
{
    public int Modules { get; init; }
    public double? SpeedMhz { get; init; }
    public string? Type { get; init; }
    public string? FormFactor { get; init; }
    public long InstalledBytes { get; init; }
}

public sealed class MemorySnapshot
{
    public long Total { get; init; }
    public long Available { get; init; }
    public long Used => Total - Available;
    public double Percent => Total == 0 ? 0 : Used * 100.0 / Total;
    public long CommitUsed { get; init; }
    public long CommitLimit { get; init; }
    public double CommitPercent => CommitLimit == 0 ? 0 : CommitUsed * 100.0 / CommitLimit;
    public long Cached { get; init; }
    public long PagedPool { get; init; }
    public long NonPagedPool { get; init; }
}

public sealed class VolumeInfo
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public string Format { get; init; } = "";
    public string DriveType { get; init; } = "";
    public long Total { get; init; }
    public long Free { get; init; }
    public long Used => Total - Free;
    public double Percent => Total == 0 ? 0 : Used * 100.0 / Total;
    public double FreePercent => Total == 0 ? 0 : Free * 100.0 / Total;
    public bool IsSystem { get; init; }
}

public sealed class DiskActivity
{
    public string Instance { get; init; } = "";
    public double ReadBps { get; init; }
    public double WriteBps { get; init; }
    public double ActivePercent { get; init; }
    public double QueueLength { get; init; }
}

public sealed class DiskSnapshot
{
    public IReadOnlyList<VolumeInfo> Volumes { get; init; } = [];
    public IReadOnlyList<DiskActivity> Physical { get; init; } = [];
    public double TotalReadBps { get; init; }
    public double TotalWriteBps { get; init; }
    public double? MaxActivePercent { get; init; }
}

public sealed class PhysicalDiskInfo
{
    public string DeviceId { get; init; } = "";
    public string Model { get; init; } = "";
    public string MediaType { get; init; } = "";
    public string BusType { get; init; } = "";
    public long Size { get; init; }
    public string HealthStatus { get; init; } = "Not available";
    public string OperationalStatus { get; init; } = "";
    public double? TemperatureC { get; init; }
    public double? WearPercent { get; init; }
    public long? PowerOnHours { get; init; }
    public long? ReadErrors { get; init; }
    public bool? PredictFailure { get; init; }
    /// <summary>Explains why SMART/reliability data could not be read, if applicable.</summary>
    public string? SmartNote { get; init; }
}

public enum ConnectionState { Unknown, Connected, NoInternet, Disconnected }

public sealed class NetworkSnapshot
{
    public string AdapterName { get; init; } = "";
    public string AdapterDescription { get; init; } = "";
    public string AdapterType { get; init; } = "";
    public string? IPv4 { get; init; }
    public string? Gateway { get; init; }
    public long? LinkSpeedBps { get; init; }
    public double RxBps { get; init; }
    public double TxBps { get; init; }
    public long TotalRx { get; init; }
    public long TotalTx { get; init; }
    public ConnectionState State { get; init; }
    public double? LatencyMs { get; init; }
    public double? GatewayLatencyMs { get; init; }
    public double? PacketLossPercent { get; init; }
    public string LatencyTarget { get; init; } = "";
}

public sealed class SystemSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public CpuSnapshot Cpu { get; init; } = new();
    public IReadOnlyList<GpuSnapshot> Gpus { get; init; } = [];
    public MemorySnapshot Memory { get; init; } = new();
    public DiskSnapshot Disk { get; init; } = new();
    public NetworkSnapshot Network { get; init; } = new();

    public GpuSnapshot? PrimaryGpu =>
        Gpus.Where(g => !g.IsIntegrated).OrderByDescending(g => g.DedicatedTotal ?? 0).FirstOrDefault() ?? Gpus.FirstOrDefault();
}

/// <summary>Rolling histories used by the dashboard charts.</summary>
public sealed class MetricHistory
{
    public const int DefaultCapacity = 300;

    public RingBuffer Cpu { get; } = new(DefaultCapacity);
    public RingBuffer CpuTemp { get; } = new(DefaultCapacity);
    public RingBuffer CpuPower { get; } = new(DefaultCapacity);
    public RingBuffer Gpu { get; } = new(DefaultCapacity);
    public RingBuffer GpuTemp { get; } = new(DefaultCapacity);
    public RingBuffer GpuMemory { get; } = new(DefaultCapacity);
    public RingBuffer Memory { get; } = new(DefaultCapacity);
    public RingBuffer DiskRead { get; } = new(DefaultCapacity);
    public RingBuffer DiskWrite { get; } = new(DefaultCapacity);
    public RingBuffer DiskActive { get; } = new(DefaultCapacity);
    public RingBuffer NetRx { get; } = new(DefaultCapacity);
    public RingBuffer NetTx { get; } = new(DefaultCapacity);
    public RingBuffer Latency { get; } = new(DefaultCapacity);
}
