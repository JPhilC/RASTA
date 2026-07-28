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

            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            Loaded += MainWindow_Loaded;

            var navigationVM = App.Services.GetRequiredService<NavigationViewModel>();
            DataContext = navigationVM;

            navigationVM.NavigatePrepareCommand.Execute(null);
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            // Start the usb watcher service to monitor for device changes
            var usbWatcherService = App.Services.GetRequiredService<UsbWatcherService>();
            
            // Start the device enumeration in a background task
            var sdrDeviceService = App.Services.GetRequiredService<SdrDeviceService>();
            sdrDeviceService.EnumerateDevicesAsync().ConfigureAwait(false);
        }
    }


}
