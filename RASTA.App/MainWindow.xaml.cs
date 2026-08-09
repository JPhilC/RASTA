using System.ComponentModel;
using System.Reflection;
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

            // Append the assembly version (set via Directory.Build.props) to the title bar,
            // e.g. "R.A.S.T.A. v0.1.0". Uses AssemblyVersion rather than InformationalVersion
            // so it stays a clean Major.Minor.Build with no git-hash suffix.
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            if (version != null)
                Title = $"{Title} v{version.Major}.{version.Minor}.{version.Build}";

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
