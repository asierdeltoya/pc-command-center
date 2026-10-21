using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PCCommandCenter.Core;
using PCCommandCenter.Hardware.Native;

namespace PCCommandCenter.Hardware.Net;

/// <summary>Throughput of the interface that carries the default route.</summary>
internal sealed class NetSampler
{
    private NetworkInterface? _active;
    private DateTime _resolvedAt = DateTime.MinValue;
    private long _lastRx, _lastTx;
    private long _lastTicks;
    private string? _lastId;

    public NetworkSnapshot Sample(IConnectivityMonitor? connectivity)
    {
        if (_active == null || DateTime.UtcNow - _resolvedAt > TimeSpan.FromSeconds(10))
        {
            _active = ResolveActiveInterface();
            _resolvedAt = DateTime.UtcNow;
        }

        var nic = _active;
        if (nic == null)
        {
            return new NetworkSnapshot
            {
                State = ConnectionState.Disconnected,
                LatencyTarget = connectivity?.Target ?? "",
            };
        }

        long rx = 0, tx = 0;
        try
        {
            var stats = nic.GetIPStatistics();
            rx = stats.BytesReceived; tx = stats.BytesSent;
        }
        catch { _active = null; }

        long now = Stopwatch.GetTimestamp();
        double rxBps = 0, txBps = 0;
        if (_lastId == nic.Id && _lastTicks != 0)
        {
            var seconds = (now - _lastTicks) / (double)Stopwatch.Frequency;
            if (seconds > 0)
            {
                rxBps = Math.Max(0, (rx - _lastRx) / seconds);
                txBps = Math.Max(0, (tx - _lastTx) / seconds);
            }
        }
        _lastId = nic.Id; _lastRx = rx; _lastTx = tx; _lastTicks = now;

        string? ipv4 = null, gateway = null;
        try
        {
            var props = nic.GetIPProperties();
            ipv4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
            gateway = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString()
                      ?? props.GatewayAddresses.FirstOrDefault()?.Address.ToString();
        }
        catch { }

        var state = nic.OperationalStatus == OperationalStatus.Up
            ? connectivity?.State ?? ConnectionState.Unknown
            : ConnectionState.Disconnected;

        return new NetworkSnapshot
        {
            AdapterName = nic.Name,
            AdapterDescription = nic.Description,
            AdapterType = Describe(nic.NetworkInterfaceType),
            IPv4 = ipv4,
            Gateway = gateway,
            LinkSpeedBps = nic.Speed > 0 ? nic.Speed : null,
            RxBps = rxBps,
            TxBps = txBps,
            TotalRx = rx,
            TotalTx = tx,
            State = state,
            LatencyMs = connectivity?.InternetLatencyMs,
            GatewayLatencyMs = connectivity?.GatewayLatencyMs,
            PacketLossPercent = connectivity?.PacketLossPercent,
            LatencyTarget = connectivity?.Target ?? "",
        };
    }

    private static NetworkInterface? ResolveActiveInterface()
    {
        try
        {
            var all = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .ToList();

            // Ask the routing table which interface reaches the internet.
            var dest = BitConverter.ToUInt32(IPAddress.Parse("8.8.8.8").GetAddressBytes(), 0);
            if (HwNative.GetBestInterface(dest, out var index) == 0)
            {
                var best = all.FirstOrDefault(n =>
                {
                    try { return n.GetIPProperties().GetIPv4Properties()?.Index == index; } catch { return false; }
                });
                if (best != null) return best;
            }

            return all.Where(n => n.OperationalStatus == OperationalStatus.Up)
                      .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0)
                      .ThenByDescending(n => n.Speed)
                      .FirstOrDefault();
        }
        catch (Exception ex)
        {
            AppLog.Debug("Unable to resolve active network interface: " + ex.Message);
            return null;
        }
    }

    public static string Describe(NetworkInterfaceType t) => t switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ppp => "PPP",
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobile broadband",
        NetworkInterfaceType.Tunnel => "Tunnel",
        NetworkInterfaceType.Loopback => "Loopback",
        _ => t.ToString()
    };
}
