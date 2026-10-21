using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PCCommandCenter.App.Services;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.ViewModels;

public sealed class PaletteCommand
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string Group { get; init; } = "";
    public string Keywords { get; init; } = "";
    public string? Shortcut { get; init; }
    public required Action Execute { get; init; }
}

/// <summary>Ctrl+K command palette with fuzzy matching over pages, actions, tools and live processes.</summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly NavigationService _nav;
    private readonly IWindowsToolsService _windows;
    private readonly IProcessService _processes;
    private readonly Func<IReadOnlyList<PaletteCommand>> _extra;
    private List<PaletteCommand> _all = [];
    private IReadOnlyList<ProcessInfo> _processCache = [];

    public CommandPaletteViewModel(NavigationService nav, IWindowsToolsService windows, IProcessService processes, Func<IReadOnlyList<PaletteCommand>> extra)
    {
        _nav = nav;
        _windows = windows;
        _processes = processes;
        _extra = extra;
    }

    public ObservableCollection<PaletteCommand> Results { get; } = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteCommand? _selected;

    partial void OnQueryChanged(string value) => Filter();

    public async void Open()
    {
        BuildCommands();
        Query = "";
        Filter();
        IsOpen = true;
        try { _processCache = await _processes.GetProcessesAsync(); } catch { _processCache = []; }
    }

    [RelayCommand] public void Close() => IsOpen = false;

    private void BuildCommands()
    {
        var list = new List<PaletteCommand>();
        foreach (var p in NavigationService.Pages)
            list.Add(new PaletteCommand
            {
                Title = Loc.F("Open {0}", Loc.T(p.Title)), Subtitle = Loc.T(p.Subtitle), Glyph = p.Glyph, Group = Loc.T("Navigation"),
                Keywords = $"{p.Key} {p.Title} go page", Execute = () => _nav.Navigate(p.Key),
            });

        list.Add(Action("Run System Health scan", "Analyze the whole system and compute a score", "", "scan diagnose health check", () => _nav.Navigate(PageKey.Health, "scan")));
        list.Add(Action("Run Ping", "Measure latency and packet loss", "", "ping latency icmp", () => { WeakReferenceMessenger.Default.Send(new NetworkToolRequest("ping")); _nav.Navigate(PageKey.Network); }));
        list.Add(Action("DNS Lookup", "Resolve a domain name", "", "dns resolve nslookup", () => { WeakReferenceMessenger.Default.Send(new NetworkToolRequest("dns")); _nav.Navigate(PageKey.Network); }));
        list.Add(Action("Traceroute", "Show the network path to a host", "", "tracert traceroute route hops", () => { WeakReferenceMessenger.Default.Send(new NetworkToolRequest("trace")); _nav.Navigate(PageKey.Network); }));
        list.Add(Action("Active connections", "TCP/UDP sockets with owning process", "", "netstat connections sockets ports", () => { WeakReferenceMessenger.Default.Send(new NetworkToolRequest("connections")); _nav.Navigate(PageKey.Network); }));
        list.Add(Action("ARP table", "IP to MAC address mappings", "", "arp mac neighbors", () => { WeakReferenceMessenger.Default.Send(new NetworkToolRequest("arp")); _nav.Navigate(PageKey.Network); }));
        list.Add(Action("Network interfaces", "Adapters, IP, DNS, DHCP, gateway", "", "ipconfig adapters interfaces mac", () => { WeakReferenceMessenger.Default.Send(new NetworkToolRequest("interfaces")); _nav.Navigate(PageKey.Network); }));
        list.Add(Action("Analyze drive C:", "Find the largest folders and files", "", "storage disk space analyze", () => _nav.Navigate(PageKey.Storage, "C:")));
        list.Add(Action("Open Terminal", "CMD and PowerShell", "", "terminal console cmd powershell shell", () => _nav.Navigate(PageKey.Terminal)));
        list.Add(Action("Ask the AI Assistant", "Ask a question about your PC", "", "ai assistant chat claude help", () => _nav.Navigate(PageKey.Ai)));
        list.Add(Action("Analyze a log file", "Detect errors and patterns", "", "log analyzer errors file", () => _nav.Navigate(PageKey.Logs)));
        list.Add(Action("Internet speed test", "Download, upload, latency and bufferbloat", "", "speed test internet bandwidth mbps fast", () => _nav.Navigate(PageKey.SpeedTest, "internet")));
        list.Add(Action("Disk benchmark", "Sequential and 4K read/write speed", "", "disk benchmark ssd speed crystaldiskmark", () => _nav.Navigate(PageKey.SpeedTest)));
        list.Add(Action("Export system report", "Save a complete HTML report of this PC", "", "report export html technician share", () => _nav.Navigate(PageKey.Health, "report")));
        list.Add(Action("Check for app updates", "Find outdated programs with winget", "", "update upgrade winget outdated apps programs", () => _nav.Navigate(PageKey.Apps)));
        list.Add(Action("Uninstall a program", "Installed programs with size and publisher", "", "uninstall remove program app", () => _nav.Navigate(PageKey.Apps)));
        list.Add(Action("Security status", "Antivirus, firewall, updates and more", "", "security antivirus defender firewall virus", () => _nav.Navigate(PageKey.Security)));
        list.Add(Action("Fix a problem", "Network reset, DNS flush, SFC, DISM, Explorer…", "", "fix repair problem broken sfc dism dns winsock", () => _nav.Navigate(PageKey.Fixes)));
        list.Add(Action("View history", "CPU, temperatures and network over 24 h / 7 days", "", "history graph past yesterday trend", () => _nav.Navigate(PageKey.History)));

        foreach (var t in _windows.Tools)
            list.Add(new PaletteCommand
            {
                Title = Loc.T(t.Name), Subtitle = Loc.T(t.Description), Glyph = t.Glyph, Group = Loc.T("Windows tools"),
                Keywords = $"{t.Id} {t.FileName} windows tool", Execute = () => _windows.Launch(t),
            });

        list.AddRange(_extra());
        _all = list;
    }

    private PaletteCommand Action(string title, string subtitle, string glyph, string keywords, Action run) => new()
    {
        Title = Loc.T(title), Subtitle = Loc.T(subtitle), Glyph = glyph, Group = Loc.T("Actions"), Keywords = keywords, Execute = run,
    };

    private void Filter()
    {
        var q = Query.Trim();
        IEnumerable<PaletteCommand> items;
        if (q.Length == 0) items = _all.Where(c => c.Group != Loc.T("Windows tools") && c.Group != Loc.T("Repairs") && c.Group != Loc.T("Diagnostics")).Take(14);
        else
        {
            items = _all.Select(c => (Cmd: c, Score: Score(q, c.Title) * 3 + Score(q, c.Keywords) + Score(q, c.Subtitle)))
                        .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Select(x => x.Cmd).Take(12).ToList();
            if (q.Length >= 2)
            {
                var procs = _processCache.Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                                     || (p.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                    .GroupBy(p => p.Name).Take(5)
                    .Select(g => new PaletteCommand
                    {
                        Title = Loc.F("Process: {0}", g.Key), Subtitle = Loc.F("{0} instance(s) · {1}", g.Count(), Fmt.Bytes(g.Sum(p => p.WorkingSet))),
                        Glyph = "", Group = Loc.T("Processes"), Execute = () => _nav.Navigate(PageKey.Processes, g.Key),
                    });
                items = items.Concat(procs);
            }
        }
        Results.Clear();
        foreach (var i in items) Results.Add(i);
        Selected = Results.FirstOrDefault();
    }

    /// <summary>Simple fuzzy score: substring matches score high, ordered subsequences score lower.</summary>
    private static int Score(string query, string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return 100 - Math.Min(idx, 50) + (idx == 0 ? 30 : 0);
        int qi = 0, score = 0;
        foreach (var ch in text)
        {
            if (qi < query.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(query[qi])) { qi++; score += 2; }
        }
        return qi == query.Length ? score : 0;
    }

    [RelayCommand]
    public void Run(PaletteCommand? cmd)
    {
        cmd ??= Selected;
        if (cmd == null) return;
        IsOpen = false;
        try { cmd.Execute(); }
        catch (Exception ex) { AppLog.Error("Palette command failed", ex); }
    }

    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        var i = Selected == null ? 0 : Results.IndexOf(Selected);
        Selected = Results[Math.Clamp(i + delta, 0, Results.Count - 1)];
    }
}
