using RASTA.Core.Calibration;
using RASTA.Core.Processing;
using RASTA.Core.Sdr;
using RASTA.Core.Storage;
using RASTA.Core.Telescope;
using RASTA.Infrastructure.Services;
using RASTA.Processing.HiPipeline;
using System.IO;
using System.Runtime;
using System.Windows.Input;

namespace RASTA.Processing.Calibration
{
    public sealed class Calibrator
    {
        private readonly IFftEngine _fftEngine;
        private readonly FitsFileIo _fitsFileWriter;
        private readonly UserOptionsService _userOptionsService;

        public Calibrator(IFftEngine fftEngine, FitsFileIo fitsFileWriter, UserOptionsService userOptionsService)
        {
            _fftEngine = fftEngine;
            _fitsFileWriter = fitsFileWriter;
            _userOptionsService = userOptionsService;
        }

        // How long after a gain change to wait, in seconds, before trusting samples -
        // keeps a switching transient from biasing that gain's averaged spectrum.
        private const double GainSettleTimeSec = 0.005;

        // Maximum fraction of raw I/Q bytes allowed at the ADC rail (0 or 255 for the
        // RTL-SDR's 8-bit samples) before a gain is considered to be overloading the
        // front end. A few rail hits are expected from ordinary Gaussian noise; this is
        // for detecting real saturation, not zero-tolerance.
        private const double SaturationFractionThreshold = 0.0005; // 0.05%

        private sealed record GainTrial(
            double Gain,
            double SaturationFraction,
            double StdDev,
            int SpurCount,
            double Slope);

