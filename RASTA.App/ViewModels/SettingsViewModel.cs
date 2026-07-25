using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using RASTA.Core.Telescope;
using RASTA.Infrastructure.Logging;
using RASTA.Infrastructure.Telescope;
using System.Windows;

namespace RASTA.App.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly ITelescopeMount _mount;
        private readonly TelescopeState _state;
        private readonly RastaLogger _logger;

        private bool _mountIsInitialising;

        public SettingsViewModel(ITelescopeMount mount, TelescopeState state, RastaLogger logger)
        {
            _mount = mount;
            _state = state;
            _logger = logger;

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
                return;

            if (_mount.IsConnected)
                _mount.SetSiteLatitudeAsync(value);

            _state.SiteLatitudeDeg = value;
        }

        partial void OnSiteLongitudeDegChanged(double value)
        {
            if (_mountIsInitialising)
                return;

            if (_mount.IsConnected)
                _mount.SetSiteLongitudeAsync(value);

            _state.SiteLongitudeDeg = value;
        }

        partial void OnSiteElevationMChanged(double value)
        {
            if (_mountIsInitialising)
                return;

            if (_mount.IsConnected)
                _mount.SetSiteElevationAsync(value);

            _state.SiteElevationM = value;
        }

        // -------------------------------
        // Coordinate Mode (session-specific)
        // -------------------------------

        [ObservableProperty]
        private CoordinateMode mode = CoordinateMode.Equatorial;

        partial void OnModeChanged(CoordinateMode value)
        {
            _state.Mode = value;
        }

        // -------------------------------
        // Tracking (session-specific)
        // -------------------------------

        [ObservableProperty]
        private bool trackingEnabled;

        [ObservableProperty]
        private int trackingRate = 0; // 0 = Sidereal

        partial void OnTrackingEnabledChanged(bool value)
        {
            _state.TrackingEnabled = value;

            if (_mount.IsConnected)
                _mount.SetTrackingAsync(value);
        }

        partial void OnTrackingRateChanged(int value)
        {
            _state.TrackingRate = value;

            if (_mount.IsConnected)
                _mount.SetTrackingRateAsync(value);
        }

        // Convenience method for PrepareViewModel
        public async Task ApplySiderealTrackingAsync()
        {
            if (_mount.IsConnected)
                await _mount.SetSiderealTrackingAsync();
        }

        // -------------------------------
        // Connection Commands
        // -------------------------------

        [ObservableProperty]
        private bool isBusy;

        // -------------------------------
        // Connection Methods (not commands)
        // -------------------------------

        public async Task ConnectTelescopeAsync()
        {
            try
            {
                IsBusy = true;

                await _mount.ConnectAsync();

                IsConnected = _mount.IsConnected;
                _state.IsConnected = _mount.IsConnected;

                if (!_mount.IsConnected)
                    return;

                // ---------------------------------------------------------
                // Check parked state
                // ---------------------------------------------------------
                bool isParked = await _mount.GetAtParkAsync();
                _state.WasParkedOnConnect = isParked;
                if (isParked)
                {
                    var result = MessageBox.Show(
                        "The telescope is currently parked. Do you want to unpark it?",
                        "Telescope Parked",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        await _mount.UnParkAsync();
                    }
                    else
                    {
                        // Graceful degradation: telescope stays parked
                        _state.IsParked = true;
                        _state.TrackingEnabled = false;

                        // StatusBar will show "Parked" via TelescopeService
                        return;
                    }
                }

                // ---------------------------------------------------------
                // Pull site values from mount
                // ---------------------------------------------------------
                _mountIsInitialising = true;

                SiteLatitudeDeg = _mount.SiteLatitudeDeg;
                SiteLongitudeDeg = _mount.SiteLongitudeDeg;
                SiteElevationM = _mount.SiteElevationM;

                _state.SiteLatitudeDeg = SiteLatitudeDeg;
                _state.SiteLongitudeDeg = SiteLongitudeDeg;
                _state.SiteElevationM = SiteElevationM;

                // ---------------------------------------------------------
                // Coordinate mode
                // ---------------------------------------------------------
                Mode = _mount.Mode;
                _state.Mode = Mode;

                _mountIsInitialising = false;

                // ---------------------------------------------------------
                // Tracking (only if not parked)
                // ---------------------------------------------------------
                await ApplySiderealTrackingAsync();
                _state.TrackingEnabled = true;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task DisconnectTelescopeAsync()
        {
            try
            {
                IsBusy = true;

                // ---------------------------------------------------------
                // Ask user if they want to park before disconnecting
                // ---------------------------------------------------------
                if (_state.WasParkedOnConnect)
                {
                    var result = MessageBox.Show(
                        "Do you want to park the telescope before disconnecting?",
                        "Park Telescope",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        _state.IsParking = true;

                        await _mount.ParkAsync();

                        // ---------------------------------------------------------
                        // Wait until slewing stops (with timeout)
                        // ---------------------------------------------------------
                        bool slewing = true;
                        int timeoutMs = 60000;        // 60 seconds
                        int pollIntervalMs = 500;     // 2 Hz
                        int waited = 0;

                        while (slewing && waited < timeoutMs)
                        {
                            await Task.Delay(pollIntervalMs);
                            waited += pollIntervalMs;

                            try
                            {
                                slewing = await _mount.GetSlewingAsync();
                            }
                            catch
                            {
                                slewing = false;
                                break;
                            }
                        }

                        if (slewing)
                        {
                            _logger.Warn("Parking timeout: telescope did not finish slewing within 60 seconds.");
                            _state.IsParked = false;
                        }
                        else
                        {
                            _state.IsParked = true;
                        }

                        _state.IsParking = false;
                    }
                }

                // ---------------------------------------------------------
                // Disconnect
                // ---------------------------------------------------------
                await _mount.DisconnectAsync();

                IsConnected = _mount.IsConnected;
                _state.IsConnected = _mount.IsConnected;
            }
            finally
            {
                IsBusy = false;
            }
        }


    }
}
