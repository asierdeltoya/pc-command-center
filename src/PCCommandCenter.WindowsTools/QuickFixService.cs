using PCCommandCenter.Core;

namespace PCCommandCenter.WindowsTools;

/// <summary>
/// Catalog of standard Windows repairs. Each fix is a documented command line that is shown to the user before it runs.
/// Fixes that need administrator rights run in a separate elevated console (after the UAC prompt) when the app itself
/// is not elevated, so the user can follow the output there.
/// </summary>
public sealed class QuickFixService : IQuickFixService
{
    private sealed record Definition(QuickFix Fix, string CommandLine);

    private const string Ps = "powershell -NoProfile -ExecutionPolicy Bypass -Command";

    private static readonly Definition[] Catalog =
    [
        // ── Network
        new(new QuickFix
        {
            Id = "flushdns", Category = "Network", Glyph = "", Title = "Flush DNS cache",
            Description = "Clears the list of website addresses Windows has remembered.",
            Symptoms = "A website does not load or opens an old version, \"server not found\" errors.",
            CommandPreview = "ipconfig /flushdns",
        }, "ipconfig /flushdns"),
        new(new QuickFix
        {
            Id = "renewip", Category = "Network", Glyph = "", Title = "Renew IP address",
            Description = "Releases and requests a new IP address from your router (DHCP). The connection drops for a few seconds.",
            Symptoms = "Limited connectivity, \"no internet\" after the router restarted, IP conflict warnings.",
            Risk = FixRisk.Moderate, CommandPreview = "ipconfig /release\nipconfig /renew",
        }, "ipconfig /release & ipconfig /renew"),
        new(new QuickFix
        {
            Id = "netreset", Category = "Network", Glyph = "", Title = "Reset network stack",
            Description = "Resets Winsock and the TCP/IP configuration to Windows defaults. VPN and proxy software may need to be reconfigured.",
            Symptoms = "No internet even though the router works, broken connections after malware or VPN removal.",
            RequiresAdmin = true, RestartRecommended = true, Risk = FixRisk.Moderate,
            CommandPreview = "netsh winsock reset\nnetsh int ip reset",
        }, "netsh winsock reset & netsh int ip reset"),
        new(new QuickFix
        {
            Id = "restartadapters", Category = "Network", Glyph = "", Title = "Restart network adapters",
            Description = "Disables and re-enables the connected physical network adapters (Ethernet / Wi-Fi).",
            Symptoms = "Wi-Fi connected but no traffic, very slow network that recovers after unplugging the cable.",
            RequiresAdmin = true, Risk = FixRisk.Moderate,
            CommandPreview = "Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | Restart-NetAdapter",
        }, $"{Ps} \"Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | Restart-NetAdapter -Confirm:$false; Get-NetAdapter -Physical | Format-Table Name, Status, LinkSpeed\""),
        new(new QuickFix
        {
            Id = "timesync", Category = "Network", Glyph = "", Title = "Synchronize system clock",
            Description = "Forces Windows to synchronize the time with the internet time server.",
            Symptoms = "Certificate / HTTPS errors in every browser, clock off by minutes.",
            RequiresAdmin = true, CommandPreview = "net start w32time\nw32tm /resync /force",
        }, "net start w32time & w32tm /resync /force"),

        // ── Windows
        new(new QuickFix
        {
            Id = "sfc", Category = "Windows", Glyph = "", Title = "Repair system files (SFC)",
            Description = "Scans protected Windows files and replaces corrupted ones with the original copies.",
            Symptoms = "Crashes of Windows components, settings that do not open, errors after a failed update.",
            RequiresAdmin = true, LongRunning = true, CommandPreview = "sfc /scannow",
        }, "sfc /scannow"),
        new(new QuickFix
        {
            Id = "dism", Category = "Windows", Glyph = "", Title = "Repair Windows image (DISM)",
            Description = "Repairs the Windows component store using Windows Update. Run it before SFC if SFC cannot fix files.",
            Symptoms = "Windows Update keeps failing, SFC reports files it cannot repair.",
            RequiresAdmin = true, LongRunning = true, CommandPreview = "DISM /Online /Cleanup-Image /RestoreHealth",
        }, "DISM /Online /Cleanup-Image /RestoreHealth"),
        new(new QuickFix
        {
            Id = "chkdsk", Category = "Windows", Glyph = "", Title = "Check system drive for errors",
            Description = "Online, read-only scan of the file system on the Windows drive. Nothing is changed while scanning.",
            Symptoms = "Files that cannot be opened, Explorer freezes on a folder, disk warnings in the event log.",
            RequiresAdmin = true, LongRunning = true, CommandPreview = "chkdsk %SystemDrive% /scan",
        }, "chkdsk %SystemDrive% /scan"),
        new(new QuickFix
        {
            Id = "wucache", Category = "Windows", Glyph = "", Title = "Clear Windows Update cache",
            Description = "Stops Windows Update, deletes the downloaded update files and starts it again. Updates are downloaded again.",
            Symptoms = "Updates stuck downloading or failing with the same error code.",
            RequiresAdmin = true, Risk = FixRisk.Moderate,
            CommandPreview = "net stop wuauserv & net stop bits\ndel %windir%\\SoftwareDistribution\\Download\\*\nnet start bits & net start wuauserv",
        }, "net stop wuauserv /y & net stop bits /y & del /f /s /q \"%windir%\\SoftwareDistribution\\Download\\*\" & for /d %d in (\"%windir%\\SoftwareDistribution\\Download\\*\") do @rd /s /q \"%d\" & net start bits & net start wuauserv"),

        // ── Desktop & apps
        new(new QuickFix
        {
            Id = "restartexplorer", Category = "Desktop", Glyph = "", Title = "Restart Windows Explorer",
            Description = "Restarts the taskbar, Start menu and file explorer. Open Explorer windows will close.",
            Symptoms = "Taskbar or Start menu not responding, desktop icons missing, Explorer frozen.",
            Risk = FixRisk.Moderate, CommandPreview = "taskkill /f /im explorer.exe\nstart explorer.exe",
        }, "taskkill /f /im explorer.exe & start \"\" explorer.exe"),
        new(new QuickFix
        {
            Id = "iconcache", Category = "Desktop", Glyph = "", Title = "Rebuild icon and thumbnail cache",
            Description = "Deletes the cached icons and thumbnails; Windows rebuilds them. Explorer restarts.",
            Symptoms = "Blank or wrong icons, thumbnails that do not update.",
            Risk = FixRisk.Moderate,
            CommandPreview = "taskkill /f /im explorer.exe\ndel %localappdata%\\IconCache.db\ndel %localappdata%\\Microsoft\\Windows\\Explorer\\iconcache* thumbcache*\nstart explorer.exe",
        }, "taskkill /f /im explorer.exe & del /a /f /q \"%localappdata%\\IconCache.db\" & del /a /f /q \"%localappdata%\\Microsoft\\Windows\\Explorer\\iconcache*\" & del /a /f /q \"%localappdata%\\Microsoft\\Windows\\Explorer\\thumbcache*\" & start \"\" explorer.exe"),
        new(new QuickFix
        {
            Id = "storereset", Category = "Desktop", Glyph = "", Title = "Reset Microsoft Store cache",
            Description = "Clears the Microsoft Store cache. The Store opens when it finishes.",
            Symptoms = "Store apps do not download or update, the Store does not open.",
            CommandPreview = "wsreset.exe",
        }, "wsreset.exe"),

        // ── Devices
        new(new QuickFix
        {
            Id = "audio", Category = "Devices", Glyph = "", Title = "Restart audio services",
            Description = "Restarts the Windows Audio services without rebooting.",
            Symptoms = "No sound, \"audio service is not running\", sound device missing after sleep.",
            RequiresAdmin = true, CommandPreview = "net stop audiosrv & net stop AudioEndpointBuilder\nnet start AudioEndpointBuilder & net start audiosrv",
        }, "net stop audiosrv /y & net stop AudioEndpointBuilder /y & net start AudioEndpointBuilder & net start audiosrv"),
        new(new QuickFix
        {
            Id = "printqueue", Category = "Devices", Glyph = "", Title = "Clear stuck print queue",
            Description = "Stops the print spooler, deletes pending print jobs and starts it again.",
            Symptoms = "Documents stuck in the printer queue that cannot be cancelled.",
            RequiresAdmin = true, Risk = FixRisk.Moderate,
            CommandPreview = "net stop spooler\ndel %windir%\\System32\\spool\\PRINTERS\\*\nnet start spooler",
        }, "net stop spooler /y & del /f /q \"%windir%\\System32\\spool\\PRINTERS\\*\" & net start spooler"),
        new(new QuickFix
        {
            Id = "bluetooth", Category = "Devices", Glyph = G(0xE702), Title = "Restart Bluetooth service",
            Description = "Restarts the Bluetooth support service without rebooting.",
            Symptoms = "Bluetooth devices do not connect or the Bluetooth toggle disappeared.",
            RequiresAdmin = true, CommandPreview = "net stop bthserv\nnet start bthserv",
        }, "net stop bthserv /y & net start bthserv"),
        new(new QuickFix
        {
            Id = "rescan", Category = "Devices", Glyph = G(0xE88E), Title = "Scan for hardware changes",
            Description = "Asks Windows to detect devices again, like \"Scan for hardware changes\" in Device Manager.",
            Symptoms = "A USB device, dock or second monitor is not detected until you restart.",
            RequiresAdmin = true, CommandPreview = "pnputil /scan-devices",
        }, "pnputil /scan-devices"),

        // ── Network (advanced)
        new(new QuickFix
        {
            Id = "arpcache", Category = "Network", Glyph = G(0xE968), Title = "Clear ARP and NetBIOS caches",
            Description = "Deletes the cached IP-to-MAC and NetBIOS name mappings so Windows looks them up again.",
            Symptoms = "A device that changed IP or network card is unreachable, a printer or NAS answers on the wrong address.",
            RequiresAdmin = true, CommandPreview = "netsh interface ip delete arpcache\nnbtstat -R\nnbtstat -RR",
        }, "netsh interface ip delete arpcache & nbtstat -R & nbtstat -RR"),
        new(new QuickFix
        {
            Id = "proxyreset", Category = "Network", Glyph = G(0xE774), Title = "Reset proxy settings",
            Description = "Turns off the user proxy and resets the WinHTTP proxy used by Windows services (Windows Update, Store).",
            Symptoms = "Proxy errors after removing a VPN, web filter or malware; Windows Update cannot connect but the browser can.",
            RequiresAdmin = true, Risk = FixRisk.Moderate,
            CommandPreview = "netsh winhttp reset proxy\nreg add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v ProxyEnable /d 0",
        }, "netsh winhttp reset proxy & reg add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v ProxyEnable /t REG_DWORD /d 0 /f"),
        new(new QuickFix
        {
            Id = "firewallreset", Category = "Network", Glyph = G(0xEA18), Title = "Reset Windows Firewall to defaults",
            Description = "Removes every custom firewall rule and restores the default Windows configuration.",
            Symptoms = "Programs or file sharing blocked by rules nobody remembers creating, firewall broken after uninstalling a security suite.",
            RequiresAdmin = true, Risk = FixRisk.Moderate, CommandPreview = "netsh advfirewall reset",
        }, "netsh advfirewall reset"),

        // ── Windows (advanced)
        new(new QuickFix
        {
            Id = "wureset", Category = "Windows", Glyph = G(0xE895), Title = "Fully reset Windows Update components",
            Description = "Stops the update services, renames SoftwareDistribution and catroot2 (kept as .old) and starts everything again. Stronger than clearing the cache.",
            Symptoms = "Updates still fail after clearing the cache, the same 0x8024xxxx / 0x80070002 error over and over.",
            RequiresAdmin = true, RestartRecommended = true, Risk = FixRisk.Moderate,
            CommandPreview = "net stop wuauserv / bits / cryptsvc / msiserver\nren %windir%\\SoftwareDistribution SoftwareDistribution.old\nren %windir%\\System32\\catroot2 catroot2.old\nnet start msiserver / cryptsvc / bits / wuauserv",
        }, "net stop wuauserv /y & net stop bits /y & net stop cryptsvc /y & net stop msiserver /y & rd /s /q \"%windir%\\SoftwareDistribution.old\" 2>nul & rd /s /q \"%windir%\\System32\\catroot2.old\" 2>nul & ren \"%windir%\\SoftwareDistribution\" SoftwareDistribution.old & ren \"%windir%\\System32\\catroot2\" catroot2.old & net start msiserver & net start cryptsvc & net start bits & net start wuauserv"),
        new(new QuickFix
        {
            Id = "wmi", Category = "Windows", Glyph = G(0xE9D9), Title = "Repair WMI repository",
            Description = "Checks the WMI database and rebuilds it from the existing data if it is inconsistent.",
            Symptoms = "Management agents (SCCM, Intune, antivirus) or Get-CimInstance fail with WMI errors; system information shows nothing.",
            RequiresAdmin = true, Risk = FixRisk.Moderate, CommandPreview = "winmgmt /verifyrepository\nwinmgmt /salvagerepository",
        }, "winmgmt /verifyrepository & winmgmt /salvagerepository"),
        new(new QuickFix
        {
            Id = "searchrebuild", Category = "Windows", Glyph = G(0xE721), Title = "Rebuild Windows Search index",
            Description = "Restarts Windows Search and makes it rebuild the index from scratch. Results are incomplete for a while.",
            Symptoms = "Start menu or Outlook search does not find files or emails that exist.",
            RequiresAdmin = true, Risk = FixRisk.Moderate,
            CommandPreview = "net stop wsearch\nreg add \"HKLM\\SOFTWARE\\Microsoft\\Windows Search\" /v SetupCompletedSuccessfully /d 0\nnet start wsearch",
        }, "net stop wsearch /y & reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows Search\" /v SetupCompletedSuccessfully /t REG_DWORD /d 0 /f & net start wsearch"),
        new(new QuickFix
        {
            Id = "startmenu", Category = "Windows", Glyph = G(0xE8FC), Title = "Re-register Start menu and taskbar",
            Description = "Re-registers the Start menu and shell experience packages for all users.",
            Symptoms = "Start menu does not open or closes at once, taskbar search or notifications broken after an update.",
            RequiresAdmin = true, Risk = FixRisk.Moderate,
            CommandPreview = "Get-AppxPackage -AllUsers StartMenuExperienceHost, ShellExperienceHost |\n  Add-AppxPackage -DisableDevelopmentMode -Register AppXManifest.xml",
        }, Ps + " \"'Microsoft.Windows.StartMenuExperienceHost','Microsoft.Windows.ShellExperienceHost' | ForEach-Object { Get-AppxPackage -AllUsers -Name $_ } | ForEach-Object { Add-AppxPackage -DisableDevelopmentMode -Register (Join-Path $_.InstallLocation 'AppXManifest.xml') -Verbose }\""),
        new(new QuickFix
        {
            Id = "componentcleanup", Category = "Windows", Glyph = G(0xE74D), Title = "Clean up the component store (WinSxS)",
            Description = "Removes superseded versions of updated Windows components. Frees space; installed updates can no longer be uninstalled.",
            Symptoms = "The WinSxS folder takes many GB, little free space on C: after years of updates.",
            RequiresAdmin = true, LongRunning = true, Risk = FixRisk.Moderate, CommandPreview = "DISM /Online /Cleanup-Image /StartComponentCleanup",
        }, "DISM /Online /Cleanup-Image /StartComponentCleanup"),
        new(new QuickFix
        {
            Id = "chkdskfix", Category = "Windows", Glyph = G(0xEDA2), Title = "Schedule disk repair at next restart",
            Description = "Schedules CHKDSK /F on the Windows drive. It runs before Windows starts and can take a long time on large drives.",
            Symptoms = "The CHKDSK scan found errors, NTFS errors in the event log, files that cannot be deleted.",
            RequiresAdmin = true, RestartRecommended = true, Risk = FixRisk.Moderate, CommandPreview = "chkdsk %SystemDrive% /f   (answers Y to schedule it)",
        }, "echo Y| chkdsk %SystemDrive% /f"),

        // ── Domain & policies
        new(new QuickFix
        {
            Id = "gpupdate", Category = "Domain & policies", Glyph = G(0xE8D7), Title = "Apply Group Policy now",
            Description = "Downloads and applies user and computer policies immediately instead of waiting for the next refresh.",
            Symptoms = "A new GPO (mapped drive, printer, setting) has not reached this PC yet.",
            CommandPreview = "gpupdate /force   (answers N to log off / restart)",
        }, "echo N| gpupdate /force"),
        new(new QuickFix
        {
            Id = "kerberos", Category = "Domain & policies", Glyph = G(0xE8D7), Title = "Purge Kerberos tickets",
            Description = "Deletes the current user's cached Kerberos tickets; new ones are requested on the next access.",
            Symptoms = "\"Access denied\" on shares or intranet sites after being added to a group, until you log off.",
            CommandPreview = "klist purge",
        }, "klist purge"),
        new(new QuickFix
        {
            Id = "registerdns", Category = "Domain & policies", Glyph = G(0xE968), Title = "Register this PC in DNS",
            Description = "Re-registers the computer name and IP addresses in the company DNS server.",
            Symptoms = "Other computers cannot reach this PC by name, its name resolves to an old IP.",
            RequiresAdmin = true, CommandPreview = "ipconfig /registerdns",
        }, "ipconfig /registerdns"),
        new(new QuickFix
        {
            Id = "reenabletools", Category = "Domain & policies", Glyph = G(0xE7EF), Title = "Re-enable Task Manager, Registry Editor and CMD",
            Description = "Removes the user policies that disable these tools, a common leftover of malware. On company PCs a domain policy may disable them again.",
            Symptoms = "\"Task Manager has been disabled by your administrator\" on a home PC.",
            Risk = FixRisk.Moderate,
            CommandPreview = "reg delete HKCU\\...\\Policies\\System /v DisableTaskMgr\nreg delete HKCU\\...\\Policies\\System /v DisableRegistryTools\nreg delete HKCU\\Software\\Policies\\Microsoft\\Windows\\System /v DisableCMD",
        }, "reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\" /v DisableTaskMgr /f 2>nul & reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\" /v DisableRegistryTools /f 2>nul & reg delete \"HKCU\\Software\\Policies\\Microsoft\\Windows\\System\" /v DisableCMD /f 2>nul & exit /b 0"),
        new(new QuickFix
        {
            Id = "resetgpo", Category = "Domain & policies", Glyph = G(0xE777), Title = "Reset local Group Policy",
            Description = "Deletes the local Group Policy files and reapplies policy. Settings made with gpedit.msc are lost; domain policies are downloaded again.",
            Symptoms = "Settings locked by a policy nobody set (\"Some settings are managed by your organization\" on a home PC).",
            RequiresAdmin = true, RestartRecommended = true, Risk = FixRisk.Moderate,
            CommandPreview = "rd /s /q %windir%\\System32\\GroupPolicy\nrd /s /q %windir%\\System32\\GroupPolicyUsers\ngpupdate /force",
        }, "rd /s /q \"%windir%\\System32\\GroupPolicy\" 2>nul & rd /s /q \"%windir%\\System32\\GroupPolicyUsers\" 2>nul & echo N| gpupdate /force"),

        // ── Apps & cloud
        new(new QuickFix
        {
            Id = "onedrive", Category = "Apps & cloud", Glyph = G(0xE753), Title = "Reset OneDrive",
            Description = "Resets the OneDrive sync engine and starts it again. No files are deleted; OneDrive re-checks everything, which can take a while.",
            Symptoms = "OneDrive stuck on \"Processing changes\" or \"Signing in\", sync icons missing.",
            Risk = FixRisk.Moderate, CommandPreview = "OneDrive.exe /reset\nOneDrive.exe",
        }, Ps + " \"$p = @((Join-Path $env:LOCALAPPDATA 'Microsoft\\OneDrive\\OneDrive.exe'), (Join-Path $env:ProgramFiles 'Microsoft OneDrive\\OneDrive.exe'), (Join-Path ${env:ProgramFiles(x86)} 'Microsoft OneDrive\\OneDrive.exe')) | Where-Object { Test-Path $_ } | Select-Object -First 1; if (-not $p) { 'OneDrive is not installed.'; exit 1 }; & $p /reset; Start-Sleep -Seconds 10; Start-Process $p; 'OneDrive was reset and restarted: ' + $p\""),
        new(new QuickFix
        {
            Id = "teams", Category = "Apps & cloud", Glyph = G(0xE716), Title = "Clear Microsoft Teams cache",
            Description = "Closes Teams and deletes its local cache. Chats and files live in the cloud and are not affected; you may need to sign in again.",
            Symptoms = "Teams shows a blank window, an old status, images that do not load, or will not sign in.",
            Risk = FixRisk.Moderate,
            CommandPreview = "taskkill /f /im ms-teams.exe & taskkill /f /im Teams.exe\nrd /s /q %LOCALAPPDATA%\\Packages\\MSTeams_8wekyb3d8bbwe\\LocalCache\\Microsoft\\MSTeams\nrd /s /q %APPDATA%\\Microsoft\\Teams",
        }, "taskkill /f /im ms-teams.exe 2>nul & taskkill /f /im Teams.exe 2>nul & rd /s /q \"%LOCALAPPDATA%\\Packages\\MSTeams_8wekyb3d8bbwe\\LocalCache\\Microsoft\\MSTeams\" 2>nul & rd /s /q \"%APPDATA%\\Microsoft\\Teams\" 2>nul & exit /b 0"),

        // ── Network diagnostics (read-only)
        Diag("ipall", "Network diagnostics", 0xE968, "Full IP configuration",
            "Every adapter with IP, MAC, DNS servers, DHCP lease and gateway.", "First step for any network problem.", "ipconfig /all"),
        Diag("wifi", "Network diagnostics", 0xE701, "Wi-Fi signal and connection",
            "Signal strength, channel, band, link speed and security of the current Wi-Fi connection.", "Slow or unstable Wi-Fi.", "netsh wlan show interfaces & exit /b 0", "netsh wlan show interfaces"),
        Diag("wlanreport", "Network diagnostics", 0xE701, "Wi-Fi history report (HTML)",
            "Generates the Windows report of the last 3 days of Wi-Fi sessions, disconnections and errors, and opens it.", "Wi-Fi drops from time to time.",
            "netsh wlan show wlanreport & start \"\" \"%ProgramData%\\Microsoft\\Windows\\WlanReport\\wlan-report-latest.html\"", "netsh wlan show wlanreport", admin: true),
        Diag("routes", "Network diagnostics", 0xE968, "Routing table",
            "IPv4 and IPv6 routes, interface metrics and persistent routes.", "VPN problems, traffic leaving through the wrong adapter.", "route print"),
        Diag("proxyshow", "Network diagnostics", 0xE774, "Proxy configuration",
            "WinHTTP proxy (used by services) and the user proxy and PAC script.", "Sites or Windows Update fail only on this PC.",
            "netsh winhttp show proxy & reg query \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v ProxyEnable 2>nul & reg query \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v ProxyServer 2>nul & reg query \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings\" /v AutoConfigURL 2>nul & exit /b 0",
            "netsh winhttp show proxy\nreg query \"HKCU\\...\\Internet Settings\" /v ProxyEnable, ProxyServer, AutoConfigURL"),
        Diag("hosts", "Network diagnostics", 0xE8A5, "Hosts file",
            "Shows the hosts file, which overrides DNS. Lines that do not start with # are active.", "A site redirects elsewhere or is blocked only on this PC.",
            "type \"%SystemRoot%\\System32\\drivers\\etc\\hosts\""),

        // ── User & domain diagnostics
        Diag("dsreg", "User & domain diagnostics", 0xE77B, "Domain and Entra ID join status",
            "Shows whether the PC is joined to Active Directory or Microsoft Entra ID, and the state of SSO and Windows Hello.", "Single sign-on, Intune or Windows Hello for Business problems.", "dsregcmd /status"),
        Diag("whoami", "User & domain diagnostics", 0xE77B, "User, groups and privileges",
            "The current user's SID, group memberships (with integrity level) and privileges.", "Checking group membership or why something is denied.", "whoami /all"),
        Diag("gpresult", "User & domain diagnostics", 0xE8D7, "Applied Group Policy report (HTML)",
            "Which GPOs were applied or filtered and every resulting setting. Run the app as administrator to include computer policies.", "A policy does not apply as expected.",
            "gpresult /h \"%TEMP%\\PCCommandCenter-gpresult.html\" /f & start \"\" \"%TEMP%\\PCCommandCenter-gpresult.html\"", "gpresult /h %TEMP%\\PCCommandCenter-gpresult.html /f"),
        Diag("localadmins", "User & domain diagnostics", 0xE7EF, "Local administrators",
            "Members of the local Administrators group (works with any Windows language).", "Auditing who has administrator rights on this PC.",
            Ps + " \"net localgroup ((New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-544').Translate([System.Security.Principal.NTAccount]).Value.Split('\\')[1])\"",
            "net localgroup <Administrators>"),

        // ── System diagnostics
        Diag("bsod", "System diagnostics", 0xE7BA, "Blue screen history",
            "Recent crashes (BugCheck) recorded by Windows with their stop code, and the memory dump files.", "The PC restarted with a blue screen.",
            "echo === BugCheck events === & wevtutil qe System /q:\"*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and (EventID=1001)]]\" /c:15 /rd:true /f:text & echo. & echo === %SystemRoot%\\Minidump === & dir /o-d \"%SystemRoot%\\Minidump\" & exit /b 0",
            "wevtutil qe System /q:\"...EventID=1001 (BugCheck)...\" /c:15 /rd:true /f:text\ndir %SystemRoot%\\Minidump"),
        Diag("shutdowns", "System diagnostics", 0xE7E8, "Shutdown and restart history",
            "Who or what restarted the PC (event 1074), unexpected shutdowns (6008) and power losses or hard resets (41).", "The PC turns off or restarts by itself.",
            "wevtutil qe System /q:\"*[System[(EventID=41 or EventID=6008 or EventID=1074)]]\" /c:25 /rd:true /f:text & exit /b 0",
            "wevtutil qe System /q:\"...EventID=41 or 6008 or 1074...\" /c:25 /rd:true /f:text"),
        Diag("devproblems", "System diagnostics", 0xE7BA, "Devices with problems",
            "Devices that Device Manager marks with an error, with their problem code.", "Yellow warning icons in Device Manager, a device that does not work.",
            "pnputil /enum-devices /problem & exit /b 0", "pnputil /enum-devices /problem"),
        Diag("drivers", "System diagnostics", 0xE964, "Third-party drivers",
            "Driver packages in the driver store (oem*.inf) with provider, class, version and date.", "Finding an old or problematic driver to update or remove.", "pnputil /enum-drivers"),
        Diag("sleep", "System diagnostics", 0xE708, "What keeps the PC awake",
            "Programs and devices blocking sleep right now, scheduled wake timers and what woke the PC last time.", "The PC does not go to sleep, or wakes up by itself at night.",
            "echo === powercfg /requests === & powercfg /requests & echo === powercfg /waketimers === & powercfg /waketimers & echo === powercfg /lastwake === & powercfg /lastwake",
            "powercfg /requests\npowercfg /waketimers\npowercfg /lastwake", admin: true),
        Diag("energy", "System diagnostics", 0xE945, "Energy efficiency report (HTML)",
            "Traces the system for 60 seconds and reports power problems: devices that block sleep, USB that does not suspend, timer resolution…", "Poor battery life or a PC that runs hot when idle.",
            "powercfg /energy /output \"%TEMP%\\PCCommandCenter-energy.html\" & start \"\" \"%TEMP%\\PCCommandCenter-energy.html\"", "powercfg /energy /output %TEMP%\\PCCommandCenter-energy.html", admin: true),
        Diag("hotfix", "System diagnostics", 0xE895, "Installed Windows updates",
            "Every installed update (KB) with its install date, newest first.", "Checking whether a specific KB is installed.",
            Ps + " \"Get-HotFix | Sort-Object InstalledOn -Descending | Format-Table HotFixID, Description, InstalledOn, InstalledBy -AutoSize | Out-String -Width 220\"",
            "Get-HotFix | Sort-Object InstalledOn -Descending"),
        Diag("dismcheck", "System diagnostics", 0xE9D9, "Quick Windows image health check",
            "Reports whether the component store is flagged as corrupted, without repairing anything. Takes seconds.", "Deciding whether DISM RestoreHealth is needed.",
            "DISM /Online /Cleanup-Image /CheckHealth", admin: true),
        Diag("license", "System diagnostics", 0xE8D7, "Windows license details",
            "License channel (OEM, Retail, Volume), partial product key and activation status.", "Activation errors, checking which license a PC uses.",
            "cscript //nologo \"%windir%\\System32\\slmgr.vbs\" /dli", "slmgr /dli"),
        Diag("bitlocker", "System diagnostics", 0xE72E, "BitLocker status of all drives",
            "Encryption method, percentage and protection status of every volume. Does not show recovery keys.", "Before a BIOS update or hardware change (suspend BitLocker first if it is on).",
            "manage-bde -status", admin: true),
        Diag("tpm", "System diagnostics", 0xE72E, "TPM information",
            "TPM version, manufacturer and readiness.", "Windows 11 compatibility, BitLocker or Windows Hello errors.", "tpmtool getdeviceinformation & exit /b 0", "tpmtool getdeviceinformation"),
        Diag("bcd", "System diagnostics", 0xE7E8, "Boot configuration (BCD)",
            "Boot manager entries, default OS, timeout and boot options.", "Dual boot or boot menu problems.", "bcdedit /enum", admin: true),
        Diag("systeminfo", "System diagnostics", 0xE770, "System summary",
            "OS version and install date, last boot, BIOS, memory, hotfixes, network cards and virtualization support.", "A quick summary to paste into a ticket.", "systeminfo"),

        // ── Security diagnostics
        Diag("tasks", "Security diagnostics", 0xE823, "Non-Microsoft scheduled tasks",
            "Scheduled tasks outside the \\Microsoft\\ folder, where malware and auto-updaters usually hide.", "Something starts by itself, checking a PC after malware.",
            Ps + " \"Get-ScheduledTask | Where-Object { $_.TaskPath -notlike '\\Microsoft\\*' } | Sort-Object TaskPath, TaskName | Format-Table TaskName, TaskPath, State -AutoSize | Out-String -Width 220\"",
            "Get-ScheduledTask | Where-Object TaskPath -notlike '\\Microsoft\\*'"),
        Diag("services3rd", "Security diagnostics", 0xE912, "Non-Microsoft services",
            "Services whose program is outside the Windows folder, with state, start mode and path.", "Unknown services, checking a PC after malware.",
            Ps + " \"Get-CimInstance Win32_Service | Where-Object { $_.PathName -and $_.PathName -notlike '*\\Windows\\*' } | Sort-Object State, Name | Format-Table Name, State, StartMode, PathName -AutoSize | Out-String -Width 320\"",
            "Get-CimInstance Win32_Service | Where-Object PathName -notlike '*\\Windows\\*'"),
        Diag("defenderexcl", "Security diagnostics", 0xEA18, "Microsoft Defender exclusions",
            "Folders, processes and extensions excluded from scanning. Malware often adds itself here.", "Checking a PC after malware or a suspicious installer.",
            Ps + " \"Get-MpPreference | Format-List ExclusionPath, ExclusionProcess, ExclusionExtension, ExclusionIpAddress | Out-String -Width 300\"",
            "Get-MpPreference | Format-List Exclusion*", admin: true),
    ];

