using System.Diagnostics;
using System.Management;
using System.Reflection;
using Microsoft.Win32;
using PCCommandCenter.Core;

namespace PCCommandCenter.WindowsTools;

/// <summary>
/// Security posture of the PC from Windows Security Center, Microsoft Defender, the firewall policy, Windows Update
/// history and the registry. Each check reports "Not available" when Windows does not expose the data.
/// </summary>
public sealed class SecurityService : ISecurityService
{
    public Task<IReadOnlyList<SecurityCheck>> GetChecksAsync(CancellationToken ct = default) => Task.Run<IReadOnlyList<SecurityCheck>>(() =>
    {
        var checks = new List<SecurityCheck>();
        void Add(Func<SecurityCheck?> check, string name)
        {
            try { if (check() is { } c) checks.Add(c); }
            catch (Exception ex) { AppLog.Warn($"Security check '{name}' failed", ex); }
        }

        var defender = ReadDefender();
        Add(() => Antivirus(defender), "antivirus");
        Add(() => RealTime(defender), "realtime");
        Add(() => Signatures(defender), "signatures");
        Add(() => LastScan(defender), "scan");
        Add(Firewall, "firewall");
        Add(Updates, "updates");
        Add(Uac, "uac");
        Add(SecureBoot, "secureboot");
        Add(BitLocker, "bitlocker");
        Add(RemoteDesktop, "rdp");
        Add(Smb1, "smb1");
        Add(Activation, "activation");
        return checks;
    }, ct);

    // ───────────── Antivirus / Defender ─────────────

    private sealed record DefenderStatus(bool? RealTime, bool? AntivirusEnabled, int? SignatureAgeDays, int? QuickScanAgeDays, DateTime? SignatureUpdated, bool? TamperProtected);

