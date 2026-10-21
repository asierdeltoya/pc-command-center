namespace PCCommandCenter.Core;

// ───────────────────────────── Quick fixes ─────────────────────────────

/// <summary>Safe: harmless repair. Moderate: changes configuration. ReadOnly: diagnostic that only reads and reports.</summary>
public enum FixRisk { Safe, Moderate, ReadOnly }

/// <summary>A well-known Windows repair, described in plain language.</summary>
public sealed class QuickFix
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Typical symptoms the fix addresses ("No internet", "Icons look wrong"…).</summary>
    public string Symptoms { get; init; } = "";
    public string Category { get; init; } = "";
    public string Glyph { get; init; } = "";
    public bool RequiresAdmin { get; init; }
    public bool RestartRecommended { get; init; }
    public bool LongRunning { get; init; }
    public FixRisk Risk { get; init; }
    /// <summary>The exact commands that will run, shown to the user before confirming.</summary>
    public string CommandPreview { get; init; } = "";
}

public sealed class FixResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string Output { get; init; } = "";
    /// <summary>True when the fix was launched in a separate elevated console whose result is shown there.</summary>
    public bool RunsExternally { get; init; }
}

// ───────────────────────────── Security ─────────────────────────────

public sealed class SecurityCheck
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public HealthStatus Status { get; init; }
    public string Value { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Recommendation { get; init; } = "";
    public string Glyph { get; init; } = "";
    /// <summary>Optional URI/command that opens the relevant Windows settings page.</summary>
    public string? OpenTarget { get; init; }
}

// ───────────────────────────── Installed apps ─────────────────────────────

public sealed class InstalledApp
{
    public string Name { get; init; } = "";
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public DateTime? InstallDate { get; init; }
    public long? SizeBytes { get; init; }
    public string? InstallLocation { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? IconPath { get; init; }
    public string Scope { get; init; } = "";
    public string RegistryKey { get; init; } = "";
    public bool IsMsi { get; init; }
}

public sealed record AppUpdate(string Name, string Id, string Version, string Available, string Source);

public sealed class AppUpdateResult
{
    public bool WingetAvailable { get; init; }
    public IReadOnlyList<AppUpdate> Updates { get; init; } = [];
    public string? Error { get; init; }
}

// ───────────────────────────── Speed tests ─────────────────────────────

public sealed record SpeedProgress(string Phase, double Fraction, double? CurrentMbps);

public sealed class InternetSpeedResult
{
    public double? DownloadMbps { get; init; }
    public double? UploadMbps { get; init; }
    public double? LatencyMs { get; init; }
    public double? JitterMs { get; init; }
    public double? LoadedLatencyMs { get; init; }
    public long BytesDownloaded { get; init; }
    public long BytesUploaded { get; init; }
    public string Server { get; init; } = "";
    public string? Colo { get; init; }
    public string? Error { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
}

public sealed class DiskBenchmarkResult
{
    public string Drive { get; init; } = "";
    public double? SeqReadMBps { get; init; }
    public double? SeqWriteMBps { get; init; }
    public double? RandReadIops { get; init; }
    public double? RandReadMBps { get; init; }
    public double? RandWriteIops { get; init; }
    public double? RandWriteMBps { get; init; }
    public long TestSizeBytes { get; init; }
    public string? Error { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
}

// ───────────────────────────── History ─────────────────────────────

/// <summary>One-minute aggregate of the live telemetry. NaN = not measured during that minute.</summary>
public sealed record MetricPoint(
    DateTime Time, double CpuAvg, double CpuMax, double RamAvg, double GpuAvg,
    double CpuTempMax, double GpuTempMax, double DiskReadAvg, double DiskWriteAvg,
    double NetRxAvg, double NetTxAvg, double LatencyAvg, double CpuPowerAvg);

// ───────────────────────────── Power ─────────────────────────────

public sealed record PowerPlan(Guid Id, string Name, bool IsActive);

public sealed class BatteryInfo
{
    public bool Present { get; init; }
    public int? ChargePercent { get; init; }
    public string Status { get; init; } = "";
    public TimeSpan? EstimatedRuntime { get; init; }
    public long? DesignCapacityMWh { get; init; }
    public long? FullChargeCapacityMWh { get; init; }
    public double? HealthPercent => DesignCapacityMWh is > 0 && FullChargeCapacityMWh is > 0
        ? Math.Min(100, FullChargeCapacityMWh.Value * 100.0 / DesignCapacityMWh.Value) : null;
    public string? Chemistry { get; init; }
    public string? Name { get; init; }
    public bool? OnAcPower { get; init; }
}
