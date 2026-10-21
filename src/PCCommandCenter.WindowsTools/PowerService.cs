using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PCCommandCenter.Core;

namespace PCCommandCenter.WindowsTools;

/// <summary>Power plans (powercfg) and battery health (WMI, design vs. full-charge capacity).</summary>
public sealed partial class PowerService : IPowerService
{
    // powercfg prints in the console OEM code page (e.g. 850), which .NET only knows after registering the provider.
    static PowerService() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    [GeneratedRegex(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\((.+?)\)\s*(\*)?\s*$")]
    private static partial Regex PlanRegex();

    public async Task<IReadOnlyList<PowerPlan>> GetPlansAsync(CancellationToken ct = default)
    {
        try
        {
            var (_, output) = await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), "/list", null, ct,
                encoding: System.Text.Encoding.GetEncoding(GetOEMCP()), timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            var plans = new List<PowerPlan>();
            foreach (var line in output.Split('\n'))
            {
                var m = PlanRegex().Match(line.Trim());
                if (m.Success) plans.Add(new PowerPlan(Guid.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim(), m.Groups[3].Success));
            }
            return plans;
        }
        catch (Exception ex)
        {
            AppLog.Warn("powercfg /list failed", ex);
            return [];
        }
    }

    public async Task<OperationResult> SetActivePlanAsync(PowerPlan plan, CancellationToken ct = default)
    {
        try
        {
            var (code, output) = await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), $"/setactive {plan.Id}", null, ct,
                timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            AppLog.Info($"Power plan set to {plan.Name} ({plan.Id}), exit {code}");
            return code == 0 ? OperationResult.Ok(Loc.F("Power plan changed to {0}", plan.Name)) : OperationResult.Fail(output.Trim());
        }
        catch (Exception ex) { return OperationResult.FromException("Set power plan", ex); }
    }

    public Task<BatteryInfo> GetBatteryAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        bool? ac = null;
        if (GetSystemPowerStatus(out var ps)) ac = ps.ACLineStatus == 1 ? true : ps.ACLineStatus == 0 ? false : null;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, EstimatedChargeRemaining, BatteryStatus, EstimatedRunTime, Chemistry FROM Win32_Battery");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    int? charge = o["EstimatedChargeRemaining"] is ushort c ? c : null;
                    TimeSpan? runtime = o["EstimatedRunTime"] is uint rt && rt < 71582788 ? TimeSpan.FromMinutes(rt) : null;
                    var status = Convert.ToInt32(o["BatteryStatus"] ?? 0) switch
                    {
                        1 => "Discharging", 2 => "On AC power", 3 => "Fully charged", 4 => "Low", 5 => "Critical",
                        6 => "Charging", 7 => "Charging", 8 => "Charging", 9 => "Charging", 11 => "Partially charged", _ => "Unknown",
                    };
                    var chemistry = Convert.ToInt32(o["Chemistry"] ?? 0) switch { 3 => "Lead acid", 4 => "NiCd", 5 => "NiMH", 6 => "Li-ion", 7 => "Zinc air", 8 => "Li-polymer", _ => null };
                    var (design, full) = ReadCapacities();
                    return new BatteryInfo
                    {
                        Present = true, Name = (o["Name"] as string)?.Trim(), ChargePercent = charge, Status = status,
                        EstimatedRuntime = runtime, Chemistry = chemistry, DesignCapacityMWh = design, FullChargeCapacityMWh = full, OnAcPower = ac,
                    };
                }
            }
        }
        catch (Exception ex) { AppLog.Debug("Battery query failed: " + ex.Message); }
        return new BatteryInfo { Present = false, OnAcPower = ac };
    }, ct);

    private static (long? Design, long? Full) ReadCapacities()
    {
        long? design = null, full = null;
        try
        {
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData"))
                foreach (ManagementObject o in s.Get()) using (o) design = Convert.ToInt64(o["DesignedCapacity"] ?? 0L);
            using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"))
                foreach (ManagementObject o in s.Get()) using (o) full = Convert.ToInt64(o["FullChargedCapacity"] ?? 0L);
        }
        catch (Exception ex) { AppLog.Debug("Battery capacity unavailable: " + ex.Message); }
        return (design > 0 ? design : null, full > 0 ? full : null);
    }

    public async Task<OperationResult> CreateBatteryReportAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(Path.GetTempPath(), "PCCommandCenter-battery-report.html");
        try
        {
            var (code, output) = await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), $"/batteryreport /output \"{path}\"", null, ct,
                timeout: TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            if (code != 0 || !File.Exists(path)) return OperationResult.Fail(output.Trim());
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return OperationResult.Ok(Loc.T("Battery report opened in your browser."));
        }
        catch (Exception ex) { return OperationResult.FromException("Battery report", ex); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("kernel32.dll")]
    private static extern int GetOEMCP();
}
