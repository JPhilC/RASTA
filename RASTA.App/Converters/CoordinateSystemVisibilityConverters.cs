using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using RASTA.Core.Telescope;

namespace RASTA.App.Converters
{
    public class EquatorialVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is CoordinateMode mode && mode == CoordinateMode.Equatorial
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class AltAzVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is CoordinateMode mode && mode == CoordinateMode.AltAz
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
