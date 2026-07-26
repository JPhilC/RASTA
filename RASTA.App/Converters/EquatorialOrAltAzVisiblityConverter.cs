using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using RASTA.Core.Capture;

namespace RASTA.App.Converters
{
    public class EquatorialOrAltAzVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is PlanType pt)
            {
                return (pt == PlanType.Equatorial || pt == PlanType.AltAz)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}