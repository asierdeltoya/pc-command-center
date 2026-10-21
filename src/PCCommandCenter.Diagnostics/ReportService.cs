using System.Net;
using System.Text;
using PCCommandCenter.Core;

namespace PCCommandCenter.Diagnostics;

/// <summary>
/// Builds a single self-contained HTML report (no external resources) that can be e-mailed to a technician or kept
/// as a snapshot: hardware, Windows, health findings, security, storage, network, startup, apps and top processes.
/// </summary>
public sealed class ReportService : IReportService
{
    private readonly IHardwareMonitor _hardware;
    private readonly IHealthService _health;
    private readonly INetworkTools _network;
    private readonly IProcessService _processes;
    private readonly IStartupService _startup;
    private readonly IWindowsToolsService _windows;
    private readonly ISecurityService _security;
    private readonly IInstalledAppsService _apps;
    private readonly IPowerService _power;

    public ReportService(IHardwareMonitor hardware, IHealthService health, INetworkTools network, IProcessService processes, IStartupService startup,
                         IWindowsToolsService windows, ISecurityService security, IInstalledAppsService apps, IPowerService power)
    {
        _hardware = hardware; _health = health; _network = network; _processes = processes; _startup = startup;
        _windows = windows; _security = security; _apps = apps; _power = power;
    }

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    public async Task<string> CreateHtmlReportAsync(string path, CancellationToken ct)
    {
        var health = _health.LastReport ?? await _health.RunScanAsync(null, ct).ConfigureAwait(false);
        var info = await _windows.GetSystemInfoAsync(ct).ConfigureAwait(false);
        var security = await _security.GetChecksAsync(ct).ConfigureAwait(false);
        var disks = await _hardware.GetPhysicalDisksAsync(false, ct).ConfigureAwait(false);
        var startup = await _startup.GetEntriesAsync(ct).ConfigureAwait(false);
        var apps = await _apps.GetAppsAsync(ct).ConfigureAwait(false);
        var battery = await _power.GetBatteryAsync(ct).ConfigureAwait(false);
        var procs = await _processes.GetProcessesAsync(ct).ConfigureAwait(false);
        var events = await _windows.GetRecentEventsAsync(TimeSpan.FromDays(1), 40, false, ct).ConfigureAwait(false);
        var s = _hardware.Latest;
        var interfaces = _network.GetInterfaces().Where(i => i.IsUp).ToList();

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"").Append(Loc.Instance.Language).Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(H(Loc.F("PC report — {0}", info.ComputerName))).Append("</title><style>").Append(Css).Append("</style></head><body><main>");

        sb.Append("<header><div class=\"logo\">PC</div><div><h1>").Append(H(Loc.T("PC Command Center report"))).Append("</h1><p>")
          .Append(H($"{info.ComputerName} · {info.OsName} {info.OsVersion} ({info.Build}) · {DateTime.Now:F}")).Append("</p></div>")
          .Append("<div class=\"score ").Append(health.Score >= 85 ? "good" : health.Score >= 60 ? "warn" : "bad").Append("\"><b>").Append(health.Score)
          .Append("</b><span>/100</span><small>").Append(H(Loc.T(health.ScoreLabel))).Append("</small></div></header>");

        // Summary cards
        sb.Append("<section class=\"cards\">");
        void Card(string title, string value, string sub) =>
            sb.Append("<div class=\"card\"><small>").Append(H(title)).Append("</small><b>").Append(H(value)).Append("</b><span>").Append(H(sub)).Append("</span></div>");
        if (s != null)
        {
            Card("CPU", s.Cpu.Name, Loc.F("{0} cores · {1} threads", s.Cpu.Cores, s.Cpu.LogicalProcessors));
            Card(Loc.T("Memory"), Fmt.Bytes(s.Memory.Total), Loc.F("{0} in use", Fmt.Percent(s.Memory.Percent)));
            Card("GPU", s.PrimaryGpu?.Name ?? Fmt.NA, s.PrimaryGpu?.DedicatedTotal is { } v ? Fmt.Bytes(v) + " VRAM" : "");
            Card(Loc.T("Network"), s.Network.AdapterName, $"{Fmt.LinkSpeed(s.Network.LinkSpeedBps)} · {Fmt.Ms(s.Network.LatencyMs)}");
        }
        Card(Loc.T("Uptime"), Fmt.Duration(info.Uptime), Loc.F("Last boot {0:g}", info.BootTime));
        sb.Append("</section>");

