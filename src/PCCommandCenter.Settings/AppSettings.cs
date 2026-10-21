using PCCommandCenter.Core;

namespace PCCommandCenter.Settings;

/// <summary>User preferences persisted to settings.json. Secrets are never stored here.</summary>
public sealed class AppSettings
{
    // Appearance
    public string Theme { get; set; } = "Dark";
    public string Accent { get; set; } = "Indigo";
    /// <summary>Defaults to Spanish on Spanish Windows installations, English otherwise.</summary>
    public string Language { get; set; } =
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? "es" : "en";
    public bool SidebarCollapsed { get; set; }

    // Behavior
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool TrayHintShown { get; set; }

    // Monitoring
    public double RefreshIntervalSeconds { get; set; } = 1.0;
    public string LatencyTarget { get; set; } = "1.1.1.1";
    public double LatencyIntervalSeconds { get; set; } = 5;

    // Units
    public TemperatureUnit TemperatureUnit { get; set; } = TemperatureUnit.Celsius;
    public bool NetworkInBits { get; set; } = true;

    // Notifications
    public bool NotificationsEnabled { get; set; } = true;
    public bool TrayBalloons { get; set; } = true;
    public double CpuTempThreshold { get; set; } = 85;
    public double GpuTempThreshold { get; set; } = 85;
    public double RamThreshold { get; set; } = 90;
    public double DiskFreeThreshold { get; set; } = 10;
    public double LatencyThreshold { get; set; } = 150;

    // AI
    public string AiModel { get; set; } = "claude-opus-5-5";
    public string AiEffort { get; set; } = "medium";
    public bool AiIncludeSystemContext { get; set; } = true;
    public bool AiIncludeProcessPaths { get; set; } = false;

    // State
    public bool FirstRunCompleted { get; set; }
    public string? LastStorageDrive { get; set; }
    public double WindowWidth { get; set; } = 1360;
    public double WindowHeight { get; set; } = 860;
    public bool WindowMaximized { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
