using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RASTA.App.Services;
using RASTA.Core.Telescope;
using RASTA.Infrastructure.Telescope;   // AscomTelescopeMount, ITelescopeMount

namespace RASTA.App.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly ITelescopeMount _mount;

        private bool _mountIsInitialising;

        public SettingsViewModel(ITelescopeMount mount)
        {
            _mount = mount;
            OnAlpacaBaseUrlChanged(alpacaBaseUrl);
        }

        // -------------------------------
        // Alpaca / Connection Settings
        // -------------------------------

        [ObservableProperty]
        private string alpacaBaseUrl = "http://127.0.0.1:11111/api/v1/telescope";

        [ObservableProperty]
        private int telescopeDeviceNumber = 0;

        partial void OnAlpacaBaseUrlChanged(string value)
        {
            if (_mount is AscomTelescopeMount m)
                m.SetBaseUrl(value);
        }

        partial void OnTelescopeDeviceNumberChanged(int value)
        {
            if (_mount is AscomTelescopeMount m)
                m.SetDeviceNumber(value);
        }

        [ObservableProperty]
        private bool isConnected;

        // -------------------------------
        // Site Settings (session-specific)
        // -------------------------------

        [ObservableProperty]
        private double siteLatitudeDeg;

        [ObservableProperty]
        private double siteLongitudeDeg;

        [ObservableProperty]
        private double siteElevationM;

        partial void OnSiteLatitudeDegChanged(double value)
        {
            if (_mountIsInitialising)
                return; // Do not push values to the mount while we are initializing from the mount's current values

            if (_mount.IsConnected)
                _mount.SetSiteLatitudeAsync(value);
        }

        partial void OnSiteLongitudeDegChanged(double value)
        {
            if (_mountIsInitialising)
                return; // Do not push values to the mount while we are initializing from the mount's current values

            if (_mount.IsConnected)
                _mount.SetSiteLongitudeAsync(value);
        }

        partial void OnSiteElevationMChanged(double value)
        {
            if (_mountIsInitialising)
                return; // Do not push values to the mount while we are initializing from the mount's current values

            if (_mount.IsConnected)
                _mount.SetSiteElevationAsync(value);
        }

        // -------------------------------
        // Coordinate Mode (session-specific)
        // -------------------------------

        [ObservableProperty]
        private CoordinateMode mode = CoordinateMode.Equatorial;

        // -------------------------------
        // Tracking (session-specific)
        // -------------------------------

        [ObservableProperty]
        private bool trackingEnabled;

        [ObservableProperty]
        private int trackingRate = 0; // 0 = Sidereal

        partial void OnTrackingEnabledChanged(bool value)
        {
            if (_mount.IsConnected)
                _mount.SetTrackingAsync(value);
        }

        partial void OnTrackingRateChanged(int value)
        {
            if (_mount.IsConnected)
                _mount.SetTrackingRateAsync(value);
        }

        // Convenience method for PrepareViewModel
        public async Task ApplySiderealTrackingAsync()
        {
            if (_mount.IsConnected)
                await _mount.SetSiderealTrackingAsync();
        }


        [ObservableProperty]
        private bool isBusy;

        [RelayCommand]
        private async Task ConnectTelescopeAsync()
        {
            try
            {
                IsBusy = true;

                await _mount.ConnectAsync();
                if (_mount.IsConnected)
                {
                    // Pull site values from mount
                    _mountIsInitialising = true;

                    SiteLatitudeDeg = _mount.SiteLatitudeDeg;
                    SiteLongitudeDeg = _mount.SiteLongitudeDeg;
                    SiteElevationM = _mount.SiteElevationM;
                    // Update coordinate mode
                    Mode = _mount.Mode;

                    _mountIsInitialising = false;

                    // Apply sidereal tracking
                    await ApplySiderealTrackingAsync();
                }
                IsConnected = _mount.IsConnected;
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task DisconnectTelescopeAsync()
        {
            try
            {
                IsBusy = true;

                await _mount.DisconnectAsync();
                IsConnected = _mount.IsConnected;
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