        // Health findings
        sb.Append("<h2>").Append(H(Loc.T("System Health"))).Append("</h2><table><tr><th>").Append(H(Loc.T("Category"))).Append("</th><th>")
          .Append(H(Loc.T("Status"))).Append("</th><th>").Append(H(Loc.T("Summary"))).Append("</th></tr>");
        foreach (var c in health.Categories)
            sb.Append("<tr><td>").Append(H(c.Name)).Append("</td><td>").Append(Pill(c.Status)).Append("</td><td>").Append(H(c.Summary))
              .Append("<div class=\"checks\">").Append(string.Join("<br>", c.Checks.Select(H))).Append("</div></td></tr>");
        sb.Append("</table>");
        if (health.Issues.Count > 0)
        {
            sb.Append("<h3>").Append(H(Loc.T("Findings"))).Append("</h3><ul class=\"issues\">");
            foreach (var i in health.Issues)
                sb.Append("<li class=\"").Append(i.Severity.ToString().ToLowerInvariant()).Append("\"><b>").Append(H(i.Title)).Append("</b> ").Append(H(i.Detail))
                  .Append("<br><em>").Append(H(i.Recommendation)).Append("</em></li>");
            sb.Append("</ul>");
        }

        // Security
        sb.Append("<h2>").Append(H(Loc.T("Security"))).Append("</h2><table>");
        foreach (var c in security)
            sb.Append("<tr><td>").Append(H(c.Title)).Append("</td><td>").Append(Pill(c.Status)).Append("</td><td>").Append(H(c.Value))
              .Append(c.Detail.Length > 0 ? "<div class=\"checks\">" + H(c.Detail) + "</div>" : "").Append("</td></tr>");
        sb.Append("</table>");

        // System
        sb.Append("<h2>").Append(H(Loc.T("System information"))).Append("</h2><table class=\"kv\">");
        void Row(string k, string? v) => sb.Append("<tr><td>").Append(H(k)).Append("</td><td>").Append(H(string.IsNullOrEmpty(v) ? Fmt.NA : v)).Append("</td></tr>");
        Row(Loc.T("Operating system"), $"{info.OsName} {info.OsVersion} ({info.Build}, {info.Architecture})");
        Row(Loc.T("Manufacturer"), info.Manufacturer); Row(Loc.T("Model"), info.Model);
        Row(Loc.T("Motherboard"), info.Motherboard); Row("BIOS", info.Bios);
        Row(Loc.T("Installed"), info.InstallDate?.ToString("d"));
        if (_hardware.MemoryModules is { } mm) Row(Loc.T("Memory modules"), $"{mm.Modules} × {mm.Type} {mm.SpeedMhz:0} MT/s");
        if (s != null) foreach (var g in s.Gpus) Row("GPU", $"{g.Name} — {Fmt.Bytes(g.DedicatedTotal)} {g.DriverVersion}");
        if (battery.Present) Row(Loc.T("Battery"), $"{battery.Name} · {Fmt.Percent(battery.ChargePercent)} · {Loc.T("health")} {Fmt.Percent(battery.HealthPercent)}");
        sb.Append("</table>");

        // Storage
        sb.Append("<h2>").Append(H(Loc.T("Storage"))).Append("</h2><table><tr><th>").Append(H(Loc.T("Drive"))).Append("</th><th>").Append(H(Loc.T("Used"))).Append("</th><th>")
          .Append(H(Loc.T("Free"))).Append("</th><th></th></tr>");
        foreach (var v in s?.Disk.Volumes ?? [])
            sb.Append("<tr><td>").Append(H($"{v.Name} {v.Label} ({v.Format})")).Append("</td><td>").Append(H($"{Fmt.Bytes(v.Used)} / {Fmt.Bytes(v.Total)}"))
              .Append("</td><td>").Append(H(Fmt.Bytes(v.Free))).Append("</td><td><div class=\"bar\"><i style=\"width:").Append(((int)v.Percent).ToString())
              .Append("%\" class=\"").Append(v.FreePercent < 10 ? "bad" : "").Append("\"></i></div></td></tr>");
        sb.Append("</table><table><tr><th>").Append(H(Loc.T("Physical disk"))).Append("</th><th>").Append(H(Loc.T("Type"))).Append("</th><th>").Append(H(Loc.T("Health")))
          .Append("</th><th>SMART</th></tr>");
        foreach (var d in disks)
            sb.Append("<tr><td>").Append(H($"{d.Model} ({Fmt.Bytes(d.Size)})")).Append("</td><td>").Append(H($"{d.MediaType} {d.BusType}")).Append("</td><td>")
              .Append(H(Loc.T(d.HealthStatus))).Append("</td><td>").Append(H(d.PredictFailure switch { true => Loc.T("Failure predicted"), false => Loc.T("OK"), _ => Loc.T("Not available") }))
              .Append("</td></tr>");
        sb.Append("</table>");

