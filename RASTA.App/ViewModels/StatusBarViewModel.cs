using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.Providers.SparseSolver;
using RASTA.Core.Sdr;
using RASTA.Core.Telescope;

namespace RASTA.App.ViewModels
{
    public partial class StatusBarViewModel : ObservableObject
    {
        [ObservableProperty]
        private string telescopeStatus = "Disconnected";

        [ObservableProperty]
        private string coordinateStatus = "RA: --  Dec: --";

        [ObservableProperty]
        private string captureStatus = "Idle";

        [ObservableProperty]
        private string calibratedGain = "Uncalibrated";

        [ObservableProperty]
        private double captureProgress;

        [ObservableProperty]
        private bool isCaptureInProgress = false;

        private readonly TelescopeState _telescopeState;
        private readonly SdrState _sdrState;

        public string SdrStatus =>
        !_sdrState.IsConnected
            ? "No SDR Device Found"
            : $"SDR: {_sdrState.SelectedDevice?.Product ?? "Unknown Device"}";

        public bool TelescopeConnected => _telescopeState.IsConnected;

        public bool SdrConnected => _sdrState.IsConnected;

        public StatusBarViewModel(TelescopeState telescopeState, SdrState sdrState)
        {
            _sdrState = sdrState;
            _telescopeState = telescopeState;

            // React to SDR state changes
            _sdrState.PropertyChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(SdrConnected));
                OnPropertyChanged(nameof(SdrStatus));
            };

            // React to Telescope state changes
            _telescopeState.PropertyChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(TelescopeConnected));
            };
        }

        public void UpdateEquatorial(double raHours, double decDeg)
        {
            CoordinateStatus = $"RA: {raHours:F2}h  Dec: {decDeg:F2}°";
        }

        public void UpdateHorizontal(double azDeg, double altDeg)
        {
            CoordinateStatus = $"Az: {azDeg:F1}°  Alt: {altDeg:F1}°";
        }

        public void UpdateTelescopeStatus(string status) =>
            TelescopeStatus = status;

        public void UpdateCaptureStatus(string status) =>
            CaptureStatus = status;
    }
}
