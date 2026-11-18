using RASTA.Processing.HiPipeline;

namespace RASTA.Tests.Synthetic
{
    /// <summary>One Gaussian emission line, as a fraction of the system (off-line) level.</summary>
    public record SyntheticLine(double VelocityKmPerSec, double FwhmKmPerSec, double AmplitudeFraction);

    /// <summary>
    /// Builds baseline/capture averaged power spectra with known properties, in the same shape
    /// HiStreamingPipeline.Process expects: raw FFT-bin order (DC at index 0), linear power.
    ///
    /// The model is: power[bin] = bandpass[bin] * (1 + sum of lines) * gain, plus Gaussian
    /// noise of relative standard deviation 1/sqrt(frames) per bin (what averaging that many
    /// independent power-spectrum frames leaves), plus an optional DC/LO spike at the centre.
    /// Velocities are topocentric (no LSR offset) in the radio convention the pipeline uses.
    /// </summary>
    public sealed class SyntheticSpectrum
    {
        public int FftSize { get; init; } = 4096;
        public double SampleRateHz { get; init; } = 2_400_000;
        public double CenterFreqHz { get; init; } = 1_420_700_000;

        /// <summary>Frames averaged into the baseline; 0 means noise-free.</summary>
        public int BaselineFrames { get; init; }

        /// <summary>Frames averaged into the capture; 0 means noise-free.</summary>
        public int CaptureFrames { get; init; }

        /// <summary>Capture gain relative to the baseline (receiver drift between the two).</summary>
        public double CaptureGain { get; init; } = 1.0;

        /// <summary>DC/LO spike height as a multiple of the local level, in both spectra; 0 for none.</summary>
        public double DcSpikeRatio { get; init; }

        public IReadOnlyList<SyntheticLine> Lines { get; init; } = Array.Empty<SyntheticLine>();

        public int Seed { get; init; } = 12345;

        /// <summary>Frequency of each bin in monotonic (fftshifted) order - matches the pipeline's own axis.</summary>
        public double[] FrequencyAxisHz()
        {
            var freq = new double[FftSize];
            double df = SampleRateHz / FftSize;
            for (int i = 0; i < FftSize; i++)
                freq[i] = CenterFreqHz + (i - FftSize / 2) * df;
            return freq;
        }

        public static double VelocityOf(double frequencyHz) =>
            HiConstants.SpeedOfLightKmPerSec * (HiConstants.HiFreqHz - frequencyHz) / HiConstants.HiFreqHz;

        /// <summary>Returns (baseline, capture) in raw FFT-bin order, ready for Process.</summary>
        public (double[] baseline, double[] capture) Build()
        {
            var rng = new Random(Seed);
            double[] freq = FrequencyAxisHz();
            int n = FftSize;

            var baseline = new double[n];
            var capture = new double[n];

            for (int i = 0; i < n; i++)
            {
                // A smooth, RTL-SDR-like bandpass: a gentle ripple that rolls off toward the edges.
                double x = (i - n / 2) / (double)(n / 2); // -1..+1 across the band
                double bandpass = 1000.0 * (1.0 - 0.35 * x * x) * (1.0 + 0.03 * Math.Cos(6.0 * x));

                double v = VelocityOf(freq[i]);
                double line = 0.0;
                foreach (var l in Lines)
                {
                    double sigma = l.FwhmKmPerSec / 2.354820045;
                    double z = (v - l.VelocityKmPerSec) / sigma;
                    line += l.AmplitudeFraction * Math.Exp(-0.5 * z * z);
                }

                double b = bandpass;
                double c = bandpass * (1.0 + line) * CaptureGain;

                if (BaselineFrames > 0)
                    b *= 1.0 + NextGaussian(rng) / Math.Sqrt(BaselineFrames);
                if (CaptureFrames > 0)
                    c *= 1.0 + NextGaussian(rng) / Math.Sqrt(CaptureFrames);

                baseline[i] = b;
                capture[i] = c;
            }

            if (DcSpikeRatio > 0)
            {
                baseline[n / 2] *= DcSpikeRatio;
                capture[n / 2] *= DcSpikeRatio;
            }

            // Built in monotonic order above; the pipeline wants raw FFT order. For an even
            // length, fftshift is its own inverse.
            return (HiStreamingPipeline.FftShift(baseline), HiStreamingPipeline.FftShift(capture));
        }

        private static double NextGaussian(Random rng)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
