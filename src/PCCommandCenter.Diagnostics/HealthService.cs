using System.Diagnostics;
using PCCommandCenter.Core;

namespace PCCommandCenter.Diagnostics;

/// <summary>
/// Runs a set of independent, evidence-based checks and computes a 0–100 health score.
/// Every finding is derived from measured data; anything that cannot be measured is reported as "Not available"
/// and never affects the score.
/// </summary>
public sealed class HealthService : IHealthService
{
    private readonly IHardwareMonitor _hardware;
    private readonly IConnectivityMonitor _connectivity;
    private readonly INetworkTools _network;
    private readonly IProcessService _processes;
    private readonly IStartupService _startup;
    private readonly IWindowsToolsService _windows;
    private readonly ISecurityService? _security;
    private readonly IPowerService? _power;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HealthService(IHardwareMonitor hardware, IConnectivityMonitor connectivity, INetworkTools network,
                         IProcessService processes, IStartupService startup, IWindowsToolsService windows,
                         ISecurityService? security = null, IPowerService? power = null)
    {
        _hardware = hardware;
        _connectivity = connectivity;
        _network = network;
        _processes = processes;
        _startup = startup;
        _windows = windows;
        _security = security;
        _power = power;
    }

    public event EventHandler<HealthReport>? ReportUpdated;
    public HealthReport? LastReport { get; private set; }
    public bool IsScanning { get; private set; }

