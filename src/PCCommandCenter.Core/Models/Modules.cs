using System.Net;

namespace PCCommandCenter.Core;

// ───────────────────────────── Processes ─────────────────────────────

public sealed class ProcessInfo
{
    public int Pid { get; init; }
    public int ParentPid { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; set; }
    public string? Company { get; set; }
    public string? Path { get; set; }
    public string? User { get; set; }
    public double CpuPercent { get; init; }
    public long WorkingSet { get; init; }
    public long PrivateBytes { get; init; }
    public double IoReadBps { get; init; }
    public double IoWriteBps { get; init; }
    public double DiskBps => IoReadBps + IoWriteBps;
    public int TcpConnections { get; init; }
    public int Threads { get; init; }
    public int Handles { get; init; }
    public int SessionId { get; init; }
    public DateTime? StartTime { get; init; }
    public bool IsCritical { get; init; }
}

public sealed class ProcessDetails
{
    public required ProcessInfo Info { get; init; }
    public string? CommandLine { get; init; }
    public string? FileVersion { get; init; }
    public string? ProductName { get; init; }
    public string? ParentName { get; init; }
    public string? Priority { get; init; }
    public bool? Is64Bit { get; init; }
}

// ───────────────────────────── Network ─────────────────────────────

public sealed record PingProbe(int Sequence, bool Success, double? RoundtripMs, string Status, string? Address, int? Ttl);

public sealed class PingSummary
{
    public string Target { get; init; } = "";
    public string? ResolvedAddress { get; init; }
    public int Sent { get; init; }
    public int Received { get; init; }
    public double LossPercent => Sent == 0 ? 0 : (Sent - Received) * 100.0 / Sent;
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Average { get; init; }
    public double? Jitter { get; init; }
    public string? Error { get; init; }
}

public sealed record DnsRecordInfo(string Type, string Name, string Data, uint Ttl);

public sealed class DnsLookupResult
{
    public string Query { get; init; } = "";
    public string Server { get; init; } = "";
    public double ElapsedMs { get; init; }
    public IReadOnlyList<DnsRecordInfo> Records { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed record TraceHop(int Hop, string? Address, string? HostName, double? RoundtripMs, string Status, bool IsDestination);

public sealed record ArpEntry(string IpAddress, string MacAddress, string Type, string InterfaceName);

public sealed class NetworkInterfaceInfo
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Type { get; init; } = "";
    public string Status { get; init; } = "";
    public bool IsUp { get; init; }
    public string Mac { get; init; } = "";
    public long SpeedBps { get; init; }
    public IReadOnlyList<string> IPv4 { get; init; } = [];
    public IReadOnlyList<string> IPv6 { get; init; } = [];
    public IReadOnlyList<string> Gateways { get; init; } = [];
    public IReadOnlyList<string> DnsServers { get; init; } = [];
    public bool? DhcpEnabled { get; init; }
    public IReadOnlyList<string> DhcpServers { get; init; } = [];
    public string? DnsSuffix { get; init; }
    public long BytesReceived { get; init; }
    public long BytesSent { get; init; }
    public int? Mtu { get; init; }
    public bool IsDefaultRoute { get; init; }
}

public sealed class ConnectionInfo
{
    public string Protocol { get; init; } = "TCP";
    public string LocalAddress { get; init; } = "";
    public int LocalPort { get; init; }
    public string RemoteAddress { get; init; } = "";
    public int RemotePort { get; init; }
    public string State { get; init; } = "";
    public int Pid { get; init; }
    public string ProcessName { get; set; } = "";
}

// ───────────────────────────── Storage ─────────────────────────────

public sealed class FolderNode
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public long Size { get; set; }
    public long FileCount { get; set; }
    public long FolderCount { get; set; }
    /// <summary>Total size of files located directly in this folder.</summary>
    public long OwnFilesSize { get; set; }
    public List<FolderNode> Children { get; } = [];
    public FolderNode? Parent { get; set; }
}

public sealed record FileEntry(string Path, long Size, DateTime Modified);

public sealed record ExtensionStat(string Extension, long Count, long Size);

public sealed record ScanProgress(long Files, long Folders, long Bytes, string CurrentPath);

public sealed class StorageScanResult
{
    public string Root { get; init; } = "";
    public FolderNode RootNode { get; init; } = new();
    public IReadOnlyList<FileEntry> LargestFiles { get; init; } = [];
    public IReadOnlyList<ExtensionStat> Extensions { get; init; } = [];
    public long TotalFiles { get; init; }
    public long TotalFolders { get; init; }
    public long TotalBytes { get; init; }
    public long InaccessibleFolders { get; init; }
    public TimeSpan Elapsed { get; init; }
    public long VolumeTotal { get; init; }
    public long VolumeFree { get; init; }
    public bool Cancelled { get; init; }
}

public sealed class CleanupTarget
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Path { get; init; }
    public long Size { get; init; }
    public long Items { get; init; }
    public bool RequiresAdmin { get; init; }
}

// ───────────────────────────── Windows ─────────────────────────────

public sealed class WindowsTool
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Arguments { get; init; } = "";
}

public sealed class SystemInfo
{
    public string OsName { get; init; } = "";
    public string OsVersion { get; init; } = "";
    public string Build { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string ComputerName { get; init; } = "";
    public string UserName { get; init; } = "";
    public DateTime? InstallDate { get; init; }
    public DateTime BootTime { get; init; }
    public TimeSpan Uptime { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? Motherboard { get; init; }
    public string? Bios { get; init; }
    public bool? PendingReboot { get; init; }
    public string? PendingRebootReason { get; init; }
    public bool IsElevated { get; init; }
}

public sealed class ServiceStatusInfo
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Status { get; init; } = "";
    public string StartType { get; init; } = "";
    public bool IsRunning { get; init; }
    public bool ShouldBeRunning { get; init; }
    public string Purpose { get; init; } = "";
}

public enum ServiceAction { Start, Stop, Restart }

public sealed class EventEntry
{
    public DateTime Time { get; init; }
    public string Level { get; init; } = "";
    public string Log { get; init; } = "";
    public string Source { get; init; } = "";
    public int EventId { get; init; }
    public string Message { get; init; } = "";
}

public sealed class StartupEntry
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string? ExecutablePath { get; init; }
    public string? Publisher { get; init; }
    public string Location { get; init; } = "";
    public string Scope { get; init; } = "";
    public bool Enabled { get; set; }
    public bool CanToggle { get; init; }
    public string? ToggleNote { get; init; }
    public bool FileExists { get; init; }
    /// <summary>Internal identifiers used to toggle the entry.</summary>
    public string ApprovedKeyPath { get; init; } = "";
    public bool ApprovedInLocalMachine { get; init; }
    public string ValueName { get; init; } = "";
}

// ───────────────────────────── Shell ─────────────────────────────

public enum ShellKind { Cmd, PowerShell }

public sealed record ShellOutputLine(string Text, bool IsError);

public sealed class ShellResult
{
    public int? ExitCode { get; init; }
    public TimeSpan Duration { get; init; }
    public bool Cancelled { get; init; }
    public string? StartError { get; init; }
}

public enum CommandRisk { Safe, Modifies, Destructive }

public sealed record CommandAssessment(CommandRisk Risk, string Reason);
