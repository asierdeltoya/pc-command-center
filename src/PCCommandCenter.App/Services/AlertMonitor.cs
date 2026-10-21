using PCCommandCenter.Core;
using PCCommandCenter.Settings;

namespace PCCommandCenter.App.Services;

/// <summary>Watches live telemetry and raises notifications when user-defined thresholds are crossed.</summary>
public sealed class AlertMonitor : IDisposable
{
    private readonly IHardwareMonitor _hardware;
    private readonly INotificationService _notify;
    private readonly ISettingsService _settings;
    private int _highRamSamples;
    private int _highLatencySamples;
    private DateTime _lastDiskCheck = DateTime.MinValue;
    private ConnectionState _lastState = ConnectionState.Unknown;

    public AlertMonitor(IHardwareMonitor hardware, INotificationService notify, ISettingsService settings)
    {
        _hardware = hardware;
        _notify = notify;
        _settings = settings;
        _hardware.SnapshotUpdated += OnSnapshot;
    }

    private void OnSnapshot(object? sender, SystemSnapshot s)
    {
        try
        {
            var cfg = _settings.Current;
            var cooldown = TimeSpan.FromMinutes(15);

            if (s.Cpu.TemperatureC is { } cpuT && cpuT >= cfg.CpuTempThreshold)
                _notify.Notify(NotificationLevel.Warning, Loc.T("High CPU temperature"),
                    Loc.F("CPU temperature exceeded {0} ({1}).", Fmt.Temp(cfg.CpuTempThreshold), Fmt.Temp(cpuT)), "cpu-temp", cooldown);

            foreach (var g in s.Gpus)
                if (g.TemperatureC is { } gpuT && gpuT >= cfg.GpuTempThreshold)
                    _notify.Notify(NotificationLevel.Warning, Loc.T("High GPU temperature"),
                        Loc.F("{0} reached {1}.", g.Name, Fmt.Temp(gpuT)), "gpu-temp-" + g.Luid, cooldown);

            // Require the condition to persist (~10 samples) to avoid alerting on brief spikes.
            _highRamSamples = s.Memory.Percent >= cfg.RamThreshold ? _highRamSamples + 1 : 0;
            if (_highRamSamples == 10)
                _notify.Notify(NotificationLevel.Warning, Loc.T("High memory usage"),
                    Loc.F("{0} of RAM is in use.", Fmt.Percent(s.Memory.Percent)), "ram", cooldown);

            if (DateTime.Now - _lastDiskCheck > TimeSpan.FromMinutes(1))
            {
                _lastDiskCheck = DateTime.Now;
                foreach (var v in s.Disk.Volumes.Where(v => v.DriveType == "Fixed" && v.FreePercent < cfg.DiskFreeThreshold))
                    _notify.Notify(NotificationLevel.Warning, Loc.T("Low disk space"),
                        Loc.F("Disk {0} has less than {1}% free space ({2} free).", v.Name.TrimEnd('\\'), cfg.DiskFreeThreshold, Fmt.Bytes(v.Free)),
                        "disk-" + v.Name, TimeSpan.FromHours(6));
            }

            if (s.Network.LatencyMs is { } lat)
            {
                _highLatencySamples = lat >= cfg.LatencyThreshold ? _highLatencySamples + 1 : 0;
                if (_highLatencySamples == 5)
                    _notify.Notify(NotificationLevel.Warning, Loc.T("Network latency increased"),
                        Loc.F("Latency to {0} is {1}.", s.Network.LatencyTarget, Fmt.Ms(lat)), "latency", cooldown);
            }

            if (s.Network.State != _lastState)
            {
                if (_lastState == ConnectionState.Connected && s.Network.State is ConnectionState.NoInternet or ConnectionState.Disconnected)
                    _notify.Notify(NotificationLevel.Error, Loc.T("Connection lost"),
                        s.Network.State == ConnectionState.NoInternet ? Loc.T("The network is up but the internet is unreachable.") : Loc.T("No network adapter is connected."),
                        "net-state", TimeSpan.FromMinutes(2));
                else if (_lastState is ConnectionState.NoInternet or ConnectionState.Disconnected && s.Network.State == ConnectionState.Connected)
                    _notify.Notify(NotificationLevel.Success, Loc.T("Connection restored"), Loc.T("Internet connectivity is back."), "net-restored", TimeSpan.FromMinutes(2));
                if (s.Network.State != ConnectionState.Unknown) _lastState = s.Network.State;
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("Alert evaluation failed: " + ex.Message);
        }
    }

    public void Dispose() => _hardware.SnapshotUpdated -= OnSnapshot;
}
