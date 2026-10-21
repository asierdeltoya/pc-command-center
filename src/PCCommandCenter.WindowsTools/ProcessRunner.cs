using System.Diagnostics;
using System.Text;
using PCCommandCenter.Core;

namespace PCCommandCenter.WindowsTools;

/// <summary>Small helper to run a console tool hidden and capture its output.</summary>
internal static class ProcessRunner
{
    public static async Task<(int ExitCode, string Output)> RunAsync(string fileName, string arguments, Action<string>? onLine, CancellationToken ct,
                                                                     Encoding? encoding = null, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Without an explicit encoding, bytes are read 1:1 (Latin-1) and each line is decoded as UTF-8 or, if that fails, the OEM code page.
            StandardOutputEncoding = encoding ?? Encoding.Latin1,
            StandardErrorEncoding = encoding ?? Encoding.Latin1,
        };
        var sb = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        void Line(string? s)
        {
            if (s == null) return;
            if (encoding == null) s = DecodeRaw(s);
            // Console tools redraw progress with \r; keep only the final state of the line.
            var clean = s.Contains('\r') ? s[(s.LastIndexOf('\r') + 1)..] : s;
            clean = clean.Replace("\0", ""); // sfc/DISM write UTF-16 when redirected
            if (clean.Length == 0 && s.Length > 0) return;
            lock (sb) sb.AppendLine(clean);
            onLine?.Invoke(clean);
        }
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout.HasValue) timeoutCts.CancelAfter(timeout.Value);
        try
        {
            await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{fileName} did not finish in time");
        }
        p.WaitForExit(1000);
        lock (sb) return (p.ExitCode, sb.ToString());
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Console tools disagree on encoding (ipconfig uses the OEM code page even after chcp 65001, winget writes UTF-8).</summary>
    private static string DecodeRaw(string latin1)
    {
        var bytes = Encoding.Latin1.GetBytes(latin1);
        if (Array.TrueForAll(bytes, b => b < 0x80)) return latin1;
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { }
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP()).GetString(bytes);
        }
        catch { return latin1; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    /// <summary>Starts an elevated, visible console that runs a command line and stays open so the user can read the result.</summary>
    public static OperationResult RunElevatedConsole(string title, string commandLine)
    {
        try
        {
            var safeTitle = title.Replace("&", "and").Replace("\"", "'");
            var args = $"/d /k \"title PC Command Center - {safeTitle} & {commandLine} & echo. & echo ==================== & echo  Finished. You can close this window. & echo ====================\"";
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), args) { UseShellExecute = true, Verb = "runas" });
            AppLog.Info($"Elevated console started: {title}");
            return OperationResult.Ok();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return OperationResult.Fail(Loc.T("The elevation prompt was cancelled."));
        }
        catch (Exception ex)
        {
            return OperationResult.FromException("Elevated console", ex);
        }
    }
}
