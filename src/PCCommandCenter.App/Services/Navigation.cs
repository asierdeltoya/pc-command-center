using CommunityToolkit.Mvvm.ComponentModel;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.Services;

public enum PageKey { Dashboard, Health, Processes, Network, Storage, Startup, Windows, Logs, Ai, Terminal, Settings, About, History, Security, Apps, Fixes, SpeedTest }

/// <summary>Implemented by page view models that want to react to navigation (start/stop refreshing, receive parameters).</summary>
public interface IPage
{
    void OnNavigatedTo(object? parameter);
    void OnNavigatedFrom();
}

public sealed record PageInfo(PageKey Key, string Title, string Subtitle, string Glyph, string Group);

public sealed class NavigationService : ObservableObject
{
    public static IReadOnlyList<PageInfo> Pages { get; } =
    [
        new(PageKey.Dashboard, "Dashboard", "Real-time overview of your system", G(0xE9D2), "Overview"),
        new(PageKey.History, "History", "What your PC did over the last hours and days", G(0xE81C), "Overview"),
        new(PageKey.Health, "System Health", "Scan, score and diagnose problems", G(0xE95E), "Overview"),
        new(PageKey.Security, "Security", "Antivirus, firewall, updates and protection", G(0xEA18), "Overview"),
        new(PageKey.Processes, "Processes", "Inspect and manage running processes", G(0xE9F5), "Tools"),
        new(PageKey.Apps, "Apps", "Installed programs and available updates", G(0xE71D), "Tools"),
        new(PageKey.Startup, "Startup", "Programs that run when Windows starts", G(0xE7E8), "Tools"),
        new(PageKey.Storage, "Storage Analyzer", "Find what is using disk space", G(0xEDA2), "Tools"),
        new(PageKey.Network, "Network", "Diagnostics, interfaces and connections", G(0xE968), "Tools"),
        new(PageKey.Windows, "Windows", "System tools, services and events", G(0xE770), "Tools"),
        new(PageKey.Fixes, "Repair & diagnose", "Guided repairs and read-only diagnostics for Windows", G(0xE90F), "Repair"),
        new(PageKey.SpeedTest, "Speed Tests", "Measure your internet and disk speed", G(0xEC4A), "Repair"),
        new(PageKey.Logs, "Log Analyzer", "Detect errors and patterns in log files", G(0xE9F9), "Analysis"),
        new(PageKey.Ai, "AI Assistant", "Ask questions about your PC", G(0xE99A), "Analysis"),
        new(PageKey.Terminal, "Terminal", "Run CMD and PowerShell commands", G(0xE756), "Analysis"),
        new(PageKey.Settings, "Settings", "Preferences, AI and privacy", G(0xE713), "System"),
        new(PageKey.About, "About", "Version and information", G(0xE946), "System"),
    ];

    private static string G(int code) => ((char)code).ToString();

    private PageKey _current = PageKey.Dashboard;

    public PageKey Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }

    public event Action<PageKey, object?>? Navigated;

    public void Navigate(PageKey key, object? parameter = null)
    {
        Current = key;
        AppLog.Debug($"Navigate → {key}");
        Navigated?.Invoke(key, parameter);
    }

    public static PageInfo Info(PageKey key) => Pages.First(p => p.Key == key);
}
