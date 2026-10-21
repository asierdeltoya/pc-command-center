using System.Reflection;
using System.Security.Principal;

namespace PCCommandCenter.Core;

/// <summary>Static application identity and well-known locations.</summary>
public static class AppInfo
{
    public const string Name = "PC Command Center";
    public const string Tagline = "Your PC. One dashboard.";
    public const string Description = "System diagnostics and management suite.";
    public const string InternalName = "PCCommandCenter";

    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    public static string DataDirectory { get; } = EnsureDir(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), InternalName));

    public static string LogsDirectory { get; } = EnsureDir(Path.Combine(DataDirectory, "logs"));

    public static string LogFile => Path.Combine(LogsDirectory, "app.log");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string SecretsFile => Path.Combine(DataDirectory, "secrets.dat");

    public static string ExecutablePath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PCCommandCenter.exe");

    public static bool IsElevated { get; } = CheckElevated();

    private static bool CheckElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string EnsureDir(string path)
    {
        try { Directory.CreateDirectory(path); } catch { /* reported later by the logger */ }
        return path;
    }
}
