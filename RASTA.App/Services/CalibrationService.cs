using CommunityToolkit.Mvvm.ComponentModel;
using RASTA.Core.Calibration;
using RASTA.Core.Sdr;
using RASTA.Processing.Calibration;

namespace RASTA.App.Services
{

    public class CalibrationService: ObservableObject
    {
        private readonly Calibrator _calibrator;

        private CalibrationProfile? _currentCalibration;
        public CalibrationProfile? CurrentCalibration
        {
            get => _currentCalibration;
            private set {
                if (SetProperty(ref _currentCalibration, value))
                {
                    OnPropertyChanged(nameof(IsCalibrationAvailable));
                }
            }
        }

        public bool IsCalibrationAvailable => CurrentCalibration is not null;


        public CalibrationService(Calibrator calibrator)
        {
            _calibrator = calibrator;
        }

        public async Task<CalibrationProfile> RunCalibrationAsync(
            ISdrDevice device,
            double frequencyHz,
            double sampleRateHz,
            TimeSpan dwell,
            int fftSize,
            Action<string, double>? progress,
            CancellationToken ct)
        {
            var profile = await _calibrator.RunFullCalibrationAsync(
                device,
                frequencyHz,
                sampleRateHz,
                dwell,
                fftSize,
                progress,
                ct);

            CurrentCalibration = profile;
            return profile;
        }
    }

}
