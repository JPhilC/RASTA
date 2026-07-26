using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using RASTA.Core.Capture;

namespace RASTA.App.Converters
{
    public class DriftVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is PlanType pt && pt == PlanType.Drift
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