        /// <summary>
        /// Runs a full gain-sweep calibration and returns a CalibrationProfile.
        /// </summary>
        public async Task<CalibrationProfile> RunFullCalibrationAsync(
            ISdrDevice device,
            double frequencyHz,
            double sampleRateHz,
            TimeSpan dwellTime,
            TimeSpan baselineDwellTime,
            int fftSize,
            Action<string, double>? progressCallback,
            CancellationToken ct)
        {
            var baseFolder = _userOptionsService.Options.CaptureFolder;
            var supportedGains = device.SupportedGainsDb.ToList();
            if (supportedGains.Count == 0)
                throw new InvalidOperationException("SDR device reports no supported gains.");

            var gainTrials = new List<GainTrial>();

            int totalSteps = supportedGains.Count + 3; // +1 baseline, +1 finalize
            int currentStep = 0;

            // Compute sample count safely
            uint sampleCount = (uint)Math.Ceiling(sampleRateHz * dwellTime.TotalSeconds);

            string filePath = null;
            FitsFileMetaData meta = null;

            var startTime = DateTime.UtcNow;
            int bytesPerFrame = fftSize * 2;
            int settleFrames = (int)Math.Ceiling(sampleRateHz * GainSettleTimeSec / fftSize);

            foreach (var gain in supportedGains)
            {
                ct.ThrowIfCancellationRequested();

                currentStep++;
                progressCallback?.Invoke($"Trying gain {gain} dB", (double)currentStep / totalSteps);

                var rawIq = await device.CaptureRawIqAsync(
                    frequencyHz,
                    sampleRateHz,
                    gain,
                    sampleCount,
                    ct).ConfigureAwait(false);

                // Real ADC-saturation check on the raw I/Q bytes: samples pinned at the
                // rails mean the front end is overloaded at this gain, regardless of what
                // the resulting spectrum looks like.
                double saturationFraction = ComputeSaturationFraction(rawIq);

                // Average the power spectrum across every fftSize frame in this trial's
                // capture (not just the first one), so flatness/spur/slope reflect this
                // gain's actual receiver response rather than a single noisy periodogram.
                // Uses the same SKAO-normalized power + accumulator as the baseline build,
                // so trials are scored consistently with how the chosen baseline is built.
                int totalFrames = rawIq.Length / bytesPerFrame;
                int skip = Math.Min(settleFrames, Math.Max(totalFrames - 1, 0));

                var trialAcc = new HiStreamingAccumulator(fftSize);
                for (int f = skip; f < totalFrames; f++)
                {
                    var chunk = new byte[bytesPerFrame];
                    Buffer.BlockCopy(rawIq, f * bytesPerFrame, chunk, 0, bytesPerFrame);

                    var spectrum = _fftEngine.ComputeSkAoPower(chunk, fftSize);
                    trialAcc.AddBaselineFrame(spectrum);
                }

                if (trialAcc.BaselineFrames == 0)
                    throw new InvalidOperationException(
                        $"Gain sweep dwell time is too short to average even one frame at gain {gain} dB.");

                var avgSpectrum = trialAcc.GetBaselineAverage();

                double std = ComputeStdDev(avgSpectrum);
                double mean = avgSpectrum.Average();
                int spurCount = avgSpectrum.Count(v => v > mean + 6 * std);
                double slope = ComputeSlope(avgSpectrum);

                gainTrials.Add(new GainTrial(gain, saturationFraction, std, spurCount, slope));
            }

            // Saturation is a hard requirement, not something to trade off against
            // flatness: any gain that measurably overloads the ADC is disqualified before
            // scoring even starts.
            var candidates = gainTrials.Where(t => t.SaturationFraction <= SaturationFractionThreshold).ToList();
            if (candidates.Count == 0)
            {
                // Every gain saturated against the terminator (shouldn't normally happen) -
                // fall back to whichever saturated the least rather than failing outright.
                candidates = gainTrials.OrderBy(t => t.SaturationFraction).Take(1).ToList();
            }

            // Normalise the surviving metrics to [0,1] before combining them, so the
            // weights below actually control the trade-off instead of whichever raw
            // metric happens to have the largest magnitude.
            double minStd = candidates.Min(t => t.StdDev), maxStd = candidates.Max(t => t.StdDev);
            double minSpur = candidates.Min(t => t.SpurCount), maxSpur = candidates.Max(t => t.SpurCount);
            double minSlope = candidates.Min(t => Math.Abs(t.Slope)), maxSlope = candidates.Max(t => Math.Abs(t.Slope));

            var best = candidates
                .Select(t => (
                    t.Gain,
                    Score: (1.0 - Normalize(t.StdDev, minStd, maxStd)) * 0.5            // flatter is better
                         + (1.0 - Normalize(t.SpurCount, minSpur, maxSpur)) * 0.3       // fewer spurs is better
                         + (1.0 - Normalize(Math.Abs(t.Slope), minSlope, maxSlope)) * 0.2)) // flatter slope is better
                .OrderByDescending(s => s.Score)
                .First();

            progressCallback?.Invoke($"Selected gain {best.Gain} dB", 0.95);

            // Capture long baseline at chosen gain. This capture alone can take far
            // longer than any single gain trial, so it gets its own 0-1 progress run
            // (real, byte-driven) instead of just advancing the coarse step counter.
            currentStep++;
            progressCallback?.Invoke($"Capturing baseline at {best.Gain} dB", 0.0);

            uint baselineSampleCount = (uint)Math.Ceiling(sampleRateHz * baselineDwellTime.TotalSeconds);

            var baselineRawIq = await device.CaptureRawIqAsync(
                frequencyHz,
                sampleRateHz,
                best.Gain,
                baselineSampleCount,
                ct,
                pct => progressCallback?.Invoke($"Capturing baseline at {best.Gain} dB", pct)
                ).ConfigureAwait(false);


            // save the baseline to a FITS file
            filePath = FitsPathBuilder.BuildCalibrationFilePath(baseFolder, "base", startTime, frequencyHz, best.Gain);

            meta = new FitsFileMetaData
            {
                Origin = "RTL-SDR",
                DataFormat = "UINT8_IQ",
                CentFreqHz = frequencyHz,
                SampFreqHz = sampleRateHz,
                FftSize = fftSize,
                GainDb = best.Gain,
                ObservationDate = DateTime.UtcNow,
                DwellTimeSec = baselineDwellTime.TotalSeconds
            };

            _fitsFileWriter.WriteRawIq(filePath, baselineRawIq, meta);

            currentStep++;
            progressCallback?.Invoke($"Calculating baseline", (double)currentStep / totalSteps);

            // Build the averaged baseline the same way HiStreamingPipeline will later
            // build the observation's averaged capture spectrum (SKAO-normalized power,
            // chunked into fftSize frames, arithmetic mean) so the two are directly
            // comparable when HiStreamingPipeline.Process combines them.
            var accumulator = new HiStreamingAccumulator(fftSize);

            int bytesPerChunk = fftSize * 2; // adjust if your IQ format differs
            for (int offset = 0; offset + bytesPerChunk <= baselineRawIq.Length; offset += bytesPerChunk)
            {
                var chunk = new byte[bytesPerChunk];
                Buffer.BlockCopy(baselineRawIq, offset, chunk, 0, bytesPerChunk);

                var spectrum = _fftEngine.ComputeSkAoPower(chunk, fftSize);
                accumulator.AddBaselineFrame(spectrum);
            }

            var baselineSpectrum = accumulator.GetBaselineAverage();

            // At this point, baselineSpectrum holds the averaged linear-power baseline

            currentStep++;
            progressCallback?.Invoke("Finalizing calibration", (double)currentStep / totalSteps);

            return new CalibrationProfile
            {
                CenterFrequencyHz = frequencyHz,
                SampleRateHz = sampleRateHz,
                FftSize = fftSize,
                GainDb = best.Gain,
                BaselineSpectrum = baselineSpectrum,
                BaselineMean = baselineSpectrum.Average(),
                BaselineStdDev = ComputeStdDev(baselineSpectrum),
                TimestampUtc = DateTime.UtcNow,
                DeviceId = device.DeviceId
            };
        }

        /// <summary>
        /// Fraction of raw I/Q bytes sitting at the ADC rail (0 or 255 for the RTL-SDR's
        /// unsigned 8-bit samples). This is the direct hardware measure of front-end
        /// overload - unlike inspecting the derived spectrum, it can't be fooled by a
        /// single strong spur that never actually saturated anything.
        /// </summary>
        private static double ComputeSaturationFraction(byte[] rawIq)
        {
            if (rawIq.Length == 0)
                return 0.0;

            int saturated = 0;
            for (int i = 0; i < rawIq.Length; i++)
            {
                byte v = rawIq[i];
                if (v == 0 || v == 255)
                    saturated++;
            }

            return (double)saturated / rawIq.Length;
        }

        private static double Normalize(double value, double min, double max) =>
            max > min ? (value - min) / (max - min) : 0.0;

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