        // Network
        sb.Append("<h2>").Append(H(Loc.T("Network"))).Append("</h2><table><tr><th>").Append(H(Loc.T("Adapter"))).Append("</th><th>IPv4</th><th>").Append(H(Loc.T("Gateway")))
          .Append("</th><th>DNS</th><th>").Append(H(Loc.T("Speed"))).Append("</th></tr>");
        foreach (var n in interfaces)
            sb.Append("<tr><td>").Append(H($"{n.Name} ({n.Type})")).Append("</td><td>").Append(H(string.Join(", ", n.IPv4))).Append("</td><td>").Append(H(string.Join(", ", n.Gateways)))
              .Append("</td><td>").Append(H(string.Join(", ", n.DnsServers))).Append("</td><td>").Append(H(Fmt.LinkSpeed(n.SpeedBps))).Append("</td></tr>");
        sb.Append("</table>");

        // Processes
        sb.Append("<h2>").Append(H(Loc.T("Top processes"))).Append("</h2><table><tr><th>").Append(H(Loc.T("Process"))).Append("</th><th>CPU</th><th>").Append(H(Loc.T("Memory"))).Append("</th></tr>");
        foreach (var p in procs.OrderByDescending(p => p.WorkingSet).Take(15))
            sb.Append("<tr><td>").Append(H(p.Description ?? p.Name)).Append(" <small>").Append(H(p.Name)).Append("</small></td><td>").Append(H(Fmt.Percent(p.CpuPercent, 1)))
              .Append("</td><td>").Append(H(Fmt.Bytes(p.WorkingSet))).Append("</td></tr>");
        sb.Append("</table>");

        // Startup
        sb.Append("<h2>").Append(H(Loc.T("Startup"))).Append("</h2><table><tr><th>").Append(H(Loc.T("Name"))).Append("</th><th>").Append(H(Loc.T("Publisher"))).Append("</th><th>")
          .Append(H(Loc.T("Status"))).Append("</th></tr>");
        foreach (var e in startup)
            sb.Append("<tr><td>").Append(H(e.Name)).Append("</td><td>").Append(H(e.Publisher)).Append("</td><td>").Append(H(e.Enabled ? Loc.T("Enabled") : Loc.T("Disabled"))).Append("</td></tr>");
        sb.Append("</table>");

        // Recent errors
        if (events.Count > 0)
        {
            sb.Append("<h2>").Append(H(Loc.T("Recent errors"))).Append("</h2><table>");
            foreach (var e in events.Take(25))
                sb.Append("<tr><td class=\"nowrap\">").Append(H(e.Time.ToString("g"))).Append("</td><td>").Append(H($"{e.Source} ({e.EventId})")).Append("</td><td>")
                  .Append(H(e.Message.Length > 220 ? e.Message[..220] + "…" : e.Message)).Append("</td></tr>");
            sb.Append("</table>");
        }

        // Apps
        sb.Append("<h2>").Append(H(Loc.F("Installed programs ({0})", apps.Count))).Append("</h2><table><tr><th>").Append(H(Loc.T("Name"))).Append("</th><th>")
          .Append(H(Loc.T("Version"))).Append("</th><th>").Append(H(Loc.T("Publisher"))).Append("</th><th>").Append(H(Loc.T("Size"))).Append("</th></tr>");
        foreach (var a in apps)
            sb.Append("<tr><td>").Append(H(a.Name)).Append("</td><td>").Append(H(a.Version)).Append("</td><td>").Append(H(a.Publisher)).Append("</td><td>")
              .Append(H(a.SizeBytes is { } sz ? Fmt.Bytes(sz) : "")).Append("</td></tr>");
        sb.Append("</table>");

