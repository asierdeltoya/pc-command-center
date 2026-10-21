using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using PCCommandCenter.Core;

namespace PCCommandCenter.Storage;

/// <summary>
/// Scans a drive or folder and aggregates sizes per folder, largest files and extension statistics.
/// The scanner never follows reparse points (junctions/symlinks) and never deletes anything by itself.
/// </summary>
public sealed class StorageAnalyzer : IStorageAnalyzer
{
    private const int LargestFilesCount = 100;

    public IReadOnlyList<VolumeInfo> GetDrives()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var list = new List<VolumeInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
                list.Add(new VolumeInfo
                {
                    Name = d.Name, Label = d.VolumeLabel, Format = d.DriveFormat, DriveType = d.DriveType.ToString(),
                    Total = d.TotalSize, Free = d.TotalFreeSpace, IsSystem = d.Name.Equals(system, StringComparison.OrdinalIgnoreCase),
                });
            }
            catch { }
        }
        return list;
    }

    private sealed class ScanState
    {
        public long Files, Folders, Bytes, Inaccessible;
        public string Current = "";
        public readonly object TopLock = new();
        public readonly PriorityQueue<FileEntry, long> Top = new();
        public readonly object ExtLock = new();
        public readonly Dictionary<string, (long Count, long Size)> Extensions = new(StringComparer.OrdinalIgnoreCase);
    }

    public Task<StorageScanResult> ScanAsync(string root, IProgress<ScanProgress>? progress, CancellationToken ct) =>
        Task.Run(() => Scan(root, progress, ct), ct);

    private StorageScanResult Scan(string root, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var state = new ScanState();
        var rootNode = new FolderNode { Name = root, FullPath = root };
        bool cancelled = false;

        using var reporter = new Timer(_ => progress?.Report(new ScanProgress(
            Interlocked.Read(ref state.Files), Interlocked.Read(ref state.Folders), Interlocked.Read(ref state.Bytes), state.Current)),
            null, 0, 150);

        try
        {
            // Files directly in the root, then the first-level folders in parallel.
            var children = ScanDirectoryShallow(rootNode, state);
            Parallel.ForEach(children, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8) },
                child => ScanTree(child, state, ct));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Aggregate(rootNode);
        long volTotal = 0, volFree = 0;
        try
        {
            var di = new DriveInfo(Path.GetPathRoot(root)!);
            volTotal = di.TotalSize; volFree = di.TotalFreeSpace;
        }
        catch { }

        List<FileEntry> top;
        lock (state.TopLock) top = state.Top.UnorderedItems.Select(x => x.Element).OrderByDescending(f => f.Size).ToList();
        List<ExtensionStat> ext;
        lock (state.ExtLock) ext = state.Extensions.Select(kv => new ExtensionStat(kv.Key, kv.Value.Count, kv.Value.Size)).OrderByDescending(e => e.Size).ToList();

        progress?.Report(new ScanProgress(state.Files, state.Folders, state.Bytes, ""));
        AppLog.Info($"Storage scan of {root}: {state.Files:N0} files, {Fmt.Bytes(state.Bytes)} in {sw.Elapsed.TotalSeconds:0.0}s (cancelled={cancelled})");

        return new StorageScanResult
        {
            Root = root,
            RootNode = rootNode,
            LargestFiles = top,
            Extensions = ext,
            TotalFiles = state.Files,
            TotalFolders = state.Folders,
            TotalBytes = state.Bytes,
            InaccessibleFolders = state.Inaccessible,
            Elapsed = sw.Elapsed,
            VolumeTotal = volTotal,
            VolumeFree = volFree,
            Cancelled = cancelled,
        };
    }

    private static void ScanTree(FolderNode node, ScanState state, CancellationToken ct)
    {
        var stack = new Stack<FolderNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = stack.Pop();
            foreach (var child in ScanDirectoryShallow(current, state)) stack.Push(child);
        }
    }

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Enumerates one directory: accumulates its files and returns child folder nodes.</summary>
    private static List<FolderNode> ScanDirectoryShallow(FolderNode node, ScanState state)
    {
        var children = new List<FolderNode>();
        state.Current = node.FullPath;
        try
        {
            var entries = new FileSystemEnumerable<(string Name, bool IsDir, long Size, DateTime Modified)>(
                node.FullPath,
                (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory, e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc.UtcDateTime),
                Options);

            long ownSize = 0, ownCount = 0;
            foreach (var (name, isDir, size, modified) in entries)
            {
                if (isDir)
                {
                    var child = new FolderNode { Name = name, FullPath = Path.Combine(node.FullPath, name), Parent = node };
                    children.Add(child);
                    continue;
                }
                ownSize += size; ownCount++;
                RecordFile(state, Path.Combine(node.FullPath, name), size, modified, name);
            }
            node.OwnFilesSize = ownSize;
            node.FileCount = ownCount;
            Interlocked.Add(ref state.Files, ownCount);
            Interlocked.Add(ref state.Bytes, ownSize);
            Interlocked.Add(ref state.Folders, children.Count);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            Interlocked.Increment(ref state.Inaccessible);
        }
        lock (node) node.Children.AddRange(children);
        return children;
    }

    private static void RecordFile(ScanState state, string path, long size, DateTime modified, string name)
    {
        var ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext)) ext = "(none)";
        lock (state.ExtLock)
        {
            state.Extensions.TryGetValue(ext, out var cur);
            state.Extensions[ext] = (cur.Count + 1, cur.Size + size);
        }
        if (size <= 0) return;
        lock (state.TopLock)
        {
            if (state.Top.Count < LargestFilesCount) state.Top.Enqueue(new FileEntry(path, size, modified.ToLocalTime()), size);
            else if (state.Top.TryPeek(out _, out var min) && size > min)
                state.Top.EnqueueDequeue(new FileEntry(path, size, modified.ToLocalTime()), size);
        }
    }

    /// <summary>Computes recursive sizes bottom-up and sorts children by size.</summary>
    private static void Aggregate(FolderNode root)
    {
        var order = new List<FolderNode>();
        var stack = new Stack<FolderNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            order.Add(n);
            foreach (var c in n.Children) stack.Push(c);
        }
        for (int i = order.Count - 1; i >= 0; i--)
        {
            var n = order[i];
            long size = n.OwnFilesSize, files = n.FileCount, folders = n.Children.Count;
            foreach (var c in n.Children) { size += c.Size; files += c.FileCount; folders += c.FolderCount; }
            n.Size = size; n.FileCount = files; n.FolderCount = folders;
            n.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        }
    }

    // ───────────────────────────── Cleanup ─────────────────────────────

    public Task<IReadOnlyList<CleanupTarget>> GetCleanupTargetsAsync(CancellationToken ct) => Task.Run<IReadOnlyList<CleanupTarget>>(() =>
    {
        var targets = new List<CleanupTarget>();
        var userTemp = Path.GetTempPath();
        var (size, count) = MeasureFolder(userTemp, TimeSpan.FromHours(24), ct);
        targets.Add(new CleanupTarget
        {
            Id = "user-temp", Name = Loc.T("Temporary files (user)"), Path = userTemp, Size = size, Items = count,
            Description = Loc.T("Files older than 24 hours in your temp folder. Files in use are skipped."),
        });

        var winTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        var (wsize, wcount) = MeasureFolder(winTemp, TimeSpan.FromHours(24), ct);
        targets.Add(new CleanupTarget
        {
            Id = "windows-temp", Name = Loc.T("Temporary files (Windows)"), Path = winTemp, Size = wsize, Items = wcount,
            RequiresAdmin = !AppInfo.IsElevated,
            Description = AppInfo.IsElevated ? Loc.T("Files older than 24 hours in C:\\Windows\\Temp.")
                                             : Loc.T("Requires administrator privileges to read and clean."),
        });

        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        if (SHQueryRecycleBin(null, ref info) == 0)
        {
            targets.Add(new CleanupTarget
            {
                Id = "recycle-bin", Name = Loc.T("Recycle Bin"), Size = info.i64Size, Items = info.i64NumItems,
                Description = Loc.T("Permanently removes the items currently in the Recycle Bin on all drives."),
            });
        }
        return targets;
    }, ct);

    private static (long Size, long Count) MeasureFolder(string path, TimeSpan minAge, CancellationToken ct)
    {
        long size = 0, count = 0;
        try
        {
            var cutoff = DateTime.UtcNow - minAge;
            var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                if (f.LastWriteTimeUtc > cutoff) continue;
                size += f.Length; count++;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return (size, count);
    }

    public Task<OperationResult> CleanAsync(CleanupTarget target, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            if (target.Id == "recycle-bin")
            {
                const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;
                int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                AppLog.Info($"User emptied the Recycle Bin (hr=0x{hr:X8})");
                return hr == 0 || hr == unchecked((int)0x8000FFFF) // E_UNEXPECTED when already empty
                    ? OperationResult.Ok(Loc.F("Recycle Bin emptied ({0} freed)", Fmt.Bytes(target.Size)))
                    : OperationResult.Fail(Loc.F("Unable to empty the Recycle Bin (0x{0:X8})", hr));
            }

            if (target.Path == null) return OperationResult.Fail(Loc.T("Nothing to clean."));
            long freed = 0, deleted = 0, skipped = 0;
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
            var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in new DirectoryInfo(target.Path).EnumerateFiles("*", opts).ToList())
            {
                ct.ThrowIfCancellationRequested();
                if (f.LastWriteTimeUtc > cutoff) continue;
                try
                {
                    long len = f.Length;
                    if (f.IsReadOnly) f.IsReadOnly = false;
                    f.Delete();
                    freed += len; deleted++;
                }
                catch { skipped++; }
            }
            // Remove directories left empty.
            foreach (var d in new DirectoryInfo(target.Path).EnumerateDirectories("*", opts).OrderByDescending(d => d.FullName.Length).ToList())
            {
                try { if (!d.EnumerateFileSystemInfos().Any()) d.Delete(); } catch { }
            }
            AppLog.Info($"Cleanup '{target.Id}': deleted {deleted} files ({freed} bytes), skipped {skipped}");
            return OperationResult.Ok(Loc.F("Freed {0} ({1} files deleted, {2} skipped because they are in use)", Fmt.Bytes(freed), deleted, skipped));
        }
        catch (Exception ex)
        {
            return OperationResult.FromException($"Cleanup {target.Id}", ex);
        }
    }, ct);

    public OperationResult MoveToRecycleBin(string path)
    {
        try
        {
            if (File.Exists(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else if (Directory.Exists(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else return OperationResult.Fail(Loc.T("The file or folder no longer exists."));
            AppLog.Info($"User moved to Recycle Bin: {path}");
            return OperationResult.Ok(Loc.T("Moved to the Recycle Bin"));
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Fail(Loc.T("The operation was cancelled."));
        }
        catch (Exception ex)
        {
            return OperationResult.FromException("Recycle " + path, ex);
        }
    }

    public void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex) { AppLog.Warn("Reveal in Explorer failed", ex); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}
