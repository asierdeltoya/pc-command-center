using System.Text;
using PCCommandCenter.Core;
using PCCommandCenter.Settings;

namespace PCCommandCenter.App.Services;

/// <summary>
/// Builds the factual SYSTEM DATA block sent to the AI. Only real measurements are included; anything that
/// cannot be measured is written as "Not available". Honors the privacy settings.
/// </summary>
public sealed class AiContextBuilder
{
    private readonly IHardwareMonitor _hardware;
    private readonly IProcessService _processes;
    private readonly IHealthService _health;
    private readonly ISettingsService _settings;

    public AiContextBuilder(IHardwareMonitor hardware, IProcessService processes, IHealthService health, ISettingsService settings)
    {
        _hardware = hardware;
        _processes = processes;
        _health = health;
        _settings = settings;
    }

    public async Task<string> BuildAsync(bool includeSystem, bool includeProcesses, bool includeHealth, bool includeNetwork, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var s = _hardware.Latest;
        {
            if (includeSystem && s != null)
            {
                var cpu = s.Cpu;
                sb.AppendLine("[CPU]");
                sb.AppendLine($"Model: {cpu.Name} ({cpu.Cores} cores / {cpu.LogicalProcessors} threads, base {Fmt.Frequency(cpu.BaseClockMhz)})");
                sb.AppendLine($"Usage now: {Fmt.Percent(cpu.Usage)}; average last 60 samples: {Fmt.Percent(_hardware.History.Cpu.Average(60))}");
                sb.AppendLine($"Current frequency: {Fmt.Frequency(cpu.FrequencyMhz)}; performance limit: {Fmt.Percent(cpu.PerformanceLimitPercent)}; throttled: {(cpu.IsThrottled is { } t ? (t ? "yes" : "no") : "Not available")}");
                sb.AppendLine($"Temperature: {(cpu.TemperatureC.HasValue ? Fmt.Temp(cpu.TemperatureC) + " (ACPI thermal zone, may not be the CPU die)" : "Not available")}; package power: {Fmt.Watts(cpu.PackagePowerW)}");
                sb.AppendLine($"Processor queue length: {Fmt.Number(cpu.QueueLength)}; processes: {cpu.ProcessCount}; threads: {cpu.ThreadCount}; handles: {cpu.HandleCount}; uptime: {Fmt.Duration(cpu.Uptime)}");
                if (cpu.PerCore.Length > 0) sb.AppendLine($"Per logical processor %: {string.Join(", ", cpu.PerCore.Select(v => v.ToString("0")))}");

                sb.AppendLine("[GPU]");
                if (s.Gpus.Count == 0) sb.AppendLine("Not available");
                foreach (var g in s.Gpus)
                    sb.AppendLine($"{g.Name} ({g.Vendor}{(g.IsIntegrated ? ", integrated" : "")}): usage {Fmt.Percent(g.Usage)}, VRAM {Fmt.Bytes(g.DedicatedUsed)} / {Fmt.Bytes(g.DedicatedTotal)}, " +
                                  $"temperature {Fmt.Temp(g.TemperatureC)}, core clock {Fmt.Frequency(g.CoreClockMhz)}, power {Fmt.Watts(g.PowerW)}, throttle reasons: {g.ThrottleReasons ?? "Not available"}");

                var m = s.Memory;
                sb.AppendLine("[MEMORY]");
                sb.AppendLine($"RAM: {Fmt.Bytes(m.Used)} used of {Fmt.Bytes(m.Total)} ({Fmt.Percent(m.Percent)}), available {Fmt.Bytes(m.Available)}; committed {Fmt.Bytes(m.CommitUsed)} of {Fmt.Bytes(m.CommitLimit)}");
                if (_hardware.MemoryModules is { } mi) sb.AppendLine($"Modules: {mi.Modules} x {mi.Type ?? "unknown type"} @ {(mi.SpeedMhz.HasValue ? mi.SpeedMhz + " MT/s" : "Not available")}");

                sb.AppendLine("[STORAGE]");
                foreach (var v in s.Disk.Volumes)
                    sb.AppendLine($"{v.Name} {v.Label} ({v.Format}): {Fmt.Bytes(v.Free)} free of {Fmt.Bytes(v.Total)} ({Fmt.Percent(v.FreePercent)} free)");
                sb.AppendLine($"Disk activity now: read {Fmt.DiskRate(s.Disk.TotalReadBps)}, write {Fmt.DiskRate(s.Disk.TotalWriteBps)}, busiest disk active {Fmt.Percent(s.Disk.MaxActivePercent)}");
                try
                {
                    foreach (var d in await _hardware.GetPhysicalDisksAsync(false, ct))
                        sb.AppendLine($"Physical: {d.Model} {d.MediaType}/{d.BusType} {Fmt.Bytes(d.Size)} health={d.HealthStatus} temp={Fmt.Temp(d.TemperatureC)} SMART={(d.PredictFailure is { } pf ? (pf ? "failure predicted" : "OK") : "Not available")}");
                }
                catch { }
            }

            if (includeNetwork && s != null)
            {
                var n = s.Network;
                sb.AppendLine("[NETWORK]");
                sb.AppendLine($"State: {n.State}; adapter: {n.AdapterName} ({n.AdapterType}, link {Fmt.LinkSpeed(n.LinkSpeedBps)}); IPv4 {n.IPv4 ?? "Not available"}; gateway {n.Gateway ?? "Not available"}");
                sb.AppendLine($"Throughput now: down {Fmt.NetRate(n.RxBps)}, up {Fmt.NetRate(n.TxBps)}");
                sb.AppendLine($"Latency to {n.LatencyTarget}: {Fmt.Ms(n.LatencyMs)} (avg recent {Fmt.Ms(_hardware.History.Latency.Average(12))}); gateway latency {Fmt.Ms(n.GatewayLatencyMs)}; recent packet loss {Fmt.Percent(n.PacketLossPercent)}");
            }

            if (includeProcesses)
            {
                var procs = await _processes.GetProcessesAsync(ct);
                bool paths = _settings.Current.AiIncludeProcessPaths;
                string Describe(ProcessInfo p) => $"{p.Name} (PID {p.Pid}{(p.Description != null ? ", " + p.Description : "")}{(paths && p.Path != null ? ", " + p.Path : "")})";
                sb.AppendLine("[TOP PROCESSES BY CPU]");
                foreach (var p in procs.OrderByDescending(p => p.CpuPercent).Take(10))
                    sb.AppendLine($"{Describe(p)}: CPU {Fmt.Percent(p.CpuPercent, 1)}, RAM {Fmt.Bytes(p.WorkingSet)}");
                sb.AppendLine("[TOP PROCESSES BY MEMORY]");
                foreach (var p in procs.OrderByDescending(p => p.WorkingSet).Take(10))
                    sb.AppendLine($"{Describe(p)}: RAM {Fmt.Bytes(p.WorkingSet)} (private commit {Fmt.Bytes(p.PrivateBytes)}), CPU {Fmt.Percent(p.CpuPercent, 1)}");
                sb.AppendLine("[TOP PROCESSES BY DISK I/O]");
                foreach (var p in procs.Where(p => p.DiskBps > 0).OrderByDescending(p => p.DiskBps).Take(5))
                    sb.AppendLine($"{Describe(p)}: I/O {Fmt.DiskRate(p.DiskBps)}");
                sb.AppendLine("Note: per-process network bandwidth is not measured; only established TCP connection counts are available.");
            }

            if (includeHealth)
            {
                sb.AppendLine("[SYSTEM HEALTH]");
                if (_health.LastReport is { } r)
                {
                    sb.AppendLine($"Score {r.Score}/100 at {r.Timestamp:g}");
                    foreach (var c in r.Categories) sb.AppendLine($"{c.Name}: {c.Status} — {c.Summary}");
                    foreach (var i in r.Issues) sb.AppendLine($"- [{i.Severity}] {i.Title}: {i.Detail}");
                }
                else sb.AppendLine("No health scan has been run yet in this session.");
            }
        }
        return sb.ToString();
    }
}
