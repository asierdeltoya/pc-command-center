using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PCCommandCenter.App.Services;
using PCCommandCenter.Core;
using PCCommandCenter.Network;

namespace PCCommandCenter.App.ViewModels;

public sealed partial class DnsTypeOption : ObservableObject
{
    public string Type { get; init; } = "";
    [ObservableProperty] private bool _isChecked;
}

public sealed partial class HopItem : ObservableObject
{
    public int Hop { get; init; }
    [ObservableProperty] private string _address = "*";
    [ObservableProperty] private string? _hostName;
    [ObservableProperty] private double? _rtt;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isDestination;
    [ObservableProperty] private double _barFraction;
    public bool IsTimeout => Rtt == null;
    public string RttText => Rtt is { } r ? Fmt.Ms(r) : "*";
    partial void OnRttChanged(double? value) { OnPropertyChanged(nameof(IsTimeout)); OnPropertyChanged(nameof(RttText)); }
}

public sealed class InterfaceItem
{
    public required NetworkInterfaceInfo Info { get; init; }
    public string Speed => Fmt.LinkSpeed(Info.SpeedBps);
    public string IPv4 => Info.IPv4.Count > 0 ? string.Join(", ", Info.IPv4) : Loc.T("None");
    public string IPv6 => Info.IPv6.Count > 0 ? string.Join("\n", Info.IPv6) : Loc.T("None");
    public string Gateway => Info.Gateways.Count > 0 ? string.Join(", ", Info.Gateways) : Loc.T("None");
    public string Dns => Info.DnsServers.Count > 0 ? string.Join(", ", Info.DnsServers) : Loc.T("None");
    public string Dhcp => Info.DhcpEnabled switch
    {
        true => Info.DhcpServers.Count > 0 ? Loc.F("Enabled ({0})", string.Join(", ", Info.DhcpServers)) : Loc.T("Enabled"),
        false => Loc.T("Disabled (static)"),
        null => Loc.T("Not available"),
    };
    public string Traffic => Loc.F("↓ {0}  ↑ {1}", Fmt.Bytes(Info.BytesReceived), Fmt.Bytes(Info.BytesSent));
    public string Glyph => Info.Type switch { "Wi-Fi" => "", "Ethernet" => "", _ => "" };
    public string Mtu => Info.Mtu?.ToString() ?? Fmt.NA;
}

public sealed partial class NetworkViewModel : PageViewModel, IRecipient<NetworkToolRequest>
{
    private readonly INetworkTools _tools;
    private readonly INotificationService _notify;
    private readonly NavigationService _nav;
    private readonly DispatcherTimer _connTimer;
    private CancellationTokenSource? _pingCts, _traceCts, _dnsCts;
    private readonly List<double> _rtts = [];

    public NetworkViewModel(INetworkTools tools, INotificationService notify, NavigationService nav)
    {
        _tools = tools;
        _notify = notify;
        _nav = nav;
        foreach (var t in NetworkTools.SupportedDnsTypes)
            DnsTypes.Add(new DnsTypeOption { Type = t, IsChecked = t is "A" or "AAAA" or "CNAME" or "MX" });
        ConnectionsView = CollectionViewSource.GetDefaultView(Connections);
        ConnectionsView.Filter = FilterConnection;
        _connTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _connTimer.Tick += (_, _) => { if (AutoRefreshConnections && SelectedTab == 4) RefreshConnections(); };
        WeakReferenceMessenger.Default.Register(this);
    }

    public static IReadOnlyList<string> Tabs { get; } = ["Ping", "DNS Lookup", "Traceroute", "Interfaces", "Connections", "ARP"];

    [ObservableProperty] private int _selectedTab;

