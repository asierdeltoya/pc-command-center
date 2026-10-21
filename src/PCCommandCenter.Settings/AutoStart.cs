using Microsoft.Win32;
using PCCommandCenter.Core;

namespace PCCommandCenter.Settings;

/// <summary>Registers the app in HKCU\...\Run so it starts (minimized to tray) with Windows.</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = AppInfo.InternalName;
    public const string TrayArgument = "--tray";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Contains(Path.GetFileName(AppInfo.ExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static OperationResult SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled) key.SetValue(ValueName, $"\"{AppInfo.ExecutablePath}\" {TrayArgument}");
            else key.DeleteValue(ValueName, false);
            AppLog.Info($"Start with Windows set to {enabled}");
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            return OperationResult.FromException("Update auto-start", ex);
        }
    }
}
