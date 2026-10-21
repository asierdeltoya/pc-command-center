using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.ServiceProcess;
using Microsoft.Win32;
using PCCommandCenter.Core;

namespace PCCommandCenter.WindowsTools;

public sealed class WindowsToolsService : IWindowsToolsService
{
    public IReadOnlyList<WindowsTool> Tools { get; } =
    [
        new() { Id = "devmgmt", Name = "Device Manager", Description = "Hardware devices and drivers", Glyph = "", FileName = "devmgmt.msc" },
        new() { Id = "services", Name = "Services", Description = "Start, stop and configure Windows services", Glyph = "", FileName = "services.msc" },
        new() { Id = "eventvwr", Name = "Event Viewer", Description = "System, application and security logs", Glyph = "", FileName = "eventvwr.msc" },
        new() { Id = "taskschd", Name = "Task Scheduler", Description = "Scheduled and triggered tasks", Glyph = "", FileName = "taskschd.msc" },
        new() { Id = "startup", Name = "Startup Apps", Description = "Windows Settings startup page", Glyph = "", FileName = "ms-settings:startupapps" },
        new() { Id = "envvars", Name = "Environment Variables", Description = "User and system variables", Glyph = "", FileName = "rundll32.exe", Arguments = "sysdm.cpl,EditEnvironmentVariables" },
        new() { Id = "winupdate", Name = "Windows Update", Description = "Check for and install updates", Glyph = "", FileName = "ms-settings:windowsupdate" },
        new() { Id = "msinfo", Name = "System Information", Description = "Detailed hardware and software report", Glyph = "", FileName = "msinfo32.exe" },
        new() { Id = "netsettings", Name = "Network Settings", Description = "Adapters, Wi-Fi, VPN and proxy", Glyph = "", FileName = "ms-settings:network" },
        new() { Id = "diskmgmt", Name = "Disk Management", Description = "Partitions, volumes and drive letters", Glyph = "", FileName = "diskmgmt.msc" },
        new() { Id = "resmon", Name = "Resource Monitor", Description = "Live CPU, disk, network and memory detail", Glyph = "", FileName = "resmon.exe" },
        new() { Id = "perfmon", Name = "Performance Monitor", Description = "Counters, data collector sets and reports", Glyph = "", FileName = "perfmon.msc" },
        new() { Id = "reliability", Name = "Reliability Monitor", Description = "Stability history and crash reports", Glyph = "", FileName = "perfmon.exe", Arguments = "/rel" },
        new() { Id = "taskmgr", Name = "Task Manager", Description = "Windows Task Manager", Glyph = "", FileName = "taskmgr.exe" },
        new() { Id = "control", Name = "Control Panel", Description = "Classic control panel", Glyph = "", FileName = "control.exe" },
        new() { Id = "firewall", Name = "Windows Firewall", Description = "Firewall with advanced security", Glyph = "", FileName = "wf.msc" },
    ];

    public OperationResult Launch(WindowsTool tool)
    {
        try
        {
            Process.Start(new ProcessStartInfo(tool.FileName, tool.Arguments) { UseShellExecute = true });
            AppLog.Info($"Launched Windows tool {tool.Id}");
            return OperationResult.Ok(Loc.F("{0} opened", Loc.T(tool.Name)));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return OperationResult.Fail(Loc.T("The elevation prompt was cancelled."));
        }
        catch (Exception ex)
        {
            return OperationResult.FromException("Launch " + tool.Id, ex);
        }
    }

