using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.Controls;

/// <summary>Shared helpers for the lightweight retained-mode charts.</summary>
internal static class ChartKit
{
    private static readonly Dictionary<Color, Brush> FillCache = [];

    public static Color ColorOf(Brush? b) => b is SolidColorBrush s ? s.Color : Colors.SlateBlue;

    public static Brush VerticalFill(Color c, byte topAlpha = 90)
    {
        var key = Color.FromArgb(topAlpha, c.R, c.G, c.B);
        if (FillCache.TryGetValue(key, out var cached)) return cached;
        var brush = new LinearGradientBrush(key, Color.FromArgb(0, c.R, c.G, c.B), 90);
        brush.Freeze();
        FillCache[key] = brush;
        return brush;
    }

    public static Pen Pen(Brush? brush, double thickness, bool dashed = false)
    {
        var pen = new Pen(brush ?? Brushes.Gray, thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (dashed) pen.DashStyle = new DashStyle([3, 4], 0);
        pen.Freeze();
        return pen;
    }

    public static Brush Resource(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    public static FormattedText Text(string s, double size, Brush brush, Visual v, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(v).PixelsPerDip);

    public static string Format(double v, string? format) => format switch
    {
        "percent" => Fmt.Percent(v, v < 10 ? 1 : 0),
        "bytes" => Fmt.Bytes(v),
        "disk" => Fmt.DiskRate(v),
        "net" => Fmt.NetRate(v),
        "ms" => Fmt.Ms(v),
        "temp" => Fmt.Temp(v),
        "watts" => Fmt.Watts(v),
        _ => v.ToString("0.#", CultureInfo.CurrentCulture),
    };

    public static void DrawTooltip(DrawingContext dc, Visual v, Point anchor, string text, double width)
    {
        var bg = Resource("Brush.Elevated", Colors.Black);
        var border = Resource("Brush.BorderStrong", Colors.Gray);
        var fg = Resource("Brush.Text", Colors.White);
        var ft = Text(text, 11.5, fg, v, true);
        var w = ft.Width + 16;
        var h = ft.Height + 10;
        var x = Math.Clamp(anchor.X - w / 2, 0, Math.Max(0, width - w));
        var y = Math.Max(0, anchor.Y - h - 8);
        dc.DrawRoundedRectangle(bg, Pen(border, 1), new Rect(x, y, w, h), 6, 6);
        dc.DrawText(ft, new Point(x + 8, y + 5));
    }
}

// ───────────────────────────── Area chart ─────────────────────────────

/// <summary>Streaming area/line chart (one or two series) with hover read-out.</summary>
public sealed class AreaChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = Reg(nameof(Values), typeof(double[]), null);
    public static readonly DependencyProperty Values2Property = Reg(nameof(Values2), typeof(double[]), null);
    public static readonly DependencyProperty StrokeProperty = Reg(nameof(Stroke), typeof(Brush), Brushes.SlateBlue);
    public static readonly DependencyProperty Stroke2Property = Reg(nameof(Stroke2), typeof(Brush), Brushes.HotPink);
    public static readonly DependencyProperty MaximumProperty = Reg(nameof(Maximum), typeof(double), double.NaN);
    public static readonly DependencyProperty CapacityProperty = Reg(nameof(Capacity), typeof(int), MetricHistory.DefaultCapacity);
    public static readonly DependencyProperty ShowGridProperty = Reg(nameof(ShowGrid), typeof(bool), true);
    public static readonly DependencyProperty FormatProperty = Reg(nameof(Format), typeof(string), "percent");
    public static readonly DependencyProperty SeriesNameProperty = Reg(nameof(SeriesName), typeof(string), null);
    public static readonly DependencyProperty Series2NameProperty = Reg(nameof(Series2Name), typeof(string), null);
    public static readonly DependencyProperty StrokeThicknessProperty = Reg(nameof(StrokeThickness), typeof(double), 1.8);
    /// <summary>Optional timestamps (history charts): the series then spans the full width and the hover shows the time.</summary>
    public static readonly DependencyProperty TimesProperty = Reg(nameof(Times), typeof(DateTime[]), null);
    public DateTime[]? Times { get => (DateTime[]?)GetValue(TimesProperty); set => SetValue(TimesProperty, value); }

    private static DependencyProperty Reg(string name, Type type, object? def) =>
        DependencyProperty.Register(name, type, typeof(AreaChart), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[]? Values { get => (double[]?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double[]? Values2 { get => (double[]?)GetValue(Values2Property); set => SetValue(Values2Property, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush Stroke2 { get => (Brush)GetValue(Stroke2Property); set => SetValue(Stroke2Property, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public string? SeriesName { get => (string?)GetValue(SeriesNameProperty); set => SetValue(SeriesNameProperty, value); }
    public string? Series2Name { get => (string?)GetValue(Series2NameProperty); set => SetValue(Series2NameProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    private double? _hoverX;

    public AreaChart()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    protected override void OnMouseMove(MouseEventArgs e) { _hoverX = e.GetPosition(this).X; InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { _hoverX = null; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-testable

        var a = Values ?? [];
        var b = Values2;
        double max = Maximum;
        if (double.IsNaN(max))
        {
            double m = 0;
            foreach (var v in a) if (!double.IsNaN(v)) m = Math.Max(m, v);
            if (b != null) foreach (var v in b) if (!double.IsNaN(v)) m = Math.Max(m, v);
            max = m <= 0 ? 1 : m * 1.18;
        }

        double top = 6, bottom = h - 2, plotH = bottom - top;
        if (ShowGrid)
        {
            var gridPen = ChartKit.Pen(ChartKit.Resource("Brush.Grid", Colors.DimGray), 1, dashed: true);
            for (int i = 0; i <= 3; i++)
            {
                double y = Math.Round(top + plotH * i / 3) + 0.5;
                dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            }
        }

        var times = Times;
        int cap = Math.Max(2, times is { Length: > 1 } ? a.Length : Capacity);
        double step = w / (cap - 1);

        void DrawSeries(double[] data, Brush stroke)
        {
            if (data.Length < 2) return;
            var color = ChartKit.ColorOf(stroke);
            int offset = cap - data.Length;
            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (var lc = line.Open())
            using (var ac = area.Open())
            {
                bool open = false;
                double firstX = 0, lastX = 0;
                for (int i = 0; i < data.Length; i++)
                {
                    var v = data[i];
                    double x = (offset + i) * step;
                    if (double.IsNaN(v))
                    {
                        if (open) { ac.LineTo(new Point(lastX, bottom), false, false); ac.LineTo(new Point(firstX, bottom), false, false); open = false; }
                        continue;
                    }
                    double y = bottom - Math.Clamp(v / max, 0, 1) * plotH;
                    var p = new Point(x, y);
                    if (!open)
                    {
                        lc.BeginFigure(p, false, false);
                        ac.BeginFigure(new Point(x, bottom), true, true);
                        ac.LineTo(p, false, false);
                        firstX = x; open = true;
                    }
                    else
                    {
                        lc.LineTo(p, true, true);
                        ac.LineTo(p, false, false);
                    }
                    lastX = x;
                }
                if (open) ac.LineTo(new Point(lastX, bottom), false, false);
            }
            line.Freeze(); area.Freeze();
            dc.DrawGeometry(ChartKit.VerticalFill(color), null, area);
            dc.DrawGeometry(null, ChartKit.Pen(stroke, StrokeThickness), line);
        }

        if (b != null) DrawSeries(b, Stroke2);
        DrawSeries(a, Stroke);

        if (_hoverX is { } hx && a.Length > 0)
        {
            int offset = cap - a.Length;
            int idx = (int)Math.Round(hx / step) - offset;
            if (idx >= 0 && idx < a.Length)
            {
                double x = (offset + idx) * step;
                dc.DrawLine(ChartKit.Pen(ChartKit.Resource("Brush.BorderStrong", Colors.Gray), 1), new Point(x, top), new Point(x, bottom));
                var va = a[idx];
                string label = double.IsNaN(va) ? Fmt.NA : ChartKit.Format(va, Format);
                if (SeriesName != null) label = $"{SeriesName} {label}";
                if (b != null && idx < b.Length)
                {
                    var vb = b[idx];
                    label += $"  ·  {Series2Name} {(double.IsNaN(vb) ? Fmt.NA : ChartKit.Format(vb, Format))}";
                }
                if (times != null && idx < times.Length) label += $"  ·  {times[idx]:g}";
                else if (a.Length - 1 - idx is var secondsAgo && secondsAgo > 0) label += $"  ·  -{secondsAgo}s";
                if (!double.IsNaN(va))
                {
                    double y = bottom - Math.Clamp(va / max, 0, 1) * plotH;
                    dc.DrawEllipse(ChartKit.Resource("Brush.Surface", Colors.Black), ChartKit.Pen(Stroke, 2), new Point(x, y), 4, 4);
                }
                ChartKit.DrawTooltip(dc, this, new Point(x, 30), label, w);
            }
        }
    }
}

// ───────────────────────────── Ring gauge ─────────────────────────────

/// <summary>Circular gauge with an eased value transition.</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingGauge),
        new PropertyMetadata(0.0, OnValueChanged));
    public static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(nameof(DisplayValue), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.SlateBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(7.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsAvailableProperty = DependencyProperty.Register(nameof(IsAvailable), typeof(bool), typeof(RingGauge),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double DisplayValue { get => (double)GetValue(DisplayValueProperty); set => SetValue(DisplayValueProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public bool IsAvailable { get => (bool)GetValue(IsAvailableProperty); set => SetValue(IsAvailableProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (RingGauge)d;
        var to = double.IsNaN((double)e.NewValue) ? 0 : Math.Clamp((double)e.NewValue, 0, 100);
        // Tiny changes are applied directly; animations are capped at 30 fps to keep the dashboard cheap to render.
        if (Math.Abs(to - g.DisplayValue) < 1.5)
        {
            g.BeginAnimation(DisplayValueProperty, null);
            g.DisplayValue = to;
            return;
        }
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(350)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Timeline.SetDesiredFrameRate(anim, 30);
        anim.Freeze();
        g.BeginAnimation(DisplayValueProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 4) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = (size - Thickness) / 2;
        dc.DrawEllipse(null, ChartKit.Pen(ChartKit.Resource("Brush.Track", Colors.DimGray), Thickness), center, r, r);
        if (!IsAvailable) return;
        double v = Math.Clamp(DisplayValue, 0, 100);
        if (v <= 0.2) return;
        double angle = v / 100 * 359.99;
        var start = new Point(center.X, center.Y - r);
        double rad = (angle - 90) * Math.PI / 180;
        var end = new Point(center.X + r * Math.Cos(rad), center.Y + r * Math.Sin(rad));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, angle > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, ChartKit.Pen(Stroke, Thickness), geo);
    }
}

// ───────────────────────────── Bar chart ─────────────────────────────

/// <summary>Vertical bars, used for ping probes. NaN values are drawn as failure markers.</summary>
public sealed class BarChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(double[]), typeof(BarChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(BarChart),
        new FrameworkPropertyMetadata(Brushes.SlateBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SlotsProperty = DependencyProperty.Register(nameof(Slots), typeof(int), typeof(BarChart),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[]? Values { get => (double[]?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public int Slots { get => (int)GetValue(SlotsProperty); set => SetValue(SlotsProperty, value); }

    private int? _hover;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var data = Values ?? [];
        int slots = Math.Max(Slots, data.Length);
        _hover = (int)(e.GetPosition(this).X / (ActualWidth / Math.Max(1, slots))) - (slots - data.Length);
        InvalidateVisual();
    }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover = null; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var data = Values ?? [];
        int slots = Math.Max(Slots, data.Length);
        double slot = w / slots;
        double max = 1;
        foreach (var v in data) if (!double.IsNaN(v)) max = Math.Max(max, v);
        max *= 1.15;
        var grid = ChartKit.Pen(ChartKit.Resource("Brush.Grid", Colors.DimGray), 1, true);
        for (int i = 1; i <= 3; i++) dc.DrawLine(grid, new Point(0, h * i / 4), new Point(w, h * i / 4));
        var fail = ChartKit.Resource("Brush.Danger", Colors.IndianRed);
        int offset = slots - data.Length;
        for (int i = 0; i < data.Length; i++)
        {
            double x = (offset + i) * slot + slot * 0.18;
            double bw = Math.Max(1.5, slot * 0.64);
            if (double.IsNaN(data[i]))
            {
                dc.DrawRoundedRectangle(fail, null, new Rect(x, h - 6, bw, 6), 2, 2);
                continue;
            }
            double bh = Math.Max(2, data[i] / max * (h - 4));
            dc.DrawRoundedRectangle(i == _hover ? ChartKit.Resource("Brush.AccentHover", Colors.White) : Fill, null, new Rect(x, h - bh, bw, bh), 2, 2);
        }
        if (_hover is { } hi && hi >= 0 && hi < data.Length)
        {
            var text = double.IsNaN(data[hi]) ? Loc.T("Timed out") : Fmt.Ms(data[hi]);
            ChartKit.DrawTooltip(dc, this, new Point((offset + hi + 0.5) * slot, 30), $"#{hi + 1}  {text}", w);
        }
    }
}

// ───────────────────────────── Stacked timeline ─────────────────────────────

public sealed class TimelineChart : FrameworkElement
{
    public static readonly DependencyProperty BucketsProperty = DependencyProperty.Register(nameof(Buckets), typeof(IReadOnlyList<TimelineBucket>), typeof(TimelineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public IReadOnlyList<TimelineBucket>? Buckets { get => (IReadOnlyList<TimelineBucket>?)GetValue(BucketsProperty); set => SetValue(BucketsProperty, value); }

    private int? _hover;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var n = Buckets?.Count ?? 0;
        if (n == 0) return;
        _hover = (int)(e.GetPosition(this).X / (ActualWidth / n));
        InvalidateVisual();
    }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover = null; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight - 16;
        if (w < 4 || h < 4) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        var buckets = Buckets;
        if (buckets == null || buckets.Count == 0) return;
        int max = Math.Max(1, buckets.Max(b => b.Info + b.Warning + b.Error + b.Critical));
        double slot = w / buckets.Count;
        var info = ChartKit.Resource("Brush.Info", Colors.SteelBlue);
        var infoSoft = new SolidColorBrush(Color.FromArgb(110, ChartKit.ColorOf(info).R, ChartKit.ColorOf(info).G, ChartKit.ColorOf(info).B));
        var warn = ChartKit.Resource("Brush.Warning", Colors.Orange);
        var err = ChartKit.Resource("Brush.Danger", Colors.IndianRed);
        var crit = new SolidColorBrush(Color.FromRgb(0xE1, 0x1D, 0x48));
        for (int i = 0; i < buckets.Count; i++)
        {
            var b = buckets[i];
            double x = i * slot + slot * 0.12, bw = Math.Max(1, slot * 0.76), y = h;
            void Seg(int count, Brush brush)
            {
                if (count <= 0) return;
                double sh = Math.Max(1.5, count / (double)max * (h - 4));
                y -= sh;
                dc.DrawRectangle(brush, null, new Rect(x, y, bw, sh));
            }
            Seg(b.Info, infoSoft); Seg(b.Warning, warn); Seg(b.Error, err); Seg(b.Critical, crit);
        }
        var muted = ChartKit.Resource("Brush.TextMuted", Colors.Gray);
        var first = ChartKit.Text(buckets[0].Start.ToString("g", CultureInfo.CurrentCulture), 10.5, muted, this);
        var last = ChartKit.Text(buckets[^1].Start.ToString("g", CultureInfo.CurrentCulture), 10.5, muted, this);
        dc.DrawText(first, new Point(0, h + 2));
        dc.DrawText(last, new Point(Math.Max(first.Width + 10, w - last.Width), h + 2));
        if (_hover is { } hi && hi >= 0 && hi < buckets.Count)
        {
            var b = buckets[hi];
            ChartKit.DrawTooltip(dc, this, new Point((hi + 0.5) * slot, 34),
                $"{b.Start:g} · C {b.Critical} · E {b.Error} · W {b.Warning} · I {b.Info}", w);
        }
    }
}

// ───────────────────────────── Donut ─────────────────────────────

public sealed class ChartSegment
{
    public string Label { get; init; } = "";
    public double Value { get; init; }
    public Brush Brush { get; init; } = Brushes.SlateBlue;
    public string? Detail { get; init; }
}

public sealed class DonutChart : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(nameof(Segments), typeof(IEnumerable), typeof(DonutChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(DonutChart),
        new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public IEnumerable? Segments { get => (IEnumerable?)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    private int _hover = -1;
    private Point _mouse;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        var segs = Segments?.OfType<ChartSegment>().ToList() ?? [];
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var angle = (Math.Atan2(_mouse.Y - c.Y, _mouse.X - c.X) * 180 / Math.PI + 450) % 360;
        double total = segs.Sum(s => s.Value), acc = 0;
        _hover = -1;
        for (int i = 0; i < segs.Count && total > 0; i++)
        {
            acc += segs[i].Value / total * 360;
            if (angle <= acc) { _hover = i; break; }
        }
        InvalidateVisual();
    }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover = -1; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 10) return;
        var segs = Segments?.OfType<ChartSegment>().Where(s => s.Value > 0).ToList() ?? [];
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = (size - Thickness) / 2 - 2;
        dc.DrawEllipse(Brushes.Transparent, ChartKit.Pen(ChartKit.Resource("Brush.Track", Colors.DimGray), Thickness), c, r, r);
        double total = segs.Sum(s => s.Value);
        if (total <= 0) return;
        double startAngle = -90;
        for (int i = 0; i < segs.Count; i++)
        {
            double sweep = segs[i].Value / total * 360;
            if (sweep < 0.4) { startAngle += sweep; continue; }
            double gap = segs.Count > 1 ? Math.Min(1.2, sweep / 4) : 0;
            var geo = Arc(c, r, startAngle + gap / 2, Math.Min(359.9, sweep - gap));
            dc.DrawGeometry(null, new Pen(segs[i].Brush, i == _hover ? Thickness + 4 : Thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat }, geo);
            startAngle += sweep;
        }
        if (_hover >= 0 && _hover < segs.Count)
        {
            var s = segs[_hover];
            ChartKit.DrawTooltip(dc, this, new Point(_mouse.X, _mouse.Y - 4), $"{s.Label}  {s.Detail ?? Fmt.Percent(s.Value / total * 100, 1)}", ActualWidth);
        }
    }

    private static Geometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        Point P(double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(startDeg), false, false);
            ctx.ArcTo(P(startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}

// ───────────────────────────── Treemap ─────────────────────────────

public sealed class TreeMapItem
{
    public string Label { get; init; } = "";
    public double Value { get; init; }
    public object? Tag { get; init; }
    public string? Detail { get; init; }
}

/// <summary>Squarified treemap. Clicking a tile executes <see cref="ItemCommand"/> with the item's Tag.</summary>
public sealed class TreeMap : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IEnumerable), typeof(TreeMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ItemCommandProperty = DependencyProperty.Register(nameof(ItemCommand), typeof(ICommand), typeof(TreeMap));
    public IEnumerable? Items { get => (IEnumerable?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public ICommand? ItemCommand { get => (ICommand?)GetValue(ItemCommandProperty); set => SetValue(ItemCommandProperty, value); }

    private readonly List<(Rect Rect, TreeMapItem Item, int Index)> _layout = [];
    private int _hover = -1;
    private static readonly string[] PaletteKeys = ["Brush.Chart.Cpu", "Brush.Chart.Gpu", "Brush.Chart.Ram", "Brush.Chart.Disk", "Brush.Chart.Rx", "Brush.Chart.Tx", "Brush.Chart.Latency"];

    public TreeMap() { Cursor = Cursors.Hand; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var hit = _layout.FindIndex(l => l.Rect.Contains(p));
        if (hit != _hover) { _hover = hit; InvalidateVisual(); }
    }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover = -1; InvalidateVisual(); }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        var hit = _layout.FirstOrDefault(l => l.Rect.Contains(p));
        if (hit.Item != null && ItemCommand?.CanExecute(hit.Item.Tag) == true) ItemCommand.Execute(hit.Item.Tag);
    }

    protected override void OnRender(DrawingContext dc)
    {
        _layout.Clear();
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        var items = Items?.OfType<TreeMapItem>().Where(i => i.Value > 0).OrderByDescending(i => i.Value).Take(40).ToList() ?? [];
        if (items.Count == 0) return;
        double total = items.Sum(i => i.Value);
        var scaled = items.Select(i => i.Value / total * w * h).ToList();
        Squarify(scaled, 0, new Rect(0, 0, w, h), items);

        var text = ChartKit.Resource("Brush.Text", Colors.White);
        var muted = ChartKit.Resource("Brush.TextSecondary", Colors.Gray);
        foreach (var (rect, item, index) in _layout)
        {
            var color = ChartKit.ColorOf(ChartKit.Resource(PaletteKeys[index % PaletteKeys.Length], Colors.SlateBlue));
            byte alpha = (byte)(index == _hover ? 150 : Math.Max(50, 115 - index * 3));
            var fill = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            var r = new Rect(rect.X + 1.5, rect.Y + 1.5, Math.Max(0, rect.Width - 3), Math.Max(0, rect.Height - 3));
            dc.DrawRoundedRectangle(fill, index == _hover ? ChartKit.Pen(new SolidColorBrush(color), 1.5) : null, r, 6, 6);
            if (r.Width > 60 && r.Height > 34)
            {
                dc.PushClip(new RectangleGeometry(r));
                var label = ChartKit.Text(item.Label, 12, text, this, true);
                label.MaxTextWidth = r.Width - 16; label.MaxLineCount = 1; label.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(label, new Point(r.X + 8, r.Y + 7));
                var detail = ChartKit.Text(item.Detail ?? "", 11, muted, this);
                detail.MaxTextWidth = r.Width - 16; detail.MaxLineCount = 1; detail.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(detail, new Point(r.X + 8, r.Y + 24));
                dc.Pop();
            }
        }
        if (_hover >= 0 && _hover < _layout.Count)
        {
            var (rect, item, _) = _layout[_hover];
            ChartKit.DrawTooltip(dc, this, new Point(rect.X + rect.Width / 2, Math.Max(40, rect.Y + 30)), $"{item.Label} · {item.Detail}", w);
        }
    }

    private void Squarify(List<double> values, int start, Rect rect, List<TreeMapItem> items)
    {
        while (start < values.Count)
        {
            double shortSide = Math.Min(rect.Width, rect.Height);
            if (shortSide <= 0) return;
            int end = start + 1;
            double rowSum = values[start];
            double worst = Worst(values, start, end, rowSum, shortSide);
            while (end < values.Count)
            {
                double newSum = rowSum + values[end];
                double newWorst = Worst(values, start, end + 1, newSum, shortSide);
                if (newWorst > worst) break;
                rowSum = newSum; worst = newWorst; end++;
            }
            bool horizontal = rect.Width >= rect.Height;
            double thickness = rowSum / shortSide;
            double offset = 0;
            for (int i = start; i < end; i++)
            {
                double len = values[i] / thickness;
                var r = horizontal
                    ? new Rect(rect.X, rect.Y + offset, thickness, len)
                    : new Rect(rect.X + offset, rect.Y, len, thickness);
                _layout.Add((r, items[i], i));
                offset += len;
            }
            rect = horizontal
                ? new Rect(rect.X + thickness, rect.Y, Math.Max(0, rect.Width - thickness), rect.Height)
                : new Rect(rect.X, rect.Y + thickness, rect.Width, Math.Max(0, rect.Height - thickness));
            start = end;
        }
    }

    private static double Worst(List<double> values, int start, int end, double sum, double side)
    {
        double max = double.MinValue, min = double.MaxValue;
        for (int i = start; i < end; i++) { max = Math.Max(max, values[i]); min = Math.Min(min, values[i]); }
        double s2 = side * side, sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }
}
