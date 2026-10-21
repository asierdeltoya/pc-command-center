using Microsoft.Win32;
using PCCommandCenter.Core;
using PCCommandCenter.Hardware.Counters;
using PCCommandCenter.Hardware.Native;

namespace PCCommandCenter.Hardware.Cpu;

/// <summary>CPU utilization, clocks, limits, thermal zones and RAPL package power from Windows counters.</summary>
internal sealed class CpuSampler
{
    private readonly CategorySampler _processor = new("Processor Information",
        "% Processor Utility", "% Processor Time", "Processor Frequency", "% Processor Performance",
        "Actual Frequency", "% Performance Limit");
    // A single counter is far cheaper than reading the whole (large) "System" category every tick.
    private readonly System.Diagnostics.PerformanceCounter? _queue = TryCounter("System", "Processor Queue Length");

    private static System.Diagnostics.PerformanceCounter? TryCounter(string category, string counter)
    {
        try { return new System.Diagnostics.PerformanceCounter(category, counter, readOnly: true); }
        catch (Exception ex) { AppLog.Info($"Counter {category}\\{counter} unavailable: {ex.Message}"); return null; }
    }
    private readonly CategorySampler _thermal = new("Thermal Zone Information", "High Precision Temperature", "Temperature", "% Passive Limit");
    private readonly CategorySampler _energy = new("Energy Meter", "Power");

    public CpuInfo Info { get; }

    public CpuSampler()
    {
        Info = ReadStaticInfo();
    }

    public CpuSnapshot Sample()
    {
        var p = _processor.Sample();
        var utilCounter = p.ContainsKey("% Processor Utility") && p["% Processor Utility"].Count > 0 ? "% Processor Utility" : "% Processor Time";

        double usage = Math.Clamp(p.Get(utilCounter, "_Total") ?? 0, 0, 100);

        double[] perCore = [];
        if (p.TryGetValue(utilCounter, out var instances))
        {
            perCore = instances
                .Where(kv => !kv.Key.Contains("_Total", StringComparison.OrdinalIgnoreCase))
                .Select(kv => (Key: ParseInstance(kv.Key), Value: Math.Clamp(kv.Value, 0, 100)))
                .OrderBy(x => x.Key.Group).ThenBy(x => x.Key.Index)
                .Select(x => x.Value)
                .ToArray();
        }

        double? freq = p.Get("Actual Frequency", "_Total");
        if (freq is null or <= 0)
        {
            var nominal = p.Get("Processor Frequency", "_Total");
            var perf = p.Get("% Processor Performance", "_Total");
            if (nominal is > 0 && perf is > 0) freq = nominal * perf / 100.0;
        }

        double? limit = p.Get("% Performance Limit", "_Total");

        // Thermal zones (ACPI). Values are Kelvin (or tenths of Kelvin for the high precision counter).
        var t = _thermal.Sample();
        double? temp = null;
        double? passiveLimit = null;
        if (t.TryGetValue("High Precision Temperature", out var hp) && hp.Count > 0)
            temp = hp.Values.Select(v => v / 10.0 - 273.15).Where(c => c is > 1 and < 125).DefaultIfEmpty(double.NaN).Max();
        else if (t.TryGetValue("Temperature", out var lp) && lp.Count > 0)
            temp = lp.Values.Select(v => v - 273.15).Where(c => c is > 1 and < 125).DefaultIfEmpty(double.NaN).Max();
        if (temp is double d && double.IsNaN(d)) temp = null;
        if (t.TryGetValue("% Passive Limit", out var pl) && pl.Count > 0) passiveLimit = pl.Values.Min();

        // Package power from the RAPL energy meter (milliwatts).
        double? power = null;
        var e = _energy.Sample();
        if (e.TryGetValue("Power", out var meters))
        {
            var pkg = meters.FirstOrDefault(kv => kv.Key.Contains("PKG", StringComparison.OrdinalIgnoreCase));
            if (pkg.Key != null && pkg.Value > 0) power = pkg.Value / 1000.0;
        }

        // "% Performance Limit" is the performance the CPU can currently guarantee as a share of nominal;
        // power-plan idle states keep it slightly below 100, so only a clear drop counts as throttling.
        // "% Passive Limit" below 100 on a thermal zone means ACPI passive (thermal) cooling is active.
        bool? throttled = null;
        if (limit.HasValue || passiveLimit.HasValue)
            throttled = (limit ?? 100) < 85 || (passiveLimit ?? 100) < 100;

        double? queue = null;
        try { if (_queue != null) queue = _queue.NextValue(); } catch { }

        int processes = 0, threads = 0, handles = 0;
        if (HwNative.GetPerformanceInfo(out var pi, (uint)System.Runtime.InteropServices.Marshal.SizeOf<HwNative.PERFORMANCE_INFORMATION>()))
        {
            processes = (int)pi.ProcessCount; threads = (int)pi.ThreadCount; handles = (int)pi.HandleCount;
        }

        return new CpuSnapshot
        {
            Name = Info.Name,
            Cores = Info.Cores,
            LogicalProcessors = Info.LogicalProcessors,
            Usage = usage,
            PerCore = perCore,
            FrequencyMhz = freq,
            BaseClockMhz = Info.BaseClockMhz,
            TemperatureC = temp,
            TemperatureSource = temp.HasValue ? "ACPI thermal zone" : null,
            PackagePowerW = power,
            PerformanceLimitPercent = limit,
            IsThrottled = throttled,
            QueueLength = queue,
            ProcessCount = processes,
            ThreadCount = threads,
            HandleCount = handles,
            Uptime = TimeSpan.FromMilliseconds(HwNative.GetTickCount64()),
        };
    }

