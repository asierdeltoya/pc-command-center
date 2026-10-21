using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using PCCommandCenter.Core;
using PCCommandCenter.Processes.Native;

namespace PCCommandCenter.Processes;

/// <summary>
/// Process enumeration through a single NtQuerySystemInformation call per refresh (the same data source
/// Task Manager uses), with per-process CPU and I/O rates computed between refreshes.
/// </summary>
public sealed class ProcessService : IProcessService, IDisposable
{
    private sealed record Previous(long CpuTime, long IoRead, long IoWrite, long CreateTime);
    private sealed record Identity(string? Path, string? User, string? Description, string? Company, bool Critical);

    /// <summary>Core Windows processes that must not be terminated casually.</summary>
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe",
        "lsaiso.exe", "svchost.exe", "dwm.exe", "fontdrvhost.exe", "Memory Compression", "sihost.exe", "explorer.exe",
        "MsMpEng.exe", "audiodg.exe", "spoolsv.exe", "wudfhost.exe", "ctfmon.exe", "LogonUI.exe", "Secure System",
    };

    private readonly INetworkTools? _network;
    private readonly ConcurrentDictionary<(int Pid, long Create), Identity> _identities = new();
    private readonly ConcurrentDictionary<string, (string? Description, string? Company)> _versionInfo = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<int, Previous> _previous = new();
    private long _previousTimestamp;
    private volatile IReadOnlyList<ProcessInfo>? _last;
    private IntPtr _buffer;
    private int _bufferSize = 1 << 20;
    private readonly int _cpuCount = Environment.ProcessorCount;

    public ProcessService(INetworkTools? network = null)
    {
        _network = network;
    }

    public async Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(Snapshot, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private IReadOnlyList<ProcessInfo> Snapshot()
    {
        if (IntPtr.Size != 8) return FallbackSnapshot();

        var buffer = QueryBuffer();
        long now = Stopwatch.GetTimestamp();
        double elapsed100ns = _previousTimestamp == 0 ? 0 : (now - _previousTimestamp) * 10_000_000.0 / Stopwatch.Frequency;
        double elapsedSec = elapsed100ns / 10_000_000.0;

        IReadOnlyDictionary<int, int> connections = new Dictionary<int, int>();
        try { if (_network != null) connections = _network.GetTcpConnectionCountsByPid(); } catch { }

        var list = new List<ProcessInfo>(400);
        var current = new Dictionary<int, Previous>(400);
        int offset = 0;
        while (true)
        {
            var p = buffer + offset;
            int pid = (int)Marshal.ReadInt64(p, ProcNative.Spi.UniqueProcessId);
            if (pid != 0)
            {
                long create = Marshal.ReadInt64(p, ProcNative.Spi.CreateTime);
                long cpu = Marshal.ReadInt64(p, ProcNative.Spi.UserTime) + Marshal.ReadInt64(p, ProcNative.Spi.KernelTime);
                long ioRead = Marshal.ReadInt64(p, ProcNative.Spi.ReadTransferCount);
                long ioWrite = Marshal.ReadInt64(p, ProcNative.Spi.WriteTransferCount);

                int nameLen = Marshal.ReadInt16(p, ProcNative.Spi.ImageNameLength);
                var namePtr = Marshal.ReadIntPtr(p, ProcNative.Spi.ImageNameBuffer);
                string name = nameLen > 0 && namePtr != IntPtr.Zero ? Marshal.PtrToStringUni(namePtr, nameLen / 2) : pid == 4 ? "System" : $"Process {pid}";

                double cpuPct = 0, readBps = 0, writeBps = 0;
                if (elapsed100ns > 0 && _previous.TryGetValue(pid, out var prev) && prev.CreateTime == create)
                {
                    cpuPct = Math.Clamp((cpu - prev.CpuTime) / (elapsed100ns * _cpuCount) * 100.0, 0, 100);
                    readBps = Math.Max(0, (ioRead - prev.IoRead) / elapsedSec);
                    writeBps = Math.Max(0, (ioWrite - prev.IoWrite) / elapsedSec);
                }
                current[pid] = new Previous(cpu, ioRead, ioWrite, create);

                var identity = _identities.GetOrAdd((pid, create), key => ResolveIdentity(key.Pid, name));

                list.Add(new ProcessInfo
                {
                    Pid = pid,
                    ParentPid = (int)Marshal.ReadInt64(p, ProcNative.Spi.ParentProcessId),
                    Name = name,
                    Path = identity.Path,
                    User = identity.User,
                    Description = identity.Description,
                    Company = identity.Company,
                    IsCritical = identity.Critical,
                    CpuPercent = cpuPct,
                    WorkingSet = Marshal.ReadInt64(p, ProcNative.Spi.WorkingSetPrivateSize),
                    PrivateBytes = Marshal.ReadInt64(p, ProcNative.Spi.PagefileUsage),
                    IoReadBps = readBps,
                    IoWriteBps = writeBps,
                    TcpConnections = connections.GetValueOrDefault(pid),
                    Threads = Marshal.ReadInt32(p, ProcNative.Spi.NumberOfThreads),
                    Handles = Marshal.ReadInt32(p, ProcNative.Spi.HandleCount),
                    SessionId = Marshal.ReadInt32(p, ProcNative.Spi.SessionId),
                    StartTime = create > 0 ? DateTime.FromFileTime(create) : null,
                });
            }

            int next = Marshal.ReadInt32(p, ProcNative.Spi.NextEntryOffset);
            if (next == 0) break;
            offset += next;
        }

        // Forget identities of processes that exited.
        if (_identities.Count > current.Count * 2)
            foreach (var key in _identities.Keys.Where(k => !current.ContainsKey(k.Pid)).ToList()) _identities.TryRemove(key, out _);

        _previous = current;
        _previousTimestamp = now;
        _last = list;
        return list;
    }

    private IntPtr QueryBuffer()
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (_buffer == IntPtr.Zero) _buffer = Marshal.AllocHGlobal(_bufferSize);
            uint status = ProcNative.NtQuerySystemInformation(ProcNative.SystemProcessInformation, _buffer, _bufferSize, out int needed);
            if (status == 0) return _buffer;
            if (status != ProcNative.STATUS_INFO_LENGTH_MISMATCH) throw new InvalidOperationException($"NtQuerySystemInformation failed: 0x{status:X8}");
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _bufferSize = Math.Max(_bufferSize * 2, needed + 64 * 1024);
        }
        throw new InvalidOperationException("Process table is too large");
    }

    private IReadOnlyList<ProcessInfo> FallbackSnapshot()
    {
        var list = new List<ProcessInfo>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { list.Add(new ProcessInfo { Pid = p.Id, Name = p.ProcessName, WorkingSet = p.WorkingSet64, Threads = p.Threads.Count, Handles = p.HandleCount }); }
                catch { }
            }
        }
        return list;
    }

    private Identity ResolveIdentity(int pid, string name)
    {
        string? path = null, user = null;
        bool critical = CriticalNames.Contains(name);
        if (pid == 4 || name is "Registry" or "Memory Compression" or "Secure System")
            return new Identity(null, "SYSTEM", name == "System" ? "NT Kernel & System" : null, "Microsoft Corporation", true);

        var h = ProcNative.OpenProcess(ProcNative.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (ProcNative.QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
                if (ProcNative.IsProcessCritical(h, out var isCritical) && isCritical) critical = true;
                user = GetUser(h);
            }
            finally { ProcNative.CloseHandle(h); }
        }

        string? description = null, company = null;
        if (path != null)
        {
            (description, company) = _versionInfo.GetOrAdd(path, static p =>
            {
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(p);
                    return (string.IsNullOrWhiteSpace(vi.FileDescription) ? null : vi.FileDescription.Trim(),
                            string.IsNullOrWhiteSpace(vi.CompanyName) ? null : vi.CompanyName.Trim());
                }
                catch { return (null, null); }
            });
        }
        return new Identity(path, user, description, company, critical);
    }

    private static string? GetUser(IntPtr process)
    {
        if (!ProcNative.OpenProcessToken(process, ProcNative.TOKEN_QUERY, out var token)) return null;
        try
        {
            ProcNative.GetTokenInformation(token, 1, IntPtr.Zero, 0, out int len);
            if (len <= 0) return null;
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!ProcNative.GetTokenInformation(token, 1, buf, len, out _)) return null;
                var sid = Marshal.ReadIntPtr(buf);
                var name = new StringBuilder(256); var domain = new StringBuilder(256);
                int nl = name.Capacity, dl = domain.Capacity;
                return ProcNative.LookupAccountSid(null, sid, name, ref nl, domain, ref dl, out _) ? name.ToString() : null;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { ProcNative.CloseHandle(token); }
    }

    public bool IsCriticalProcess(string name, int pid) =>
        pid <= 4 || CriticalNames.Contains(name) || _identities.Any(kv => kv.Key.Pid == pid && kv.Value.Critical);

    public async Task<ProcessDetails?> GetDetailsAsync(int pid, CancellationToken ct = default)
    {
        var processes = _last ?? await GetProcessesAsync(ct).ConfigureAwait(false);
        var info = processes.FirstOrDefault(p => p.Pid == pid);
        if (info == null) return null;

        return await Task.Run(() =>
        {
            string? commandLine = null, fileVersion = null, product = null, priority = null;
            bool? is64 = null;
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
                foreach (var o in searcher.Get()) using (o) commandLine = o["CommandLine"] as string;
            }
            catch { }
            try
            {
                using var proc = Process.GetProcessById(pid);
                priority = proc.PriorityClass.ToString();
                if (ProcNative.IsWow64Process(proc.Handle, out var wow)) is64 = !wow;
            }
            catch { }
            if (info.Path != null)
            {
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(info.Path);
                    fileVersion = vi.FileVersion;
                    product = vi.ProductName;
                }
                catch { }
            }
            return new ProcessDetails
            {
                Info = info,
                CommandLine = commandLine,
                FileVersion = fileVersion,
                ProductName = product,
                ParentName = processes.FirstOrDefault(p => p.Pid == info.ParentPid)?.Name,
                Priority = priority,
                Is64Bit = is64,
            };
        }, ct).ConfigureAwait(false);
    }

    public async Task<OperationResult> KillAsync(int pid, bool entireTree = false)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            var name = proc.ProcessName;
            proc.Kill(entireTree);
            var exited = await Task.Run(() => proc.WaitForExit(5000)).ConfigureAwait(false);
            AppLog.Info($"User terminated process {name} (PID {pid}), exited={exited}");
            return exited ? OperationResult.Ok(Loc.F("{0} was terminated", name))
                          : OperationResult.Fail(Loc.F("{0} did not exit within 5 seconds", name));
        }
        catch (ArgumentException)
        {
            return OperationResult.Ok(Loc.T("The process has already exited."));
        }
        catch (Exception ex)
        {
            return OperationResult.FromException($"Terminate PID {pid}", ex);
        }
    }

    public void OpenFileLocation(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\"");
        }
        catch (Exception ex) { AppLog.Warn("Open file location failed", ex); }
    }

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; }
    }
}
