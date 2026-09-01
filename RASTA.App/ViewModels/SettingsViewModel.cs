using CommunityToolkit.Mvvm.ComponentModel;
using RASTA.Core.Antenna;
using RASTA.Core.Telescope;
using RASTA.Infrastructure.Logging;
using RASTA.Infrastructure.Services;
using RASTA.Infrastructure.Telescope;
using System.Windows;

namespace RASTA.App.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly ITelescopeMount _mount;
        private readonly TelescopeState _state;
        private readonly RastaLogger _logger;
        private readonly UserOptionsService _optionsService;

        private bool _mountIsInitialising;

        public SettingsViewModel(ITelescopeMount mount, TelescopeState state, RastaLogger logger, UserOptionsService optionsService)
        {
            _mount = mount;
            _state = state;
            _logger = logger;
            _optionsService = optionsService;

            OnAlpacaBaseUrlChanged(alpacaBaseUrl);

            CalibrationFrequencyHz = _optionsService.Options.DefaultCentreFrequencyHz;
            SampleRateHz = _optionsService.Options.DefaultBandwidthHz;
            FftSize = _optionsService.Options.DefaultFftSize;

            // Site settings are editable independently of a mount connection (see the Site
            // Settings region below) and persisted across restarts, so the last-confirmed
            // value is already in TelescopeState (and available to e.g. Mosaic's Zenith Dome
            // view) even before any mount is ever connected this session.
            SiteLatitudeDeg = _optionsService.Options.SiteLatitudeDeg;
            SiteLongitudeDeg = _optionsService.Options.SiteLongitudeDeg;
            SiteElevationM = _optionsService.Options.SiteElevationM;
            DishDiameterM = _optionsService.Options.DishDiameterM;
            FocalLengthM = _optionsService.Options.FocalLengthM;
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
        // Site Settings
        // -------------------------------
        // Editable at any time, with or without a mount attached - e.g. to set up a real
        // site for Mosaic's Zenith Dome view before ever connecting hardware. Persisted via
        // UserOptionsService (see the constructor) so they survive an app restart rather than
        // resetting to 0/0/0. See ConnectTelescopeAsync for what happens when a mount that
        // reports a *different* site actually connects.
        // -------------------------------

        [ObservableProperty]
        private double siteLatitudeDeg;

        [ObservableProperty]
        private double siteLongitudeDeg;

        [ObservableProperty]
        private double siteElevationM;


        partial void OnSiteLatitudeDegChanged(double value)
        {
            _optionsService.Options.SiteLatitudeDeg = value;

            if (_mountIsInitialising)
                return;

            if (_mount.IsConnected)
                _mount.SetSiteLatitudeAsync(value);

            _state.SiteLatitudeDeg = value;
        }

        partial void OnSiteLongitudeDegChanged(double value)
        {
            _optionsService.Options.SiteLongitudeDeg = value;

            if (_mountIsInitialising)
                return;

            if (_mount.IsConnected)
                _mount.SetSiteLongitudeAsync(value);

            _state.SiteLongitudeDeg = value;
        }

        partial void OnSiteElevationMChanged(double value)
        {
            _optionsService.Options.SiteElevationM = value;

            if (_mountIsInitialising)
                return;

            if (_mount.IsConnected)
                _mount.SetSiteElevationAsync(value);

            _state.SiteElevationM = value;
        }

        // -------------------------------
        // Antenna
        // -------------------------------
        // Persisted, editable at any time without a mount attached - same treatment as Site
        // Settings above. Feeds BeamwidthDeg, which PlanViewModel uses to suggest a default
        // AngularSeparationDeg for a new plan instead of leaving it at 0.
        // -------------------------------

        [ObservableProperty]
        private double dishDiameterM;

        partial void OnDishDiameterMChanged(double value)
        {
            _optionsService.Options.DishDiameterM = value;
            OnPropertyChanged(nameof(BeamwidthDeg));
            OnPropertyChanged(nameof(FocalRatio));
        }

        // Not used by BeamwidthDeg itself - see AntennaUtils.ComputeBeamwidthDeg's remarks on
        // why f/D can't rigorously refine the estimate without the feed's own illumination
        // pattern. Stored for context (FocalRatio below) and a future antenna-gain estimate.
        [ObservableProperty]
        private double focalLengthM;

        partial void OnFocalLengthMChanged(double value)
        {
            _optionsService.Options.FocalLengthM = value;
            OnPropertyChanged(nameof(FocalRatio));
        }

        /// <summary>
        /// Half-power beamwidth (see AntennaUtils) for DishDiameterM at the app's default
        /// centre frequency - the same frequency a new CapturePlan's own CenterFrequency starts
        /// from (UserOptions.DefaultCentreFrequencyHz). Computed on read, not persisted itself.
        /// </summary>
        public double BeamwidthDeg =>
            AntennaUtils.ComputeBeamwidthDeg(DishDiameterM, _optionsService.Options.DefaultCentreFrequencyHz);

        /// <summary>
        /// f/D - shown alongside BeamwidthDeg purely as context: the 70*wavelength/diameter
        /// estimate assumes a feed reasonably well-matched to the dish, which in practice means
        /// a focal ratio roughly in the 0.35-0.5 range. Not fed into BeamwidthDeg itself.
        /// </summary>
        public double FocalRatio => DishDiameterM > 0 ? FocalLengthM / DishDiameterM : 0;

        // -------------------------------
        // Coordinate Mode (session-specific)
        // -------------------------------

        [ObservableProperty]
        private CoordinateMode mode = CoordinateMode.Unknown;

        partial void OnModeChanged(CoordinateMode value)
        {
            _state.Mode = value;
        }

        // -------------------------------
        // Slew rate (user will need to determine what is appropriate for their mount)
        // -------------------------------
        [ObservableProperty]
        private double slewRateDegPerSec = 3.0;

        [ObservableProperty]
        private double horizonLimitDeg = 10.0;

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
                // Reconcile site values: RASTA's site settings can now be entered and used
                // (e.g. by Mosaic's Zenith Dome view) before any mount is ever connected, so a
                // connecting mount's own site settings can no longer just be pulled in
                // unconditionally - that would silently overwrite a real, deliberately-entered
                // RASTA value with whatever the mount happens to report, and the reverse
                // (always pushing RASTA's value to the mount) would just as wrongly stomp a
                // mount that's genuinely set up correctly for a different location. Compare the
                // two and ask only when they actually disagree.
                // ---------------------------------------------------------
                double mountLat = _mount.SiteLatitudeDeg;
                double mountLon = _mount.SiteLongitudeDeg;
                double mountElevM = _mount.SiteElevationM;

                // Loose enough to absorb float/round-trip noise, tight enough that a genuinely
                // different site (or a mistyped value) still trips it.
                const double latLonToleranceDeg = 0.01;  // ~1 km at the equator
                const double elevationToleranceM = 5.0;

                bool sitesDiffer =
                    Math.Abs(mountLat - SiteLatitudeDeg) > latLonToleranceDeg ||
                    Math.Abs(mountLon - SiteLongitudeDeg) > latLonToleranceDeg ||
                    Math.Abs(mountElevM - SiteElevationM) > elevationToleranceM;

                bool pullFromMount = true;
                if (sitesDiffer)
                {
                    string message =
                        "The connected mount's site settings differ from what's currently set in RASTA:\n\n" +
                        $"              RASTA          Mount\n" +
                        $"Latitude:   {SiteLatitudeDeg,9:F5}°   {mountLat,9:F5}°\n" +
                        $"Longitude:  {SiteLongitudeDeg,9:F5}°   {mountLon,9:F5}°\n" +
                        $"Elevation:  {SiteElevationM,9:F1} m   {mountElevM,9:F1} m\n\n" +
                        "Update the MOUNT to match RASTA (Yes), or update RASTA to match the MOUNT (No)?";

                    var siteResult = MessageBox.Show(
                        message,
                        "Site Settings Differ",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    pullFromMount = siteResult == MessageBoxResult.No;
                }

                _mountIsInitialising = true;

                if (pullFromMount)
                {
                    SiteLatitudeDeg = mountLat;
                    SiteLongitudeDeg = mountLon;
                    SiteElevationM = mountElevM;
                }
                else
                {
                    await _mount.SetSiteLatitudeAsync(SiteLatitudeDeg);
                    await _mount.SetSiteLongitudeAsync(SiteLongitudeDeg);
                    await _mount.SetSiteElevationAsync(SiteElevationM);
                }

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

        /// <summary>
        /// Resets local connection state to disconnected without any live mount I/O -
        /// used when TelescopeService's poll loop has already learned the mount is
        /// unreachable (see TelescopeService.ConnectionLost / App.xaml.cs). Deliberately
        /// skips DisconnectTelescopeAsync's "ask to park"/live DisconnectAsync() round-trip:
        /// the link is already known down at this point, so either would just hang or fail
        /// again, and there's no way to know what physical state the mount was actually
        /// left in - reconnecting from here is left as a deliberate, informed action for
        /// the user rather than something attempted automatically.
        /// </summary>
        public void ForceDisconnectTelescope()
        {
            _mount.MarkDisconnected();
            IsConnected = false;
            _state.IsConnected = false;
            _state.IsParking = false;
        }

        // ---------------------------------------
        // Calibration Settings (session-specific)
        // ---------------------------------------

        [ObservableProperty]
        private double calibrationFrequencyHz = 1_420_405_752.0; // 1420.405752 MHz

        [ObservableProperty]
        private double sampleRateHz = 2.4e6; // 2.4 MHz

        [ObservableProperty]
        private int fftSize = 4096;

        [ObservableProperty]
        private int gainDwellSeconds = 3;

        // Baseline capture is independent of the per-gain sweep dwell above - it only
        // needs to happen once, at the chosen gain, and deserves a much better-averaged
        // reference since every later observation is divided by it.
        [ObservableProperty]
        private int baselineDwellSeconds = 20;
    }
}
