using System.Globalization;

namespace PCCommandCenter.Core;

public enum TemperatureUnit { Celsius, Fahrenheit }

/// <summary>Formatting helpers honoring the user's unit preferences. Null values render as "N/A".</summary>
public static class Fmt
{
    public static TemperatureUnit TemperatureUnit { get; set; } = TemperatureUnit.Celsius;
    public static bool NetworkInBits { get; set; } = true;

    private static CultureInfo C => CultureInfo.CurrentCulture;

    public static string NA => Loc.T("N/A");

    public static string Bytes(double? bytes, int decimals = 1)
    {
        if (bytes is not { } b || double.IsNaN(b)) return NA;
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        int u = 0;
        double v = Math.Abs(b);
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        if (b < 0) v = -v;
        return u == 0 ? $"{v:0} {units[u]}" : v.ToString(v >= 100 ? "0" : "0." + new string('0', decimals), C) + " " + units[u];
    }

    /// <summary>Network throughput, in bits or bytes depending on the user's preference.</summary>
    public static string NetRate(double? bytesPerSec)
    {
        if (bytesPerSec is not { } b || double.IsNaN(b)) return NA;
        if (!NetworkInBits) return Bytes(b) + "/s";
        double bits = b * 8;
        string[] units = ["bps", "Kbps", "Mbps", "Gbps", "Tbps"];
        int u = 0;
        while (bits >= 1000 && u < units.Length - 1) { bits /= 1000; u++; }
        return bits.ToString(bits >= 100 || u == 0 ? "0" : "0.0", C) + " " + units[u];
    }

    public static string LinkSpeed(long? bitsPerSec)
    {
        if (bitsPerSec is not { } b || b <= 0) return NA;
        if (b >= 1_000_000_000) return (b / 1e9).ToString(b % 1_000_000_000 == 0 ? "0" : "0.#", C) + " Gbps";
        if (b >= 1_000_000) return (b / 1e6).ToString("0.#", C) + " Mbps";
        return (b / 1e3).ToString("0", C) + " Kbps";
    }

    public static string DiskRate(double? bytesPerSec) =>
        bytesPerSec is { } b && !double.IsNaN(b) ? Bytes(b) + "/s" : NA;

    public static string Percent(double? value, int decimals = 0) =>
        value is { } v && !double.IsNaN(v) ? v.ToString(decimals == 0 ? "0" : "0." + new string('0', decimals), C) + "%" : NA;

    public static string Temp(double? celsius)
    {
        if (celsius is not { } c || double.IsNaN(c)) return NA;
        return TemperatureUnit == TemperatureUnit.Fahrenheit
            ? (c * 9 / 5 + 32).ToString("0", C) + " °F"
            : c.ToString("0", C) + " °C";
    }

    public static string Frequency(double? mhz)
    {
        if (mhz is not { } m || double.IsNaN(m) || m <= 0) return NA;
        return m >= 1000 ? (m / 1000).ToString("0.00", C) + " GHz" : m.ToString("0", C) + " MHz";
    }

    public static string Watts(double? w) => w is { } v && !double.IsNaN(v) ? v.ToString(v >= 100 ? "0" : "0.0", C) + " W" : NA;

    public static string Ms(double? ms)
    {
        if (ms is not { } v || double.IsNaN(v)) return NA;
        return v < 1 ? "<1 ms" : v.ToString(v >= 100 ? "0" : "0.#", C) + " ms";
    }

    public static string Number(double? v, int decimals = 0) =>
        v is { } x && !double.IsNaN(x) ? x.ToString("N" + decimals, C) : NA;

    public static string Duration(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
        if (t.TotalHours >= 1) return $"{t.Hours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        if (t.TotalSeconds >= 1) return t.TotalSeconds.ToString("0.0", C) + " s";
        return t.TotalMilliseconds.ToString("0", C) + " ms";
    }

    public static string RelativeTime(DateTime time)
    {
        var d = DateTime.Now - time;
        if (d.TotalSeconds < 60) return Loc.T("just now");
        if (d.TotalMinutes < 60) return Loc.F("{0} min ago", (int)d.TotalMinutes);
        if (d.TotalHours < 24) return Loc.F("{0} h ago", (int)d.TotalHours);
        return time.ToString("g", C);
    }
}
