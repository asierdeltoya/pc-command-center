using System.Runtime.InteropServices;

namespace PCCommandCenter.Network.Native;

internal static class NetNative
{
    public const int AF_INET = 2;
    public const int AF_INET6 = 23;
    public const int TCP_TABLE_OWNER_PID_ALL = 5;
    public const int UDP_TABLE_OWNER_PID = 1;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern int GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern int GetIpNetTable(IntPtr table, ref int size, bool order);

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCPROW_OWNER_PID
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct MIB_TCP6ROW_OWNER_PID
    {
        public fixed byte LocalAddr[16];
        public uint LocalScopeId, LocalPort;
        public fixed byte RemoteAddr[16];
        public uint RemoteScopeId, RemotePort, State, OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_UDPROW_OWNER_PID
    {
        public uint LocalAddr, LocalPort, OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct MIB_UDP6ROW_OWNER_PID
    {
        public fixed byte LocalAddr[16];
        public uint LocalScopeId, LocalPort, OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct MIB_IPNETROW
    {
        public uint Index;
        public uint PhysAddrLen;
        public fixed byte PhysAddr[8];
        public uint Addr;
        public uint Type;
    }

    // ───── dnsapi ─────

    public const uint DNS_QUERY_STANDARD = 0x0;
    public const uint DNS_QUERY_BYPASS_CACHE = 0x8;
    public const int DnsFreeRecordList = 1;

    [DllImport("dnsapi.dll", EntryPoint = "DnsQuery_W", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int DnsQuery(string name, ushort type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);

    [DllImport("dnsapi.dll")]
    public static extern void DnsRecordListFree(IntPtr list, int freeType);

    /// <summary>Offsets inside DNS_RECORDW on 64-bit Windows.</summary>
    public static class DnsRecordOffsets
    {
        public const int Next = 0;
        public const int Name = 8;
        public const int Type = 16;
        public const int DataLength = 18;
        public const int Ttl = 24;
        public const int Data = 32;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IP4_ARRAY
    {
        public uint AddrCount;
        public uint Addr0;
    }

    public static int Port(uint networkOrderPort) => (int)(((networkOrderPort & 0xFF) << 8) | ((networkOrderPort >> 8) & 0xFF));
}
