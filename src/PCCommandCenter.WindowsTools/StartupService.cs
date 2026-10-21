using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using PCCommandCenter.Core;

namespace PCCommandCenter.WindowsTools;

/// <summary>
/// Reads startup entries from the Run keys and Startup folders, and enables/disables them through the
/// same StartupApproved mechanism used by Task Manager (entries are never deleted).
/// </summary>
public sealed class StartupService : IStartupService
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string Run32Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public Task<IReadOnlyList<StartupEntry>> GetEntriesAsync(CancellationToken ct = default) => Task.Run<IReadOnlyList<StartupEntry>>(() =>
    {
        var list = new List<StartupEntry>();
        ReadRunKey(list, Registry.CurrentUser, RunPath, "HKCU\\...\\Run", "Current user", ApprovedBase + "Run", false, true);
        ReadRunKey(list, Registry.LocalMachine, RunPath, "HKLM\\...\\Run", "All users", ApprovedBase + "Run", true, true);
        ReadRunKey(list, Registry.LocalMachine, Run32Path, "HKLM\\...\\WOW6432Node\\Run", "All users (32-bit)", ApprovedBase + "Run32", true, true);
        ReadRunKey(list, Registry.CurrentUser, RunOncePath, "HKCU\\...\\RunOnce", "Current user (once)", "", false, false);
        ReadRunKey(list, Registry.LocalMachine, RunOncePath, "HKLM\\...\\RunOnce", "All users (once)", "", true, false);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Startup folder (user)", "Current user", false);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Startup folder (all users)", "All users", true);
        return list.OrderByDescending(e => e.Enabled).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }, ct);

    private static void ReadRunKey(List<StartupEntry> list, RegistryKey hive, string path, string location, string scope,
                                   string approvedPath, bool machine, bool toggleable)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key == null) return;
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                var command = key.GetValue(name)?.ToString() ?? "";
                var exe = ExtractExecutable(command);
                bool canToggle = toggleable && (!machine || AppInfo.IsElevated);
                list.Add(new StartupEntry
                {
                    Name = name,
                    Command = command,
                    ExecutablePath = exe,
                    Publisher = GetPublisher(exe),
                    Location = location,
                    Scope = scope,
                    Enabled = !toggleable || IsApproved(machine ? Registry.LocalMachine : Registry.CurrentUser, approvedPath, name),
                    CanToggle = canToggle,
                    ToggleNote = !toggleable ? "RunOnce entries run a single time and cannot be disabled"
                               : !canToggle ? "Requires administrator privileges" : null,
                    FileExists = exe != null && File.Exists(exe),
                    ApprovedKeyPath = approvedPath,
                    ApprovedInLocalMachine = machine,
                    ValueName = name,
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Reading startup key {location} failed", ex);
        }
    }

    private static void ReadFolder(List<StartupEntry> list, string folder, string location, string scope, bool machine)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(file) ?? file : file;
                bool canToggle = !machine || AppInfo.IsElevated;
                list.Add(new StartupEntry
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Command = target,
                    ExecutablePath = target,
                    Publisher = GetPublisher(target),
                    Location = location,
                    Scope = scope,
                    Enabled = IsApproved(machine ? Registry.LocalMachine : Registry.CurrentUser, ApprovedBase + "StartupFolder", fileName),
                    CanToggle = canToggle,
                    ToggleNote = canToggle ? null : "Requires administrator privileges",
                    FileExists = File.Exists(target),
                    ApprovedKeyPath = ApprovedBase + "StartupFolder",
                    ApprovedInLocalMachine = machine,
                    ValueName = fileName,
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Reading startup folder {folder} failed", ex);
        }
    }

    private static bool IsApproved(RegistryKey hive, string approvedPath, string valueName)
    {
        try
        {
            using var key = hive.OpenSubKey(approvedPath);
            // Even first byte = enabled, odd = disabled. A missing value means enabled.
            return key?.GetValue(valueName) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
        }
        catch { return true; }
    }

    public Task<OperationResult> SetEnabledAsync(StartupEntry entry, bool enabled) => Task.Run(() =>
    {
        if (!entry.CanToggle) return OperationResult.Fail(Loc.T(entry.ToggleNote ?? "This entry cannot be changed"));
        try
        {
            var hive = entry.ApprovedInLocalMachine ? Registry.LocalMachine : Registry.CurrentUser;
            using var key = hive.CreateSubKey(entry.ApprovedKeyPath, true);
            var value = new byte[12];
            if (enabled)
            {
                value[0] = 0x02;
            }
            else
            {
                value[0] = 0x03;
                BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(value, 4);
            }
            key.SetValue(entry.ValueName, value, RegistryValueKind.Binary);
            entry.Enabled = enabled;
            AppLog.Info($"Startup entry '{entry.Name}' ({entry.Location}) set to enabled={enabled}");
            return OperationResult.Ok(Loc.F(enabled ? "{0} will start with Windows" : "{0} will no longer start with Windows", entry.Name));
        }
        catch (Exception ex)
        {
            return OperationResult.FromException($"Toggle startup entry {entry.Name}", ex);
        }
    });

    public static string? ExtractExecutable(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        string candidate;
        if (expanded.StartsWith('"'))
        {
            var end = expanded.IndexOf('"', 1);
            candidate = end > 1 ? expanded[1..end] : expanded.Trim('"');
        }
        else
        {
            var idx = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            candidate = idx > 0 ? expanded[..(idx + 4)] : expanded.Split(' ')[0];
        }
        if (!Path.IsPathRooted(candidate))
        {
            var sys = Path.Combine(Environment.SystemDirectory, candidate);
            if (File.Exists(sys)) return sys;
            var win = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), candidate);
            if (File.Exists(win)) return win;
        }
        return candidate;
    }

    private static string? GetPublisher(string? exe)
    {
        if (exe == null || !File.Exists(exe)) return null;
        try
        {
            var company = FileVersionInfo.GetVersionInfo(exe).CompanyName;
            return string.IsNullOrWhiteSpace(company) ? null : company.Trim();
        }
        catch { return null; }
    }

    private static string? ResolveShortcut(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            var shell = Activator.CreateInstance(type);
            var shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [lnk]);
            var target = shortcut?.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut!);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell!);
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch { return null; }
    }
}
