using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PCCommandCenter.Core;

namespace PCCommandCenter.Network;

/// <summary>
/// Periodically pings the default gateway and an internet target to measure latency, packet loss and
/// connection state. Runs on a background task with a single in-flight probe at a time.
/// </summary>
public sealed class ConnectivityMonitor : IConnectivityMonitor
{
    private const string FallbackTarget = "8.8.8.8";
    private readonly CancellationTokenSource _cts = new();
    private readonly Queue<bool> _recent = new();
    private Task? _loop;
    private volatile string _target = "1.1.1.1";
    private TimeSpan _interval = TimeSpan.FromSeconds(5);

    public event EventHandler? Updated;

    public ConnectionState State { get; private set; } = ConnectionState.Unknown;
    public double? InternetLatencyMs { get; private set; }
    public double? GatewayLatencyMs { get; private set; }
    public double? PacketLossPercent { get; private set; }

    public string Target
    {
        get => _target;
        set => _target = string.IsNullOrWhiteSpace(value) ? "1.1.1.1" : value.Trim();
    }

    public TimeSpan Interval
    {
        get => _interval;
        set => _interval = TimeSpan.FromSeconds(Math.Clamp(value.TotalSeconds, 2, 120));
    }

    public void Start() => _loop ??= Task.Run(RunAsync);

    private async Task RunAsync()
    {
        using var ping = new Ping();
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await ProbeAsync(ping).ConfigureAwait(false);
                Updated?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                AppLog.Debug("Connectivity probe failed: " + ex.Message);
            }
            try { await Task.Delay(_interval, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProbeAsync(Ping ping)
    {
        var gateway = FindGateway();
        if (gateway == null)
        {
            State = ConnectionState.Disconnected;
            InternetLatencyMs = null;
            GatewayLatencyMs = null;
            return;
        }

        GatewayLatencyMs = await PingOnceAsync(ping, gateway).ConfigureAwait(false);

        double? internet = null;
        try
        {
            var address = IPAddress.TryParse(_target, out var ip) ? ip
                : (await Dns.GetHostAddressesAsync(_target, _cts.Token).ConfigureAwait(false))
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (address != null) internet = await PingOnceAsync(ping, address).ConfigureAwait(false);
        }
        catch (SocketException) { /* name resolution failed: treated as no internet below */ }

        lock (_recent)
        {
            _recent.Enqueue(internet.HasValue);
            while (_recent.Count > 20) _recent.Dequeue();
            PacketLossPercent = _recent.Count(x => !x) * 100.0 / _recent.Count;
        }

        if (internet == null)
        {
            // Confirm with a second, independent target before declaring the internet unreachable.
            var fallback = await PingOnceAsync(ping, IPAddress.Parse(FallbackTarget)).ConfigureAwait(false);
            State = fallback.HasValue ? ConnectionState.Connected : ConnectionState.NoInternet;
            InternetLatencyMs = null;
        }
        else
        {
            State = ConnectionState.Connected;
            InternetLatencyMs = internet;
        }
    }

    private async Task<double?> PingOnceAsync(Ping ping, IPAddress address)
    {
        try
        {
            var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(1500), new byte[32], null, _cts.Token).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? Math.Max(reply.RoundtripTime, 0.5) : null;
        }
        catch (PingException) { return null; }
    }

    private static IPAddress? FindGateway()
    {
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                var gw = n.GetIPProperties().GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                if (gw != null) return gw;
            }
            catch { }
        }
        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
}
