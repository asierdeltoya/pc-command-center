using System.ComponentModel;
using System.Globalization;

namespace PCCommandCenter.Core;

/// <summary>
/// Localization service. Keys are the English source strings; other languages map them to
/// translations and fall back to English when a key is missing. XAML binds to <see cref="Version"/>
/// so that switching language updates the UI live.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    public static IReadOnlyList<(string Code, string Name)> Languages { get; } =
    [
        ("en", "English"),
        ("es", "Español"),
    ];

    private Loc() { }

    public string Language { get; private set; } = "en";

    /// <summary>Incremented on every language change; bound by the XAML localization extension.</summary>
    public int Version { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static void SetLanguage(string code)
    {
        code = Languages.Any(l => l.Code == code) ? code : "en";
        if (Instance.Language == code) return;
        Instance.Language = code;
        Instance.Version++;
        try
        {
            var culture = CultureInfo.GetCultureInfo(code == "es" ? "es-ES" : "en-US");
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        catch { }
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(nameof(Version)));
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(nameof(Language)));
    }

    public static string T(string key)
    {
        if (string.IsNullOrEmpty(key)) return key;
        return Instance.Language == "es" && Strings.Es.TryGetValue(key, out var v) ? v : key;
    }

    public static string F(string key, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, T(key), args); }
        catch (FormatException) { return key; }
    }
}
