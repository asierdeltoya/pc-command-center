using System.Runtime.InteropServices;
using System.Text;
using PCCommandCenter.Core;

namespace PCCommandCenter.Hardware.Gpu;

/// <summary>
/// Minimal dynamic binding to NVIDIA's NVML (installed with the GeForce/Studio driver).
/// Provides real temperature, clocks, power and throttle reasons for NVIDIA GPUs without admin rights.
/// </summary>
internal sealed unsafe class Nvml : IDisposable
{
    public sealed class Reading
    {
        public string Name = "";
        public double? Temperature, Utilization, CoreClock, MemoryClock, PowerW, PowerLimitW, FanPercent;
        public long? MemUsed, MemTotal;
        public string? ThrottleReasons;
    }

    private IntPtr _lib;
    private readonly List<IntPtr> _devices = [];

    private delegate* unmanaged<int> _init;
    private delegate* unmanaged<int> _shutdown;
    private delegate* unmanaged<uint*, int> _getCount;
    private delegate* unmanaged<uint, IntPtr*, int> _getHandle;
    private delegate* unmanaged<IntPtr, byte*, uint, int> _getName;
    private delegate* unmanaged<IntPtr, int, uint*, int> _getTemp;
    private delegate* unmanaged<IntPtr, uint*, int> _getUtil;
    private delegate* unmanaged<IntPtr, ulong*, int> _getMem;
    private delegate* unmanaged<IntPtr, int, uint*, int> _getClock;
    private delegate* unmanaged<IntPtr, uint*, int> _getPower;
    private delegate* unmanaged<IntPtr, uint*, int> _getPowerLimit;
    private delegate* unmanaged<IntPtr, uint*, int> _getFan;
    private delegate* unmanaged<IntPtr, ulong*, int> _getThrottle;
    private delegate* unmanaged<byte*, uint, int> _getDriver;

    public string? DriverVersion { get; private set; }

    public static Nvml? TryCreate()
    {
        var nvml = new Nvml();
        return nvml.Initialize() ? nvml : null;
    }

    private bool Initialize()
    {
        string[] candidates =
        [
            Path.Combine(Environment.SystemDirectory, "nvml.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll"),
        ];
        foreach (var path in candidates)
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out _lib)) break;
        }
        if (_lib == IntPtr.Zero) return false;

        try
        {
            _init = (delegate* unmanaged<int>)Export("nvmlInit_v2");
            _shutdown = (delegate* unmanaged<int>)Export("nvmlShutdown");
            _getCount = (delegate* unmanaged<uint*, int>)Export("nvmlDeviceGetCount_v2");
            _getHandle = (delegate* unmanaged<uint, IntPtr*, int>)Export("nvmlDeviceGetHandleByIndex_v2");
            _getName = (delegate* unmanaged<IntPtr, byte*, uint, int>)Export("nvmlDeviceGetName");
            _getTemp = (delegate* unmanaged<IntPtr, int, uint*, int>)Export("nvmlDeviceGetTemperature");
            _getUtil = (delegate* unmanaged<IntPtr, uint*, int>)Export("nvmlDeviceGetUtilizationRates");
            _getMem = (delegate* unmanaged<IntPtr, ulong*, int>)Export("nvmlDeviceGetMemoryInfo");
            _getClock = (delegate* unmanaged<IntPtr, int, uint*, int>)Export("nvmlDeviceGetClockInfo");
            _getPower = (delegate* unmanaged<IntPtr, uint*, int>)Export("nvmlDeviceGetPowerUsage", optional: true);
            _getPowerLimit = (delegate* unmanaged<IntPtr, uint*, int>)Export("nvmlDeviceGetEnforcedPowerLimit", optional: true);
            _getFan = (delegate* unmanaged<IntPtr, uint*, int>)Export("nvmlDeviceGetFanSpeed", optional: true);
            _getThrottle = (delegate* unmanaged<IntPtr, ulong*, int>)(Export("nvmlDeviceGetCurrentClocksEventReasons", optional: true) is var p && p != IntPtr.Zero
                ? p : Export("nvmlDeviceGetCurrentClocksThrottleReasons", optional: true));
            _getDriver = (delegate* unmanaged<byte*, uint, int>)Export("nvmlSystemGetDriverVersion", optional: true);

            if (_init() != 0) { NativeLibrary.Free(_lib); _lib = IntPtr.Zero; return false; }

            uint count;
            if (_getCount(&count) != 0) return false;
            for (uint i = 0; i < count; i++)
            {
                IntPtr handle;
                if (_getHandle(i, &handle) == 0) _devices.Add(handle);
            }
            if (_getDriver != null)
            {
                var buf = stackalloc byte[96];
                if (_getDriver(buf, 96) == 0) DriverVersion = Marshal.PtrToStringAnsi((IntPtr)buf);
            }
            AppLog.Info($"NVML initialized: {_devices.Count} NVIDIA device(s), driver {DriverVersion}");
            return _devices.Count > 0;
        }
        catch (Exception ex)
        {
            AppLog.Warn("NVML is present but could not be initialized", ex);
            return false;
        }
    }

    private IntPtr Export(string name, bool optional = false)
    {
        if (NativeLibrary.TryGetExport(_lib, name, out var p)) return p;
        if (optional) return IntPtr.Zero;
        throw new EntryPointNotFoundException(name);
    }

    public IReadOnlyList<Reading> Read()
    {
        var list = new List<Reading>(_devices.Count);
        var name = stackalloc byte[96];
        var util = stackalloc uint[2];
        var mem = stackalloc ulong[3];
        foreach (var d in _devices)
        {
            var r = new Reading();
            if (_getName(d, name, 96) == 0) r.Name = Marshal.PtrToStringAnsi((IntPtr)name) ?? "";
            uint v;
            if (_getTemp(d, 0, &v) == 0) r.Temperature = v;
            if (_getUtil(d, util) == 0) r.Utilization = util[0];
            if (_getMem(d, mem) == 0) { r.MemTotal = (long)mem[0]; r.MemUsed = (long)mem[2]; }
            if (_getClock(d, 0, &v) == 0) r.CoreClock = v;
            if (_getClock(d, 2, &v) == 0) r.MemoryClock = v;
            if (_getPower != null && _getPower(d, &v) == 0) r.PowerW = v / 1000.0;
            if (_getPowerLimit != null && _getPowerLimit(d, &v) == 0) r.PowerLimitW = v / 1000.0;
            if (_getFan != null && _getFan(d, &v) == 0) r.FanPercent = v;
            ulong reasons;
            if (_getThrottle != null && _getThrottle(d, &reasons) == 0) r.ThrottleReasons = DescribeThrottle(reasons);
            list.Add(r);
        }
        return list;
    }

    private static string DescribeThrottle(ulong mask)
    {
        if ((mask & ~0x1UL) == 0) return "None";
        var sb = new StringBuilder();
        void Add(ulong bit, string text) { if ((mask & bit) != 0) sb.Append(sb.Length > 0 ? ", " : "").Append(text); }
        Add(0x2, "App clocks");
        Add(0x4, "Power cap");
        Add(0x8, "HW slowdown");
        Add(0x10, "Sync boost");
        Add(0x20, "SW thermal");
        Add(0x40, "HW thermal");
        Add(0x80, "Power brake");
        Add(0x100, "Display clock");
        return sb.Length == 0 ? "None" : sb.ToString();
    }

    public void Dispose()
    {
        if (_lib == IntPtr.Zero) return;
        try { if (_shutdown != null) _shutdown(); } catch { }
        NativeLibrary.Free(_lib);
        _lib = IntPtr.Zero;
    }
}
