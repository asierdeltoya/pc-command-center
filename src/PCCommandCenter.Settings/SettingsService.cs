using System.Text.Json;
using System.Text.Json.Serialization;
using PCCommandCenter.Core;

namespace PCCommandCenter.Settings;

public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler<AppSettings>? Changed;
    void Update(Action<AppSettings> mutate);
    void Save();
    void Reset();
}

/// <summary>JSON-backed settings with debounced, atomic writes.</summary>
public sealed class JsonSettingsService : ISettingsService, IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Timer _debounce;
    private readonly object _lock = new();

    public JsonSettingsService() : this(AppInfo.SettingsFile) { }

    public JsonSettingsService(string path)
    {
        _path = path;
        _debounce = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event EventHandler<AppSettings>? Changed;

    public void Update(Action<AppSettings> mutate)
    {
        lock (_lock) mutate(Current);
        _debounce.Change(500, Timeout.Infinite);
        Changed?.Invoke(this, Current);
    }

    public void Save()
    {
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(Current, Options);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Unable to save settings", ex);
        }
    }

    public void Reset()
    {
        var firstRun = Current.FirstRunCompleted;
        lock (_lock) Current = new AppSettings { FirstRunCompleted = firstRun };
        Save();
        Changed?.Invoke(this, Current);
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options);
                if (loaded != null) return Sanitize(loaded);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Settings file is corrupt; defaults will be used", ex);
            try { File.Copy(_path, _path + ".bak", true); } catch { }
        }
        return new AppSettings();
    }

    private static AppSettings Sanitize(AppSettings s)
    {
        s.RefreshIntervalSeconds = Math.Clamp(s.RefreshIntervalSeconds, 0.5, 10);
        s.LatencyIntervalSeconds = Math.Clamp(s.LatencyIntervalSeconds, 2, 60);
        if (string.IsNullOrWhiteSpace(s.LatencyTarget)) s.LatencyTarget = "1.1.1.1";
        s.WindowWidth = Math.Clamp(s.WindowWidth, 1000, 4000);
        s.WindowHeight = Math.Clamp(s.WindowHeight, 680, 3000);
        return s;
    }

    public void Dispose()
    {
        _debounce.Dispose();
        Save();
    }
}
