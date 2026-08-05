using CommunityToolkit.Mvvm.ComponentModel;
using RASTA.Core.Calibration;
using RASTA.Core.Sdr;
using RASTA.Infrastructure.Storage;
using RASTA.Processing.Calibration;

namespace RASTA.App.Services
{
    public class CalibrationService : ObservableObject
    {
        private readonly Calibrator _calibrator;
        private readonly CalibrationRepository _repository;

        private CalibrationProfile? _currentCalibration;
        public CalibrationProfile? CurrentCalibration
        {
            get => _currentCalibration;
            private set
            {
                if (SetProperty(ref _currentCalibration, value))
                    OnPropertyChanged(nameof(IsCalibrationAvailable));
            }
        }

        public bool IsCalibrationAvailable => CurrentCalibration is not null;

        public CalibrationService(Calibrator calibrator, CalibrationRepository repository)
        {
            _calibrator = calibrator;
            _repository = repository;
        }

        /// <summary>
        /// Loads a previously saved calibration profile from disk.
        /// Returns null if none exists.
        /// </summary>
        public async Task<CalibrationProfile?> TryLoadSavedCalibrationAsync()
        {
            var profile = await _repository.LoadAsync();
            if (profile != null)
                CurrentCalibration = profile;

            return profile;
        }

        /// <summary>
        /// Persists the calibration profile to disk.
        /// </summary>
        public async Task SaveCalibrationAsync(CalibrationProfile profile)
        {
            await _repository.SaveAsync(profile);
        }

        /// <summary>
        /// Runs a new calibration, updates CurrentCalibration,
        /// and persists it to disk.
        /// </summary>
        public async Task<CalibrationProfile> RunCalibrationAsync(
            ISdrDevice device,
            double frequencyHz,
            double sampleRateHz,
            TimeSpan dwell,
            TimeSpan baselineDwell,
            int fftSize,
            Action<string, double>? progress,
            CancellationToken ct)
        {
            var profile = await _calibrator.RunFullCalibrationAsync(
                device,
                frequencyHz,
                sampleRateHz,
                dwell,
                baselineDwell,
                fftSize,
                progress,
                ct);

            CurrentCalibration = profile;

            await SaveCalibrationAsync(profile);

            return profile;
        }
    }
}