    private static DefenderStatus? ReadDefender()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender",
                "SELECT RealTimeProtectionEnabled, AntivirusEnabled, AntivirusSignatureAge, QuickScanAge, AntivirusSignatureLastUpdated, IsTamperProtected FROM MSFT_MpComputerStatus");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    int? Age(object? v) => v is uint u && u < 65000 ? (int)u : null;
                    DateTime? updated = o["AntivirusSignatureLastUpdated"] is string dt ? ManagementDateTimeConverter.ToDateTime(dt) : null;
                    return new DefenderStatus(o["RealTimeProtectionEnabled"] as bool?, o["AntivirusEnabled"] as bool?, Age(o["AntivirusSignatureAge"]),
                        Age(o["QuickScanAge"]), updated, o["IsTamperProtected"] as bool?);
                }
            }
        }
        catch (Exception ex) { AppLog.Debug("Defender status unavailable: " + ex.Message); }
        return null;
    }

    private static SecurityCheck Antivirus(DefenderStatus? defender)
    {
        var products = new List<(string Name, bool Enabled, bool UpToDate)>();
        try
        {
            using var s = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    var state = Convert.ToInt32(o["productState"] ?? 0);
                    // productState: byte 1 = scanner state (0x10 = on), byte 0 = signatures (0x00 = up to date).
                    products.Add((o["displayName"] as string ?? "?", ((state >> 12) & 0xF) == 1, ((state >> 4) & 0xF) == 0));
                }
            }
        }
        catch (Exception ex) { AppLog.Debug("SecurityCenter2 unavailable: " + ex.Message); }

        if (products.Count == 0)
            return new SecurityCheck { Id = "antivirus", Title = Loc.T("Antivirus"), Status = HealthStatus.NotAvailable, Value = Loc.T("Not available"), Glyph = "\uEA18",
                Detail = Loc.T("Windows Security Center did not report any antivirus product."), OpenTarget = "windowsdefender:" };
        var active = products.Where(p => p.Enabled).ToList();
        var status = active.Count == 0 ? HealthStatus.Critical : active.All(p => p.UpToDate) ? HealthStatus.Good : HealthStatus.Warning;
        return new SecurityCheck
        {
            Id = "antivirus", Title = Loc.T("Antivirus"), Status = status, Glyph = "\uEA18", OpenTarget = "windowsdefender:",
            Value = active.Count > 0 ? string.Join(", ", active.Select(p => p.Name)) : Loc.T("Disabled"),
            Detail = Loc.F("Registered products: {0}", string.Join(", ", products.Select(p => $"{p.Name} ({(p.Enabled ? Loc.T("on") : Loc.T("off"))}{(p.UpToDate ? "" : ", " + Loc.T("outdated"))})"))),
            Recommendation = status switch
            {
                HealthStatus.Critical => Loc.T("Turn on real-time protection in Windows Security or install an antivirus."),
                HealthStatus.Warning => Loc.T("Update the antivirus definitions."),
                _ => "",
            },
        };
    }

    private static SecurityCheck? RealTime(DefenderStatus? d)
    {
        if (d?.AntivirusEnabled != true) return null; // a third-party antivirus is in charge
        return new SecurityCheck
        {
            Id = "realtime", Title = Loc.T("Real-time protection"), Glyph = "\uE83D", OpenTarget = "windowsdefender://threat",
            Status = d.RealTime == true ? HealthStatus.Good : HealthStatus.Critical,
            Value = d.RealTime == true ? Loc.T("On") : Loc.T("Off"),
            Detail = d.TamperProtected is { } tp ? Loc.F("Tamper protection: {0}", tp ? Loc.T("On") : Loc.T("Off")) : "",
            Recommendation = d.RealTime == true ? "" : Loc.T("Turn real-time protection back on in Windows Security."),
        };
    }

    private static SecurityCheck? Signatures(DefenderStatus? d)
    {
        if (d?.AntivirusEnabled != true || d.SignatureAgeDays is not { } age) return null;
        return new SecurityCheck
        {
            Id = "signatures", Title = Loc.T("Virus definitions"), Glyph = "\uE895", OpenTarget = "windowsdefender://threat",
            Status = age <= 3 ? HealthStatus.Good : age <= 7 ? HealthStatus.Warning : HealthStatus.Critical,
            Value = age == 0 ? Loc.T("Updated today") : Loc.F("{0} day(s) old", age),
            Detail = d.SignatureUpdated is { } u ? Loc.F("Last update: {0:g}", u) : "",
            Recommendation = age > 3 ? Loc.T("Check for updates in Windows Security → Virus & threat protection.") : "",
        };
    }

    private static SecurityCheck? LastScan(DefenderStatus? d)
    {
        if (d?.AntivirusEnabled != true) return null;
        var age = d.QuickScanAgeDays;
        return new SecurityCheck
        {
            Id = "scan", Title = Loc.T("Last quick scan"), Glyph = "\uE721", OpenTarget = "windowsdefender://threat",
            Status = age is null ? HealthStatus.Warning : age <= 14 ? HealthStatus.Good : HealthStatus.Warning,
            Value = age is null ? Loc.T("Never") : age == 0 ? Loc.T("Today") : Loc.F("{0} day(s) ago", age),
            Recommendation = age is null or > 14 ? Loc.T("Run a quick scan.") : "",
        };
    }

    // ───────────── Firewall ─────────────

    private static SecurityCheck Firewall()
    {
        var profiles = new List<(string Name, bool? On)>();
        foreach (var (key, name) in new[] { ("DomainProfile", "Domain"), ("StandardProfile", "Private"), ("PublicProfile", "Public") })
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{key}");
            profiles.Add((name, k?.GetValue("EnableFirewall") is int v ? v == 1 : null));
        }
        string? thirdParty = null;
        try
        {
            using var s = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM FirewallProduct");
            foreach (ManagementObject o in s.Get())
                using (o) if (((Convert.ToInt32(o["productState"] ?? 0) >> 12) & 0xF) == 1) thirdParty = o["displayName"] as string;
        }
        catch { }

        bool allOn = profiles.All(p => p.On == true);
        var status = allOn || thirdParty != null ? HealthStatus.Good
                   : profiles.Any(p => p.On == false) ? (profiles.First(p => p.Name == "Public").On == false ? HealthStatus.Critical : HealthStatus.Warning)
                   : HealthStatus.NotAvailable;
        return new SecurityCheck
        {
            Id = "firewall", Title = Loc.T("Firewall"), Glyph = "\uE8D7", Status = status, OpenTarget = "windowsdefender://network",
            Value = thirdParty ?? (allOn ? Loc.T("On for all networks") : Loc.T("Partially off")),
            Detail = string.Join(" · ", profiles.Select(p => $"{Loc.T(p.Name)}: {(p.On == true ? Loc.T("on") : p.On == false ? Loc.T("off") : "?")}")),
            Recommendation = status is HealthStatus.Warning or HealthStatus.Critical ? Loc.T("Turn the firewall on for all network profiles in Windows Security.") : "",
        };
    }

    // ───────────── Updates ─────────────

    private static SecurityCheck Updates()
    {
        DateTime? last = null;
        try
        {
            var t = Task.Run(() =>
            {
                var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
                if (type == null) return (DateTime?)null;
                dynamic session = Activator.CreateInstance(type)!;
                dynamic searcher = session.CreateUpdateSearcher();
                int count = searcher.GetTotalHistoryCount();
                if (count == 0) return null;
                dynamic history = searcher.QueryHistory(0, Math.Min(count, 50));
                DateTime? newest = null;
                foreach (dynamic entry in history)
                {
                    // Operation 1 = installation, ResultCode 2 = succeeded
                    if ((int)entry.Operation == 1 && (int)entry.ResultCode == 2)
                    {
                        DateTime date = ((DateTime)entry.Date).ToLocalTime();
                        if (newest == null || date > newest) newest = date;
                    }
                }
                return newest;
            });
            if (t.Wait(TimeSpan.FromSeconds(15))) last = t.Result;
        }
        catch (Exception ex) { AppLog.Debug("Windows Update history unavailable: " + ex.Message); }

        bool pending = false;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            pending = k != null;
        }
        catch { }

        if (last == null)
            return new SecurityCheck { Id = "updates", Title = Loc.T("Windows Update"), Glyph = "\uE895", Status = HealthStatus.NotAvailable,
                Value = Loc.T("Not available"), OpenTarget = "ms-settings:windowsupdate" };
        var days = (DateTime.Now - last.Value).TotalDays;
        return new SecurityCheck
        {
            Id = "updates", Title = Loc.T("Windows Update"), Glyph = "\uE895", OpenTarget = "ms-settings:windowsupdate",
            Status = days <= 35 ? (pending ? HealthStatus.Warning : HealthStatus.Good) : days <= 60 ? HealthStatus.Warning : HealthStatus.Critical,
            Value = Loc.F("Last update installed {0:d}", last),
            Detail = pending ? Loc.T("A restart is required to finish installing updates.") : "",
            Recommendation = days > 35 ? Loc.T("Open Windows Update and install the pending updates.") : pending ? Loc.T("Restart Windows to finish installing updates.") : "",
        };
    }

    // ───────────── Configuration ─────────────

    private static SecurityCheck Uac()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        var lua = k?.GetValue("EnableLUA") as int?;
        var prompt = k?.GetValue("ConsentPromptBehaviorAdmin") as int?;
        var status = lua == 0 ? HealthStatus.Critical : prompt == 0 ? HealthStatus.Warning : lua == 1 ? HealthStatus.Good : HealthStatus.NotAvailable;
        return new SecurityCheck
        {
            Id = "uac", Title = Loc.T("User Account Control"), Glyph = "\uE7EF", Status = status, OpenTarget = "useraccountcontrolsettings.exe",
            Value = lua == 0 ? Loc.T("Off") : prompt == 0 ? Loc.T("On, but never prompts") : Loc.T("On"),
            Recommendation = status is HealthStatus.Critical or HealthStatus.Warning ? Loc.T("Keep UAC enabled so programs cannot silently get administrator rights.") : "",
        };
    }

    private static SecurityCheck SecureBoot()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        var v = k?.GetValue("UEFISecureBootEnabled") as int?;
        return new SecurityCheck
        {
            Id = "secureboot", Title = Loc.T("Secure Boot"), Glyph = "\uE72E",
            Status = v == 1 ? HealthStatus.Good : v == 0 ? HealthStatus.Warning : HealthStatus.NotAvailable,
            Value = v == 1 ? Loc.T("On") : v == 0 ? Loc.T("Off") : Loc.T("Not available (legacy BIOS)"),
            Recommendation = v == 0 ? Loc.T("Secure Boot protects against boot-level malware. It is enabled in the UEFI firmware settings.") : "",
        };
    }

    private static SecurityCheck BitLocker()
    {
        int? state = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type != null)
            {
                var shell = Activator.CreateInstance(type);
                var drive = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
                var ns = type.InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, shell, [17]); // "This PC"
                var item = ns?.GetType().InvokeMember("ParseName", BindingFlags.InvokeMethod, null, ns, [drive + "\\"]);
                var value = item?.GetType().InvokeMember("ExtendedProperty", BindingFlags.InvokeMethod, null, item, ["System.Volume.BitLockerProtection"]);
                if (value != null) state = Convert.ToInt32(value);
            }
        }
        catch (Exception ex) { AppLog.Debug("BitLocker state unavailable: " + ex.Message); }
        // 1 = on, 2 = off, 3 = encrypting, 4 = decrypting, 5 = suspended, 6 = on (locked), 0/null = not supported
        return new SecurityCheck
        {
            Id = "bitlocker", Title = Loc.T("Drive encryption (BitLocker)"), Glyph = "\uE72E",
            Status = state is 1 or 3 or 6 ? HealthStatus.Good : state is 2 or 4 or 5 ? HealthStatus.Warning : HealthStatus.NotAvailable,
            Value = state switch
            {
                1 or 6 => Loc.T("On"), 2 => Loc.T("Off"), 3 => Loc.T("Encrypting"), 4 => Loc.T("Decrypting"), 5 => Loc.T("Suspended"),
                _ => Loc.T("Not available on this edition"),
            },
            Recommendation = state is 2 or 5 ? Loc.T("Encryption protects your data if the PC is lost or stolen (laptops especially).") : "",
        };
    }

    private static SecurityCheck RemoteDesktop()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server");
        var deny = k?.GetValue("fDenyTSConnections") as int?;
        return new SecurityCheck
        {
            Id = "rdp", Title = Loc.T("Remote Desktop"), Glyph = "\uE7F4", OpenTarget = "ms-settings:remotedesktop",
            Status = deny == 0 ? HealthStatus.Warning : deny == 1 ? HealthStatus.Good : HealthStatus.NotAvailable,
            Value = deny == 0 ? Loc.T("Enabled") : deny == 1 ? Loc.T("Disabled") : Loc.T("Not available"),
            Recommendation = deny == 0 ? Loc.T("Disable Remote Desktop if you do not use it, or make sure the account has a strong password.") : "",
        };
    }

    private static SecurityCheck Smb1()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\mrxsmb10");
        var start = k?.GetValue("Start") as int?;
        bool enabled = k != null && start is not 4;
        return new SecurityCheck
        {
            Id = "smb1", Title = Loc.T("SMBv1 protocol"), Glyph = "\uE839",
            Status = enabled ? HealthStatus.Warning : HealthStatus.Good,
            Value = enabled ? Loc.T("Installed") : Loc.T("Not installed"),
            Recommendation = enabled ? Loc.T("SMBv1 is obsolete and was used by ransomware such as WannaCry. Remove it in Windows Features unless an old device needs it.") : "",
        };
    }

    private static SecurityCheck? Activation()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT LicenseStatus FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL AND ApplicationID = '55c92734-d682-4d71-983e-d6ec3f16059f'");
            int? status = null;
            foreach (ManagementObject o in s.Get()) using (o) status = Math.Max(status ?? 0, Convert.ToInt32(o["LicenseStatus"] ?? 0));
            if (status == null) return null;
            return new SecurityCheck
            {
                Id = "activation", Title = Loc.T("Windows activation"), Glyph = "\uE8D7", OpenTarget = "ms-settings:activation",
                Status = status == 1 ? HealthStatus.Good : HealthStatus.Warning,
                Value = status == 1 ? Loc.T("Activated") : Loc.T("Not activated"),
                Recommendation = status == 1 ? "" : Loc.T("Unactivated Windows has limited personalization and may stop receiving some features."),
            };
        }
        catch { return null; }
    }

    public Task<OperationResult> StartDefenderQuickScanAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var mp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        if (!File.Exists(mp)) return OperationResult.Fail(Loc.T("Microsoft Defender is not available on this PC."));
        try
        {
            Process.Start(new ProcessStartInfo(mp, "-Scan -ScanType 1") { UseShellExecute = false, CreateNoWindow = true });
            AppLog.Info("Defender quick scan started by user");
            return OperationResult.Ok(Loc.T("Quick scan started. Windows Security will notify you if it finds a threat."));
        }
        catch (Exception ex) { return OperationResult.FromException("Defender quick scan", ex); }
    }, ct);
}
