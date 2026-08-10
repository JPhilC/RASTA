using System;
using System.Globalization;
using System.Windows.Data;

namespace RASTA.App.Converters
{
    /// <summary>
    /// Binds a single enum-valued property to a group of mutually-exclusive RadioButtons: each
    /// RadioButton's IsChecked binds to the same enum property with ConverterParameter set to
    /// the enum member name it represents (e.g. ConverterParameter=Velocity for
    /// MosaicSurfaceMetric.Velocity - see MosaicView.xaml's "Height:" radio group). Convert
    /// compares the bound value's ToString() against the parameter to decide IsChecked;
    /// ConvertBack fires only for the RadioButton the user just checked (WPF doesn't raise it
    /// for the one that became unchecked), parsing the parameter back to the enum's own type.
    /// </summary>
    public class EnumToBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is null || parameter is null)
                return false;

            return value.ToString() == parameter.ToString();
        }

        public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not bool isChecked || !isChecked || parameter is null)
                return Binding.DoNothing;

            return Enum.Parse(targetType, parameter.ToString()!);
        }
    }
}
