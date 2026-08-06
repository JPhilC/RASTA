using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RASTA.App.Converters
{
    /// <summary>
    /// Visible when the bound value is non-null (e.g. a nullable DateTime that's only
    /// set once something's actually happened), Collapsed when null.
    /// </summary>
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is null ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
