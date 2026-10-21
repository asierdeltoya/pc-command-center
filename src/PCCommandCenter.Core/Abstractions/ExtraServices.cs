namespace PCCommandCenter.Core;

/// <summary>Curated, explained Windows repairs. Nothing runs without an explicit call from the UI after confirmation.</summary>
public interface IQuickFixService
{
    IReadOnlyList<QuickFix> Fixes { get; }
    Task<FixResult> RunAsync(QuickFix fix, Action<string>? onOutput, CancellationToken ct);
    Task<FixResult> CreateRestorePointAsync(CancellationToken ct);
}

public interface ISecurityService
{
    Task<IReadOnlyList<SecurityCheck>> GetChecksAsync(CancellationToken ct = default);
    Task<OperationResult> StartDefenderQuickScanAsync(CancellationToken ct = default);
}

public interface IInstalledAppsService
{
    Task<IReadOnlyList<InstalledApp>> GetAppsAsync(CancellationToken ct = default);
    OperationResult Uninstall(InstalledApp app);
    Task<AppUpdateResult> GetUpdatesAsync(CancellationToken ct = default);
    OperationResult Upgrade(AppUpdate update);
    OperationResult UpgradeAll();
}

/// <summary>Internet speed test against Cloudflare's public speed-test endpoints.</summary>
public interface IInternetSpeedTest
{
    string Provider { get; }
    Task<InternetSpeedResult> RunAsync(IProgress<SpeedProgress>? progress, CancellationToken ct);
}

/// <summary>Sequential and 4K random read/write benchmark using unbuffered I/O on a temporary file.</summary>
public interface IDiskBenchmark
{
    Task<DiskBenchmarkResult> RunAsync(string drive, long sizeBytes, IProgress<SpeedProgress>? progress, CancellationToken ct);
}

/// <summary>Records one-minute aggregates of the telemetry and keeps 7 days on disk.</summary>
public interface IMetricsHistory
{
    event EventHandler? Updated;
    IReadOnlyList<MetricPoint> GetRange(DateTime from, DateTime to);
    DateTime? FirstSample { get; }
}

public interface IReportService
{
    /// <summary>Generates a self-contained HTML report of the machine and returns its path.</summary>
    Task<string> CreateHtmlReportAsync(string path, CancellationToken ct);
}

public interface IPowerService
{
    Task<IReadOnlyList<PowerPlan>> GetPlansAsync(CancellationToken ct = default);
    Task<OperationResult> SetActivePlanAsync(PowerPlan plan, CancellationToken ct = default);
    Task<BatteryInfo> GetBatteryAsync(CancellationToken ct = default);
    Task<OperationResult> CreateBatteryReportAsync(CancellationToken ct = default);
}
