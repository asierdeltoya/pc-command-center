using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PCCommandCenter.Core;

namespace PCCommandCenter.WindowsTools;

/// <summary>
/// Runs a single command in cmd.exe or Windows PowerShell with redirected, UTF-8 output.
/// Commands never run without an explicit user action; risky commands are flagged by <see cref="Assess"/>.
/// </summary>
public sealed partial class ShellService : IShellService
{
    public async Task<ShellResult> RunAsync(ShellKind shell, string command, string workingDirectory, Action<ShellOutputLine> onOutput, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (shell == ShellKind.Cmd)
        {
            psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            psi.Arguments = $"/d /s /c \"chcp 65001 >nul & {command}\"";
        }
        else
        {
            psi.FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            // Error records are written to stderr as plain text ourselves; otherwise PowerShell serializes them as CLIXML
            // when stderr is redirected. Output is formatted exactly like an interactive console.
            var script = "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; $global:__pccErr=$false\n"
                       + "try { & {\n" + command + "\n} 2>&1 | ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { [Console]::Error.WriteLine(($_ | Out-String -Width 250).TrimEnd()); $global:__pccErr=$true } else { $_ } } | Out-String -Stream -Width 250 }\n"
                       + "catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }\n"
                       + "if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE } elseif ($global:__pccErr) { exit 1 } else { exit 0 }";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            psi.Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand {encoded}";
        }

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) onOutput(new ShellOutputLine(e.Data, false)); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) onOutput(new ShellOutputLine(StripClixml(e.Data), true)); };

        try
        {
            if (!process.Start()) return new ShellResult { StartError = Loc.T("The shell could not be started.") };
        }
        catch (Exception ex)
        {
            AppLog.Warn("Shell start failed", ex);
            return new ShellResult { StartError = OperationResult.FriendlyError(ex) };
        }

        AppLog.Info($"Terminal ({shell}) ran: {command}");
        process.StandardInput.Close(); // commands that wait for input fail fast instead of hanging
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        bool cancelled = false;
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            try { process.Kill(entireProcessTree: true); } catch { }
        }
        try { process.WaitForExit(2000); } catch { }

        sw.Stop();
        return new ShellResult
        {
            ExitCode = cancelled ? null : process.HasExited ? process.ExitCode : null,
            Duration = sw.Elapsed,
            Cancelled = cancelled,
        };
    }

    private static string StripClixml(string line) =>
        line.StartsWith("#< CLIXML", StringComparison.Ordinal) ? "" : line;

    // ───────────────────────────── Safety ─────────────────────────────

    private static readonly (Regex Pattern, CommandRisk Risk, string Reason)[] Rules =
    [
        (R(@"\bformat(\.com)?\s+[a-z]:|\b(diskpart|bcdedit|bootrec|cipher\s+/w|sdelete)\b"), CommandRisk.Destructive, "Can erase disks or change boot configuration"),
        (R(@"\b(del|erase|rd|rmdir)\b"), CommandRisk.Destructive, "Deletes files or folders"),
        (R(@"\b(remove-item|ri|clear-content|clear-disk|format-volume|remove-partition|initialize-disk)\b"), CommandRisk.Destructive, "Deletes data"),
        (R(@"\breg(\.exe)?\s+(delete|add|import|restore|load|unload)\b"), CommandRisk.Destructive, "Modifies the Windows registry"),
        (R(@"\b(remove-itemproperty|set-itemproperty|new-itemproperty)\b"), CommandRisk.Modifies, "Modifies the registry or item properties"),
        (R(@"\b(shutdown|restart-computer|stop-computer|logoff)\b"), CommandRisk.Destructive, "Shuts down, restarts or logs off"),
        (R(@"\b(taskkill|stop-process|kill|tskill)\b"), CommandRisk.Modifies, "Terminates processes"),
        (R(@"\b(sc(\.exe)?\s+(stop|delete|config|create)|net\s+(stop|start|user|localgroup)|stop-service|set-service|remove-service|new-service|restart-service)\b"), CommandRisk.Modifies, "Changes services or accounts"),
        (R(@"\b(netsh|route\s+(add|delete|change)|ipconfig\s+/(release|renew|flushdns)|set-netipaddress|new-netroute|disable-netadapter|enable-netadapter|set-dnsclientserveraddress)\b"), CommandRisk.Modifies, "Changes network configuration"),
        (R(@"\b(set-executionpolicy|set-mppreference|add-mppreference|disable-windowsoptionalfeature|enable-windowsoptionalfeature|dism|sfc\s+/scannow|chkdsk\s+.*/[frx])\b"), CommandRisk.Modifies, "Changes system configuration"),
        (R(@"\b(icacls|takeown|cacls|attrib|set-acl)\b"), CommandRisk.Modifies, "Changes file permissions or attributes"),
        (R(@"\b(move|ren|rename|copy|xcopy|robocopy|move-item|rename-item|copy-item|new-item|set-content|add-content|out-file|mkdir|md)\b"), CommandRisk.Modifies, "Creates, moves or overwrites files"),
        (R(@"\b(schtasks\s+/(create|delete|change)|register-scheduledtask|unregister-scheduledtask)\b"), CommandRisk.Modifies, "Changes scheduled tasks"),
        (R(@"\b(setx|\[environment\]::setenvironmentvariable)\b"), CommandRisk.Modifies, "Changes environment variables"),
        (R(@"\b(invoke-webrequest|iwr|invoke-restmethod|irm|curl|wget|start-bitstransfer|certutil\s+.*-urlcache)\b.*(\||iex|invoke-expression|-outfile|-o\s)"), CommandRisk.Destructive, "Downloads and runs or saves remote content"),
        (R(@"\b(iex|invoke-expression)\b"), CommandRisk.Destructive, "Executes dynamically generated code"),
        (R(@"\b(winget|choco|msiexec|install-package|install-module|uninstall-package)\b"), CommandRisk.Modifies, "Installs or removes software"),
        (R(@"(^|[^>])>{1,2}\s*[^&\s]"), CommandRisk.Modifies, "Redirects output into a file"),
    ];

    private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public CommandAssessment Assess(ShellKind shell, string command)
    {
        var text = command.Trim();
        // "2>nul" and ">nul" are harmless output suppression.
        var normalized = NullRedirect().Replace(text, "");
        CommandAssessment? worst = null;
        foreach (var (pattern, risk, reason) in Rules)
        {
            if (!pattern.IsMatch(normalized)) continue;
            if (worst == null || risk > worst.Risk) worst = new CommandAssessment(risk, Loc.T(reason));
            if (risk == CommandRisk.Destructive) break;
        }
        return worst ?? new CommandAssessment(CommandRisk.Safe, "");
    }

    [GeneratedRegex(@"\d?>\s*\$?nul\b|\|\s*out-null", RegexOptions.IgnoreCase)]
    private static partial Regex NullRedirect();
}
