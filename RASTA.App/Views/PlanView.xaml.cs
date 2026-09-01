using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

using RASTA.App.ViewModels;

namespace RASTA.App.Views
{
    /// <summary>
    /// Interaction logic for PlanView.xaml. Mouse handling on the sky map Canvas is kept here
    /// (translating pixel coordinates to VM calls) rather than in PlanViewModel, consistent with
    /// how the rest of the app keeps pixel/View-specific concerns out of ViewModels - the actual
    /// projection math and resulting state all live in PlanViewModel/DomeProjector.
    /// </summary>
    public partial class PlanView : UserControl
    {
        public PlanView()
        {
            InitializeComponent();
        }

        private void MapCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (DataContext is not PlanViewModel vm) return;
            var p = e.GetPosition(MapCanvas);
            vm.HandleMapMouseMove(p.X, p.Y);
        }

        private void MapCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not PlanViewModel vm) return;
            var p = e.GetPosition(MapCanvas);
            vm.HandleMapLeftClick(p.X, p.Y);
        }

        // PreviewMouseRightButtonDown (not the ContextMenu's own opening event) so the target
        // point is computed and stashed into PlanViewModel.ContextTargetPoint before the
        // ContextMenu opens - its "Slew & Capture Here" MenuItem's CanExecute needs it current
        // by the time the menu actually shows.
        private void MapCanvas_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not PlanViewModel vm) return;
            var p = e.GetPosition(MapCanvas);
            vm.HandleMapRightClick(p.X, p.Y);
        }
    }
}
