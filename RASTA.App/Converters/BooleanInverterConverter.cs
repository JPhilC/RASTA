using System;
using System.Globalization;
using System.Windows.Data;

namespace RASTA.App.Converters
{
    public sealed class BooleanInverterConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b)
                return !b;

            return value; // fallback: return unchanged
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b)
                return !b;

            return value;
        }
    }
}
