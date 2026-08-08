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
        /// Runs the gain-sweep phase of a new calibration and returns the chosen gain (dB).
        /// Does not touch CurrentCalibration or persist anything - the profile isn't complete
        /// until CaptureColdSkyBaselineAsync below finishes. Split out so PrepareViewModel can
        /// insert its reconnect-antenna prompt, cold-sky picker, and slew between the two.
        /// </summary>
        public Task<double> RunGainSweepAsync(
            ISdrDevice device,
            double frequencyHz,
            double sampleRateHz,
            TimeSpan dwell,
            int fftSize,
            Action<string, double>? progress,
            CancellationToken ct)
        {
            return _calibrator.RunGainSweepAsync(
                device,
                frequencyHz,
                sampleRateHz,
                dwell,
                fftSize,
                progress,
                ct);
        }

        /// <summary>
        /// Captures the cold-sky baseline at a pointing already slewed to by the caller,
        /// completing the calibration - updates CurrentCalibration and persists it to disk.
        /// </summary>
        public async Task<CalibrationProfile> CaptureColdSkyBaselineAsync(
            ISdrDevice device,
            double frequencyHz,
            double sampleRateHz,
            double gainDb,
            TimeSpan baselineDwell,
            int fftSize,
            ColdSkyCandidate location,
            double siteLatitudeDeg,
            double siteLongitudeDeg,
            double siteElevationM,
            Action<string, double>? progress,
            CancellationToken ct)
        {
            var profile = await _calibrator.CaptureColdSkyBaselineAsync(
                device,
                frequencyHz,
                sampleRateHz,
                gainDb,
                baselineDwell,
                fftSize,
                location,
                siteLatitudeDeg,
                siteLongitudeDeg,
                siteElevationM,
                progress,
                ct);

            CurrentCalibration = profile;

            await SaveCalibrationAsync(profile);

            return profile;
        }
    }
}