    private static (int Group, int Index) ParseInstance(string name)
    {
        var parts = name.Split(',');
        if (parts.Length == 2 && int.TryParse(parts[0], out var g) && int.TryParse(parts[1], out var i)) return (g, i);
        return int.TryParse(name, out var only) ? (0, only) : (int.MaxValue, 0);
    }

    private static CpuInfo ReadStaticInfo()
    {
        string name = "Unknown CPU", vendor = "";
        double mhz = 0;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            name = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? name;
            vendor = (key?.GetValue("VendorIdentifier") as string)?.Trim() ?? "";
            if (key?.GetValue("~MHz") is int m) mhz = m;
        }
        catch (Exception ex) { AppLog.Warn("Unable to read CPU name from registry", ex); }

        int cores = 0, logical = Environment.ProcessorCount;
        string? socket = null;
        long? l2 = null, l3 = null;
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, SocketDesignation, L2CacheSize, L3CacheSize FROM Win32_Processor");
            int lp = 0;
            foreach (var o in searcher.Get())
            {
                using (o)
                {
                    cores += Convert.ToInt32(o["NumberOfCores"] ?? 0);
                    lp += Convert.ToInt32(o["NumberOfLogicalProcessors"] ?? 0);
                    if (o["MaxClockSpeed"] is uint max && max > 0) mhz = max;
                    socket ??= o["SocketDesignation"] as string;
                    if (o["L2CacheSize"] is uint l2v) l2 = (l2 ?? 0) + l2v;
                    if (o["L3CacheSize"] is uint l3v) l3 = (l3 ?? 0) + l3v;
                }
            }
            if (lp > 0) logical = lp;
        }
        catch (Exception ex) { AppLog.Warn("WMI Win32_Processor query failed", ex); }

        return new CpuInfo
        {
            Name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " "),
            Vendor = vendor,
            Cores = cores > 0 ? cores : logical,
            LogicalProcessors = logical,
            BaseClockMhz = mhz,
            Socket = socket,
            L2CacheKb = l2,
            L3CacheKb = l3,
        };
    }
}
