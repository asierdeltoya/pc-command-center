using PCCommandCenter.Core;

namespace PCCommandCenter.Diagnostics;

/// <summary>
/// Aggregates live telemetry into one-minute points and persists them (7 days, ~0.5 MB) to history.dat, so the user can
/// see what happened while they were away. Only covers the time the app was running (window or tray).
/// </summary>
public sealed class MetricsHistory : IMetricsHistory, IDisposable
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private const int RecordSize = 8 + 12 * 4;

    private readonly IHardwareMonitor _hardware;
    private readonly string _path = Path.Combine(AppInfo.DataDirectory, "history.dat");
    private readonly List<MetricPoint> _points = [];
    private readonly object _lock = new();
    private DateTime _minute = DateTime.MinValue;
    private readonly Acc _acc = new();

    private sealed class Acc
    {
        public int N;
        public double Cpu, CpuMax, Ram, Gpu, CpuTemp = double.NaN, GpuTemp = double.NaN, Read, Write, Rx, Tx, Lat, Power;
        public int GpuN, LatN, PowerN;
        public void Reset() { N = GpuN = LatN = PowerN = 0; Cpu = CpuMax = Ram = Gpu = Read = Write = Rx = Tx = Lat = Power = 0; CpuTemp = GpuTemp = double.NaN; }
    }

    public MetricsHistory(IHardwareMonitor hardware)
    {
        _hardware = hardware;
        Load();
        _hardware.SnapshotUpdated += OnSnapshot;
    }

    public event EventHandler? Updated;

    public DateTime? FirstSample { get { lock (_lock) return _points.Count > 0 ? _points[0].Time : null; } }

    public IReadOnlyList<MetricPoint> GetRange(DateTime from, DateTime to)
    {
        lock (_lock) return _points.Where(p => p.Time >= from && p.Time <= to).ToList();
    }

    private static double MaxNaN(double a, double b) => double.IsNaN(a) ? b : double.IsNaN(b) ? a : Math.Max(a, b);

    private void OnSnapshot(object? sender, SystemSnapshot s)
    {
        var minute = new DateTime(s.Timestamp.Year, s.Timestamp.Month, s.Timestamp.Day, s.Timestamp.Hour, s.Timestamp.Minute, 0);
        MetricPoint? completed = null;
        lock (_lock)
        {
            if (_minute != minute)
            {
                if (_acc.N > 0 && _minute != DateTime.MinValue) completed = Build(_minute);
                _acc.Reset();
                _minute = minute;
            }
            _acc.N++;
            _acc.Cpu += s.Cpu.Usage;
            _acc.CpuMax = Math.Max(_acc.CpuMax, s.Cpu.Usage);
            _acc.Ram += s.Memory.Percent;
            if (s.PrimaryGpu?.Usage is { } g) { _acc.Gpu += g; _acc.GpuN++; }
            _acc.CpuTemp = MaxNaN(_acc.CpuTemp, s.Cpu.TemperatureC ?? double.NaN);
            _acc.GpuTemp = MaxNaN(_acc.GpuTemp, s.PrimaryGpu?.TemperatureC ?? double.NaN);
            _acc.Read += s.Disk.TotalReadBps;
            _acc.Write += s.Disk.TotalWriteBps;
            _acc.Rx += s.Network.RxBps;
            _acc.Tx += s.Network.TxBps;
            if (s.Network.LatencyMs is { } l) { _acc.Lat += l; _acc.LatN++; }
            if (s.Cpu.PackagePowerW is { } pw) { _acc.Power += pw; _acc.PowerN++; }
            if (completed != null)
            {
                _points.Add(completed);
                var cutoff = DateTime.Now - Retention;
                int old = _points.FindIndex(p => p.Time >= cutoff);
                if (old > 0) _points.RemoveRange(0, old);
            }
        }
        if (completed != null)
        {
            Append(completed);
            Updated?.Invoke(this, EventArgs.Empty);
        }
    }

    private MetricPoint Build(DateTime minute)
    {
        double n = _acc.N;
        return new MetricPoint(minute, _acc.Cpu / n, _acc.CpuMax, _acc.Ram / n, _acc.GpuN > 0 ? _acc.Gpu / _acc.GpuN : double.NaN,
            _acc.CpuTemp, _acc.GpuTemp, _acc.Read / n, _acc.Write / n, _acc.Rx / n, _acc.Tx / n,
            _acc.LatN > 0 ? _acc.Lat / _acc.LatN : double.NaN, _acc.PowerN > 0 ? _acc.Power / _acc.PowerN : double.NaN);
    }

    // ───────────── Persistence (fixed-size binary records) ─────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var cutoff = DateTime.Now - Retention;
            var bytes = File.ReadAllBytes(_path);
            using var br = new BinaryReader(new MemoryStream(bytes));
            int count = bytes.Length / RecordSize;
            for (int i = 0; i < count; i++)
            {
                var t = new DateTime(br.ReadInt64());
                var v = new float[12];
                for (int j = 0; j < 12; j++) v[j] = br.ReadSingle();
                if (t < cutoff || t > DateTime.Now.AddMinutes(5)) continue;
                _points.Add(new MetricPoint(t, v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11]));
            }
            _points.Sort((a, b) => a.Time.CompareTo(b.Time));
            if (count > _points.Count + 1440) Rewrite(); // compact once old data accumulates
            AppLog.Info($"Metrics history loaded: {_points.Count} points");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Metrics history is unreadable; starting fresh", ex);
            try { File.Delete(_path); } catch { }
        }
    }

    private static void Write(BinaryWriter bw, MetricPoint p)
    {
        bw.Write(p.Time.Ticks);
        foreach (var v in new[] { p.CpuAvg, p.CpuMax, p.RamAvg, p.GpuAvg, p.CpuTempMax, p.GpuTempMax, p.DiskReadAvg, p.DiskWriteAvg, p.NetRxAvg, p.NetTxAvg, p.LatencyAvg, p.CpuPowerAvg })
            bw.Write((float)v);
    }

    private void Append(MetricPoint p)
    {
        try
        {
            using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var bw = new BinaryWriter(fs);
            Write(bw, p);
        }
        catch (Exception ex) { AppLog.Debug("History append failed: " + ex.Message); }
    }

    private void Rewrite()
    {
        try
        {
            var tmp = _path + ".tmp";
            using (var bw = new BinaryWriter(File.Create(tmp)))
                foreach (var p in _points) Write(bw, p);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) { AppLog.Debug("History compaction failed: " + ex.Message); }
    }

    public void Dispose()
    {
        _hardware.SnapshotUpdated -= OnSnapshot;
        lock (_lock)
        {
            if (_acc.N >= 10 && _minute != DateTime.MinValue) { var p = Build(_minute); _points.Add(p); Append(p); }
        }
    }
}
