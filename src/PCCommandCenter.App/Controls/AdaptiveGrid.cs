using System.Windows;
using System.Windows.Controls;

namespace PCCommandCenter.App.Controls;

/// <summary>
/// Responsive grid: the number of columns adapts to the available width (MinColumnWidth), children can span
/// several columns, and every row stretches its children to the tallest one.
/// </summary>
public sealed class AdaptiveGrid : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(nameof(MinColumnWidth), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(320.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ColumnSpanProperty = DependencyProperty.RegisterAttached("ColumnSpan", typeof(int), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
    /// <summary>When true the child always takes a full row.</summary>
    public static readonly DependencyProperty FullRowProperty = DependencyProperty.RegisterAttached("FullRow", typeof(bool), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public static int GetColumnSpan(DependencyObject o) => (int)o.GetValue(ColumnSpanProperty);
    public static void SetColumnSpan(DependencyObject o, int v) => o.SetValue(ColumnSpanProperty, v);
    public static bool GetFullRow(DependencyObject o) => (bool)o.GetValue(FullRowProperty);
    public static void SetFullRow(DependencyObject o, bool v) => o.SetValue(FullRowProperty, v);

    private readonly List<Rect> _slots = [];

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? MinColumnWidth * MaxColumns : available.Width;
        int cols = Math.Max(1, Math.Min(MaxColumns, (int)((width + Gap) / (MinColumnWidth + Gap))));
        double colW = Math.Max(0, (width - Gap * (cols - 1)) / cols);

        _slots.Clear();
        var rows = new List<(int Start, int End, double Height)>();
        int col = 0, rowStart = 0;
        double rowHeight = 0, y = 0;
        var pending = new List<(UIElement Child, int Col, int Span)>();

        void CloseRow(int endIndex)
        {
            foreach (var (child, c, span) in pending)
                _slots.Add(new Rect(c * (colW + Gap), y, span == 0 ? 0 : Math.Max(0, colW * span + Gap * (span - 1)), rowHeight));
            rows.Add((rowStart, endIndex, rowHeight));
            y += rowHeight + Gap;
            pending.Clear();
            col = 0; rowHeight = 0; rowStart = endIndex;
        }

        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Measure(new Size(0, 0));
                pending.Add((child, col, 0));
                continue;
            }
            int span = GetFullRow(child) ? cols : Math.Clamp(GetColumnSpan(child), 1, cols);
            if (col + span > cols && col > 0) CloseRow(i);
            double w = colW * span + Gap * (span - 1);
            child.Measure(new Size(w, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            pending.Add((child, col, span));
            col += span;
        }
        if (pending.Count > 0) CloseRow(InternalChildren.Count);
        return new Size(width, Math.Max(0, y - Gap));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < InternalChildren.Count && i < _slots.Count; i++)
        {
            var r = _slots[i];
            InternalChildren[i].Arrange(r.Width <= 0 ? new Rect(r.X, r.Y, 0, 0) : r);
        }
        return finalSize;
    }
}
