using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.Controls;

public sealed class HalfRadiusConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => new CornerRadius(value is double d ? d / 2 : 3);
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>true → Visible. Parameter "invert" flips the logic. Also accepts non-empty strings, non-zero numbers and non-empty collections.</summary>
public sealed class VisibleIfConverter : IValueConverter
{
    public static readonly VisibleIfConverter Instance = new();

    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool visible = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            long l => l != 0,
            double d => d != 0 && !double.IsNaN(d),
            ICollection col => col.Count > 0,
            _ => true,
        };
        if (p is string s2 && s2 == "invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
}

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        long l => Fmt.Bytes(l),
        double d => Fmt.Bytes(d),
        int i => Fmt.Bytes(i),
        _ => Fmt.NA,
    };
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Maps severities/statuses to theme brushes. Parameter "soft" returns the translucent variant.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public static readonly StatusBrushConverter Instance = new();

    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var key = value switch
        {
            HealthStatus.Good => "Success",
            HealthStatus.Warning => "Warning",
            HealthStatus.Critical => "Danger",
            HealthStatus.NotAvailable => "Muted",
            IssueSeverity.Critical => "Danger",
            IssueSeverity.Warning => "Warning",
            IssueSeverity.Info => "Info",
            LogSeverity.Critical => "Danger",
            LogSeverity.Error => "Danger",
            LogSeverity.Warning => "Warning",
            LogSeverity.Info => "Info",
            LogSeverity.Debug => "Muted",
            NotificationLevel.Success => "Success",
            NotificationLevel.Warning => "Warning",
            NotificationLevel.Error => "Danger",
            NotificationLevel.Info => "Info",
            ConnectionState.Connected => "Success",
            ConnectionState.NoInternet => "Warning",
            ConnectionState.Disconnected => "Danger",
            PCCommandCenter.App.Services.DialogKind.Danger => "Danger",
            PCCommandCenter.App.Services.DialogKind.Warning => "Warning",
            PCCommandCenter.App.Services.DialogKind.Info => "Info",
            CommandRisk.Destructive => "Danger",
            CommandRisk.Modifies => "Warning",
            CommandRisk.Safe => "Success",
            bool b => b ? "Success" : "Muted",
            string s when s is "Healthy" or "Running" or "OK" or "Up" or "Established" or "Enabled" => "Success",
            string s when s is "Warning" or "Degraded" or "Paused" or "StartPending" or "StopPending" or "TimeWait" or "CloseWait" => "Warning",
            string s when s is "Unhealthy" or "Error" or "Critical" or "Stopped" or "Down" or "Failed" => "Danger",
            string s when s is "Listen" or "Listening" => "Info",
            _ => "Muted",
        };
        bool soft = p as string == "soft";
        if (key == "Muted") return Find(soft ? "Brush.SurfaceAlt" : "Brush.TextMuted");
        return Find($"Brush.{key}{(soft ? "Soft" : "")}");
    }

    private static object Find(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Usage percent → brush (green → amber → red).</summary>
public sealed class LoadBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        double v = value is double d ? d : 0;
        var key = v >= 90 ? "Brush.Danger" : v >= 75 ? "Brush.Warning" : p as string ?? "Brush.Accent";
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Per-core heat tile: blends the accent color by load.</summary>
public sealed class HeatBrushConverter : IValueConverter
{
    private static readonly Dictionary<int, SolidColorBrush> Cache = [];

    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        double v = Math.Clamp(value is double d ? d : 0, 0, 100);
        int bucket = (int)(v / 5);
        var accent = (Application.Current?.TryFindResource("Brush.Chart.Cpu") as SolidColorBrush)?.Color ?? Colors.SlateBlue;
        var hot = (Application.Current?.TryFindResource("Brush.Danger") as SolidColorBrush)?.Color ?? Colors.IndianRed;
        int key = bucket * 100_000 + accent.GetHashCode() % 100_000;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var f = bucket / 20.0;
        Color col = f < 0.75
            ? Color.FromArgb((byte)(40 + 215 * (f / 0.75)), accent.R, accent.G, accent.B)
            : Blend(accent, hot, (f - 0.75) / 0.25);
        var brush = new SolidColorBrush(col);
        brush.Freeze();
        Cache[key] = brush;
        return brush;
    }

    private static Color Blend(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}


/// <summary>0..1 fraction → 0..100 percent.</summary>
public sealed class ProgressToPercent : IValueConverter
{
    public static readonly ProgressToPercent Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is double d ? d * 100 : 0.0;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Visible only when every bound boolean is false.</summary>
public sealed class AllFalseVisible : IMultiValueConverter
{
    public static readonly AllFalseVisible Instance = new();
    public object Convert(object[] values, Type t, object p, CultureInfo c) =>
        values.All(v => v is false) ? Visibility.Visible : Visibility.Collapsed;
    public object[] ConvertBack(object value, Type[] t, object p, CultureInfo c) => [];
}

/// <summary>Static status values usable from XAML via x:Static (e.g. for StatusPill tags).</summary>
public static class Statuses
{
    public static readonly IssueSeverity Critical = IssueSeverity.Critical;
    public static readonly IssueSeverity Warning = IssueSeverity.Warning;
    public static readonly IssueSeverity Info = IssueSeverity.Info;
    public static readonly HealthStatus Good = HealthStatus.Good;
    public static readonly LogSeverity Error = LogSeverity.Error;
}
