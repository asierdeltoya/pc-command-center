using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PCCommandCenter.Core;

namespace PCCommandCenter.WindowsTools;

/// <summary>
/// Installed desktop programs from the Uninstall registry keys (the same source as "Installed apps"), plus available
/// updates from winget. Uninstall and upgrade always run the vendor's own installer, visibly, after confirmation.
/// </summary>
public sealed partial class InstalledAppsService : IInstalledAppsService
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public Task<IReadOnlyList<InstalledApp>> GetAppsAsync(CancellationToken ct = default) => Task.Run<IReadOnlyList<InstalledApp>>(() =>
    {
        var apps = new List<InstalledApp>();
        Read(apps, RegistryHive.LocalMachine, RegistryView.Registry64, "All users");
        Read(apps, RegistryHive.LocalMachine, RegistryView.Registry32, "All users (32-bit)");
        Read(apps, RegistryHive.CurrentUser, RegistryView.Default, "Current user");
        return apps
            .GroupBy(a => (a.Name.ToLowerInvariant(), a.Version))
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }, ct);

    private static void Read(List<InstalledApp> apps, RegistryHive hive, RegistryView view, string scope)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(UninstallPath);
            if (key == null) return;
            foreach (var name in key.GetSubKeyNames())
            {
                try
                {
                    using var k = key.OpenSubKey(name);
                    if (k == null) continue;
                    var display = (k.GetValue("DisplayName") as string)?.Trim();
                    if (string.IsNullOrEmpty(display)) continue;
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (k.GetValue("ParentKeyName") != null || k.GetValue("ReleaseType") is "Update" or "Hotfix" or "Security Update") continue;
                    var uninstall = k.GetValue("UninstallString") as string;
                    if (string.IsNullOrWhiteSpace(uninstall)) continue;

                    DateTime? date = null;
                    if (k.GetValue("InstallDate") is string d && DateTime.TryParseExact(d.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                        date = parsed;
                    long? size = k.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : null;
                    var icon = (k.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim('"');

                    apps.Add(new InstalledApp
                    {
                        Name = display,
                        Publisher = (k.GetValue("Publisher") as string)?.Trim(),
                        Version = (k.GetValue("DisplayVersion") as string)?.Trim(),
                        InstallDate = date,
                        SizeBytes = size,
                        InstallLocation = (k.GetValue("InstallLocation") as string)?.Trim().Trim('"'),
                        UninstallString = uninstall,
                        QuietUninstallString = k.GetValue("QuietUninstallString") as string,
                        IconPath = icon,
                        Scope = scope,
                        RegistryKey = name,
                        IsMsi = k.GetValue("WindowsInstaller") is int wi && wi == 1,
                    });
                }
                catch { /* skip unreadable entries */ }
            }
        }
        catch (Exception ex) { AppLog.Warn($"Reading installed programs ({scope}) failed", ex); }
    }

    public OperationResult Uninstall(InstalledApp app)
    {
        try
        {
            string file, args;
            if (app.IsMsi && Regex.Match(app.RegistryKey, @"^\{[0-9A-Fa-f-]{36}\}$").Success)
            {
                file = "msiexec.exe";
                args = $"/x {app.RegistryKey}";
            }
            else
            {
                (file, args) = SplitCommand(Environment.ExpandEnvironmentVariables(app.UninstallString!));
            }
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
            AppLog.Info($"User started uninstaller for '{app.Name}': {file} {args}");
            return OperationResult.Ok(Loc.F("The uninstaller for {0} was opened. Follow its steps to finish.", app.Name));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return OperationResult.Fail(Loc.T("The elevation prompt was cancelled."));
        }
        catch (Exception ex)
        {
            return OperationResult.FromException("Uninstall " + app.Name, ex);
        }
    }

    private static (string File, string Args) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0) return (command[1..end], command[(end + 1)..].Trim());
        }
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return (command[..(exe + 4)], command[(exe + 4)..].Trim());
        var space = command.IndexOf(' ');
        return space > 0 ? (command[..space], command[(space + 1)..]) : (command, "");
    }

    // ───────────────────────────── winget ─────────────────────────────

    private static string? FindWinget()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
        if (File.Exists(alias)) return alias;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { var p = Path.Combine(dir.Trim(), "winget.exe"); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    [GeneratedRegex(@"^-{10,}\s*$")]
    private static partial Regex SeparatorRegex();

    public async Task<AppUpdateResult> GetUpdatesAsync(CancellationToken ct = default)
    {
        var winget = FindWinget();
        if (winget == null) return new AppUpdateResult { WingetAvailable = false, Error = Loc.T("winget (App Installer) is not installed. Get it from the Microsoft Store.") };
        try
        {
            var (_, output) = await ProcessRunner.RunAsync(winget, "upgrade --include-unknown --accept-source-agreements --disable-interactivity", null, ct,
                timeout: TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            return new AppUpdateResult { WingetAvailable = true, Updates = ParseTable(output) };
        }
        catch (Exception ex)
        {
            AppLog.Warn("winget upgrade failed", ex);
            return new AppUpdateResult { WingetAvailable = true, Error = OperationResult.FriendlyError(ex) };
        }
    }

    /// <summary>Parses winget's fixed-width table. Column positions come from the (localized) header line.</summary>
    internal static List<AppUpdate> ParseTable(string output)
    {
        var lines = output.Replace("\r", "").Split('\n');
        int sep = Array.FindIndex(lines, l => SeparatorRegex().IsMatch(l));
        var list = new List<AppUpdate>();
        if (sep <= 0) return list;
        var header = lines[sep - 1].TrimEnd();
        // Column starts = positions where a header word begins after whitespace.
        var starts = new List<int>();
        for (int i = 0; i < header.Length; i++)
            if (header[i] != ' ' && (i == 0 || header[i - 1] == ' ')) starts.Add(i);
        if (starts.Count < 4) return list;

        // Rows may contain wide characters; winget pads by display width, so slice by text elements.
        for (int r = sep + 1; r < lines.Length; r++)
        {
            var row = lines[r];
            if (string.IsNullOrWhiteSpace(row)) break;
            if (row.Length < starts[^1]) break; // summary line ("N upgrades available.")
            string Col(int c)
            {
                int s = starts[c], e = c + 1 < starts.Count ? starts[c + 1] : row.Length;
                if (s >= row.Length) return "";
                return row[s..Math.Min(e, row.Length)].Trim();
            }
            var name = Col(0); var id = Col(1); var version = Col(2); var available = Col(3);
            var source = starts.Count > 4 ? Col(4) : "";
            if (id.Length == 0 || available.Length == 0 || id.Contains(' ')) continue;
            list.Add(new AppUpdate(name.TrimEnd('…'), id, version, available, source));
        }
        return list;
    }

    public OperationResult Upgrade(AppUpdate update) =>
        Regex.IsMatch(update.Id, @"^[\w.\-+]+$")
            ? RunWingetConsole($"upgrade --id {update.Id} -e --accept-package-agreements --accept-source-agreements", update.Name)
            : OperationResult.Fail(Loc.T("This package cannot be updated automatically."));

    public OperationResult UpgradeAll() =>
        RunWingetConsole("upgrade --all --include-unknown --accept-package-agreements --accept-source-agreements", Loc.T("all apps"));

    private static OperationResult RunWingetConsole(string args, string what)
    {
        var winget = FindWinget();
        if (winget == null) return OperationResult.Fail(Loc.T("winget (App Installer) is not installed. Get it from the Microsoft Store."));
        try
        {
            // Unquoted /k command line: "winget" resolves through the App Execution Alias, so no path quoting is needed.
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                $"/d /k title PC Command Center - winget & winget {args} & echo. & echo Finished. You can close this window.")
            { UseShellExecute = true });
            AppLog.Info($"winget {args}");
            return OperationResult.Ok(Loc.F("Updating {0} in a separate window. Installers may ask for permission.", what));
        }
        catch (Exception ex) { return OperationResult.FromException("winget", ex); }
    }
}
