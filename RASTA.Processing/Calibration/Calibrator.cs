using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using RASTA.Core.Calibration;
using RASTA.Core.Processing;
using RASTA.Core.Sdr;

namespace RASTA.Processing.Calibration
{
    public sealed class Calibrator
    {
        private readonly ISdrDevice _sdr;
        private readonly IFftEngine _fft;

        public Calibrator(ISdrDevice sdr, IFftEngine fft)
        {
            _sdr = sdr;
            _fft = fft;
        }

        public async Task<CalibrationProfile> RunAsync(
            double centerFreqHz,
            double sampleRateHz,
            double gainDb,
            TimeSpan duration,
            uint fftSize,
            CancellationToken ct)
        {

            // Total samples required for the calibration duration
            uint totalSamples = (uint)(sampleRateHz * duration.TotalSeconds);

            // Number of FFT blocks to average
            uint blocksNeeded = (uint)(totalSamples / fftSize);

            var accumulator = new double[fftSize];
            uint count = 0;

            // -----------------------------
            // 2. Capture FFT blocks
            // -----------------------------
            while (count < blocksNeeded)
            {
                ct.ThrowIfCancellationRequested();

                // Capture fftSize IQ samples → 2*fftSize bytes
                var rawIq = await _sdr.CaptureRawIqAsync(centerFreqHz, sampleRateHz, gainDb, fftSize, ct);

                // Convert RAW IQ → Complex[]
                var block = new Complex[fftSize];
                for (int i = 0; i < fftSize; i++)
                {
                    double iSample = rawIq[2 * i] - 128;
                    double qSample = rawIq[2 * i + 1] - 128;
                    block[i] = new Complex(iSample, qSample);
                }

                // FFT → power spectrum
                var spectrum = _fft.PowerSpectrum(block);

                // Accumulate
                for (int i = 0; i < fftSize; i++)
                    accumulator[i] += spectrum[i];

                count++;
            }

            // -----------------------------
            // 3. Average noise spectrum
            // -----------------------------
            for (int i = 0; i < fftSize; i++)
                accumulator[i] /= count;

            // -----------------------------
            // 4. Compute gain factor
            // -----------------------------
            double avgNoise = accumulator.Average();
            double gainFactor = 1.0 / avgNoise;

            // -----------------------------
            // 5. Build calibration profile
            // -----------------------------
            return new CalibrationProfile
            {
                TimestampUtc = DateTime.UtcNow,
                CenterFrequencyHz = centerFreqHz,
                SampleRateHz = sampleRateHz,
                FftSize = fftSize,
                NoiseSpectrum = accumulator,
                GainFactor = gainFactor,
                GainDb = gainDb
            };
        }
    }
}
