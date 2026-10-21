using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PCCommandCenter.Core;

namespace PCCommandCenter.Storage;

/// <summary>
/// CrystalDiskMark-style benchmark (SEQ 1 MiB Q1T1 and RND 4 KiB Q1T1) using unbuffered, write-through I/O on a
/// temporary file, so the Windows file cache does not inflate the results. The file is always deleted afterwards.
/// </summary>
public sealed unsafe class DiskBenchmark : IDiskBenchmark
{
    private const FileOptions NoBuffering = (FileOptions)0x20000000;
    private const int SeqBlock = 1 << 20;
    private const int RndBlock = 4096;
    private static readonly TimeSpan RandomDuration = TimeSpan.FromSeconds(4);

    public Task<DiskBenchmarkResult> RunAsync(string drive, long sizeBytes, IProgress<SpeedProgress>? progress, CancellationToken ct) =>
        Task.Run(() => Run(drive, sizeBytes, progress, ct), ct);

    private static DiskBenchmarkResult Run(string drive, long size, IProgress<SpeedProgress>? progress, CancellationToken ct)
    {
        size = Math.Max(64L << 20, size / SeqBlock * SeqBlock);
        string root = Path.GetPathRoot(drive) ?? drive;
        try
        {
            var info = new DriveInfo(root);
            if (!info.IsReady) return Fail(root, size, Loc.T("The drive is not ready."));
            if (info.AvailableFreeSpace < size + (1L << 30))
                return Fail(root, size, Loc.F("Not enough free space: the test needs {0} free.", Fmt.Bytes(size + (1L << 30))));
        }
        catch (Exception ex) { return Fail(root, size, OperationResult.FriendlyError(ex)); }

        string path = PickPath(root);
        void* buffer = NativeMemory.AlignedAlloc(SeqBlock, 4096);
        try
        {
            var span = new Span<byte>(buffer, SeqBlock);
            Random.Shared.NextBytes(span); // incompressible data
            AppLog.Info($"Disk benchmark on {root} ({Fmt.Bytes(size)}) using {path}");

            // Sequential write (also creates the test file)
            progress?.Report(new SpeedProgress(Loc.T("Sequential write"), 0.05, null));
            double seqWrite;
            using (var h = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.WriteThrough | NoBuffering, size))
                seqWrite = Sequential(h, size, write: true, buffer, ct);

            progress?.Report(new SpeedProgress(Loc.T("Sequential read"), 0.3, seqWrite));
            double seqRead;
            using (var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.None, NoBuffering | FileOptions.SequentialScan))
                seqRead = Sequential(h, size, write: false, buffer, ct);

            progress?.Report(new SpeedProgress(Loc.T("Random 4K read"), 0.55, seqRead));
            double rndReadIops;
            using (var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.None, NoBuffering | FileOptions.RandomAccess))
                rndReadIops = Random4K(h, size, write: false, buffer, ct);

            progress?.Report(new SpeedProgress(Loc.T("Random 4K write"), 0.8, rndReadIops * RndBlock / 1e6));
            double rndWriteIops;
            using (var h = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, NoBuffering | FileOptions.WriteThrough | FileOptions.RandomAccess))
                rndWriteIops = Random4K(h, size, write: true, buffer, ct);

            progress?.Report(new SpeedProgress(Loc.T("Done"), 1, null));
            var result = new DiskBenchmarkResult
            {
                Drive = root, TestSizeBytes = size,
                SeqWriteMBps = seqWrite, SeqReadMBps = seqRead,
                RandReadIops = rndReadIops, RandReadMBps = rndReadIops * RndBlock / 1e6,
                RandWriteIops = rndWriteIops, RandWriteMBps = rndWriteIops * RndBlock / 1e6,
            };
            AppLog.Info($"Disk benchmark {root}: SEQ R {seqRead:0} W {seqWrite:0} MB/s, RND4K R {rndReadIops:0} W {rndWriteIops:0} IOPS");
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn("Disk benchmark failed", ex);
            return Fail(root, size, OperationResult.FriendlyError(ex));
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { AppLog.Warn("Could not delete benchmark file " + path, ex); }
        }
    }

    /// <summary>Uses the drive root when writable, otherwise (system drive without admin) the user's temp folder on that drive.</summary>
    private static string PickPath(string root)
    {
        var candidate = Path.Combine(root, $"PCCommandCenter-benchmark-{Environment.ProcessId}.tmp");
        try
        {
            using (File.Create(candidate, 1, FileOptions.DeleteOnClose)) { }
            return candidate;
        }
        catch
        {
            var temp = Path.GetTempPath();
            if (string.Equals(Path.GetPathRoot(temp), root, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(temp, $"PCCommandCenter-benchmark-{Environment.ProcessId}.tmp");
            throw new UnauthorizedAccessException(Loc.T("Windows does not allow writing a test file on this drive."));
        }
    }

    private static double Sequential(SafeFileHandle h, long size, bool write, void* buffer, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        for (long offset = 0; offset < size; offset += SeqBlock)
        {
            ct.ThrowIfCancellationRequested();
            if (write) RandomAccess.Write(h, new ReadOnlySpan<byte>(buffer, SeqBlock), offset);
            else RandomAccess.Read(h, new Span<byte>(buffer, SeqBlock), offset);
        }
        if (write) RandomAccess.FlushToDisk(h);
        return size / 1e6 / sw.Elapsed.TotalSeconds;
    }

    private static double Random4K(SafeFileHandle h, long size, bool write, void* buffer, CancellationToken ct)
    {
        long blocks = size / RndBlock;
        long ops = 0;
        var rng = new Random(42);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < RandomDuration)
        {
            ct.ThrowIfCancellationRequested();
            long offset = rng.NextInt64(blocks) * RndBlock;
            if (write) RandomAccess.Write(h, new ReadOnlySpan<byte>(buffer, RndBlock), offset);
            else RandomAccess.Read(h, new Span<byte>(buffer, RndBlock), offset);
            ops++;
        }
        return ops / sw.Elapsed.TotalSeconds;
    }

    private static DiskBenchmarkResult Fail(string drive, long size, string error) => new() { Drive = drive, TestSizeBytes = size, Error = error };
}