    public Task<SystemInfo> GetSystemInfoAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        string os = "Windows", version = "", build = "";
        DateTime? installDate = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            os = key?.GetValue("ProductName") as string ?? os;
            version = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string ?? "";
            var currentBuild = key?.GetValue("CurrentBuild") as string ?? "";
            var ubr = key?.GetValue("UBR");
            build = ubr != null ? $"{currentBuild}.{ubr}" : currentBuild;
            // ProductName still says "Windows 10" on Windows 11; the build number is authoritative.
            if (int.TryParse(currentBuild, out var b) && b >= 22000) os = os.Replace("Windows 10", "Windows 11");
            if (key?.GetValue("InstallDate") is int unix) installDate = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
        }
        catch (Exception ex) { AppLog.Warn("Reading OS version failed", ex); }

        string? manufacturer = null, model = null, board = null, bios = null;
        try
        {
            using (var s = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                foreach (var o in s.Get()) using (o) { manufacturer = (o["Manufacturer"] as string)?.Trim(); model = (o["Model"] as string)?.Trim(); }
            using (var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
                foreach (var o in s.Get()) using (o) board = $"{(o["Manufacturer"] as string)?.Trim()} {(o["Product"] as string)?.Trim()}".Trim();
            using (var s = new ManagementObjectSearcher("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
                foreach (var o in s.Get()) using (o)
                {
                    var date = o["ReleaseDate"] is string rd && rd.Length >= 8 ? $" ({rd[..4]}-{rd[4..6]}-{rd[6..8]})" : "";
                    bios = $"{(o["Manufacturer"] as string)?.Trim()} {(o["SMBIOSBIOSVersion"] as string)?.Trim()}{date}".Trim();
                }
        }
        catch (Exception ex) { AppLog.Warn("WMI system info query failed", ex); }

        var (pending, reason) = CheckPendingReboot();
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return new SystemInfo
        {
            OsName = os,
            OsVersion = version,
            Build = build,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            ComputerName = Environment.MachineName,
            UserName = Environment.UserName,
            InstallDate = installDate,
            BootTime = DateTime.Now - uptime,
            Uptime = uptime,
            Manufacturer = manufacturer,
            Model = model,
            Motherboard = board,
            Bios = bios,
            PendingReboot = pending,
            PendingRebootReason = reason,
            IsElevated = AppInfo.IsElevated,
        };
    }, ct);

    private static (bool? Pending, string? Reason) CheckPendingReboot()
    {
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                if (k != null) return (true, "Windows Update");
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                if (k != null) return (true, "Component servicing");
            using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
                if (k?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 }) return (true, "Pending file operations");
            return (false, null);
        }
        catch { return (null, null); }
    }

    /// <summary>Services whose state matters for a healthy, connected and secure system.</summary>
    private static readonly (string Name, string Purpose)[] ImportantServices =
    [
        ("Dnscache", "DNS name resolution cache"),
        ("Dhcp", "Automatic IP configuration"),
        ("NlaSvc", "Network location awareness"),
        ("nsi", "Network store interface"),
        ("LanmanWorkstation", "File and printer sharing client"),
        ("EventLog", "Windows event logging"),
        ("Winmgmt", "Windows Management Instrumentation"),
        ("RpcSs", "Remote procedure call"),
        ("Schedule", "Task Scheduler"),
        ("mpssvc", "Windows Defender Firewall"),
        ("WinDefend", "Microsoft Defender Antivirus"),
        ("wuauserv", "Windows Update"),
        ("BITS", "Background transfers (updates)"),
        ("CryptSvc", "Cryptographic services"),
        ("Audiosrv", "Windows Audio"),
        ("Spooler", "Print spooler"),
        ("W32Time", "Time synchronization"),
        ("Themes", "Visual themes"),
    ];

    public Task<IReadOnlyList<ServiceStatusInfo>> GetImportantServicesAsync(CancellationToken ct = default) => Task.Run<IReadOnlyList<ServiceStatusInfo>>(() =>
    {
        var list = new List<ServiceStatusInfo>();
        foreach (var (name, purpose) in ImportantServices)
        {
            try
            {
                using var sc = new ServiceController(name);
                var status = sc.Status;
                var start = sc.StartType;
                list.Add(new ServiceStatusInfo
                {
                    Name = name,
                    DisplayName = sc.DisplayName,
                    Status = status.ToString(),
                    StartType = start.ToString(),
                    IsRunning = status == ServiceControllerStatus.Running,
                    ShouldBeRunning = start == ServiceStartMode.Automatic,
                    Purpose = purpose,
                });
            }
            catch (InvalidOperationException)
            {
                // Service not installed on this edition (e.g. WinDefend replaced by third-party AV).
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Service {name}: {ex.Message}");
            }
        }
        return list;
    }, ct);

    public Task<OperationResult> ControlServiceAsync(string serviceName, ServiceAction action, CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            var timeout = TimeSpan.FromSeconds(30);
            if (action is ServiceAction.Stop or ServiceAction.Restart && sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            }
            if (action is ServiceAction.Start or ServiceAction.Restart)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
            }
            AppLog.Info($"Service {serviceName}: {action} by user");
            return OperationResult.Ok(Loc.F("{0}: {1} completed", sc.DisplayName, Loc.T(action.ToString())));
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return OperationResult.Fail(Loc.T("The service did not respond in time."));
        }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 5 })
        {
            return OperationResult.Fail(Loc.T("Changing services requires administrator privileges. Restart PC Command Center as administrator."));
        }
        catch (Exception ex)
        {
            return OperationResult.FromException($"Service {serviceName} {action}", ex);
        }
    }, ct);

    public Task<IReadOnlyList<EventEntry>> GetRecentEventsAsync(TimeSpan window, int maxEntries, bool includeWarnings, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<EventEntry>>(() =>
        {
            var list = new List<EventEntry>();
            var levels = includeWarnings ? "Level=1 or Level=2 or Level=3" : "Level=1 or Level=2";
            var query = $"*[System[({levels}) and TimeCreated[timediff(@SystemTime) <= {(long)window.TotalMilliseconds}]]]";
            foreach (var log in new[] { "System", "Application" })
            {
                try
                {
                    using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, query) { ReverseDirection = true });
                    for (int i = 0; i < maxEntries; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        using var record = reader.ReadEvent();
                        if (record == null) break;
                        string message;
                        try { message = record.FormatDescription() ?? ""; } catch { message = ""; }
                        if (string.IsNullOrWhiteSpace(message)) message = Loc.F("Event {0} from {1} (no description available)", record.Id, record.ProviderName);
                        list.Add(new EventEntry
                        {
                            Time = record.TimeCreated ?? DateTime.MinValue,
                            Level = record.Level switch { 1 => "Critical", 2 => "Error", 3 => "Warning", _ => "Information" },
                            Log = log,
                            Source = record.ProviderName ?? "",
                            EventId = record.Id,
                            Message = message.Trim(),
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppLog.Warn($"Reading the {log} event log failed", ex);
                }
            }
            return list.OrderByDescending(e => e.Time).Take(maxEntries).ToList();
        }, ct);

    public OperationResult RestartAsAdministrator()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppInfo.ExecutablePath, "--elevated-restart") { UseShellExecute = true, Verb = "runas" });
            return OperationResult.Ok();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return OperationResult.Fail(Loc.T("The elevation prompt was cancelled."));
        }
        catch (Exception ex)
        {
            return OperationResult.FromException("Restart elevated", ex);
        }
    }
}