    public async Task<HealthReport> RunScanAsync(IProgress<HealthProgress>? progress, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        IsScanning = true;
        var sw = Stopwatch.StartNew();
        try
        {
            var issues = new List<HealthIssue>();
            var categories = new List<HealthCategory>();
            var steps = new (string Label, Func<List<HealthIssue>, CancellationToken, Task<HealthCategory>> Run)[]
            {
                ("Analyzing CPU", CheckCpuAsync),
                ("Analyzing GPU", (i, c) => Task.FromResult(CheckGpu(i))),
                ("Analyzing memory", CheckMemoryAsync),
                ("Analyzing storage", CheckStorageAsync),
                ("Testing network", CheckNetworkAsync),
                ("Reviewing startup programs", CheckStartupAsync),
                ("Checking Windows", CheckWindowsAsync),
                ("Checking security", CheckSecurityAsync),
                ("Checking battery", CheckBatteryAsync),
            };

            for (int i = 0; i < steps.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new HealthProgress((double)i / steps.Length, Loc.T(steps[i].Label)));
                try
                {
                    var category = await steps[i].Run(issues, ct).ConfigureAwait(false);
                    if (category.Key != "skip") categories.Add(category);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppLog.Error($"Health check '{steps[i].Label}' failed", ex);
                    categories.Add(new HealthCategory { Key = $"step{i}", Name = steps[i].Label, Status = HealthStatus.NotAvailable, Summary = Loc.T("Not available") });
                }
            }

            int penalty = issues.Sum(x => x.Severity switch { IssueSeverity.Critical => 15, IssueSeverity.Warning => 5, _ => 0 });
            var report = new HealthReport
            {
                Timestamp = DateTime.Now,
                Score = Math.Clamp(100 - penalty, 0, 100),
                Categories = categories,
                Issues = issues.OrderByDescending(x => x.Severity).ToList(),
                Duration = sw.Elapsed,
            };
            progress?.Report(new HealthProgress(1, Loc.T("Scan complete")));
            LastReport = report;
            AppLog.Info($"Health scan finished: score {report.Score}, {issues.Count} finding(s) in {sw.Elapsed.TotalSeconds:0.0}s");
            ReportUpdated?.Invoke(this, report);
            return report;
        }
        finally
        {
            IsScanning = false;
            _gate.Release();
        }
    }

    private static HealthCategory Category(string key, string name) => new() { Key = key, Name = Loc.T(name), Status = HealthStatus.Good };

    private static void Finalize(HealthCategory cat, List<HealthIssue> all, List<HealthIssue> mine, string goodSummary)
    {
        all.AddRange(mine);
        if (mine.Any(i => i.Severity == IssueSeverity.Critical)) cat.Status = HealthStatus.Critical;
        else if (mine.Any(i => i.Severity == IssueSeverity.Warning)) cat.Status = HealthStatus.Warning;
        if (cat.Status != HealthStatus.NotAvailable)
            cat.Summary = mine.Where(i => i.Severity != IssueSeverity.Info).Select(i => i.Title).FirstOrDefault() ?? goodSummary;
    }

    private async Task<SystemSnapshot?> WaitForSnapshotAsync(CancellationToken ct)
    {
        for (int i = 0; i < 50 && _hardware.Latest == null; i++) await Task.Delay(100, ct).ConfigureAwait(false);
        return _hardware.Latest;
    }

    // ───────────────────────────── CPU ─────────────────────────────

    private async Task<HealthCategory> CheckCpuAsync(List<HealthIssue> all, CancellationToken ct)
    {
        var cat = Category("cpu", "CPU");
        var mine = new List<HealthIssue>();
        var snap = await WaitForSnapshotAsync(ct).ConfigureAwait(false);
        if (snap == null)
        {
            cat.Status = HealthStatus.NotAvailable; cat.Summary = Loc.T("Unable to read CPU counters");
            return cat;
        }

        // Use the recent average rather than a single instantaneous sample.
        var avg = _hardware.History.Cpu.Average(30);
        if (double.IsNaN(avg)) avg = snap.Cpu.Usage;
        cat.Checks.Add(Loc.F("Average load (last 30 samples): {0}", Fmt.Percent(avg)));
        if (avg >= 90)
            mine.Add(new HealthIssue { Category = "CPU", Severity = IssueSeverity.Critical, Title = Loc.T("CPU is saturated"),
                Detail = Loc.F("Average CPU usage is {0}.", Fmt.Percent(avg)), Recommendation = Loc.T("Review the processes using the most CPU."), Action = IssueAction.OpenProcesses });
        else if (avg >= 75)
            mine.Add(new HealthIssue { Category = "CPU", Severity = IssueSeverity.Warning, Title = Loc.T("High CPU usage"),
                Detail = Loc.F("Average CPU usage is {0}.", Fmt.Percent(avg)), Recommendation = Loc.T("Review the processes using the most CPU."), Action = IssueAction.OpenProcesses });

        if (snap.Cpu.TemperatureC is { } t)
        {
            cat.Checks.Add(Loc.F("Temperature ({0}): {1}", Loc.T(snap.Cpu.TemperatureSource ?? "sensor"), Fmt.Temp(t)));
            if (t >= 90)
                mine.Add(new HealthIssue { Category = "CPU", Severity = IssueSeverity.Critical, Title = Loc.T("Very high temperature"),
                    Detail = Loc.F("The thermal zone reports {0}.", Fmt.Temp(t)), Recommendation = Loc.T("Check cooling: fans, dust, thermal paste and airflow.") });
            else if (t >= 80)
                mine.Add(new HealthIssue { Category = "CPU", Severity = IssueSeverity.Warning, Title = Loc.T("Elevated temperature"),
                    Detail = Loc.F("The thermal zone reports {0}.", Fmt.Temp(t)), Recommendation = Loc.T("Check cooling: fans, dust, thermal paste and airflow.") });
        }
        else cat.Checks.Add(Loc.T("Temperature: Not available"));

        if (snap.Cpu.IsThrottled == true)
            mine.Add(new HealthIssue { Category = "CPU", Severity = IssueSeverity.Warning, Title = Loc.T("CPU performance is being limited"),
                Detail = Loc.F("Windows reports the processor can currently deliver {0} of its nominal performance.", Fmt.Percent(snap.Cpu.PerformanceLimitPercent)),
                Recommendation = Loc.T("This can be caused by power plans, power limits or temperature. Check the power plan and cooling.") });
        else if (snap.Cpu.IsThrottled == false) cat.Checks.Add(Loc.T("No performance limiting detected"));

        // Processes with sustained abnormal CPU use: two samples ~1.5 s apart.
        await _processes.GetProcessesAsync(ct).ConfigureAwait(false);
        await Task.Delay(1500, ct).ConfigureAwait(false);
        var procs = await _processes.GetProcessesAsync(ct).ConfigureAwait(false);
        foreach (var p in procs.Where(p => p.CpuPercent >= 25).OrderByDescending(p => p.CpuPercent).Take(3))
        {
            mine.Add(new HealthIssue { Category = "CPU", Severity = IssueSeverity.Warning, Title = Loc.F("{0} is using {1} CPU", p.Name, Fmt.Percent(p.CpuPercent)),
                Detail = Loc.F("PID {0}. {1}", p.Pid, p.Description ?? p.Path ?? ""), Recommendation = Loc.T("If this is unexpected, inspect or close the process."), Action = IssueAction.OpenProcesses });
        }

        Finalize(cat, all, mine, Loc.F("{0} at {1} load", snap.Cpu.Name.Length > 0 ? snap.Cpu.Name : "CPU", Fmt.Percent(avg)));
        return cat;
    }

    // ───────────────────────────── GPU ─────────────────────────────

    private HealthCategory CheckGpu(List<HealthIssue> all)
    {
        var cat = Category("gpu", "GPU");
        var mine = new List<HealthIssue>();
        var gpus = _hardware.Latest?.Gpus ?? [];
        if (gpus.Count == 0)
        {
            cat.Status = HealthStatus.NotAvailable; cat.Summary = Loc.T("No GPU telemetry available");
            return cat;
        }
        foreach (var g in gpus)
        {
            cat.Checks.Add($"{g.Name}: {Loc.T("usage")} {Fmt.Percent(g.Usage)}, {Loc.T("temperature")} {Fmt.Temp(g.TemperatureC)}");
            if (g.TemperatureC is { } t && t >= 90)
                mine.Add(new HealthIssue { Category = "GPU", Severity = IssueSeverity.Critical, Title = Loc.F("{0} is very hot", g.Name), Detail = Fmt.Temp(t), Recommendation = Loc.T("Check GPU fans and case airflow.") });
            else if (g.TemperatureC is { } t2 && t2 >= 83)
                mine.Add(new HealthIssue { Category = "GPU", Severity = IssueSeverity.Warning, Title = Loc.F("{0} temperature is high", g.Name), Detail = Fmt.Temp(t2), Recommendation = Loc.T("Check GPU fans and case airflow.") });
            if (g is { DedicatedUsed: { } used, DedicatedTotal: { } total } && total > 0 && used * 100.0 / total >= 92)
                mine.Add(new HealthIssue { Category = "GPU", Severity = IssueSeverity.Warning, Title = Loc.F("{0} video memory is nearly full", g.Name),
                    Detail = $"{Fmt.Bytes(used)} / {Fmt.Bytes(total)}", Recommendation = Loc.T("Close GPU-heavy applications or lower graphics settings."), Action = IssueAction.OpenProcesses });
            if (g.ThrottleReasons is { } reasons && reasons.Contains("thermal", StringComparison.OrdinalIgnoreCase))
                mine.Add(new HealthIssue { Category = "GPU", Severity = IssueSeverity.Warning, Title = Loc.F("{0} is thermally throttling", g.Name), Detail = reasons, Recommendation = Loc.T("Check GPU fans and case airflow.") });
        }
        var primary = _hardware.Latest?.PrimaryGpu;
        Finalize(cat, all, mine, primary != null ? $"{primary.Name}" : Loc.T("GPU OK"));
        return cat;
    }

    // ───────────────────────────── Memory ─────────────────────────────

    private async Task<HealthCategory> CheckMemoryAsync(List<HealthIssue> all, CancellationToken ct)
    {
        var cat = Category("memory", "Memory");
        var mine = new List<HealthIssue>();
        var mem = _hardware.Latest?.Memory;
        if (mem == null || mem.Total == 0)
        {
            cat.Status = HealthStatus.NotAvailable; cat.Summary = Loc.T("Not available");
            return cat;
        }
        cat.Checks.Add(Loc.F("In use: {0} of {1} ({2})", Fmt.Bytes(mem.Used), Fmt.Bytes(mem.Total), Fmt.Percent(mem.Percent)));
        cat.Checks.Add(Loc.F("Committed: {0} of {1}", Fmt.Bytes(mem.CommitUsed), Fmt.Bytes(mem.CommitLimit)));

        if (mem.Percent >= 92)
            mine.Add(new HealthIssue { Category = "Memory", Severity = IssueSeverity.Critical, Title = Loc.T("Memory is almost exhausted"),
                Detail = Loc.F("{0} of RAM in use.", Fmt.Percent(mem.Percent)), Recommendation = Loc.T("Close applications that use a lot of memory."), Action = IssueAction.OpenProcesses });
        else if (mem.Percent >= 80)
            mine.Add(new HealthIssue { Category = "Memory", Severity = IssueSeverity.Warning, Title = Loc.T("High memory usage"),
                Detail = Loc.F("{0} of RAM in use.", Fmt.Percent(mem.Percent)), Recommendation = Loc.T("Close applications that use a lot of memory."), Action = IssueAction.OpenProcesses });
        if (mem.CommitPercent >= 90)
            mine.Add(new HealthIssue { Category = "Memory", Severity = IssueSeverity.Warning, Title = Loc.T("Commit charge is close to the limit"),
                Detail = Loc.F("{0} of the commit limit is used.", Fmt.Percent(mem.CommitPercent)), Recommendation = Loc.T("Increase the page file size or close applications.") });

        var procs = await _processes.GetProcessesAsync(ct).ConfigureAwait(false);
        var threshold = Math.Max(2L << 30, mem.Total / 5);
        foreach (var p in procs.Where(p => p.WorkingSet >= threshold).OrderByDescending(p => p.WorkingSet).Take(3))
            mine.Add(new HealthIssue { Category = "Memory", Severity = IssueSeverity.Info, Title = Loc.F("{0} uses {1} of RAM", p.Name, Fmt.Bytes(p.WorkingSet)),
                Detail = Loc.F("PID {0}", p.Pid), Recommendation = Loc.T("Large but not necessarily abnormal. Restart it if memory keeps growing."), Action = IssueAction.OpenProcesses });

        Finalize(cat, all, mine, Loc.F("{0} in use", Fmt.Percent(mem.Percent)));
        return cat;
    }

    // ───────────────────────────── Storage ─────────────────────────────

    private async Task<HealthCategory> CheckStorageAsync(List<HealthIssue> all, CancellationToken ct)
    {
        var cat = Category("storage", "Storage");
        var mine = new List<HealthIssue>();
        var volumes = _hardware.Latest?.Disk.Volumes ?? [];
        foreach (var v in volumes.Where(v => v.DriveType == "Fixed"))
        {
            cat.Checks.Add(Loc.F("{0} {1} free of {2}", v.Name, Fmt.Bytes(v.Free), Fmt.Bytes(v.Total)));
            if (v.FreePercent < 5 || v.Free < 2L << 30)
                mine.Add(new HealthIssue { Category = "Storage", Severity = IssueSeverity.Critical, Title = Loc.F("Disk {0} is almost full", v.Name),
                    Detail = Loc.F("Only {0} free ({1}).", Fmt.Bytes(v.Free), Fmt.Percent(v.FreePercent)), Recommendation = Loc.T("Use the Storage Analyzer to find what is taking space."), Action = IssueAction.OpenStorage });
            else if (v.FreePercent < 10)
                mine.Add(new HealthIssue { Category = "Storage", Severity = IssueSeverity.Warning, Title = Loc.F("Disk {0} has less than 10% free space", v.Name),
                    Detail = Loc.F("{0} free ({1}).", Fmt.Bytes(v.Free), Fmt.Percent(v.FreePercent)), Recommendation = Loc.T("Use the Storage Analyzer to find what is taking space."), Action = IssueAction.OpenStorage });
        }

        var disks = await _hardware.GetPhysicalDisksAsync(true, ct).ConfigureAwait(false);
        bool smartUnavailable = false;
        foreach (var d in disks)
        {
            cat.Checks.Add($"{d.Model}: {Loc.T(d.HealthStatus)}{(d.TemperatureC is { } t ? ", " + Fmt.Temp(t) : "")}");
            if (d.HealthStatus == "Unhealthy" || d.PredictFailure == true)
                mine.Add(new HealthIssue { Category = "Storage", Severity = IssueSeverity.Critical, Title = Loc.F("{0} reports a health problem", d.Model),
                    Detail = d.PredictFailure == true ? Loc.T("SMART predicts an imminent failure.") : Loc.F("Health status: {0}. {1}", Loc.T(d.HealthStatus), d.OperationalStatus),
                    Recommendation = Loc.T("Back up your data immediately and plan to replace the drive.") });
            else if (d.HealthStatus == "Warning")
                mine.Add(new HealthIssue { Category = "Storage", Severity = IssueSeverity.Warning, Title = Loc.F("{0} health warning", d.Model),
                    Detail = d.OperationalStatus, Recommendation = Loc.T("Back up important data and check the drive with the manufacturer's tool.") });
            if (d.TemperatureC is { } temp && temp >= 60)
                mine.Add(new HealthIssue { Category = "Storage", Severity = IssueSeverity.Warning, Title = Loc.F("{0} is running hot", d.Model), Detail = Fmt.Temp(temp), Recommendation = Loc.T("Improve airflow around the drive.") });
            if (d.WearPercent is >= 90)
                mine.Add(new HealthIssue { Category = "Storage", Severity = IssueSeverity.Warning, Title = Loc.F("{0} is near the end of its rated life", d.Model), Detail = Loc.F("Wear: {0}", Fmt.Percent(d.WearPercent)), Recommendation = Loc.T("Plan to replace the SSD.") });
            if (d.SmartNote != null) smartUnavailable = true;
        }
        if (smartUnavailable) cat.Checks.Add(Loc.T("SMART details: Not available without administrator privileges"));
        if (volumes.Count == 0 && disks.Count == 0)
        {
            cat.Status = HealthStatus.NotAvailable; cat.Summary = Loc.T("Not available");
            return cat;
        }
        Finalize(cat, all, mine, Loc.F("{0} drive(s) healthy", disks.Count > 0 ? disks.Count : volumes.Count));
        return cat;
    }

    // ───────────────────────────── Network ─────────────────────────────

    private async Task<HealthCategory> CheckNetworkAsync(List<HealthIssue> all, CancellationToken ct)
    {
        var cat = Category("network", "Network");
        var mine = new List<HealthIssue>();
        var interfaces = _network.GetInterfaces();
        var active = interfaces.FirstOrDefault(i => i.IsDefaultRoute) ?? interfaces.FirstOrDefault(i => i.IsUp && i.Gateways.Count > 0);
        if (active == null)
        {
            mine.Add(new HealthIssue { Category = "Network", Severity = IssueSeverity.Critical, Title = Loc.T("No active network connection"),
                Detail = Loc.T("No adapter with a default gateway is connected."), Recommendation = Loc.T("Check the cable or Wi-Fi connection."), Action = IssueAction.OpenNetwork });
            Finalize(cat, all, mine, "");
            return cat;
        }
        cat.Checks.Add(Loc.F("Active adapter: {0} ({1})", active.Name, Fmt.LinkSpeed(active.SpeedBps)));

        var disconnected = interfaces.Where(i => !i.IsUp && i.Type is "Ethernet" or "Wi-Fi" && !i.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                                                 && !i.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)).ToList();
        if (disconnected.Count > 0) cat.Checks.Add(Loc.F("Disconnected physical adapters: {0}", string.Join(", ", disconnected.Select(d => d.Name))));

        var gateway = active.Gateways.FirstOrDefault(g => !g.Contains(':')) ?? active.Gateways.FirstOrDefault();
        if (gateway != null)
        {
            var gw = await _network.PingAsync(gateway, 4, TimeSpan.FromMilliseconds(250), 1000, null, ct).ConfigureAwait(false);
            cat.Checks.Add(Loc.F("Gateway {0}: {1} avg, {2} loss", gateway, Fmt.Ms(gw.Average), Fmt.Percent(gw.LossPercent)));
            if (gw.Received == 0)
                mine.Add(new HealthIssue { Category = "Network", Severity = IssueSeverity.Warning, Title = Loc.T("The router does not answer ping"),
                    Detail = Loc.F("Gateway {0} did not reply (some routers block ping).", gateway), Recommendation = Loc.T("If browsing also fails, restart the router."), Action = IssueAction.OpenNetwork });
            else if (gw.LossPercent >= 25)
                mine.Add(new HealthIssue { Category = "Network", Severity = IssueSeverity.Warning, Title = Loc.T("Packet loss to the router"),
                    Detail = Loc.F("{0} packet loss to {1}.", Fmt.Percent(gw.LossPercent), gateway), Recommendation = Loc.T("Check Wi-Fi signal or the network cable."), Action = IssueAction.OpenNetwork });
        }

        var target = _connectivity.Target;
        var inet = await _network.PingAsync(target, 5, TimeSpan.FromMilliseconds(250), 1500, null, ct).ConfigureAwait(false);
        cat.Checks.Add(Loc.F("Internet ({0}): {1} avg, {2} loss", target, Fmt.Ms(inet.Average), Fmt.Percent(inet.LossPercent)));
        if (inet.Received == 0)
        {
            var alt = await _network.PingAsync("8.8.8.8", 3, TimeSpan.FromMilliseconds(250), 1500, null, ct).ConfigureAwait(false);
            if (alt.Received == 0)
                mine.Add(new HealthIssue { Category = "Network", Severity = IssueSeverity.Critical, Title = Loc.T("No internet connectivity"),
                    Detail = Loc.T("Neither the configured target nor 8.8.8.8 replied."), Recommendation = Loc.T("Check the router and your internet provider."), Action = IssueAction.OpenNetwork });
        }
        else
        {
            if (inet.Average >= 150)
                mine.Add(new HealthIssue { Category = "Network", Severity = IssueSeverity.Warning, Title = Loc.T("High internet latency"),
                    Detail = Loc.F("Average {0} to {1}.", Fmt.Ms(inet.Average), target), Recommendation = Loc.T("Check for downloads or streaming saturating the connection."), Action = IssueAction.OpenNetwork });
            if (inet.LossPercent >= 20)
                mine.Add(new HealthIssue { Category = "Network", Severity = IssueSeverity.Warning, Title = Loc.T("Packet loss to the internet"),
                    Detail = Loc.F("{0} packet loss.", Fmt.Percent(inet.LossPercent)), Recommendation = Loc.T("Run a traceroute to see where packets are lost."), Action = IssueAction.OpenNetwork });
        }

        var dns = await _network.MeasureDnsResolutionAsync("www.microsoft.com", ct).ConfigureAwait(false);
        cat.Checks.Add(Loc.F("DNS resolution: {0}", dns.Success ? Fmt.Ms(dns.ElapsedMs) : dns.Error));
        if (!dns.Success)
            mine.Add(new HealthIssue { Category = "Network", Severity = IssueSeverity.Critical, Title = Loc.T("DNS resolution is failing"),
                Detail = dns.Error ?? "", Recommendation = Loc.T("Try another DNS server (e.g. 1.1.1.1) in the adapter settings."), Action = IssueAction.OpenNetwork });
        else if (dns.ElapsedMs >= 300)
            mine.Add(new HealthIssue { Category = "Network", Severity = IssueSeverity.Warning, Title = Loc.T("Slow DNS"),
                Detail = Loc.F("Uncached lookup took {0}.", Fmt.Ms(dns.ElapsedMs)), Recommendation = Loc.T("Compare DNS servers in Network → DNS Lookup."), Action = IssueAction.OpenNetwork });

        Finalize(cat, all, mine, Loc.F("Connected via {0}, {1}", active.Name, Fmt.Ms(inet.Average)));
        return cat;
    }

    // ───────────────────────────── Startup ─────────────────────────────

    private async Task<HealthCategory> CheckStartupAsync(List<HealthIssue> all, CancellationToken ct)
    {
        var cat = Category("startup", "Startup");
        var mine = new List<HealthIssue>();
        var entries = await _startup.GetEntriesAsync(ct).ConfigureAwait(false);
        var enabled = entries.Count(e => e.Enabled);
        cat.Checks.Add(Loc.F("{0} enabled of {1} startup entries", enabled, entries.Count));
        if (enabled > 20)
            mine.Add(new HealthIssue { Category = "Startup", Severity = IssueSeverity.Warning, Title = Loc.F("{0} programs start with Windows", enabled),
                Detail = Loc.T("Many startup programs slow down boot and use memory in the background."), Recommendation = Loc.T("Disable the ones you don't need at startup."), Action = IssueAction.OpenStartup });
        else if (enabled > 12)
            mine.Add(new HealthIssue { Category = "Startup", Severity = IssueSeverity.Info, Title = Loc.F("{0} programs start with Windows", enabled),
                Detail = Loc.T("Review whether all of them are needed."), Recommendation = Loc.T("Disable the ones you don't need at startup."), Action = IssueAction.OpenStartup });
        var broken = entries.Where(e => e.Enabled && e.ExecutablePath != null && !e.FileExists && Path.IsPathRooted(e.ExecutablePath)).ToList();
        if (broken.Count > 0)
            mine.Add(new HealthIssue { Category = "Startup", Severity = IssueSeverity.Info, Title = Loc.F("{0} startup entries point to missing files", broken.Count),
                Detail = string.Join(", ", broken.Select(b => b.Name).Take(5)), Recommendation = Loc.T("These entries are leftovers from uninstalled software and can be disabled."), Action = IssueAction.OpenStartup });
        Finalize(cat, all, mine, Loc.F("{0} startup programs", enabled));
        return cat;
    }

    // ───────────────────────────── Windows ─────────────────────────────

    private async Task<HealthCategory> CheckWindowsAsync(List<HealthIssue> all, CancellationToken ct)
    {
        var cat = Category("windows", "Windows");
        var mine = new List<HealthIssue>();

        var events = await _windows.GetRecentEventsAsync(TimeSpan.FromHours(24), 300, false, ct).ConfigureAwait(false);
        int critical = events.Count(e => e.Level == "Critical");
        int errors = events.Count(e => e.Level == "Error" && e.Log == "System");
        cat.Checks.Add(Loc.F("Last 24 h: {0} critical and {1} system errors", critical, errors));
        if (critical > 0)
        {
            var top = events.First(e => e.Level == "Critical");
            mine.Add(new HealthIssue { Category = "Windows", Severity = IssueSeverity.Warning, Title = Loc.F("{0} critical event(s) in the last 24 hours", critical),
                Detail = $"{top.Source} ({top.EventId}): {Truncate(top.Message, 160)}", Recommendation = Loc.T("Kernel-Power 41 means an unexpected shutdown or power loss. Review the events in Windows → Recent errors."), Action = IssueAction.OpenWindows });
        }
        if (errors >= 25)
        {
            var common = events.Where(e => e.Level == "Error" && e.Log == "System").GroupBy(e => e.Source).OrderByDescending(g => g.Count()).First();
            mine.Add(new HealthIssue { Category = "Windows", Severity = IssueSeverity.Warning, Title = Loc.F("{0} system errors in the last 24 hours", errors),
                Detail = Loc.F("Most frequent source: {0} ({1} events).", common.Key, common.Count()), Recommendation = Loc.T("Review the recurring error source."), Action = IssueAction.OpenWindows });
        }

        var services = await _windows.GetImportantServicesAsync(ct).ConfigureAwait(false);
        var stopped = services.Where(s => s.ShouldBeRunning && !s.IsRunning).ToList();
        cat.Checks.Add(Loc.F("{0} key services checked, {1} automatic services stopped", services.Count, stopped.Count));
        foreach (var s in stopped)
            mine.Add(new HealthIssue { Category = "Windows", Severity = IssueSeverity.Warning, Title = Loc.F("Service '{0}' is stopped", s.DisplayName),
                Detail = Loc.F("{0}. Startup type: {1}.", Loc.T(s.Purpose), s.StartType), Recommendation = Loc.T("Start the service from Windows → Services (requires administrator)."), Action = IssueAction.OpenWindows });

        var info = await _windows.GetSystemInfoAsync(ct).ConfigureAwait(false);
        if (info.PendingReboot == true)
            mine.Add(new HealthIssue { Category = "Windows", Severity = IssueSeverity.Info, Title = Loc.T("A restart is pending"),
                Detail = Loc.F("Reason: {0}.", Loc.T(info.PendingRebootReason ?? "")), Recommendation = Loc.T("Restart Windows to finish installing updates."), Action = IssueAction.OpenWindows });
        cat.Checks.Add(Loc.F("Uptime: {0}", Fmt.Duration(info.Uptime)));
        if (info.Uptime > TimeSpan.FromDays(21))
            mine.Add(new HealthIssue { Category = "Windows", Severity = IssueSeverity.Info, Title = Loc.F("Windows has been running for {0} days", (int)info.Uptime.TotalDays),
                Detail = Loc.T("Long uptimes can accumulate leaks and delay updates."), Recommendation = Loc.T("Restart when convenient.") });

        Finalize(cat, all, mine, $"{info.OsName} {info.OsVersion}");
        return cat;
    }

    // ───────────────────────────── Security ─────────────────────────────

    /// <summary>Checks that protect the PC directly (antivirus, firewall, updates, UAC) affect the score; the rest are notes.</summary>
    private static readonly HashSet<string> ScoredSecurityChecks = ["antivirus", "realtime", "signatures", "firewall", "updates", "uac"];

    private async Task<HealthCategory> CheckSecurityAsync(List<HealthIssue> all, CancellationToken ct)
    {
        var cat = Category("security", "Security");
        if (_security == null) { cat.Status = HealthStatus.NotAvailable; cat.Summary = Loc.T("Not available"); return cat; }
        var mine = new List<HealthIssue>();
        var checks = await _security.GetChecksAsync(ct).ConfigureAwait(false);
        foreach (var c in checks)
        {
            cat.Checks.Add($"{c.Title}: {c.Value}");
            if (c.Status is not (HealthStatus.Warning or HealthStatus.Critical)) continue;
            bool scored = ScoredSecurityChecks.Contains(c.Id);
            mine.Add(new HealthIssue
            {
                Category = "Security",
                Severity = !scored ? IssueSeverity.Info : c.Status == HealthStatus.Critical ? IssueSeverity.Critical : IssueSeverity.Warning,
                Title = $"{c.Title}: {c.Value}", Detail = c.Detail, Recommendation = c.Recommendation, Action = IssueAction.OpenSecurity,
            });
        }
        if (checks.Count == 0) { cat.Status = HealthStatus.NotAvailable; cat.Summary = Loc.T("Not available"); return cat; }
        Finalize(cat, all, mine, Loc.T("Protected"));
        return cat;
    }

    // ───────────────────────────── Battery ─────────────────────────────

    private async Task<HealthCategory> CheckBatteryAsync(List<HealthIssue> all, CancellationToken ct)
    {
        if (_power == null) return new HealthCategory { Key = "skip" };
        var b = await _power.GetBatteryAsync(ct).ConfigureAwait(false);
        if (!b.Present) return new HealthCategory { Key = "skip" }; // desktops: nothing to report
        var cat = Category("battery", "Battery");
        var mine = new List<HealthIssue>();
        cat.Checks.Add(Loc.F("Charge: {0} ({1})", Fmt.Percent(b.ChargePercent), Loc.T(b.Status)));
        if (b.HealthPercent is { } h)
        {
            cat.Checks.Add(Loc.F("Capacity: {0} of the original ({1} / {2} mWh)", Fmt.Percent(h), b.FullChargeCapacityMWh, b.DesignCapacityMWh));
            if (h < 60)
                mine.Add(new HealthIssue { Category = "Battery", Severity = IssueSeverity.Warning, Title = Loc.T("The battery is significantly worn"),
                    Detail = Loc.F("It holds {0} of its original capacity.", Fmt.Percent(h)), Recommendation = Loc.T("Consider replacing the battery if autonomy is no longer enough.") });
            else if (h < 80)
                mine.Add(new HealthIssue { Category = "Battery", Severity = IssueSeverity.Info, Title = Loc.T("The battery shows normal wear"),
                    Detail = Loc.F("It holds {0} of its original capacity.", Fmt.Percent(h)), Recommendation = Loc.T("Avoid keeping it at 100% for long periods to slow down wear.") });
        }
        else cat.Checks.Add(Loc.T("Capacity: Not available"));
        Finalize(cat, all, mine, Loc.F("{0} charged", Fmt.Percent(b.ChargePercent)));
        return cat;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
