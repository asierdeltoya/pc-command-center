using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCCommandCenter.App.Services;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.ViewModels;

public sealed record RangeOption(string Key, string Label, TimeSpan Span)
{
    public override string ToString() => Label;
}

/// <summary>Charts of the one-minute history (1 h / 6 h / 24 h / 7 days) with peaks and averages.</summary>
public sealed partial class HistoryViewModel : PageViewModel
{
    private readonly IMetricsHistory _history;

    public HistoryViewModel(IMetricsHistory history)
    {
        _history = history;
        Ranges =
        [
            new("1h", Loc.T("Last hour"), TimeSpan.FromHours(1)),
            new("6h", Loc.T("Last 6 hours"), TimeSpan.FromHours(6)),
            new("24h", Loc.T("Last 24 hours"), TimeSpan.FromHours(24)),
            new("7d", Loc.T("Last 7 days"), TimeSpan.FromDays(7)),
        ];
        _range = Ranges[2];
        _history.Updated += (_, _) => { if (IsActive && WindowVisible) Ui(Refresh); };
    }

    public IReadOnlyList<RangeOption> Ranges { get; }

    [ObservableProperty] private RangeOption _range;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _coverage = "";
    [ObservableProperty] private DateTime[] _times = [];
    [ObservableProperty] private double[] _cpu = [];
    [ObservableProperty] private double[] _cpuMax = [];
    [ObservableProperty] private double[] _ram = [];
    [ObservableProperty] private double[] _gpu = [];
    [ObservableProperty] private double[] _cpuTemp = [];
    [ObservableProperty] private double[] _gpuTemp = [];
    [ObservableProperty] private double[] _diskRead = [];
    [ObservableProperty] private double[] _diskWrite = [];
    [ObservableProperty] private double[] _netRx = [];
    [ObservableProperty] private double[] _netTx = [];
    [ObservableProperty] private double[] _latency = [];
    [ObservableProperty] private bool _hasTemps;
    [ObservableProperty] private string _cpuStats = "";
    [ObservableProperty] private string _ramStats = "";
    [ObservableProperty] private string _gpuStats = "";
    [ObservableProperty] private string _tempStats = "";
    [ObservableProperty] private string _diskStats = "";
    [ObservableProperty] private string _netStats = "";
    [ObservableProperty] private string _latencyStats = "";
    [ObservableProperty] private string _rangeStart = "";
    [ObservableProperty] private string _rangeEnd = "";

    partial void OnRangeChanged(RangeOption value) => Refresh();

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        Refresh();
    }

    [RelayCommand] private void SetRange(string key) => Range = Ranges.First(r => r.Key == key);

    private void Refresh()
    {
        var to = DateTime.Now;
        var from = to - Range.Span;
        var points = _history.GetRange(from, to).ToList();
        // Long ranges are down-sampled so charts stay readable and fast (~360 points max).
        int bucket = Math.Max(1, points.Count / 360);
        var view = bucket == 1 ? points : points.Chunk(bucket).Select(Merge).ToList();

        HasData = view.Count >= 2;
        var first = _history.FirstSample;
        Coverage = first == null
            ? Loc.T("History is recorded while PC Command Center is running (also when minimized to the tray). Come back in a few minutes.")
            : Loc.F("Recording since {0:g}. One point per minute, kept for 7 days. Only time when the app was running is covered.", first);
        RangeStart = view.Count > 0 ? view[0].Time.ToString(Range.Span.TotalHours > 24 ? "g" : "t") : "";
        RangeEnd = view.Count > 0 ? view[^1].Time.ToString(Range.Span.TotalHours > 24 ? "g" : "t") : "";

        Times = view.Select(p => p.Time).ToArray();
        Cpu = view.Select(p => p.CpuAvg).ToArray();
        CpuMax = view.Select(p => p.CpuMax).ToArray();
        Ram = view.Select(p => p.RamAvg).ToArray();
        Gpu = view.Select(p => p.GpuAvg).ToArray();
        CpuTemp = view.Select(p => p.CpuTempMax).ToArray();
        GpuTemp = view.Select(p => p.GpuTempMax).ToArray();
        DiskRead = view.Select(p => p.DiskReadAvg).ToArray();
        DiskWrite = view.Select(p => p.DiskWriteAvg).ToArray();
        NetRx = view.Select(p => p.NetRxAvg).ToArray();
        NetTx = view.Select(p => p.NetTxAvg).ToArray();
        Latency = view.Select(p => p.LatencyAvg).ToArray();
        HasTemps = CpuTemp.Concat(GpuTemp).Any(v => !double.IsNaN(v));

        if (points.Count == 0) { CpuStats = RamStats = GpuStats = TempStats = DiskStats = NetStats = LatencyStats = ""; return; }
        string Peak(Func<MetricPoint, double> f, Func<double, string> fmt)
        {
            var valid = points.Where(p => !double.IsNaN(f(p))).ToList();
            if (valid.Count == 0) return Fmt.NA;
            var max = valid.MaxBy(f)!;
            return Loc.F("avg {0} · peak {1} at {2:g}", fmt(valid.Average(f)), fmt(f(max)), max.Time);
        }
        CpuStats = Peak(p => p.CpuMax, v => Fmt.Percent(v));
        RamStats = Peak(p => p.RamAvg, v => Fmt.Percent(v));
        GpuStats = Peak(p => p.GpuAvg, v => Fmt.Percent(v));
        TempStats = $"CPU {Peak(p => p.CpuTempMax, v => Fmt.Temp(v))}  ·  GPU {Peak(p => p.GpuTempMax, v => Fmt.Temp(v))}";
        DiskStats = $"R {Peak(p => p.DiskReadAvg, v => Fmt.DiskRate(v))}  ·  W {Peak(p => p.DiskWriteAvg, v => Fmt.DiskRate(v))}";
        NetStats = $"↓ {Peak(p => p.NetRxAvg, v => Fmt.NetRate(v))}  ·  ↑ {Peak(p => p.NetTxAvg, v => Fmt.NetRate(v))}";
        LatencyStats = Peak(p => p.LatencyAvg, v => Fmt.Ms(v));
    }

    private static MetricPoint Merge(MetricPoint[] chunk)
    {
        double Avg(Func<MetricPoint, double> f) { var v = chunk.Select(f).Where(x => !double.IsNaN(x)).ToList(); return v.Count > 0 ? v.Average() : double.NaN; }
        double Max(Func<MetricPoint, double> f) { var v = chunk.Select(f).Where(x => !double.IsNaN(x)).ToList(); return v.Count > 0 ? v.Max() : double.NaN; }
        return new MetricPoint(chunk[0].Time, Avg(p => p.CpuAvg), Max(p => p.CpuMax), Avg(p => p.RamAvg), Avg(p => p.GpuAvg), Max(p => p.CpuTempMax), Max(p => p.GpuTempMax),
            Avg(p => p.DiskReadAvg), Avg(p => p.DiskWriteAvg), Avg(p => p.NetRxAvg), Avg(p => p.NetTxAvg), Avg(p => p.LatencyAvg), Avg(p => p.CpuPowerAvg));
    }
}
