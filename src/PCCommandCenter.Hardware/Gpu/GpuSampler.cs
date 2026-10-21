using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PCCommandCenter.Core;
using PCCommandCenter.Hardware.Counters;
using PCCommandCenter.Hardware.Native;

namespace PCCommandCenter.Hardware.Gpu;

/// <summary>
/// GPU telemetry from three real sources:
/// DXGI (adapter identity and VRAM size), Windows "GPU Engine"/"GPU Adapter Memory" counters
/// (utilization and VRAM usage for any vendor) and NVML (sensors for NVIDIA GPUs).
/// </summary>
internal sealed partial class GpuSampler : IDisposable
{
    private sealed record Adapter(string Name, string Vendor, string LuidKey, long Luid, long DedicatedBytes, bool Integrated);

    private readonly List<Adapter> _adapters = [];
    private readonly CategorySampler _engines = new("GPU Engine", "Utilization Percentage");
    private readonly CategorySampler _memory = new("GPU Adapter Memory", "Dedicated Usage", "Shared Usage");
    private Nvml? _nvml;
    private bool _nvmlTried;
    private readonly Dictionary<string, (string Key, int Index, string Type)?> _parsed = new(StringComparer.Ordinal);

    [GeneratedRegex(@"luid_0x([0-9a-fA-F]+)_0x([0-9a-fA-F]+)_phys_\d+_eng_(\d+)_engtype_(.*)$")]
    private static partial Regex EngineRegex();

    public GpuSampler()
    {
        EnumerateAdapters();
    }

    private void EnumerateAdapters()
    {
        try
        {
            var iid = typeof(HwNative.IDXGIFactory1).GUID;
            if (HwNative.CreateDXGIFactory1(ref iid, out var factory) != 0 || factory == null) return;
            try
            {
                for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0; i++)
                {
                    try
                    {
                        if (adapter.GetDesc1(out var desc) != 0) continue;
                        if ((desc.Flags & 2) != 0 || desc.VendorId == 0x1414) continue; // software / Basic Render
                        var vendor = desc.VendorId switch { 0x10DE => "NVIDIA", 0x1002 or 0x1022 => "AMD", 0x8086 => "Intel", _ => $"0x{desc.VendorId:X4}" };
                        long luid = ((long)desc.LuidHigh << 32) | desc.LuidLow;
                        var key = $"luid_0x{(uint)desc.LuidHigh:X8}_0x{desc.LuidLow:X8}";
                        if (_adapters.Any(a => a.Luid == luid)) continue;
                        var dedicated = (long)desc.DedicatedVideoMemory;
                        _adapters.Add(new Adapter(desc.Description.Trim(), vendor, key, luid, dedicated, dedicated < 1L << 30));
                    }
                    finally { Marshal.ReleaseComObject(adapter); }
                }
            }
            finally { Marshal.ReleaseComObject(factory); }
            AppLog.Info("GPU adapters: " + string.Join("; ", _adapters.Select(a => $"{a.Name} ({Fmt.Bytes(a.DedicatedBytes)})")));
        }
        catch (Exception ex)
        {
            AppLog.Warn("DXGI adapter enumeration failed", ex);
        }
    }

    public IReadOnlyList<GpuSnapshot> Sample()
    {
        if (!_nvmlTried)
        {
            _nvmlTried = true;
            if (_adapters.Any(a => a.Vendor == "NVIDIA")) _nvml = Nvml.TryCreate();
        }

        // Per adapter, per engine index: sum utilization over all processes; keep the engine type name.
        var engineSums = new Dictionary<string, Dictionary<int, (string Type, double Sum)>>(StringComparer.OrdinalIgnoreCase);
        var engineData = _engines.Sample();
        if (engineData.TryGetValue("Utilization Percentage", out var engines))
        {
            if (_parsed.Count > 8000) _parsed.Clear(); // instance names include PIDs, so the cache slowly grows
            foreach (var (instance, value) in engines)
            {
                if (!_parsed.TryGetValue(instance, out var parsed))
                {
                    var m = EngineRegex().Match(instance);
                    parsed = m.Success ? ($"luid_0x{m.Groups[1].Value}_0x{m.Groups[2].Value}", int.Parse(m.Groups[3].Value), m.Groups[4].Value) : null;
                    _parsed[instance] = parsed;
                }
                if (parsed is not { } p) continue;
                if (!engineSums.TryGetValue(p.Key, out var perEngine)) engineSums[p.Key] = perEngine = [];
                perEngine.TryGetValue(p.Index, out var cur);
                perEngine[p.Index] = (p.Type, cur.Sum + value);
            }
        }

        var memData = _memory.Sample();
        var nvmlReadings = _nvml?.Read() ?? [];
        var nvidiaAdapters = _adapters.Where(a => a.Vendor == "NVIDIA").ToList();

        var result = new List<GpuSnapshot>(_adapters.Count);
        foreach (var a in _adapters)
        {
            double? usage = null;
            var engineUsage = new Dictionary<string, double>();
            var sumKey = engineSums.Keys.FirstOrDefault(k => k.Equals(a.LuidKey, StringComparison.OrdinalIgnoreCase));
            if (sumKey != null)
            {
                foreach (var (_, (type, sum)) in engineSums[sumKey])
                {
                    var v = Math.Min(100, sum);
                    engineUsage[type] = Math.Max(engineUsage.GetValueOrDefault(type), v);
                }
                usage = engineUsage.Count > 0 ? engineUsage.Values.Max() : 0;
            }
            else if (_engines.IsAvailable)
            {
                usage = 0; // adapter exists but no engine is in use
            }

            long? dedicatedUsed = null, sharedUsed = null;
            foreach (var counter in new[] { "Dedicated Usage", "Shared Usage" })
            {
                if (!memData.TryGetValue(counter, out var inst)) continue;
                foreach (var (name, value) in inst)
                {
                    if (!name.StartsWith(a.LuidKey, StringComparison.OrdinalIgnoreCase)) continue;
                    if (counter == "Dedicated Usage") dedicatedUsed = (dedicatedUsed ?? 0) + (long)value;
                    else sharedUsed = (sharedUsed ?? 0) + (long)value;
                }
            }

            Nvml.Reading? nv = null;
            if (a.Vendor == "NVIDIA" && nvmlReadings.Count > 0)
            {
                nv = nvmlReadings.FirstOrDefault(r => r.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase))
                     ?? (nvidiaAdapters.Count == nvmlReadings.Count ? nvmlReadings[nvidiaAdapters.IndexOf(a)] : null);
            }

            result.Add(new GpuSnapshot
            {
                Name = a.Name,
                Vendor = a.Vendor,
                Luid = a.Luid,
                IsIntegrated = a.Integrated,
                Usage = usage ?? nv?.Utilization,
                EngineUsage = engineUsage,
                DedicatedTotal = nv?.MemTotal ?? (a.DedicatedBytes > 0 ? a.DedicatedBytes : null),
                DedicatedUsed = dedicatedUsed ?? nv?.MemUsed,
                SharedUsed = sharedUsed,
                TemperatureC = nv?.Temperature,
                CoreClockMhz = nv?.CoreClock,
                MemoryClockMhz = nv?.MemoryClock,
                PowerW = nv?.PowerW,
                PowerLimitW = nv?.PowerLimitW,
                FanPercent = nv?.FanPercent,
                ThrottleReasons = nv?.ThrottleReasons,
                DriverVersion = nv != null ? _nvml?.DriverVersion : null,
                SensorSource = nv != null ? "NVIDIA NVML" : null,
            });
        }
        return result;
    }

    public void Dispose() => _nvml?.Dispose();
}
