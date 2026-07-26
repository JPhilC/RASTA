using System.ComponentModel;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RASTA.App.Services;
using RASTA.App.ViewModels;

namespace RASTA.App
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // Skip DI when in design mode
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            Loaded += MainWindow_Loaded;

            var navigationVM = App.Services.GetRequiredService<NavigationViewModel>();
            DataContext = navigationVM;

            // Default page
            navigationVM.NavigatePrepareCommand.Execute(null);
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            // Now the window exists and has a valid handle
            var usbWatcher = App.Services.GetRequiredService<UsbWatcherService>();

            // Enumerate SDR devices immediately
            var sdrService = App.Services.GetRequiredService<SdrDeviceService>();
            _ = sdrService.EnumerateDevicesAsync();
        }
    }
}
