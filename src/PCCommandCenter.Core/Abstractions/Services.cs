namespace PCCommandCenter.Core;

/// <summary>Real-time hardware telemetry sampled on a background thread.</summary>
public interface IHardwareMonitor : IDisposable
{
    event EventHandler<SystemSnapshot>? SnapshotUpdated;
    SystemSnapshot? Latest { get; }
    MetricHistory History { get; }
    CpuInfo? Cpu { get; }
    MemoryInfo? MemoryModules { get; }
    TimeSpan Interval { get; set; }
    void Start();
    Task<IReadOnlyList<PhysicalDiskInfo>> GetPhysicalDisksAsync(bool forceRefresh = false, CancellationToken ct = default);
}

/// <summary>Background connectivity probe (gateway + internet latency, packet loss).</summary>
public interface IConnectivityMonitor : IDisposable
{
    event EventHandler? Updated;
    ConnectionState State { get; }
    double? InternetLatencyMs { get; }
    double? GatewayLatencyMs { get; }
    double? PacketLossPercent { get; }
    string Target { get; set; }
    TimeSpan Interval { get; set; }
    void Start();
}

public interface INetworkTools
{
    Task<PingSummary> PingAsync(string host, int count, TimeSpan interval, int timeoutMs, IProgress<PingProbe>? progress, CancellationToken ct);
    Task<DnsLookupResult> DnsLookupAsync(string name, IReadOnlyList<string> recordTypes, string? server, CancellationToken ct);
    Task<IReadOnlyList<TraceHop>> TraceRouteAsync(string host, int maxHops, int timeoutMs, IProgress<TraceHop>? progress, CancellationToken ct);
    IReadOnlyList<ArpEntry> GetArpTable();
    IReadOnlyList<NetworkInterfaceInfo> GetInterfaces();
    IReadOnlyList<ConnectionInfo> GetConnections(bool includeUdp);
    IReadOnlyDictionary<int, int> GetTcpConnectionCountsByPid();
    Task<(bool Success, double? ElapsedMs, string? Error)> MeasureDnsResolutionAsync(string host, CancellationToken ct);
}

public interface IProcessService
{
    /// <summary>Returns a fresh process list. CPU and I/O rates are computed against the previous call.</summary>
    Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken ct = default);
    Task<ProcessDetails?> GetDetailsAsync(int pid, CancellationToken ct = default);
    Task<OperationResult> KillAsync(int pid, bool entireTree = false);
    bool IsCriticalProcess(string name, int pid);
    void OpenFileLocation(string path);
}

public interface IStorageAnalyzer
{
    IReadOnlyList<VolumeInfo> GetDrives();
    Task<StorageScanResult> ScanAsync(string root, IProgress<ScanProgress>? progress, CancellationToken ct);
    Task<IReadOnlyList<CleanupTarget>> GetCleanupTargetsAsync(CancellationToken ct);
    Task<OperationResult> CleanAsync(CleanupTarget target, CancellationToken ct);
    OperationResult MoveToRecycleBin(string path);
    void RevealInExplorer(string path);
}

public interface IWindowsToolsService
{
    IReadOnlyList<WindowsTool> Tools { get; }
    OperationResult Launch(WindowsTool tool);
    Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ServiceStatusInfo>> GetImportantServicesAsync(CancellationToken ct = default);
    Task<OperationResult> ControlServiceAsync(string serviceName, ServiceAction action, CancellationToken ct = default);
    Task<IReadOnlyList<EventEntry>> GetRecentEventsAsync(TimeSpan window, int maxEntries, bool includeWarnings, CancellationToken ct = default);
    OperationResult RestartAsAdministrator();
}

public interface IStartupService
{
    Task<IReadOnlyList<StartupEntry>> GetEntriesAsync(CancellationToken ct = default);
    Task<OperationResult> SetEnabledAsync(StartupEntry entry, bool enabled);
}

public interface IShellService
{
    Task<ShellResult> RunAsync(ShellKind shell, string command, string workingDirectory, Action<ShellOutputLine> onOutput, CancellationToken ct);
    CommandAssessment Assess(ShellKind shell, string command);
}

public interface IHealthService
{
    event EventHandler<HealthReport>? ReportUpdated;
    HealthReport? LastReport { get; }
    bool IsScanning { get; }
    Task<HealthReport> RunScanAsync(IProgress<HealthProgress>? progress, CancellationToken ct = default);
}

public interface ILogAnalyzer
{
    IReadOnlyList<string> SupportedExtensions { get; }
    Task<LogAnalysisResult> AnalyzeFileAsync(string path, IProgress<double>? progress, CancellationToken ct);
    Task<LogAnalysisResult> AnalyzeTextAsync(string text, string sourceName, CancellationToken ct);
}

public interface IAiAssistant
{
    bool IsConfigured { get; }
    string ModelName { get; }
    Task<AiResult> StreamReplyAsync(IReadOnlyList<ChatTurn> conversation, string? systemContext, Action<string> onTextDelta, CancellationToken ct);
    Task<OperationResult> TestConnectionAsync(CancellationToken ct);
}

/// <summary>In-app notification sink usable by any module.</summary>
public interface INotificationService
{
    void Notify(NotificationLevel level, string title, string message, string? dedupKey = null, TimeSpan? cooldown = null);
}
