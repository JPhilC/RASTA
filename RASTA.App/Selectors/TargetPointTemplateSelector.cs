using RASTA.Core.Capture;
using RASTA.Core.Telescope;
using System.Windows;
using System.Windows.Controls;

namespace RASTA.App.Selectors
{
    public class TargetPointTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? EquatorialTemplate { get; set; }
        public DataTemplate? AltAzTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is not TargetPoint point)
                return base.SelectTemplate(item, container);

            return point.Mode == CoordinateMode.Equatorial
                ? EquatorialTemplate
                : AltAzTemplate;
        }
    }
}