        sb.Append("<footer>").Append(H(Loc.F("Generated by PC Command Center {0}. All values were measured on this computer; \"N/A\" means Windows does not expose the value.", AppInfo.Version)))
          .Append("</footer></main></body></html>");

        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        AppLog.Info("HTML report written to " + path);
        return path;
    }

    private static string Pill(HealthStatus s) => s switch
    {
        HealthStatus.Good => $"<span class=\"pill good\">{H(Loc.T("GOOD"))}</span>",
        HealthStatus.Warning => $"<span class=\"pill warn\">{H(Loc.T("WARNING"))}</span>",
        HealthStatus.Critical => $"<span class=\"pill bad\">{H(Loc.T("CRITICAL"))}</span>",
        _ => $"<span class=\"pill na\">{H(Loc.T("NOT AVAILABLE"))}</span>",
    };

    private const string Css = """
        :root{--bg:#f5f7fb;--card:#fff;--text:#0e1320;--muted:#5b6578;--line:#e3e7ee;--accent:#5b6cf0;--good:#059669;--warn:#d97706;--bad:#dc2626}
        @media (prefers-color-scheme:dark){:root{--bg:#0b0d12;--card:#141821;--text:#edf0f5;--muted:#9aa4b5;--line:#232a36;--accent:#7c8cff;--good:#34d399;--warn:#fbbf24;--bad:#f87171}}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif}
        main{max-width:1100px;margin:0 auto;padding:32px 20px 60px}header{display:flex;gap:18px;align-items:center;margin-bottom:24px}
        .logo{width:54px;height:54px;border-radius:15px;background:linear-gradient(135deg,#6e7fff,#9b5cf6);color:#fff;display:grid;place-items:center;font-weight:700}
        h1{margin:0;font-size:24px}header p{margin:2px 0 0;color:var(--muted)}header>div:nth-child(2){flex:1}
        .score{text-align:center;padding:10px 18px;border-radius:14px;background:var(--card);border:1px solid var(--line)}.score b{font-size:30px}.score span{color:var(--muted)}
        .score small{display:block;color:var(--muted)}.score.good b{color:var(--good)}.score.warn b{color:var(--warn)}.score.bad b{color:var(--bad)}
        .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:12px;margin-bottom:10px}
        .card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:14px}.card small{color:var(--muted);text-transform:uppercase;font-size:11px;font-weight:600}
        .card b{display:block;font-size:15px;margin:4px 0 2px}.card span{color:var(--muted);font-size:12px}
        h2{margin:34px 0 10px;font-size:18px}h3{margin:20px 0 8px;font-size:15px}
        table{width:100%;border-collapse:collapse;background:var(--card);border:1px solid var(--line);border-radius:12px;overflow:hidden;margin-bottom:12px}
        th,td{text-align:left;padding:9px 12px;border-bottom:1px solid var(--line);vertical-align:top}th{color:var(--muted);font-size:12px;font-weight:600}
        tr:last-child td{border-bottom:0}.kv td:first-child{color:var(--muted);width:30%}.checks{color:var(--muted);font-size:12px;margin-top:4px}small{color:var(--muted)}
        .pill{display:inline-block;padding:1px 9px;border-radius:999px;font-size:11px;font-weight:700}.pill.good{color:var(--good);background:color-mix(in srgb,var(--good) 14%,transparent)}
        .pill.warn{color:var(--warn);background:color-mix(in srgb,var(--warn) 14%,transparent)}.pill.bad{color:var(--bad);background:color-mix(in srgb,var(--bad) 14%,transparent)}
        .pill.na{color:var(--muted);background:var(--line)}.issues{padding:0;list-style:none}.issues li{background:var(--card);border:1px solid var(--line);border-left:4px solid var(--accent);border-radius:10px;padding:10px 14px;margin:8px 0}
        .issues li.critical{border-left-color:var(--bad)}.issues li.warning{border-left-color:var(--warn)}.issues em{color:var(--muted)}
        .bar{height:8px;background:var(--line);border-radius:4px;overflow:hidden;min-width:120px}.bar i{display:block;height:100%;background:var(--accent)}.bar i.bad{background:var(--bad)}
        .nowrap{white-space:nowrap}footer{margin-top:40px;color:var(--muted);font-size:12px}
        """;
}
