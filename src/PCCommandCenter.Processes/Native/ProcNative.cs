using System.Runtime.InteropServices;
using System.Text;

namespace PCCommandCenter.Processes.Native;

internal static class ProcNative
{
    public const int SystemProcessInformation = 5;
    public const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x0008;

    /// <summary>Field offsets in SYSTEM_PROCESS_INFORMATION (x64).</summary>
    public static class Spi
    {
        public const int NextEntryOffset = 0;
        public const int NumberOfThreads = 4;
        public const int WorkingSetPrivateSize = 8;
        public const int CreateTime = 32;
        public const int UserTime = 40;
        public const int KernelTime = 48;
        public const int ImageNameLength = 56;
        public const int ImageNameBuffer = 64;
        public const int UniqueProcessId = 80;
        public const int ParentProcessId = 88;
        public const int HandleCount = 96;
        public const int SessionId = 100;
        public const int WorkingSetSize = 144;
        public const int PagefileUsage = 184;
        public const int ReadTransferCount = 232;
        public const int WriteTransferCount = 240;
        public const int OtherTransferCount = 248;
    }

    [DllImport("ntdll.dll")]
    public static extern uint NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsProcessCritical(IntPtr process, out bool critical);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsWow64Process(IntPtr process, out bool wow64);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LookupAccountSid(string? system, IntPtr sid, StringBuilder name, ref int nameLen, StringBuilder domain, ref int domainLen, out int use);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr mem);
}
