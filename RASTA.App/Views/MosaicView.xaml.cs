using RASTA.App.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace RASTA.App.Views
{
    /// <summary>
    /// Interaction logic for MosaicView.xaml
    /// </summary>
    public partial class MosaicView : UserControl
    {
        public MosaicView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is INotifyPropertyChanged vm)
                vm.PropertyChanged += OnViewModelPropertyChanged;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is INotifyPropertyChanged vm)
                vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        /// <summary>
        /// Scrolls the Positions DataGrid to whichever row a 3D Dome stem click (or a plain row
        /// click) just selected - a bound SelectedItem alone doesn't auto-scroll a DataGrid into
        /// view, that needs an explicit ScrollIntoView call, which only code-behind can make.
        /// </summary>
        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MosaicViewModel.SelectedPosition) &&
                DataContext is MosaicViewModel vm && vm.SelectedPosition is not null)
            {
                PositionsDataGrid.ScrollIntoView(vm.SelectedPosition);
            }
        }
    }
}
