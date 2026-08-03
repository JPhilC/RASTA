using MathNet.Numerics.IntegralTransforms;
using RASTA.Processing.IfAverage;
using System.Numerics;

namespace RASTA.Processing.HiPipeline
{

namespace RASTA.Processing.HiPipeline
    {
        public static class SkaoConstants
        {
            // From SKAO tabletop code (constants.py)
            public const double HiFreqHz = 1_420_405_751.77;          // 1420.40575177 MHz
            public const double SpeedOfLightKmPerSec = 299_792.458;   // km/s

            // These should match SKAO NUM_INTEGRATION_BINS / NUM_INTEGRATIONS
            // e.g. 256 bins, 16 integrations → 4096 FFT size.
            public const int NumIntegrationBins = 256;
            public const int NumIntegrations = 16;

            // Continuum masking (CH_CUT / CH_OFF in velocity space)
            public const double ChCutKmPerSec = 20.0;    // exclude central HI/DC region
            public const double ChOffKmPerSec = 300.0;   // exclude far wings / RFI
        }

        /// <summary>
        /// SKAO-style FFT power calculator: abs(fft/N * 2)**2 with Hann window.
        /// This mirrors record_data.record_power_spectrum() behaviour. 
        /// </summary>
        public class SkaoFftPower
        {
            public double[] ComputePower(byte[] rawIq, int fftSize)
            {
                int n = rawIq.Length / 2;
                if (n != fftSize)
                    throw new InvalidOperationException("Raw IQ chunk size must match FFT size.");

                var buffer = new Complex[fftSize];

                // IQ bytes → Complex
                for (int i = 0; i < fftSize; i++)
                {
                    double re = rawIq[2 * i] - 128;
                    double im = rawIq[2 * i + 1] - 128;
                    buffer[i] = new Complex(re, im);
                }

                // Hann window
                ApplyHannWindow(buffer);

                // FFT (Matlab-style scaling)
                Fourier.Forward(buffer, FourierOptions.Matlab);

                // abs(fft/N * 2)**2
                double[] pwr = new double[fftSize];
                double scale = 2.0 / fftSize;

                for (int i = 0; i < fftSize; i++)
                {
                    double re = buffer[i].Real * scale;
                    double im = buffer[i].Imaginary * scale;
                    pwr[i] = (re * re) + (im * im);
                }

                return pwr;
            }

            private static void ApplyHannWindow(Complex[] buffer)
            {
                int n = buffer.Length;
                for (int i = 0; i < n; i++)
                {
                    double w = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
                    buffer[i] *= w;
                }
            }
        }

        /// <summary>
        /// SKAO-style HI pipeline: bin integration + baseline division + continuum subtraction + SG smoothing. 
        /// </summary>
        public class SkaoHiPipelineProcessor
        {
            public double[] FrequencyHz { get; private set; }
            public double[] VelocityKmPerSec { get; private set; }
            public double[] RatioSpectrum { get; private set; }
            public double[] HiSpectrum { get; private set; }

            /// <summary>
            /// Full SKAO-style pipeline from already-accumulated baseline/capture power.
            /// baselinePower/capturePower are expected to be length = NumIntegrationBins * NumIntegrations.
            /// </summary>
            public void Process(
                double[] baselinePower,
                double[] capturePower,
                double sampleRateHz,
                double centerFreqHz)
            {
                if (baselinePower == null) throw new ArgumentNullException(nameof(baselinePower));
                if (capturePower == null) throw new ArgumentNullException(nameof(capturePower));
                if (baselinePower.Length != capturePower.Length)
                    throw new ArgumentException("Baseline and capture spectra must have the same length.");

                int totalBins = baselinePower.Length;
                int bins = SkaoConstants.NumIntegrationBins;
                int ints = SkaoConstants.NumIntegrations;

                if (totalBins != bins * ints)
                    throw new InvalidOperationException(
                        $"Expected {bins * ints} bins (NumIntegrationBins * NumIntegrations), got {totalBins}.");

                // 1. Bin integration: reshape (NumIntegrations, NumIntegrationBins) and mean over axis=0
                var baselineIntegrated = IntegrateBins(baselinePower, ints, bins);
                var captureIntegrated = IntegrateBins(capturePower, ints, bins);

                // 2. fftshift spectra so DC is in the middle
                baselineIntegrated = FftShift(baselineIntegrated);
                captureIntegrated = FftShift(captureIntegrated);

                int n = baselineIntegrated.Length;

                // 3. Frequency axis (fftshift-style, centred on centreFreqHz)
                FrequencyHz = ComputeFrequencyAxis(n, sampleRateHz, centerFreqHz);

                // 4. Velocity axis from frequency axis
                VelocityKmPerSec = new double[n];
                for (int i = 0; i < n; i++)
                {
                    double f = FrequencyHz[i];
                    VelocityKmPerSec[i] =
                        SkaoConstants.SpeedOfLightKmPerSec * ((f - SkaoConstants.HiFreqHz) / SkaoConstants.HiFreqHz);
                }

                // 5. Baseline division: capture / baseline, then SKAO scale factor (×300)
                RatioSpectrum = new double[n];
                for (int i = 0; i < n; i++)
                {
                    double b = baselineIntegrated[i];
                    double c = captureIntegrated[i];
                    RatioSpectrum[i] = (b <= 0.0) ? 0.0 : c / b;
                }

                const double scale = 300.0;
                for (int i = 0; i < n; i++)
                    RatioSpectrum[i] *= scale;

                // 6. Mask “clean” continuum region (CH_CUT / CH_OFF in velocity space)
                bool[] mask = VelocityKmPerSec
                    .Select(v => Math.Abs(v) > SkaoConstants.ChCutKmPerSec &&
                                 Math.Abs(v) < SkaoConstants.ChOffKmPerSec)
                    .ToArray();

                (double m, double b0) = FitLinearMasked(VelocityKmPerSec, RatioSpectrum, mask);

                // 7. Subtract continuum to isolate HI line
                HiSpectrum = new double[n];
                for (int i = 0; i < n; i++)
                {
                    double v = VelocityKmPerSec[i];
                    double continuum = m * v + b0;
                    HiSpectrum[i] = RatioSpectrum[i] - continuum;
                }

                // 8. Savitzky–Golay smoothing (SKAO always applies savgol_filter(sp, 5, 2))
                ApplySavitzkyGolay(HiSpectrum);
            }

