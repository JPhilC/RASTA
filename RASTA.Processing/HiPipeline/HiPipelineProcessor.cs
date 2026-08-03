using System;
using System.Linq;
using RASTA.Processing.IfAverage;

namespace RASTA.Processing.HiPipeline
{
    public static class HiConstants
    {
        public const double SpeedOfLightKmPerSec = 299_792.458;
        public const double HiFreqHz = 1_420_405_751.77; // 1420.40575177 MHz

        // Continuum mask in velocity space (you can tune these)
        public const double ChCutKmPerSec = 20.0;   // exclude central HI region
        public const double ChOffKmPerSec = 300.0;  // exclude far wings / RFI
    }

    /// <summary>
    /// Streaming HI accumulator: accepts arbitrary FFT-size frames and accumulates them.
    /// </summary>
    public class HiStreamingAccumulator
    {
        private readonly int _fftSize;

        private readonly double[] _baselineSum;
        private readonly double[] _captureSum;

        private int _baselineFrames;
        private int _captureFrames;

        public HiStreamingAccumulator(int fftSize)
        {
            if (fftSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(fftSize));

            _fftSize = fftSize;
            _baselineSum = new double[fftSize];
            _captureSum = new double[fftSize];
        }

        public int FftSize => _fftSize;
        public int BaselineFrames => _baselineFrames;
        public int CaptureFrames => _captureFrames;

        public void AddBaselineFrame(double[] frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (frame.Length != _fftSize)
                throw new ArgumentException("Baseline frame length must match FFT size.");

            for (int i = 0; i < _fftSize; i++)
                _baselineSum[i] += frame[i];

            _baselineFrames++;
        }

        public void AddCaptureFrame(double[] frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (frame.Length != _fftSize)
                throw new ArgumentException("Capture frame length must match FFT size.");

            for (int i = 0; i < _fftSize; i++)
                _captureSum[i] += frame[i];

            _captureFrames++;
        }

        public (double[] baselineAvg, double[] captureAvg) GetAveragedSpectra()
        {
            if (_baselineFrames == 0 || _captureFrames == 0)
                throw new InvalidOperationException("Need at least one baseline and one capture frame.");

            var baselineAvg = new double[_fftSize];
            var captureAvg = new double[_fftSize];

            for (int i = 0; i < _fftSize; i++)
            {
                baselineAvg[i] = _baselineSum[i] / _baselineFrames;
                captureAvg[i] = _captureSum[i] / _captureFrames;
            }

            return (baselineAvg, captureAvg);
        }
    }

    /// <summary>
    /// FFT-size-agnostic HI pipeline: baseline division, continuum subtraction, SG smoothing.
    /// </summary>
    public class HiStreamingPipeline
    {
        public double[] FrequencyHz { get; private set; }
        public double[] VelocityKmPerSec { get; private set; }
        public double[] RatioSpectrum { get; private set; }
        public double[] HiSpectrum { get; private set; }

        public void Process(
            double[] baselinePower,
            double[] capturePower,
            double sampleRateHz,
            double centerFreqHz,
            bool applySmoothing = true)
        {
            if (baselinePower == null) throw new ArgumentNullException(nameof(baselinePower));
            if (capturePower == null) throw new ArgumentNullException(nameof(capturePower));
            if (baselinePower.Length != capturePower.Length)
                throw new ArgumentException("Baseline and capture spectra must have the same length.");

            int n = baselinePower.Length;

            // 1. fftshift spectra
            baselinePower = FftShift(baselinePower);
            capturePower = FftShift(capturePower);

            // 2. Frequency axis
            FrequencyHz = ComputeFrequencyAxis(n, sampleRateHz, centerFreqHz);

            // 3. Velocity axis
            VelocityKmPerSec = new double[n];
            for (int i = 0; i < n; i++)
            {
                double f = FrequencyHz[i];
                VelocityKmPerSec[i] =
                    HiConstants.SpeedOfLightKmPerSec * ((f - HiConstants.HiFreqHz) / HiConstants.HiFreqHz);
            }

            // 4. Baseline division + scale
            RatioSpectrum = new double[n];
            for (int i = 0; i < n; i++)
            {
                double b = baselinePower[i];
                double c = capturePower[i];
                RatioSpectrum[i] = (b <= 0.0) ? 0.0 : c / b;
            }

            const double scale = 300.0;
            for (int i = 0; i < n; i++)
                RatioSpectrum[i] *= scale;

            // 5. Continuum mask (velocity-based, FFT-size agnostic)
            bool[] mask = VelocityKmPerSec
                .Select(v => Math.Abs(v) > HiConstants.ChCutKmPerSec &&
                             Math.Abs(v) < HiConstants.ChOffKmPerSec)
                .ToArray();

            (double m, double b0) = FitLinearMasked(VelocityKmPerSec, RatioSpectrum, mask);

            // 6. Subtract continuum
            HiSpectrum = new double[n];
            for (int i = 0; i < n; i++)
            {
                double v = VelocityKmPerSec[i];
                double continuum = m * v + b0;
                HiSpectrum[i] = RatioSpectrum[i] - continuum;
            }

            // 7. Optional Savitzky–Golay smoothing
            if (applySmoothing)
            {
                ApplySavitzkyGolay(HiSpectrum);
            }
        }

        private static double[] FftShift(double[] data)
        {
            int n = data.Length;
            int half = n / 2;

            var shifted = new double[n];
            Array.Copy(data, half, shifted, 0, n - half);
            Array.Copy(data, 0, shifted, n - half, half);

            return shifted;
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

        private static void ApplySavitzkyGolay(double[] data)
        {
            var sg = new SavitzkyGolay { Enabled = true };
            sg.Process(data);
        }
    }

    /// <summary>
    /// High-level streaming HI processor: add frames, then compute spectrum.
    /// </summary>
    public class HiStreamingProcessor
    {
        private readonly HiStreamingAccumulator _acc;
        private readonly HiStreamingPipeline _pipe = new HiStreamingPipeline();

        public HiStreamingProcessor(int fftSize)
        {
            _acc = new HiStreamingAccumulator(fftSize);
        }

        public int FftSize => _acc.FftSize;
        public int BaselineFrames => _acc.BaselineFrames;
        public int CaptureFrames => _acc.CaptureFrames;

        public double[] FrequencyHz => _pipe.FrequencyHz;
        public double[] VelocityKmPerSec => _pipe.VelocityKmPerSec;
        public double[] RatioSpectrum => _pipe.RatioSpectrum;
        public double[] HiSpectrum => _pipe.HiSpectrum;

        public void AddBaselineFrame(double[] baselineFrame) =>
            _acc.AddBaselineFrame(baselineFrame);

        public void AddCaptureFrame(double[] captureFrame) =>
            _acc.AddCaptureFrame(captureFrame);

        public void Compute(
            double sampleRateHz,
            double centerFreqHz,
            bool applySmoothing = true)
        {
            var (baselineAvg, captureAvg) = _acc.GetAveragedSpectra();
            _pipe.Process(
                baselineAvg,
                captureAvg,
                sampleRateHz,
                centerFreqHz,
                applySmoothing);
        }
    }
}
