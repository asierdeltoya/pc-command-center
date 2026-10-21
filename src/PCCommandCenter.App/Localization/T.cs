using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.Localization;

/// <summary>
/// XAML markup extension: <c>Text="{l:T 'Running processes'}"</c>. The binding re-evaluates when the
/// language changes, so the UI switches language live.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding(nameof(Loc.Version))
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
            Converter = LocConverter.Instance,
            ConverterParameter = Key,
        };
        return binding.ProvideValue(serviceProvider);
    }
}

public sealed class LocConverter : IValueConverter
{
    public static readonly LocConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Loc.T(parameter as string ?? "");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Translates a bound string value (e.g. a status coming from a service).</summary>
public sealed class TranslateConverter : IValueConverter
{
    public static readonly TranslateConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string s ? Loc.T(s) : value?.ToString() ?? "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
