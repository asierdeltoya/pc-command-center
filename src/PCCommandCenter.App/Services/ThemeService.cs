using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.Services;

/// <summary>Switches between dark/light palettes and accent colors at runtime (all styles use DynamicResource).</summary>
public sealed class ThemeService
{
    public static IReadOnlyList<(string Key, string Name)> Themes { get; } = [("Dark", "Dark"), ("Light", "Light"), ("System", "Use Windows setting")];

    public static IReadOnlyList<(string Key, Color Dark, Color Light)> Accents { get; } =
    [
        ("Indigo", Color.FromRgb(0x7C, 0x8C, 0xFF), Color.FromRgb(0x4F, 0x5B, 0xD5)),
        ("Violet", Color.FromRgb(0xA7, 0x8B, 0xFA), Color.FromRgb(0x7C, 0x3A, 0xED)),
        ("Blue", Color.FromRgb(0x60, 0xA5, 0xFA), Color.FromRgb(0x25, 0x63, 0xEB)),
        ("Teal", Color.FromRgb(0x2D, 0xD4, 0xBF), Color.FromRgb(0x0D, 0x94, 0x88)),
        ("Green", Color.FromRgb(0x4A, 0xDE, 0x80), Color.FromRgb(0x16, 0xA3, 0x4A)),
        ("Rose", Color.FromRgb(0xFB, 0x71, 0x85), Color.FromRgb(0xE1, 0x1D, 0x48)),
        ("Orange", Color.FromRgb(0xFB, 0x92, 0x3C), Color.FromRgb(0xEA, 0x58, 0x0C)),
    ];

    public bool IsDark { get; private set; } = true;

    public event Action? ThemeChanged;

    public void Apply(string theme, string accent)
    {
        try
        {
            IsDark = theme switch { "Light" => false, "System" => !SystemUsesLightTheme(), _ => true };
            var dict = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/PCCommandCenter;component/Themes/Colors.{(IsDark ? "Dark" : "Light")}.xaml"),
            };
            var a = Accents.FirstOrDefault(x => x.Key == accent);
            if (a.Key == null) a = Accents[0];
            var c = IsDark ? a.Dark : a.Light;
            dict["Brush.Accent"] = new SolidColorBrush(c);
            dict["Brush.AccentHover"] = new SolidColorBrush(Shift(c, IsDark ? 0.12 : -0.08));
            dict["Brush.AccentPressed"] = new SolidColorBrush(Shift(c, IsDark ? -0.1 : -0.18));
            dict["Brush.AccentSoft"] = new SolidColorBrush(Color.FromArgb((byte)(IsDark ? 0x26 : 0x1A), c.R, c.G, c.B));
            dict["Brush.AccentGradient"] = new LinearGradientBrush(c, Shift(c, IsDark ? -0.35 : 0.25), 45);
            dict["Color.Accent"] = c;

            var merged = Application.Current.Resources.MergedDictionaries;
            if (merged.Count > 0) merged[0] = dict; else merged.Insert(0, dict);
            ThemeChanged?.Invoke();
            foreach (Window w in Application.Current.Windows) ApplyWindowFrame(w);
        }
        catch (Exception ex)
        {
            AppLog.Error("Applying theme failed", ex);
        }
    }

    /// <summary>Asks DWM for a dark/light title bar and rounded corners matching the theme.</summary>
    public void ApplyWindowFrame(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
        }
        catch { /* older Windows builds don't support these attributes */ }
    }

    private static Color Shift(Color c, double amount)
    {
        byte S(byte v) => (byte)Math.Clamp(amount >= 0 ? v + (255 - v) * amount : v * (1 + amount), 0, 255);
        return Color.FromRgb(S(c.R), S(c.G), S(c.B));
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
