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

        // Requires an actual cold-sky baseline, not just a gain selection - a gain-only
        // profile (see SaveGainOnlyCalibrationAsync) isn't usable for capture/spectrum
        // work yet, only for resuming the Capture Baseline step.
        public bool IsCalibrationAvailable => CurrentCalibration is not null && CurrentCalibration.BaselineSpectrum.Length > 0;

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
        /// Runs the gain-sweep phase of calibration and returns the chosen gain (dB) without
        /// touching CurrentCalibration or persisting anything - use SaveGainOnlyCalibrationAsync
        /// below to turn the result into a profile. Split out so PrepareViewModel can insert its
        /// reconnect-antenna prompt, cold-sky picker, and slew before a baseline is captured.
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
        /// Builds a gain-only CalibrationProfile (empty BaselineSpectrum) from a completed gain
        /// sweep, sets it as CurrentCalibration, and persists it to disk immediately - this is
        /// what lets a "Calibrate Device Gain" step done in one session survive an interrupted
        /// app restart and be picked up again by "Load Last Calibration" ready for "Capture
        /// Baseline", rather than only ever being saved once a baseline is also captured.
        /// </summary>
        public async Task<CalibrationProfile> SaveGainOnlyCalibrationAsync(
            double gainDb,
            double frequencyHz,
            double sampleRateHz,
            int fftSize,
            string deviceId)
        {
            var profile = new CalibrationProfile
            {
                TimestampUtc = DateTime.UtcNow,
                CenterFrequencyHz = frequencyHz,
                SampleRateHz = sampleRateHz,
                FftSize = fftSize,
                GainDb = gainDb,
                DeviceId = deviceId,
                BaselineSpectrum = Array.Empty<double>()
            };

            CurrentCalibration = profile;
            await SaveCalibrationAsync(profile);

            return profile;
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
