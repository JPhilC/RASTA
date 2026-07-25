using RASTA.App.ViewModels;
using RASTA.Core.Telescope;
using RASTA.Infrastructure.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RASTA.App.Services
{
    public class TelescopeService
    {
        private readonly ITelescopeMount _mount;
        private readonly TelescopeState _state;
        private readonly StatusBarViewModel _statusBar;

        private CancellationTokenSource _cts;

        public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

        public TelescopeService(
            ITelescopeMount mount,
            TelescopeState state,
            StatusBarViewModel statusBar)
        {
            _mount = mount;
            _state = state;
            _statusBar = statusBar;
        }

        // ---------------------------------------------------------
        // Start polling telescope position (RA/Dec or Az/El)
        // ---------------------------------------------------------
        public void Start()
        {
            if (!_mount.IsConnected)
            {
                _statusBar.TelescopeStatus = "Disconnected";
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        // -----------------------------
                        // Query mount for pointing
                        // -----------------------------
                        double raHours = await _mount.GetRightAscensionHoursAsync();
                        double decDeg = await _mount.GetDeclinationDegAsync();
                        double azDeg = await _mount.GetAzimuthDegAsync();
                        double altDeg = await _mount.GetAltitudeDegAsync();

                        // -----------------------------
                        // Update shared state
                        // -----------------------------
                        _state.RightAscensionHours = raHours;
                        _state.DeclinationDeg = decDeg;
                        _state.AzimuthDeg = azDeg;
                        _state.ElevationDeg = altDeg;

                        // Mode is already maintained by SettingsViewModel
                        _state.Mode = _mount.Mode;

                        // -----------------------------
                        // Update status bar UI
                        // -----------------------------
                        if (_state.Mode == CoordinateMode.Equatorial)
                        {
                            _statusBar.UpdateEquatorial(raHours, decDeg);
                        }
                        else
                        {
                            _statusBar.UpdateHorizontal(azDeg, altDeg);
                        }

                        // -----------------------------
                        // Query mount status flags
                        // -----------------------------
                        _state.IsSlewing = await _mount.GetSlewingAsync();
                        _state.IsParked = await _mount.GetAtParkAsync();
                        _state.IsHome = await _mount.GetAtHomeAsync();
                        _state.TrackingEnabled = await _mount.GetTrackingAsync();

                        if (_state.IsParking)
                        {
                            _statusBar.TelescopeStatus = "Parking";
                        }
                        else if (_state.IsSlewing)
                        {
                            _statusBar.TelescopeStatus = "Slewing";
                        }
                        else if (_state.TrackingEnabled)
                        {
                            _statusBar.TelescopeStatus = "Tracking";
                        }
                        else if (_state.IsParked)
                        {
                            _statusBar.TelescopeStatus = "Parked";
                        }
                        else if (_state.IsHome)
                        {
                            _statusBar.TelescopeStatus = "Homed";
                        }
                        else
                        {
                            _statusBar.TelescopeStatus = "Connected";
                        }
                    }
                    catch (Exception ex)
                    {
                        _statusBar.TelescopeStatus = $"Error: {ex.Message}";
                    }

                    await Task.Delay(500); // 2 Hz update
                }
            });
        }

        // ---------------------------------------------------------
        // Stop polling
        // ---------------------------------------------------------
        public void Stop()
        {
            _cts?.Cancel();
            _statusBar.TelescopeStatus = _mount.IsConnected ? "Connected" : "Disconnected";
        }
    }
}
