using System.Diagnostics;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCCommandCenter.AI;
using PCCommandCenter.App.Services;
using PCCommandCenter.Core;
using PCCommandCenter.Settings;

namespace PCCommandCenter.App.ViewModels;

public sealed record Option(string Key, string Label)
{
    public override string ToString() => Label;
}

public sealed record AccentOption(string Key, Brush Brush);

/// <summary>Settings page. Every change is applied immediately and saved to settings.json (debounced).</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly IAiAssistant _ai;
    private readonly ThemeService _themeService;
    private readonly INotificationService _notify;
    private readonly DialogService _dialogs;
    private readonly IHardwareMonitor _hardware;
    private readonly IConnectivityMonitor _connectivity;
    private readonly IWindowsToolsService _windows;
    private bool _loading;

    public SettingsViewModel(ISettingsService settings, ISecretStore secrets, IAiAssistant ai, ThemeService theme, INotificationService notify,
                             DialogService dialogs, IHardwareMonitor hardware, IConnectivityMonitor connectivity, IWindowsToolsService windows)
    {
        _settings = settings;
        _secrets = secrets;
        _ai = ai;
        _themeService = theme;
        _notify = notify;
        _dialogs = dialogs;
        _hardware = hardware;
        _connectivity = connectivity;
        _windows = windows;
        Load();
    }

    public IReadOnlyList<Option> Themes { get; } = ThemeService.Themes.Select(t => new Option(t.Key, Loc.T(t.Name))).ToList();
    public IReadOnlyList<AccentOption> Accents { get; } = ThemeService.Accents.Select(a => new AccentOption(a.Key, Frozen(a.Dark))).ToList();
    public IReadOnlyList<Option> Languages { get; } = Loc.Languages.Select(l => new Option(l.Code, l.Name)).ToList();
    public IReadOnlyList<Option> TemperatureUnits { get; } = [new("C", "°C (Celsius)"), new("F", "°F (Fahrenheit)")];
    public IReadOnlyList<Option> NetworkUnits { get; } = [new("bits", "Mbps (bits per second)"), new("bytes", "MB/s (bytes per second)")];
    public IReadOnlyList<Option> AiModels { get; } = AnthropicAssistant.Models.Select(m => new Option(m.Id, m.Label)).ToList();
    public IReadOnlyList<Option> AiEfforts { get; } = AnthropicAssistant.EffortLevels.Select(e => new Option(e, Loc.T(e))).ToList();

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    [ObservableProperty] private Option? _theme;
    [ObservableProperty] private string _accent = "Indigo";
    [ObservableProperty] private Option? _language;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private double _refreshInterval;
    [ObservableProperty] private string _latencyTarget = "";
    [ObservableProperty] private double _latencyInterval;
    [ObservableProperty] private Option? _temperatureUnit;
    [ObservableProperty] private Option? _networkUnit;
    [ObservableProperty] private bool _notificationsEnabled;
    [ObservableProperty] private bool _trayBalloons;
    [ObservableProperty] private double _cpuTempThreshold;
    [ObservableProperty] private double _gpuTempThreshold;
    [ObservableProperty] private double _ramThreshold;
    [ObservableProperty] private double _diskFreeThreshold;
    [ObservableProperty] private double _latencyThreshold;
    [ObservableProperty] private Option? _aiModel;
    [ObservableProperty] private Option? _aiEffort;
    [ObservableProperty] private bool _aiIncludeSystemContext;
    [ObservableProperty] private bool _aiIncludeProcessPaths;
    [ObservableProperty] private string _apiKeyInput = "";
    [ObservableProperty] private bool _hasApiKey;
    [ObservableProperty] private string _aiStatus = "";
    [ObservableProperty] private bool _testingAi;
    [ObservableProperty] private string _section = "";

    public string DataFolder => AppInfo.DataDirectory;
    public string LogFile => AppInfo.LogFile;
    public bool IsElevated => AppInfo.IsElevated;

    public override void OnNavigatedTo(object? parameter)
    {
        base.OnNavigatedTo(parameter);
        Section = parameter as string ?? "";
        HasApiKey = _secrets.Has(AnthropicAssistant.ApiKeySecret);
        StartWithWindows = AutoStart.IsEnabled();
    }

    private void Load()
    {
        _loading = true;
        var s = _settings.Current;
        Theme = Themes.FirstOrDefault(t => t.Key == s.Theme) ?? Themes[0];
        Accent = s.Accent;
        Language = Languages.FirstOrDefault(l => l.Key == s.Language) ?? Languages[0];
        StartWithWindows = AutoStart.IsEnabled();
        MinimizeToTray = s.MinimizeToTray;
        StartMinimized = s.StartMinimized;
        RefreshInterval = s.RefreshIntervalSeconds;
        LatencyTarget = s.LatencyTarget;
        LatencyInterval = s.LatencyIntervalSeconds;
        TemperatureUnit = TemperatureUnits[s.TemperatureUnit == Core.TemperatureUnit.Fahrenheit ? 1 : 0];
        NetworkUnit = NetworkUnits[s.NetworkInBits ? 0 : 1];
        NotificationsEnabled = s.NotificationsEnabled;
        TrayBalloons = s.TrayBalloons;
        CpuTempThreshold = s.CpuTempThreshold;
        GpuTempThreshold = s.GpuTempThreshold;
        RamThreshold = s.RamThreshold;
        DiskFreeThreshold = s.DiskFreeThreshold;
        LatencyThreshold = s.LatencyThreshold;
        AiModel = AiModels.FirstOrDefault(m => m.Key == s.AiModel) ?? AiModels[0];
        AiEffort = AiEfforts.FirstOrDefault(e => e.Key == s.AiEffort) ?? AiEfforts[1];
        AiIncludeSystemContext = s.AiIncludeSystemContext;
        AiIncludeProcessPaths = s.AiIncludeProcessPaths;
        HasApiKey = _secrets.Has(AnthropicAssistant.ApiKeySecret);
        _loading = false;
    }

    private void Save(Action<AppSettings> change)
    {
        if (_loading) return;
        _settings.Update(change);
    }

    partial void OnThemeChanged(Option? value) { if (value == null) return; Save(s => s.Theme = value.Key); if (!_loading) _themeService.Apply(value.Key, Accent); }
    partial void OnAccentChanged(string value) { Save(s => s.Accent = value); if (!_loading) _themeService.Apply(Theme?.Key ?? "Dark", value); }
    partial void OnLanguageChanged(Option? value) { if (value == null) return; Save(s => s.Language = value.Key); if (!_loading) Loc.SetLanguage(value.Key); }
    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        var r = AutoStart.SetEnabled(value);
        if (!r.Success) _notify.Notify(NotificationLevel.Error, Loc.T("Unable to change startup setting"), r.Message);
        Save(s => s.StartWithWindows = value);
    }
    partial void OnMinimizeToTrayChanged(bool value) => Save(s => s.MinimizeToTray = value);
    partial void OnStartMinimizedChanged(bool value) => Save(s => s.StartMinimized = value);
    partial void OnRefreshIntervalChanged(double value)
    {
        var v = Math.Round(value * 2) / 2;
        Save(s => s.RefreshIntervalSeconds = v);
        if (!_loading) _hardware.Interval = TimeSpan.FromSeconds(v);
    }
    partial void OnLatencyTargetChanged(string value)
    {
        Save(s => s.LatencyTarget = value);
        if (!_loading && !string.IsNullOrWhiteSpace(value)) _connectivity.Target = value;
    }
    partial void OnLatencyIntervalChanged(double value)
    {
        Save(s => s.LatencyIntervalSeconds = value);
        if (!_loading) _connectivity.Interval = TimeSpan.FromSeconds(value);
    }
    partial void OnTemperatureUnitChanged(Option? value)
    {
        if (value == null) return;
        var unit = value.Key == "F" ? Core.TemperatureUnit.Fahrenheit : Core.TemperatureUnit.Celsius;
        Save(s => s.TemperatureUnit = unit);
        Fmt.TemperatureUnit = unit;
    }
    partial void OnNetworkUnitChanged(Option? value)
    {
        if (value == null) return;
        Save(s => s.NetworkInBits = value.Key == "bits");
        Fmt.NetworkInBits = value.Key == "bits";
    }
    partial void OnNotificationsEnabledChanged(bool value) => Save(s => s.NotificationsEnabled = value);
    partial void OnTrayBalloonsChanged(bool value) => Save(s => s.TrayBalloons = value);
    partial void OnCpuTempThresholdChanged(double value) => Save(s => s.CpuTempThreshold = Math.Round(value));
    partial void OnGpuTempThresholdChanged(double value) => Save(s => s.GpuTempThreshold = Math.Round(value));
    partial void OnRamThresholdChanged(double value) => Save(s => s.RamThreshold = Math.Round(value));
    partial void OnDiskFreeThresholdChanged(double value) => Save(s => s.DiskFreeThreshold = Math.Round(value));
    partial void OnLatencyThresholdChanged(double value) => Save(s => s.LatencyThreshold = Math.Round(value / 10) * 10);
    partial void OnAiModelChanged(Option? value) { if (value != null) Save(s => s.AiModel = value.Key); }
    partial void OnAiEffortChanged(Option? value) { if (value != null) Save(s => s.AiEffort = value.Key); }
    partial void OnAiIncludeSystemContextChanged(bool value) => Save(s => s.AiIncludeSystemContext = value);
    partial void OnAiIncludeProcessPathsChanged(bool value) => Save(s => s.AiIncludeProcessPaths = value);

    [RelayCommand] private void SetAccent(string key) => Accent = key;

    [RelayCommand]
    private void SaveApiKey()
    {
        var key = ApiKeyInput.Trim();
        if (key.Length < 20)
        {
            AiStatus = Loc.T("That does not look like a valid API key.");
            return;
        }
        _secrets.Set(AnthropicAssistant.ApiKeySecret, key);
        ApiKeyInput = "";
        HasApiKey = true;
        AiStatus = Loc.T("API key saved securely (encrypted with Windows DPAPI for your account).");
        AppLog.Info("AI API key updated");
    }

    [RelayCommand]
    private async Task RemoveApiKeyAsync()
    {
        if (!await _dialogs.ConfirmAsync(Loc.T("Remove the API key?"), Loc.T("The AI Assistant will stop working until a new key is added."), Loc.T("Remove"), DialogKind.Warning)) return;
        _secrets.Set(AnthropicAssistant.ApiKeySecret, null);
        HasApiKey = false;
        AiStatus = Loc.T("API key removed.");
    }

    [RelayCommand]
    private async Task TestAiAsync()
    {
        TestingAi = true;
        AiStatus = Loc.T("Testing connection…");
        var r = await _ai.TestConnectionAsync(CancellationToken.None);
        AiStatus = r.Message;
        TestingAi = false;
    }

    [RelayCommand] private void OpenDataFolder() => Open(AppInfo.DataDirectory);
    [RelayCommand] private void OpenLog() => Open(AppInfo.LogFile);
    [RelayCommand] private void OpenConsole() => Open("https://console.anthropic.com/settings/keys");

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Warn("Open failed: " + target, ex); }
    }

    [RelayCommand]
    private void RestartElevated()
    {
        var r = _windows.RestartAsAdministrator();
        if (r.Success) ((App)System.Windows.Application.Current).ExitApplication();
        else _notify.Notify(NotificationLevel.Warning, Loc.T("Not restarted"), r.Message);
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        if (!await _dialogs.ConfirmAsync(Loc.T("Reset all settings?"), Loc.T("All preferences return to their defaults. Your API key is kept."), Loc.T("Reset"), DialogKind.Warning)) return;
        _settings.Reset();
        Load();
        _themeService.Apply(_settings.Current.Theme, _settings.Current.Accent);
        Loc.SetLanguage(_settings.Current.Language);
        Fmt.TemperatureUnit = _settings.Current.TemperatureUnit;
        Fmt.NetworkInBits = _settings.Current.NetworkInBits;
        _hardware.Interval = TimeSpan.FromSeconds(_settings.Current.RefreshIntervalSeconds);
        AutoStart.SetEnabled(false);
        StartWithWindows = false;
        _notify.Notify(NotificationLevel.Success, Loc.T("Settings reset"), "");
    }
}
