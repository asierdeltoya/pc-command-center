using System.Windows;
using System.Windows.Media;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.Views;

/// <summary>Quick-status popup shown when the tray icon is clicked.</summary>
public partial class TrayFlyout : Window
{
    public sealed record Row(string Label, string Value, double Percent, Brush Brush);

    private readonly IHardwareMonitor _hardware;
    private readonly IHealthService _health;
    private bool _closing;

    public event Action? OpenRequested;
    public event Action? DiagnosticRequested;
    public event Action? ExitRequested;

    public TrayFlyout(IHardwareMonitor hardware, IHealthService health)
    {
        InitializeComponent();
        _hardware = hardware;
        _health = health;
        Update(hardware.Latest);
        hardware.SnapshotUpdated += OnSnapshot;
        Deactivated += (_, _) => SafeClose();
        Loaded += (_, _) => Position();
        Closed += (_, _) => _hardware.SnapshotUpdated -= OnSnapshot;
    }

    private void OnSnapshot(object? sender, SystemSnapshot s) => Dispatcher.BeginInvoke(() => Update(s));

    private Brush B(string key) => (Brush)FindResource(key);

    private void Update(SystemSnapshot? s)
    {
        if (s == null) return;
        var gpu = s.PrimaryGpu;
        var busiest = s.Disk.Volumes.Where(v => v.IsSystem).Select(v => v.Percent).DefaultIfEmpty(0).First();
        Rows.ItemsSource = new[]
        {
            new Row("CPU", Fmt.Percent(s.Cpu.Usage) + (s.Cpu.TemperatureC is { } t ? $" · {Fmt.Temp(t)}" : ""), s.Cpu.Usage, B("Brush.Chart.Cpu")),
            new Row("RAM", $"{Fmt.Bytes(s.Memory.Used)}", s.Memory.Percent, B("Brush.Chart.Ram")),
            new Row("GPU", gpu?.Usage is { } g ? Fmt.Percent(g) + (gpu.TemperatureC is { } gt ? $" · {Fmt.Temp(gt)}" : "") : Fmt.NA, gpu?.Usage ?? 0, B("Brush.Chart.Gpu")),
            new Row(Loc.T("Disk"), Loc.F("{0} used", Fmt.Percent(busiest)), busiest, B("Brush.Chart.Disk")),
            new Row(Loc.T("Network"), $"↓ {Fmt.NetRate(s.Network.RxBps)}", Math.Min(100, (s.Network.LatencyMs ?? 0) / 2), B("Brush.Chart.Rx")),
        };
        StateText.Text = s.Network.State switch
        {
            ConnectionState.Connected => Loc.F("Online · {0}", Fmt.Ms(s.Network.LatencyMs)),
            ConnectionState.NoInternet => Loc.T("No internet"),
            ConnectionState.Disconnected => Loc.T("Disconnected"),
            _ => Loc.T("Checking…"),
        };
        if (_health.LastReport is { } r)
        {
            ScoreChip.Visibility = Visibility.Visible;
            ScoreText.Text = Loc.F("Health {0}", r.Score);
        }
        else ScoreChip.Visibility = Visibility.Collapsed;
    }

    private void Position()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 4;
        Top = area.Bottom - ActualHeight - 4;
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); } catch { }
    }

    private void Open_Click(object sender, RoutedEventArgs e) { SafeClose(); OpenRequested?.Invoke(); }
    private void Diagnose_Click(object sender, RoutedEventArgs e) { SafeClose(); DiagnosticRequested?.Invoke(); }
    private void Exit_Click(object sender, RoutedEventArgs e) { SafeClose(); ExitRequested?.Invoke(); }
}