    partial void OnSelectedTabChanged(int value) => LoadTab(value);

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        LoadTab(SelectedTab);
        _connTimer.Start();
    }

    public override void OnNavigatedFrom()
    {
        base.OnNavigatedFrom();
        _connTimer.Stop();
    }

    public void Receive(NetworkToolRequest message)
    {
        SelectedTab = message.Tool switch { "dns" => 1, "trace" => 2, "interfaces" => 3, "connections" => 4, "arp" => 5, _ => 0 };
        if (message.Target is { } t)
        {
            if (SelectedTab == 0) PingHost = t;
            if (SelectedTab == 1) DnsQuery = t;
            if (SelectedTab == 2) TraceHost = t;
        }
    }

    private void LoadTab(int tab)
    {
        if (!IsActive) return;
        switch (tab)
        {
            case 3 when Interfaces.Count == 0: RefreshInterfaces(); break;
            case 4 when Connections.Count == 0: RefreshConnections(); break;
            case 5 when Arp.Count == 0: RefreshArp(); break;
        }
    }

    // ───────────────────────────── Ping ─────────────────────────────

    public sealed record CountOption(int Value, string Label)
    {
        public override string ToString() => Label;
    }

    public static IReadOnlyList<CountOption> PingCounts { get; } =
        [new(4, "4"), new(10, "10"), new(25, "25"), new(50, "50"), new(100, "100"), new(0, "∞")];

    [ObservableProperty] private string _pingHost = "1.1.1.1";
    [ObservableProperty] private CountOption _pingCount = PingCounts[1];
    [ObservableProperty] private bool _pingRunning;
    [ObservableProperty] private double[] _pingValues = [];
    [ObservableProperty] private string _pingSent = "0";
    [ObservableProperty] private string _pingReceived = "0";
    [ObservableProperty] private string _pingLoss = "—";
    [ObservableProperty] private string _pingMin = "—";
    [ObservableProperty] private string _pingAvg = "—";
    [ObservableProperty] private string _pingMax = "—";
    [ObservableProperty] private string _pingJitter = "—";
    [ObservableProperty] private string _pingResolved = "";
    [ObservableProperty] private string? _pingError;
    public ObservableCollection<PingProbe> PingProbes { get; } = [];

    [RelayCommand]
    private async Task StartPingAsync()
    {
        if (PingRunning) { _pingCts?.Cancel(); return; }
        if (string.IsNullOrWhiteSpace(PingHost)) return;
        PingRunning = true;
        PingError = null;
        PingProbes.Clear();
        _rtts.Clear();
        PingValues = [];
        int sent = 0, received = 0;
        _pingCts = new CancellationTokenSource();
        var progress = new Progress<PingProbe>(p =>
        {
            sent++;
            if (p.Success) received++;
            PingProbes.Insert(0, p);
            if (PingProbes.Count > 200) PingProbes.RemoveAt(PingProbes.Count - 1);
            _rtts.Add(p.RoundtripMs ?? double.NaN);
            PingValues = _rtts.TakeLast(100).ToArray();
            var ok = _rtts.Where(x => !double.IsNaN(x)).ToList();
            PingSent = sent.ToString();
            PingReceived = received.ToString();
            PingLoss = Fmt.Percent((sent - received) * 100.0 / sent);
            if (ok.Count > 0) { PingMin = Fmt.Ms(ok.Min()); PingAvg = Fmt.Ms(ok.Average()); PingMax = Fmt.Ms(ok.Max()); }
            if (ok.Count > 1) PingJitter = Fmt.Ms(ok.Zip(ok.Skip(1), (a, b) => Math.Abs(b - a)).Average());
            PingResolved = p.Address ?? PingResolved;
        });
        try
        {
            var summary = await _tools.PingAsync(PingHost.Trim(), PingCount.Value, TimeSpan.FromSeconds(1), 2000, progress, _pingCts.Token);
            if (summary.Error != null) PingError = summary.Error;
            else PingResolved = summary.ResolvedAddress ?? "";
        }
        catch (Exception ex)
        {
            PingError = OperationResult.FriendlyError(ex);
        }
        finally { PingRunning = false; }
    }

    // ───────────────────────────── DNS ─────────────────────────────

    public ObservableCollection<DnsTypeOption> DnsTypes { get; } = [];
    public static IReadOnlyList<string> DnsServers { get; } = ["System default", "1.1.1.1 (Cloudflare)", "8.8.8.8 (Google)", "9.9.9.9 (Quad9)", "Custom"];

    [ObservableProperty] private string _dnsQuery = "microsoft.com";
    [ObservableProperty] private string _dnsServer = "System default";
    [ObservableProperty] private string _customDnsServer = "";
    [ObservableProperty] private bool _dnsRunning;
    [ObservableProperty] private string _dnsElapsed = "";
    [ObservableProperty] private string _dnsServerUsed = "";
    [ObservableProperty] private string _dnsErrors = "";
    [ObservableProperty] private bool _dnsHasResult;
    public ObservableCollection<DnsRecordInfo> DnsRecords { get; } = [];
    public bool IsCustomDns => DnsServer == "Custom";
    partial void OnDnsServerChanged(string value) => OnPropertyChanged(nameof(IsCustomDns));

    [RelayCommand]
    private async Task LookupAsync()
    {
        if (string.IsNullOrWhiteSpace(DnsQuery)) return;
        _dnsCts?.Cancel();
        _dnsCts = new CancellationTokenSource();
        DnsRunning = true;
        try
        {
            string? server = DnsServer switch
            {
                "Custom" => CustomDnsServer.Trim(),
                "System default" => null,
                _ => DnsServer.Split(' ')[0],
            };
            var types = DnsTypes.Where(t => t.IsChecked).Select(t => t.Type).ToList();
            if (types.Count == 0) types = ["A"];
            var r = await _tools.DnsLookupAsync(DnsQuery.Trim(), types, server, _dnsCts.Token);
            DnsRecords.Clear();
            foreach (var rec in r.Records) DnsRecords.Add(rec);
            DnsElapsed = Fmt.Ms(r.ElapsedMs);
            DnsServerUsed = r.Server;
            DnsErrors = string.Join("\n", r.Errors);
            DnsHasResult = true;
            if (r.Records.Count == 0 && r.Errors.Count == 0) DnsErrors = Loc.T("No records found for the selected types.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { DnsErrors = OperationResult.FriendlyError(ex); DnsHasResult = true; }
        finally { DnsRunning = false; }
    }

    // ───────────────────────────── Traceroute ─────────────────────────────

    [ObservableProperty] private string _traceHost = "8.8.8.8";
    [ObservableProperty] private bool _traceRunning;
    [ObservableProperty] private string _traceStatus = "";
    public ObservableCollection<HopItem> Hops { get; } = [];

    [RelayCommand]
    private async Task TraceAsync()
    {
        if (TraceRunning) { _traceCts?.Cancel(); return; }
        if (string.IsNullOrWhiteSpace(TraceHost)) return;
        TraceRunning = true;
        Hops.Clear();
        TraceStatus = Loc.T("Tracing route…");
        _traceCts = new CancellationTokenSource();
        var progress = new Progress<TraceHop>(h =>
        {
            var item = Hops.FirstOrDefault(x => x.Hop == h.Hop);
            if (item == null) { item = new HopItem { Hop = h.Hop }; Hops.Add(item); }
            item.Address = h.Address ?? "*";
            item.HostName = h.HostName ?? item.HostName;
            item.Rtt = h.RoundtripMs;
            item.Status = Loc.T(h.Status);
            item.IsDestination = h.IsDestination;
            var max = Math.Max(1, Hops.Where(x => x.Rtt.HasValue).Select(x => x.Rtt!.Value).DefaultIfEmpty(1).Max());
            foreach (var x in Hops) x.BarFraction = x.Rtt is { } r ? Math.Max(0.02, r / max) : 0;
        });
        try
        {
            var hops = await _tools.TraceRouteAsync(TraceHost.Trim(), 30, 2000, progress, _traceCts.Token);
            TraceStatus = hops.Any(h => h.IsDestination)
                ? Loc.F("Destination reached in {0} hops", hops.Count)
                : Loc.F("Destination not reached after {0} hops", hops.Count);
        }
        catch (OperationCanceledException) { TraceStatus = Loc.T("Traceroute stopped"); }
        catch (Exception ex) { TraceStatus = OperationResult.FriendlyError(ex); }
        finally { TraceRunning = false; }
    }

    // ───────────────────────────── Interfaces ─────────────────────────────

    public ObservableCollection<InterfaceItem> Interfaces { get; } = [];
    [ObservableProperty] private bool _showInactive;
    partial void OnShowInactiveChanged(bool value) => RefreshInterfaces();

    [RelayCommand]
    private void RefreshInterfaces()
    {
        try
        {
            Interfaces.Clear();
            foreach (var i in _tools.GetInterfaces().Where(i => ShowInactive || i.IsUp)) Interfaces.Add(new InterfaceItem { Info = i });
        }
        catch (Exception ex) { AppLog.Warn("Interfaces failed", ex); ErrorMessage = Loc.T("Unable to read network interfaces"); }
    }

    // ───────────────────────────── Connections ─────────────────────────────

    public ObservableCollection<ConnectionInfo> Connections { get; } = [];
    public ICollectionView ConnectionsView { get; }
    public static IReadOnlyList<string> StateFilters { get; } = ["All", "Established", "Listen", "TimeWait", "CloseWait"];
    [ObservableProperty] private string _connectionFilter = "";
    [ObservableProperty] private string _stateFilter = "All";
    [ObservableProperty] private bool _includeUdp;
    [ObservableProperty] private bool _autoRefreshConnections = true;
    [ObservableProperty] private string _connectionSummary = "";
    partial void OnConnectionFilterChanged(string value) => ConnectionsView.Refresh();
    partial void OnStateFilterChanged(string value) => ConnectionsView.Refresh();
    partial void OnIncludeUdpChanged(bool value) => RefreshConnections();

    private bool FilterConnection(object o)
    {
        var c = (ConnectionInfo)o;
        if (StateFilter != "All" && c.State != StateFilter) return false;
        if (string.IsNullOrWhiteSpace(ConnectionFilter)) return true;
        var q = ConnectionFilter.Trim();
        return c.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase) || c.RemoteAddress.Contains(q) || c.LocalAddress.Contains(q)
               || c.RemotePort.ToString() == q || c.LocalPort.ToString() == q || c.Pid.ToString() == q;
    }

    [RelayCommand]
    private void RefreshConnections()
    {
        try
        {
            var list = _tools.GetConnections(IncludeUdp)
                .OrderBy(c => c.State == "Established" ? 0 : c.State == "Listen" || c.State == "Listening" ? 2 : 1)
                .ThenBy(c => c.ProcessName).ToList();
            Connections.Clear();
            foreach (var c in list) Connections.Add(c);
            ConnectionSummary = Loc.F("{0} sockets · {1} established · {2} listening", list.Count,
                list.Count(c => c.State == "Established"), list.Count(c => c.State is "Listen" or "Listening"));
        }
        catch (Exception ex) { AppLog.Warn("Connections failed", ex); ErrorMessage = Loc.T("Unable to read the connection table"); }
    }

    [RelayCommand]
    private void CopyConnection(ConnectionInfo? c)
    {
        if (c == null) return;
        try { Clipboard.SetText($"{c.Protocol} {c.LocalAddress}:{c.LocalPort} -> {c.RemoteAddress}:{c.RemotePort} {c.State} {c.ProcessName} ({c.Pid})"); }
        catch { }
    }

    [RelayCommand]
    private void ShowProcess(ConnectionInfo? c)
    {
        if (c != null) _nav.Navigate(PageKey.Processes, c.Pid.ToString());
    }

    // ───────────────────────────── ARP ─────────────────────────────

    public ObservableCollection<ArpEntry> Arp { get; } = [];

    [RelayCommand]
    private void RefreshArp()
    {
        try
        {
            Arp.Clear();
            foreach (var e in _tools.GetArpTable().OrderBy(e => e.InterfaceName).ThenBy(e => Version.TryParse(e.IpAddress, out var v) ? v : new Version()))
                Arp.Add(e);
        }
        catch (Exception ex) { AppLog.Warn("ARP failed", ex); ErrorMessage = Loc.T("Unable to read the ARP table"); }
    }

    [RelayCommand]
    private void SendToAi()
    {
        string content = SelectedTab switch
        {
            0 => $"Ping {PingHost} ({PingResolved}): sent {PingSent}, received {PingReceived}, loss {PingLoss}, min {PingMin}, avg {PingAvg}, max {PingMax}, jitter {PingJitter}",
            1 => $"DNS lookup {DnsQuery} via {DnsServerUsed} in {DnsElapsed}:\n" + string.Join("\n", DnsRecords.Select(r => $"{r.Type} {r.Name} {r.Data} TTL={r.Ttl}")) + "\n" + DnsErrors,
            2 => $"Traceroute to {TraceHost}:\n" + string.Join("\n", Hops.Select(h => $"{h.Hop}. {h.Address} {h.HostName} {h.RttText}")),
            _ => string.Join("\n", Interfaces.Select(i => $"{i.Info.Name} ({i.Info.Type}, {i.Info.Status}) {i.Speed} IPv4 {i.IPv4} GW {i.Gateway} DNS {i.Dns} DHCP {i.Dhcp}")),
        };
        WeakReferenceMessenger.Default.Send(new AiAttachmentMessage(Loc.F("Network: {0}", Loc.T(Tabs[Math.Min(SelectedTab, Tabs.Count - 1)])), content,
            Loc.T("Analyze these network results. Is anything wrong?")));
        _nav.Navigate(PageKey.Ai);
    }
}
