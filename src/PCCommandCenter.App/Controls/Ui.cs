using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PCCommandCenter.App.Controls;

/// <summary>Attached properties used by the control templates (icons, placeholders, corner radius).</summary>
public static class Ui
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.RegisterAttached("Glyph", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));
    public static string? GetGlyph(DependencyObject o) => (string?)o.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject o, string? v) => o.SetValue(GlyphProperty, v);

    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached("Placeholder", typeof(string), typeof(Ui), new PropertyMetadata(null));
    public static string? GetPlaceholder(DependencyObject o) => (string?)o.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject o, string? v) => o.SetValue(PlaceholderProperty, v);

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached("CornerRadius", typeof(CornerRadius), typeof(Ui), new PropertyMetadata(new CornerRadius(8)));
    public static CornerRadius GetCornerRadius(DependencyObject o) => (CornerRadius)o.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject o, CornerRadius v) => o.SetValue(CornerRadiusProperty, v);

    public static readonly DependencyProperty BadgeProperty =
        DependencyProperty.RegisterAttached("Badge", typeof(string), typeof(Ui), new PropertyMetadata(null));
    public static string? GetBadge(DependencyObject o) => (string?)o.GetValue(BadgeProperty);
    public static void SetBadge(DependencyObject o, string? v) => o.SetValue(BadgeProperty, v);

    /// <summary>Executes a command when Enter is pressed in a TextBox.</summary>
    public static readonly DependencyProperty EnterCommandProperty =
        DependencyProperty.RegisterAttached("EnterCommand", typeof(ICommand), typeof(Ui), new PropertyMetadata(null, OnEnterCommandChanged));
    public static ICommand? GetEnterCommand(DependencyObject o) => (ICommand?)o.GetValue(EnterCommandProperty);
    public static void SetEnterCommand(DependencyObject o, ICommand? v) => o.SetValue(EnterCommandProperty, v);

    private static void OnEnterCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.PreviewKeyDown -= OnEnterKey;
        if (e.NewValue != null) el.PreviewKeyDown += OnEnterKey;
    }

    private static void OnEnterKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        if (sender is TextBox tb) tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        var cmd = GetEnterCommand((DependencyObject)sender);
        if (cmd?.CanExecute(null) == true) { cmd.Execute(null); e.Handled = true; }
    }

    /// <summary>Scrolls a ScrollViewer to the end whenever the bound value changes (used by chat and terminal).</summary>
    public static readonly DependencyProperty AutoScrollTriggerProperty =
        DependencyProperty.RegisterAttached("AutoScrollTrigger", typeof(object), typeof(Ui), new PropertyMetadata(null, (d, _) =>
        {
            if (d is ScrollViewer sv)
            {
                // Only follow the output when the user is already near the bottom.
                if (sv.VerticalOffset >= sv.ScrollableHeight - 80)
                    sv.Dispatcher.BeginInvoke(sv.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
            }
        }));
    public static object? GetAutoScrollTrigger(DependencyObject o) => o.GetValue(AutoScrollTriggerProperty);
    public static void SetAutoScrollTrigger(DependencyObject o, object? v) => o.SetValue(AutoScrollTriggerProperty, v);

    public static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}
