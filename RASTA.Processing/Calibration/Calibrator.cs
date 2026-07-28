using RASTA.Core.Calibration;
using RASTA.Core.Processing;
using RASTA.Core.Sdr;

namespace RASTA.Processing.Calibration
{
    public sealed class Calibrator
    {
        private readonly IFftEngine _fftEngine;

        public Calibrator(IFftEngine fftEngine)
        {
            _fftEngine = fftEngine;
        }

        /// <summary>
        /// Runs a full gain-sweep calibration and returns a CalibrationProfile.
        /// </summary>
        public async Task<CalibrationProfile> RunFullCalibrationAsync(
            ISdrDevice device,
            double frequencyHz,
            double sampleRateHz,
            TimeSpan dwellTime,
            int fftSize,
            Action<string, double>? progressCallback,
            CancellationToken ct)
        {
            var supportedGains = device.SupportedGainsDb.ToList();
            if (supportedGains.Count == 0)
                throw new InvalidOperationException("SDR device reports no supported gains.");

            var gainScores = new List<(double Gain, double Score, double[] Spectrum)>();

            int totalSteps = supportedGains.Count + 2; // +1 baseline, +1 finalize
            int currentStep = 0;

            foreach (var gain in supportedGains)
            {
                ct.ThrowIfCancellationRequested();

                currentStep++;
                progressCallback?.Invoke($"Trying gain {gain} dB", (double)currentStep / totalSteps);

                var spectrum = await device.CaptureSpectrumAsync(
                    frequencyHz,
                    sampleRateHz,
                    gain,
                    dwellTime,
                    fftSize,
                    _fftEngine,
                    ct);

                double score = ScoreSpectrum(spectrum);
                gainScores.Add((gain, score, spectrum));
            }

            // Choose best gain
            var best = gainScores.OrderByDescending(g => g.Score).First();

            progressCallback?.Invoke($"Selected gain {best.Gain} dB", 0.95);

            // Capture long baseline at chosen gain
            currentStep++;
            progressCallback?.Invoke($"Selected gain {best.Gain} dB", (double)currentStep / totalSteps);

            var baselineSpectrum = await device.CaptureSpectrumAsync(
                frequencyHz,
                sampleRateHz,
                best.Gain,
                dwellTime * 4,
                fftSize,
                _fftEngine,
                ct);

            currentStep++;
            progressCallback?.Invoke("Finalizing calibration", (double)currentStep / totalSteps);

            return new CalibrationProfile
            {
                GainDb = best.Gain,
                BaselineSpectrum = baselineSpectrum,
                BaselineMean = baselineSpectrum.Average(),
                BaselineStdDev = ComputeStdDev(baselineSpectrum),
                TimestampUtc = DateTime.UtcNow,
                DeviceId = device.DeviceId
            };
        }

        private double ScoreSpectrum(double[] spectrum)
        {
            double mean = spectrum.Average();
            double std = ComputeStdDev(spectrum);

            int spurCount = spectrum.Count(v => v > mean + 6 * std);

            double max = spectrum.Max();
            int clipCount = spectrum.Count(v => v > max * 0.98);

            double slope = ComputeSlope(spectrum);

            double flatnessScore = 1.0 / (std + 1e-9);
            double spurScore = 1.0 / (spurCount + 1);
            double clipScore = 1.0 / (clipCount + 1);
            double slopeScore = 1.0 / (Math.Abs(slope) + 1e-9);

            return flatnessScore * 0.4 +
                   spurScore * 0.3 +
                   clipScore * 0.2 +
                   slopeScore * 0.1;
        }

        private static double ComputeStdDev(double[] values)
        {
            double mean = values.Average();
            double sumSq = values.Sum(v => (v - mean) * (v - mean));
            return Math.Sqrt(sumSq / values.Length);
        }

        private static double ComputeSlope(double[] y)
        {
            int n = y.Length;
            double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;

            for (int i = 0; i < n; i++)
            {
                sumX += i;
                sumY += y[i];
                sumXY += i * y[i];
                sumXX += i * i;
            }

            return (n * sumXY - sumX * sumY) / (n * sumXX - sumX * sumX + 1e-9);
        }
    }
}
