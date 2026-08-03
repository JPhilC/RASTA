using RASTA.Processing.IfAverage;
using System;
using System.Linq;

namespace RASTA.Processing.HiPipeline
{
    public class HiPipelineProcessor
    {
        public const double SpeedOfLightKmPerSec = 299_792.458;
        public const double HiFreqHz = 1_420_405_751.77; // 1420.40575177 MHz

        // SKAO-style masking constants (velocity domain)
        private const double ChCutKmPerSec = 20.0;   // exclude central HI/DC region
        private const double ChOffKmPerSec = 300.0;  // exclude far wings / RFI

        public double[] FrequencyHz { get; private set; }
        public double[] VelocityKmPerSec { get; private set; }
        public double[] HiSpectrum { get; private set; }
        public double[] RatioSpectrum { get; private set; }

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

            // 0. fftshift spectra so DC is in the middle (SKAO does this once)
            baselinePower = FftShift(baselinePower);
            capturePower = FftShift(capturePower);

            int n = baselinePower.Length;

            // 1. Frequency axis (fftshift-style, centred on centreFreqHz)
            FrequencyHz = ComputeFrequencyAxis(n, sampleRateHz, centerFreqHz);

            // 2. Velocity axis from frequency axis
            VelocityKmPerSec = new double[n];
            for (int i = 0; i < n; i++)
            {
                double f = FrequencyHz[i];
                VelocityKmPerSec[i] =
                    SpeedOfLightKmPerSec * ((f - HiFreqHz) / HiFreqHz);
            }

            // 3. Baseline division: capture / baseline, then SKAO scale factor (×300)
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

            // 4. Mask “clean” continuum region (SKAO-style CH_CUT / CH_OFF)
            //    - exclude central ±ChCutKmPerSec (HI bump + DC)
            //    - exclude far wings beyond ±ChOffKmPerSec
            bool[] mask = VelocityKmPerSec
                .Select(v => Math.Abs(v) > ChCutKmPerSec && Math.Abs(v) < ChOffKmPerSec)
                .ToArray();

            (double m, double b0) = FitLinearMasked(VelocityKmPerSec, RatioSpectrum, mask);

            // 5. Subtract continuum to isolate HI line
            HiSpectrum = new double[n];
            for (int i = 0; i < n; i++)
            {
                double v = VelocityKmPerSec[i];
                double continuum = m * v + b0;
                HiSpectrum[i] = RatioSpectrum[i] - continuum;
            }

            // 6. Savitzky–Golay smoothing (SKAO always applies savgol_filter(sp, 5, 2))
            ApplySavitzkyGolay(HiSpectrum);
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
}