            /// <summary>
            /// Integrate FFT bins: reshape (numIntegrations, numBins) and mean over integrations.
            /// Mirrors pwr.reshape(NUM_INTEGRATIONS, NUM_INTEGRATION_BINS).mean(axis=0). 
            /// </summary>
            private static double[] IntegrateBins(double[] data, int numIntegrations, int numBins)
            {
                var output = new double[numBins];

                for (int bin = 0; bin < numBins; bin++)
                {
                    double sum = 0.0;
                    for (int integ = 0; integ < numIntegrations; integ++)
                    {
                        int idx = integ * numBins + bin;
                        sum += data[idx];
                    }
                    output[bin] = sum / numIntegrations;
                }

                return output;
            }

            private static double[] FftShift(double[] data)
            {
                int n = data.Length;
                int half = n / 2;

                var shifted = new double[n];
                Array.Copy(data, half, shifted, 0, half);
                Array.Copy(data, 0, shifted, half, half);

                return shifted;
            }

            private static (double m, double b) FitLinearMasked(double[] x, double[] y, bool[] mask)
            {
                double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
                int count = 0;

                for (int i = 0; i < x.Length; i++)
                {
                    if (!mask[i]) continue;

                    sumX += x[i];
                    sumY += y[i];
                    sumXY += x[i] * y[i];
                    sumX2 += x[i] * x[i];
                    count++;
                }

                if (count < 2)
                    return (0, 0);

                double denom = count * sumX2 - sumX * sumX;
                double m = (count * sumXY - sumX * sumY) / denom;
                double b = (sumY - m * sumX) / count;

                return (m, b);
            }

            private static double[] ComputeFrequencyAxis(int length, double sampleRateHz, double centerFreqHz)
            {
                var freq = new double[length];

                double df = sampleRateHz / length;
                int mid = length / 2;

                for (int i = 0; i < length; i++)
                    freq[i] = centerFreqHz + (i - mid) * df;

                return freq;
            }

            private static void ApplySavitzkyGolay(double[] data)
            {
                var sg = new SavitzkyGolay { Enabled = true };
                sg.Process(data);
            }
        }

        /// <summary>
        /// Convenience wrapper: from raw IQ files to SKAO-style HI spectrum.
        /// This mirrors the overall flow in obs_manager/tabletop_app: record → FFT → integrate → HI pipeline. 
        /// </summary>
        public class SkaoHiObservation
        {
            private readonly SkaoFftPower _fft = new SkaoFftPower();
            private readonly SkaoHiPipelineProcessor _pipeline = new SkaoHiPipelineProcessor();

            public SkaoHiPipelineProcessor Pipeline => _pipeline;

            public void ProcessIq(
                byte[] baselineIq,
                byte[] captureIq,
                int fftSize,
                double sampleRateHz,
                double centerFreqHz)
            {
                int bytesPerChunk = fftSize * 2;

                // Accumulate baseline power over NUM_INTEGRATIONS chunks
                var baselineAccum = new double[fftSize * SkaoConstants.NumIntegrations];
                int baselineChunks = 0;

                for (int offset = 0;
                     offset + bytesPerChunk <= baselineIq.Length &&
                     baselineChunks < SkaoConstants.NumIntegrations;
                     offset += bytesPerChunk)
                {
                    var chunk = new byte[bytesPerChunk];
                    Buffer.BlockCopy(baselineIq, offset, chunk, 0, bytesPerChunk);

                    var pwr = _fft.ComputePower(chunk, fftSize);

                    // Store this integration’s spectrum into the big array
                    int baseIdx = baselineChunks * fftSize;
                    Array.Copy(pwr, 0, baselineAccum, baseIdx, fftSize);

                    baselineChunks++;
                }

                // Accumulate capture power over NUM_INTEGRATIONS chunks
                var captureAccum = new double[fftSize * SkaoConstants.NumIntegrations];
                int captureChunks = 0;

                for (int offset = 0;
                     offset + bytesPerChunk <= captureIq.Length &&
                     captureChunks < SkaoConstants.NumIntegrations;
                     offset += bytesPerChunk)
                {
                    var chunk = new byte[bytesPerChunk];
                    Buffer.BlockCopy(captureIq, offset, chunk, 0, bytesPerChunk);

                    var pwr = _fft.ComputePower(chunk, fftSize);

                    int baseIdx = captureChunks * fftSize;
                    Array.Copy(pwr, 0, captureAccum, baseIdx, fftSize);

                    captureChunks++;
                }

                if (baselineChunks != SkaoConstants.NumIntegrations ||
                    captureChunks != SkaoConstants.NumIntegrations)
                {
                    throw new InvalidOperationException(
                        $"Expected {SkaoConstants.NumIntegrations} integrations for both baseline and capture.");
                }

                // Now run the SKAO-style HI pipeline
                _pipeline.Process(
                    baselineAccum,
                    captureAccum,
                    sampleRateHz,
                    centerFreqHz);
            }
        }
    }

}