    private static string G(int code) => ((char)code).ToString();

    /// <summary>A read-only diagnostic: it only reads and reports, so it runs without a confirmation dialog.</summary>
    private static Definition Diag(string id, string category, int glyph, string title, string description, string symptoms, string commandLine,
                                   string? preview = null, bool admin = false) =>
        new(new QuickFix
        {
            Id = id, Category = category, Glyph = G(glyph), Title = title, Description = description, Symptoms = symptoms,
            RequiresAdmin = admin, Risk = FixRisk.ReadOnly, CommandPreview = preview ?? commandLine,
        }, commandLine);

    public IReadOnlyList<QuickFix> Fixes { get; } = Catalog.Select(d => d.Fix).ToList();

    public async Task<FixResult> RunAsync(QuickFix fix, Action<string>? onOutput, CancellationToken ct)
    {
        var def = Catalog.FirstOrDefault(d => d.Fix.Id == fix.Id);
        if (def == null) return new FixResult { Message = Loc.T("Unknown fix") };
        AppLog.Info($"Quick fix '{fix.Id}' started by user (elevated={AppInfo.IsElevated})");

        if (fix.RequiresAdmin && !AppInfo.IsElevated)
        {
            var r = ProcessRunner.RunElevatedConsole(Loc.T(fix.Title), def.CommandLine);
            return new FixResult
            {
                Success = r.Success, RunsExternally = r.Success,
                Message = r.Success ? Loc.T("Running in an administrator window. Follow the progress there.") : r.Message,
            };
        }

        try
        {
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var (code, output) = await ProcessRunner.RunAsync(cmd, $"/d /s /c \"chcp 65001 >nul & {def.CommandLine}\"", onOutput, ct,
                timeout: fix.LongRunning ? TimeSpan.FromHours(2) : TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            AppLog.Info($"Quick fix '{fix.Id}' finished with exit code {code}");
            return new FixResult
            {
                Success = code == 0,
                Output = output,
                Message = code == 0
                    ? (fix.RestartRecommended ? Loc.T("Completed. Restart the computer to apply the changes.") : Loc.T("Completed successfully."))
                    : Loc.F("Finished with exit code {0}. Review the output.", code),
            };
        }
        catch (OperationCanceledException) { return new FixResult { Message = Loc.T("The operation was cancelled.") }; }
        catch (Exception ex)
        {
            AppLog.Warn($"Quick fix '{fix.Id}' failed", ex);
            return new FixResult { Message = OperationResult.FriendlyError(ex) };
        }
    }

    public async Task<FixResult> CreateRestorePointAsync(CancellationToken ct)
    {
        const string script = "Checkpoint-Computer -Description 'PC Command Center' -RestorePointType MODIFY_SETTINGS; " +
                              "Get-ComputerRestorePoint | Select-Object -Last 1 | Format-List Description, CreationTime";
        if (!AppInfo.IsElevated)
        {
            var r = ProcessRunner.RunElevatedConsole(Loc.T("Create restore point"), $"{Ps} \"{script}\"");
            return new FixResult { Success = r.Success, RunsExternally = r.Success, Message = r.Success ? Loc.T("Running in an administrator window. Follow the progress there.") : r.Message };
        }
        try
        {
            var (code, output) = await ProcessRunner.RunAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"", null, ct, timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            return new FixResult
            {
                Success = code == 0, Output = output,
                Message = code == 0 ? Loc.T("Restore point created.") : Loc.T("Windows could not create a restore point. System Protection may be disabled, or one was already created in the last 24 hours."),
            };
        }
        catch (Exception ex) { return new FixResult { Message = OperationResult.FriendlyError(ex) }; }
    }
}
