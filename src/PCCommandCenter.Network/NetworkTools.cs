using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PCCommandCenter.Core;
using PCCommandCenter.Network.Native;

namespace PCCommandCenter.Network;

/// <summary>Network diagnostics backed directly by the Windows networking stack.</summary>
public sealed class NetworkTools : INetworkTools
{
    private static readonly byte[] PingPayload = new byte[32];

    // ───────────────────────────── Ping ─────────────────────────────

    public async Task<PingSummary> PingAsync(string host, int count, TimeSpan interval, int timeoutMs, IProgress<PingProbe>? progress, CancellationToken ct)
    {
        IPAddress address;
        try
        {
            address = await ResolveAsync(host, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PingSummary { Target = host, Error = Loc.F("Unable to resolve '{0}'", host) };
        }

        var rtts = new List<double>();
        int sent = 0;
        using var ping = new Ping();
        try
        {
            for (int seq = 1; count <= 0 || seq <= count; seq++)
            {
                ct.ThrowIfCancellationRequested();
                sent++;
                PingProbe probe;
                try
                {
                    var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(timeoutMs), PingPayload, null, ct).ConfigureAwait(false);
                    bool ok = reply.Status == IPStatus.Success;
                    if (ok) rtts.Add(reply.RoundtripTime);
                    probe = new PingProbe(seq, ok, ok ? reply.RoundtripTime : null, DescribeStatus(reply.Status), reply.Address?.ToString(), reply.Options?.Ttl);
                }
                catch (PingException ex)
                {
                    probe = new PingProbe(seq, false, null, ex.InnerException?.Message ?? ex.Message, null, null);
                }
                progress?.Report(probe);
                if (count <= 0 || seq < count) await Task.Delay(interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* user stopped: summarize what we have */ }

        double? jitter = null;
        if (rtts.Count > 1)
            jitter = rtts.Zip(rtts.Skip(1), (a, b) => Math.Abs(b - a)).Average();

        return new PingSummary
        {
            Target = host,
            ResolvedAddress = address.ToString(),
            Sent = sent,
            Received = rtts.Count,
            Min = rtts.Count > 0 ? rtts.Min() : null,
            Max = rtts.Count > 0 ? rtts.Max() : null,
            Average = rtts.Count > 0 ? rtts.Average() : null,
            Jitter = jitter,
        };
    }

    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? addresses.FirstOrDefault()
               ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    private static string DescribeStatus(IPStatus status) => status switch
    {
        IPStatus.Success => "Reply",
        IPStatus.TimedOut => "Timed out",
        IPStatus.DestinationHostUnreachable => "Host unreachable",
        IPStatus.DestinationNetworkUnreachable => "Network unreachable",
        IPStatus.TtlExpired => "TTL expired",
        IPStatus.DestinationUnreachable => "Destination unreachable",
        _ => status.ToString()
    };

    // ───────────────────────────── DNS ─────────────────────────────

    private static readonly Dictionary<string, ushort> DnsTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = 1, ["NS"] = 2, ["CNAME"] = 5, ["SOA"] = 6, ["PTR"] = 12, ["MX"] = 15, ["TXT"] = 16, ["AAAA"] = 28, ["SRV"] = 33,
    };

    public static IReadOnlyList<string> SupportedDnsTypes { get; } = ["A", "AAAA", "CNAME", "MX", "TXT", "NS", "SOA", "SRV", "PTR"];

    public Task<DnsLookupResult> DnsLookupAsync(string name, IReadOnlyList<string> recordTypes, string? server, CancellationToken ct) =>
        Task.Run(() => DnsLookup(name.Trim().TrimEnd('.'), recordTypes, server, ct), ct);

    private DnsLookupResult DnsLookup(string name, IReadOnlyList<string> recordTypes, string? server, CancellationToken ct)
    {
        var records = new List<DnsRecordInfo>();
        var errors = new List<string>();
        IPAddress? serverIp = null;
        if (!string.IsNullOrWhiteSpace(server))
        {
            if (!IPAddress.TryParse(server, out serverIp) || serverIp.AddressFamily != AddressFamily.InterNetwork)
                return new DnsLookupResult { Query = name, Server = server!, Errors = [Loc.T("Custom DNS server must be an IPv4 address")] };
        }

        // Reverse lookups: allow typing an IP address directly for PTR.
        var queryName = name;
        if (IPAddress.TryParse(name, out var asIp) && recordTypes.Contains("PTR"))
            queryName = ReverseName(asIp);

        IntPtr extra = IntPtr.Zero;
        try
        {
            if (serverIp != null)
            {
                extra = Marshal.AllocHGlobal(Marshal.SizeOf<NetNative.IP4_ARRAY>());
                Marshal.StructureToPtr(new NetNative.IP4_ARRAY { AddrCount = 1, Addr0 = BitConverter.ToUInt32(serverIp.GetAddressBytes(), 0) }, extra, false);
            }

            var sw = Stopwatch.StartNew();
            foreach (var typeName in recordTypes)
            {
                ct.ThrowIfCancellationRequested();
                if (!DnsTypes.TryGetValue(typeName, out var type)) continue;
                var q = type == 12 ? queryName : name;
                int status = NetNative.DnsQuery(q, type, NetNative.DNS_QUERY_BYPASS_CACHE, extra, out var list, IntPtr.Zero);
                if (status != 0)
                {
                    if (status != 9501) errors.Add($"{typeName}: {DescribeDnsError(status)}");
                    continue;
                }
                try { records.AddRange(ParseRecords(list)); }
                finally { NetNative.DnsRecordListFree(list, NetNative.DnsFreeRecordList); }
            }
            sw.Stop();

            return new DnsLookupResult
            {
                Query = name,
                Server = serverIp?.ToString() ?? DefaultDnsServerLabel(),
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Records = records.DistinctBy(r => (r.Type, r.Name, r.Data)).ToList(),
                Errors = errors,
            };
        }
        finally
        {
            if (extra != IntPtr.Zero) Marshal.FreeHGlobal(extra);
        }
    }

    private static IEnumerable<DnsRecordInfo> ParseRecords(IntPtr list)
    {
        var result = new List<DnsRecordInfo>();
        for (var p = list; p != IntPtr.Zero; p = Marshal.ReadIntPtr(p, NetNative.DnsRecordOffsets.Next))
        {
            var type = (ushort)Marshal.ReadInt16(p, NetNative.DnsRecordOffsets.Type);
            var flags = (uint)Marshal.ReadInt32(p, 20);
            if ((flags & 3) != 1) continue; // answer section only
            var owner = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, NetNative.DnsRecordOffsets.Name)) ?? "";
            var ttl = (uint)Marshal.ReadInt32(p, NetNative.DnsRecordOffsets.Ttl);
            var d = p + NetNative.DnsRecordOffsets.Data;
            string? typeName = DnsTypes.FirstOrDefault(kv => kv.Value == type).Key;
            string? data = type switch
            {
                1 => new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(d))).ToString(),
                28 => ReadIPv6(d),
                2 or 5 or 12 => Marshal.PtrToStringUni(Marshal.ReadIntPtr(d)),
                15 => $"{(ushort)Marshal.ReadInt16(d, 8)} {Marshal.PtrToStringUni(Marshal.ReadIntPtr(d))}",
                16 => ReadTxt(d),
                6 => $"{Marshal.PtrToStringUni(Marshal.ReadIntPtr(d))} {Marshal.PtrToStringUni(Marshal.ReadIntPtr(d, 8))} serial={(uint)Marshal.ReadInt32(d, 16)} refresh={(uint)Marshal.ReadInt32(d, 20)} retry={(uint)Marshal.ReadInt32(d, 24)} expire={(uint)Marshal.ReadInt32(d, 28)} minimum={(uint)Marshal.ReadInt32(d, 32)}",
                33 => $"priority={(ushort)Marshal.ReadInt16(d, 8)} weight={(ushort)Marshal.ReadInt16(d, 10)} port={(ushort)Marshal.ReadInt16(d, 12)} {Marshal.PtrToStringUni(Marshal.ReadIntPtr(d))}",
                _ => null
            };
            if (typeName != null && data != null) result.Add(new DnsRecordInfo(typeName, owner, data, ttl));
        }
        return result;
    }

    private static string ReadIPv6(IntPtr p)
    {
        var bytes = new byte[16];
        Marshal.Copy(p, bytes, 0, 16);
        return new IPAddress(bytes).ToString();
    }

    private static string ReadTxt(IntPtr p)
    {
        int count = Marshal.ReadInt32(p);
        var parts = new List<string>(count);
        for (int i = 0; i < count; i++)
            parts.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, 8 + i * IntPtr.Size)) ?? "");
        return string.Join("", parts);
    }

    private static string ReverseName(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork) return $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa";
        var nibbles = b.Reverse().SelectMany(x => new[] { (x & 0xF).ToString("x"), (x >> 4).ToString("x") });
        return string.Join(".", nibbles) + ".ip6.arpa";
    }

    private static string DescribeDnsError(int code) => code switch
    {
        9003 => Loc.T("Domain does not exist (NXDOMAIN)"),
        9002 => Loc.T("Server failure (SERVFAIL)"),
        9005 => Loc.T("Query refused by server"),
        1460 or 9560 => Loc.T("Timed out"),
        9852 => Loc.T("No DNS servers configured"),
        _ => Loc.F("DNS error {0}", code)
    };

    private string DefaultDnsServerLabel()
    {
        var dns = GetInterfaces().Where(i => i.IsDefaultRoute).SelectMany(i => i.DnsServers).FirstOrDefault()
                  ?? GetInterfaces().SelectMany(i => i.DnsServers).FirstOrDefault();
        return dns != null ? Loc.F("System default ({0})", dns) : Loc.T("System default");
    }

    public Task<(bool Success, double? ElapsedMs, string? Error)> MeasureDnsResolutionAsync(string host, CancellationToken ct) =>
        Task.Run<(bool, double?, string?)>(() =>
        {
            var sw = Stopwatch.StartNew();
            int status = NetNative.DnsQuery(host, 1, NetNative.DNS_QUERY_BYPASS_CACHE, IntPtr.Zero, out var list, IntPtr.Zero);
            sw.Stop();
            if (status == 0) { NetNative.DnsRecordListFree(list, NetNative.DnsFreeRecordList); return (true, sw.Elapsed.TotalMilliseconds, null); }
            return (false, sw.Elapsed.TotalMilliseconds, DescribeDnsError(status));
        }, ct);

    // ───────────────────────────── Traceroute ─────────────────────────────

    public async Task<IReadOnlyList<TraceHop>> TraceRouteAsync(string host, int maxHops, int timeoutMs, IProgress<TraceHop>? progress, CancellationToken ct)
    {
        var hops = new List<TraceHop>();
        var target = await ResolveAsync(host, ct).ConfigureAwait(false);
        using var ping = new Ping();
        var reverseLookups = new List<Task>();

        for (int ttl = 1; ttl <= maxHops; ttl++)
        {
            ct.ThrowIfCancellationRequested();
            TraceHop hop;
            var sw = Stopwatch.StartNew();
            try
            {
                var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(timeoutMs), PingPayload, new PingOptions(ttl, true), ct).ConfigureAwait(false);
                sw.Stop();
                bool reached = reply.Status == IPStatus.Success;
                bool responded = reached || reply.Status == IPStatus.TtlExpired;
                double? rtt = responded ? (reply.RoundtripTime > 0 ? reply.RoundtripTime : sw.Elapsed.TotalMilliseconds) : null;
                hop = new TraceHop(ttl, responded ? reply.Address?.ToString() : null, null, rtt,
                                   responded ? (reached ? "Destination" : "Hop") : DescribeStatus(reply.Status), reached);
            }
            catch (PingException ex)
            {
                hop = new TraceHop(ttl, null, null, null, ex.InnerException?.Message ?? ex.Message, false);
            }

            lock (hops) hops.Add(hop);
            progress?.Report(hop);

            if (hop.Address != null)
            {
                var captured = hop;
                reverseLookups.Add(Task.Run(async () =>
                {
                    var name = await ReverseLookupAsync(captured.Address!, 2000).ConfigureAwait(false);
                    if (name != null)
                    {
                        var updated = captured with { HostName = name };
                        lock (hops) { var i = hops.IndexOf(captured); if (i >= 0) hops[i] = updated; }
                        progress?.Report(updated);
                    }
                }, CancellationToken.None));
            }

            if (hop.IsDestination) break;
        }

        await Task.WhenAny(Task.WhenAll(reverseLookups), Task.Delay(2500, ct)).ConfigureAwait(false);
        lock (hops) return hops.ToList();
    }

    private static async Task<string?> ReverseLookupAsync(string address, int timeoutMs)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(address);
            // Observe a late failure (e.g. "host unknown") so it doesn't surface as an unobserved task exception.
            _ = lookup.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (await Task.WhenAny(lookup, Task.Delay(timeoutMs)).ConfigureAwait(false) != lookup) return null;
            var name = lookup.Result.HostName;
            return string.IsNullOrEmpty(name) || name == address ? null : name;
        }
        catch { return null; }
    }

    // ───────────────────────────── ARP ─────────────────────────────

    public unsafe IReadOnlyList<ArpEntry> GetArpTable()
    {
        int size = 0;
        NetNative.GetIpNetTable(IntPtr.Zero, ref size, true);
        if (size == 0) return [];
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NetNative.GetIpNetTable(buffer, ref size, true) != 0) return [];
            int count = Marshal.ReadInt32(buffer);
            var names = InterfaceNamesByIndex();
            var list = new List<ArpEntry>(count);
            var row = (NetNative.MIB_IPNETROW*)(buffer + 4);
            for (int i = 0; i < count; i++, row++)
            {
                var mac = string.Join("-", Enumerable.Range(0, (int)Math.Min(row->PhysAddrLen, 8)).Select(j => row->PhysAddr[j].ToString("X2")));
                var type = row->Type switch { 4 => "Static", 3 => "Dynamic", 2 => "Invalid", _ => "Other" };
                if (row->Type == 2) continue;
                list.Add(new ArpEntry(new IPAddress(row->Addr).ToString(), mac, type, names.GetValueOrDefault((int)row->Index, $"#{row->Index}")));
            }
            return list;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static Dictionary<int, string> InterfaceNamesByIndex()
    {
        var map = new Dictionary<int, string>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            try { if (n.GetIPProperties().GetIPv4Properties() is { } p) map[p.Index] = n.Name; } catch { }
        }
        return map;
    }

    // ───────────────────────────── Interfaces ─────────────────────────────

    public IReadOnlyList<NetworkInterfaceInfo> GetInterfaces()
    {
        uint defaultIndex = 0;
        try
        {
            GetBestInterface(BitConverter.ToUInt32(IPAddress.Parse("8.8.8.8").GetAddressBytes(), 0), out defaultIndex);
        }
        catch { }

        var list = new List<NetworkInterfaceInfo>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback || IsFilterBinding(n)) continue;
                var props = n.GetIPProperties();
                IPv4InterfaceProperties? v4 = null;
                try { v4 = props.GetIPv4Properties(); } catch { }
                IPInterfaceStatistics? stats = null;
                try { stats = n.GetIPStatistics(); } catch { }
                list.Add(new NetworkInterfaceInfo
                {
                    Name = n.Name,
                    Description = n.Description,
                    Type = DescribeType(n.NetworkInterfaceType),
                    Status = n.OperationalStatus.ToString(),
                    IsUp = n.OperationalStatus == OperationalStatus.Up,
                    Mac = FormatMac(n.GetPhysicalAddress()),
                    SpeedBps = n.Speed,
                    IPv4 = props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                                .Select(a => $"{a.Address}/{a.PrefixLength}").ToList(),
                    IPv6 = props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6)
                                .Select(a => a.Address.ToString()).ToList(),
                    Gateways = props.GatewayAddresses.Select(g => g.Address.ToString()).ToList(),
                    DnsServers = props.DnsAddresses.Select(d => d.ToString()).ToList(),
                    DhcpEnabled = v4?.IsDhcpEnabled,
                    DhcpServers = props.DhcpServerAddresses.Select(d => d.ToString()).ToList(),
                    DnsSuffix = string.IsNullOrEmpty(props.DnsSuffix) ? null : props.DnsSuffix,
                    BytesReceived = stats?.BytesReceived ?? 0,
                    BytesSent = stats?.BytesSent ?? 0,
                    Mtu = v4?.Mtu,
                    IsDefaultRoute = v4 != null && v4.Index == defaultIndex && n.OperationalStatus == OperationalStatus.Up,
                });
            }
            catch (Exception ex)
            {
                AppLog.Debug($"Interface {n.Name}: {ex.Message}");
            }
        }
        return list.OrderByDescending(i => i.IsDefaultRoute).ThenByDescending(i => i.IsUp).ThenBy(i => i.Name).ToList();
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    /// <summary>
    /// Windows exposes NDIS filter/lightweight-filter bindings (QoS, WFP, vendor filters) and the kernel debugger
    /// adapter as extra "interfaces"; they are not real adapters and only add noise.
    /// </summary>
    private static bool IsFilterBinding(NetworkInterface n)
    {
        var name = n.Name;
        var desc = n.Description;
        if (name.EndsWith("-0000", StringComparison.Ordinal) || desc.EndsWith("-0000", StringComparison.Ordinal)) return true;
        string[] markers = ["Filter", "WFP", "QoS Packet Scheduler", "Kernel Debug", "depurador de kernel", "LightWeight"];
        return markers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase) || desc.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatMac(PhysicalAddress mac)
    {
        var b = mac.GetAddressBytes();
        return b.Length == 0 ? "" : string.Join(":", b.Select(x => x.ToString("X2")));
    }

    private static string DescribeType(NetworkInterfaceType t) => t switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Tunnel => "Tunnel",
        NetworkInterfaceType.Ppp => "PPP",
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobile broadband",
        _ => t.ToString()
    };

    // ───────────────────────────── Connections ─────────────────────────────

    private static readonly string[] TcpStates =
        ["", "Closed", "Listen", "SynSent", "SynReceived", "Established", "FinWait1", "FinWait2", "CloseWait", "Closing", "LastAck", "TimeWait", "DeleteTcb"];

    public IReadOnlyList<ConnectionInfo> GetConnections(bool includeUdp)
    {
        var list = new List<ConnectionInfo>(512);
        ReadTcp4(list); ReadTcp6(list);
        if (includeUdp) { ReadUdp4(list); ReadUdp6(list); }

        var names = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p) names[p.Id] = p.Id == 0 ? "System Idle Process" : p.Id == 4 ? "System" : p.ProcessName;
        }
        foreach (var c in list) c.ProcessName = names.GetValueOrDefault(c.Pid, "—");
        return list;
    }

    public IReadOnlyDictionary<int, int> GetTcpConnectionCountsByPid()
    {
        var list = new List<ConnectionInfo>(512);
        ReadTcp4(list); ReadTcp6(list);
        return list.Where(c => c.State == "Established").GroupBy(c => c.Pid).ToDictionary(g => g.Key, g => g.Count());
    }

    private static unsafe void ReadTcp4(List<ConnectionInfo> list) =>
        ReadTable(true, NetNative.AF_INET, NetNative.TCP_TABLE_OWNER_PID_ALL, buffer =>
        {
            int count = Marshal.ReadInt32(buffer);
            var row = (NetNative.MIB_TCPROW_OWNER_PID*)(buffer + 4);
            for (int i = 0; i < count; i++, row++)
            {
                list.Add(new ConnectionInfo
                {
                    Protocol = "TCP",
                    LocalAddress = new IPAddress(row->LocalAddr).ToString(),
                    LocalPort = NetNative.Port(row->LocalPort),
                    RemoteAddress = new IPAddress(row->RemoteAddr).ToString(),
                    RemotePort = row->State == 2 ? 0 : NetNative.Port(row->RemotePort),
                    State = row->State < TcpStates.Length ? TcpStates[row->State] : row->State.ToString(),
                    Pid = (int)row->OwningPid,
                });
            }
        });

    private static unsafe void ReadTcp6(List<ConnectionInfo> list) =>
        ReadTable(true, NetNative.AF_INET6, NetNative.TCP_TABLE_OWNER_PID_ALL, buffer =>
        {
            int count = Marshal.ReadInt32(buffer);
            var row = (NetNative.MIB_TCP6ROW_OWNER_PID*)(buffer + 4);
            for (int i = 0; i < count; i++, row++)
            {
                list.Add(new ConnectionInfo
                {
                    Protocol = "TCPv6",
                    LocalAddress = new IPAddress(new ReadOnlySpan<byte>(row->LocalAddr, 16)).ToString(),
                    LocalPort = NetNative.Port(row->LocalPort),
                    RemoteAddress = new IPAddress(new ReadOnlySpan<byte>(row->RemoteAddr, 16)).ToString(),
                    RemotePort = row->State == 2 ? 0 : NetNative.Port(row->RemotePort),
                    State = row->State < TcpStates.Length ? TcpStates[row->State] : row->State.ToString(),
                    Pid = (int)row->OwningPid,
                });
            }
        });

    private static unsafe void ReadUdp4(List<ConnectionInfo> list) =>
        ReadTable(false, NetNative.AF_INET, NetNative.UDP_TABLE_OWNER_PID, buffer =>
        {
            int count = Marshal.ReadInt32(buffer);
            var row = (NetNative.MIB_UDPROW_OWNER_PID*)(buffer + 4);
            for (int i = 0; i < count; i++, row++)
            {
                list.Add(new ConnectionInfo
                {
                    Protocol = "UDP",
                    LocalAddress = new IPAddress(row->LocalAddr).ToString(),
                    LocalPort = NetNative.Port(row->LocalPort),
                    RemoteAddress = "*",
                    State = "Listening",
                    Pid = (int)row->OwningPid,
                });
            }
        });

    private static unsafe void ReadUdp6(List<ConnectionInfo> list) =>
        ReadTable(false, NetNative.AF_INET6, NetNative.UDP_TABLE_OWNER_PID, buffer =>
        {
            int count = Marshal.ReadInt32(buffer);
            var row = (NetNative.MIB_UDP6ROW_OWNER_PID*)(buffer + 4);
            for (int i = 0; i < count; i++, row++)
            {
                list.Add(new ConnectionInfo
                {
                    Protocol = "UDPv6",
                    LocalAddress = new IPAddress(new ReadOnlySpan<byte>(row->LocalAddr, 16)).ToString(),
                    LocalPort = NetNative.Port(row->LocalPort),
                    RemoteAddress = "*",
                    State = "Listening",
                    Pid = (int)row->OwningPid,
                });
            }
        });

    private static void ReadTable(bool tcp, int af, int tableClass, Action<IntPtr> parse)
    {
        int size = 0;
        int rc = tcp ? NetNative.GetExtendedTcpTable(IntPtr.Zero, ref size, true, af, tableClass, 0)
                     : NetNative.GetExtendedUdpTable(IntPtr.Zero, ref size, true, af, tableClass, 0);
        for (int attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                rc = tcp ? NetNative.GetExtendedTcpTable(buffer, ref size, true, af, tableClass, 0)
                         : NetNative.GetExtendedUdpTable(buffer, ref size, true, af, tableClass, 0);
                if (rc == 0) { parse(buffer); return; }
                if (rc != NetNative.ERROR_INSUFFICIENT_BUFFER) return;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}
