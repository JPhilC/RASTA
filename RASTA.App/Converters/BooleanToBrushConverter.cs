using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace RASTA.App.Converters
{
    public class BooleanToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush GreenBrush = new SolidColorBrush(Color.FromRgb(0, 200, 0));
        private static readonly SolidColorBrush RedBrush = new SolidColorBrush(Color.FromRgb(200, 0, 0));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool connected = value is bool b && b;
            return connected ? GreenBrush : RedBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
