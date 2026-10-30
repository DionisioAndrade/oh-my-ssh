using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using OhMySsh.Core.Localization;

namespace OhMySsh.Desktop.Localization;

/// <summary>Binds interface text to the active catalog, including a single live format argument.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public BindingBase? Value { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var textBinding = new Binding($"[{Key}]") { Source = UiText.Current, Mode = BindingMode.OneWay };
        if (Value is null)
        {
            return textBinding.ProvideValue(serviceProvider);
        }

        var binding = new MultiBinding { Mode = BindingMode.OneWay, Converter = LocalizedFormatConverter.Instance };
        binding.Bindings.Add(textBinding);
        binding.Bindings.Add(Value);
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class LocalizedFormatConverter : IMultiValueConverter
    {
        public static LocalizedFormatConverter Instance { get; } = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not string template)
            {
                return DependencyProperty.UnsetValue;
            }

            var argument = values[1] == DependencyProperty.UnsetValue ? null : values[1];
            return string.Format(UiText.Current.Culture, template, argument);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException("Localized interface text is read-only.");
    }
}
