using System.Management;
using PCCommandCenter.Core;
using PCCommandCenter.Hardware.Counters;

namespace PCCommandCenter.Hardware.Storage;

/// <summary>Volume capacity, physical disk throughput and storage reliability (SMART) information.</summary>
internal sealed class DiskSampler
{
    private readonly CategorySampler _physical = new("PhysicalDisk",
        "Disk Read Bytes/sec", "Disk Write Bytes/sec", "% Idle Time", "Current Disk Queue Length");
    private IReadOnlyList<VolumeInfo> _volumes = [];
    private DateTime _volumesAt = DateTime.MinValue;
    private readonly string _systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

    public DiskSnapshot Sample()
    {
        if (DateTime.UtcNow - _volumesAt > TimeSpan.FromSeconds(5))
        {
            _volumes = ReadVolumes(_systemDrive);
            _volumesAt = DateTime.UtcNow;
        }

        var data = _physical.Sample();
        var physical = new List<DiskActivity>();
        double totalRead = 0, totalWrite = 0;
        double? maxActive = null;
        if (data.TryGetValue("Disk Read Bytes/sec", out var reads))
        {
            foreach (var instance in reads.Keys.Where(k => k != "_Total").OrderBy(k => k))
            {
                var r = reads[instance];
                var w = data.Get("Disk Write Bytes/sec", instance) ?? 0;
                var idle = data.Get("% Idle Time", instance);
                var active = idle.HasValue ? Math.Clamp(100 - idle.Value, 0, 100) : 0;
                physical.Add(new DiskActivity
                {
                    Instance = instance,
                    ReadBps = r,
                    WriteBps = w,
                    ActivePercent = active,
                    QueueLength = data.Get("Current Disk Queue Length", instance) ?? 0,
                });
                totalRead += r; totalWrite += w;
                maxActive = Math.Max(maxActive ?? 0, active);
            }
        }

        return new DiskSnapshot
        {
            Volumes = _volumes,
            Physical = physical,
            TotalReadBps = totalRead,
            TotalWriteBps = totalWrite,
            MaxActivePercent = maxActive,
        };
    }

    public static IReadOnlyList<VolumeInfo> ReadVolumes(string systemDrive)
    {
        var list = new List<VolumeInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                // Network drives can block for a long time when the share is unreachable; skip them here.
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
                list.Add(new VolumeInfo
                {
                    Name = d.Name,
                    Label = d.VolumeLabel,
                    Format = d.DriveFormat,
                    DriveType = d.DriveType.ToString(),
                    Total = d.TotalSize,
                    Free = d.TotalFreeSpace,
                    IsSystem = d.Name.Equals(systemDrive, StringComparison.OrdinalIgnoreCase),
                });
            }
            catch { /* drive removed or locked (e.g. BitLocker) */ }
        }
        return list;
    }

    /// <summary>
    /// Physical disk health via the Storage Management API, plus reliability counters and SMART failure
    /// prediction when the process has the required privileges.
    /// </summary>
    public static IReadOnlyList<PhysicalDiskInfo> ReadPhysicalDisks()
    {
        var disks = new List<PhysicalDiskInfo>();
        var predict = ReadSmartPredictions(out var smartError);
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
                "SELECT ObjectId, DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus, OperationalStatus FROM MSFT_PhysicalDisk"));
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    var model = (o["FriendlyName"] as string ?? "Disk").Trim();
                    double? temp = null, wear = null;
                    long? hours = null, readErrors = null;
                    string? note = null;
                    try
                    {
                        using var rel = new ManagementObjectSearcher(scope, new ObjectQuery(
                            $"ASSOCIATORS OF {{MSFT_PhysicalDisk.ObjectId=\"{Escape(o["ObjectId"] as string)}\"}} WHERE ResultClass = MSFT_StorageReliabilityCounter"));
                        foreach (ManagementObject r in rel.Get())
                        {
                            using (r)
                            {
                                if (r["Temperature"] is ushort t && t > 0) temp = t;
                                if (r["Wear"] is byte w) wear = w;
                                if (r["PowerOnHours"] is uint h && h > 0) hours = h;
                                if (r["ReadErrorsUncorrected"] is ulong re) readErrors = (long)re;
                            }
                        }
                    }
                    catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
                    {
                        note = "Reliability counters require administrator privileges";
                    }
                    catch (Exception ex)
                    {
                        note = "Reliability counters not available";
                        AppLog.Debug($"Reliability counters for {model}: {ex.Message}");
                    }

                    bool? failure = null;
                    if (predict != null)
                    {
                        var token = model.Replace(' ', '_');
                        var match = predict.FirstOrDefault(p => p.Instance.Contains(token, StringComparison.OrdinalIgnoreCase)
                                                             || p.Instance.Contains(model.Split(' ').Last(), StringComparison.OrdinalIgnoreCase));
                        if (match.Instance != null) failure = match.Predict;
                    }
                    else if (smartError != null && note == null)
                    {
                        note = smartError;
                    }

                    disks.Add(new PhysicalDiskInfo
                    {
                        DeviceId = o["DeviceId"]?.ToString() ?? "",
                        Model = model,
                        MediaType = Convert.ToInt32(o["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unspecified" },
                        BusType = Convert.ToInt32(o["BusType"] ?? 0) switch
                        {
                            1 => "SCSI", 3 => "ATA", 7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA",
                            12 => "SD", 13 => "MMC", 15 => "File-backed", 16 => "Storage Spaces", 17 => "NVMe", _ => "Other"
                        },
                        Size = Convert.ToInt64(o["Size"] ?? 0L),
                        HealthStatus = Convert.ToInt32(o["HealthStatus"] ?? 5) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" },
                        OperationalStatus = o["OperationalStatus"] is ushort[] ops ? string.Join(", ", ops.Select(DescribeOperational)) : "",
                        TemperatureC = temp,
                        WearPercent = wear,
                        PowerOnHours = hours,
                        ReadErrors = readErrors,
                        PredictFailure = failure,
                        SmartNote = note,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("MSFT_PhysicalDisk query failed", ex);
        }
        return disks.OrderBy(d => int.TryParse(d.DeviceId, out var n) ? n : 99).ToList();
    }

    private static List<(string Instance, bool Predict)>? ReadSmartPredictions(out string? error)
    {
        error = null;
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus");
            var list = new List<(string, bool)>();
            foreach (ManagementObject o in s.Get())
                using (o) list.Add((o["InstanceName"] as string ?? "", o["PredictFailure"] is true));
            return list;
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            error = "SMART data requires administrator privileges";
        }
        catch (Exception)
        {
            error = "SMART data not available";
        }
        return null;
    }

    private static string DescribeOperational(ushort code) => code switch
    {
        2 => "OK", 3 => "Degraded", 5 => "Predictive failure", 6 => "Error", 10 => "Stopped", 11 => "In service",
        13 => "Lost communication", 0xD012 => "Not usable", 0xD013 => "Transient error", 0xD014 => "Failed media",
        _ => code.ToString()
    };

    private static string Escape(string? s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
}
