using MathNet.Numerics.IntegralTransforms;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace RASTA.Processing.HiPipeline
{
    /// <summary>
    /// Constants taken directly from the actual SKAO tabletop-radiotelescope source
    /// (src/skao_trt/constants.py, record_data.py, plotting.py) - not from an earlier
    /// paraphrase of it. See SkaoHiPipelineProcessor/SkaoHiObservation's own remarks
    /// for exactly how each of these is used; several values here previously didn't
    /// match the reference at all (wrong NUM_INTEGRATIONS, CH_CUT/CH_OFF treated as
    /// km/s magnitudes instead of channel indices, HiFreqHz rounded to the nearest
    /// whole Hz).
    /// </summary>
    public static class SkaoConstants
    {
        public const double HiFreqHz = 1_420_405_751.77; // 1420.40575177 MHz
        public const double SpeedOfLightKmPerSec = 299_792.458;

        // NUM_INTEGRATION_BINS in the reference: the fixed number of channels the
        // final spectrum is always binned down to, regardless of how it was recorded.
        public const int NumIntegrationBins = 256;

        // CH_CUT / CH_OFF are channel *indices* into the 256-bin spectrum (25, 40),
        // not velocity thresholds. The reference's continuum fit only ever looks at
        // two small windows - velocity[CH_CUT:CH_OFF] and velocity[-CH_OFF:-CH_CUT],
        // 15 channels each - and the spectrum it finally plots is trimmed to
        // velocity[CH_CUT:-CH_CUT] (206 of the 256 channels survive).
        public const int ChCut = 25;
        public const int ChOff = 40;

        // filter_rfi's own fixed parameters: a 5-point/2nd-order Savitzky-Golay
        // smooth of the continuum-fit windows, with any channel whose residual from
        // that smooth exceeds this many population standard deviations excluded
        // before the linear fit.
        public const int RfiFilterWindow = 5;
        public const int RfiFilterPolyOrder = 2;
        public const double RfiFilterSigma = 3.0;

        // compute_hi_spectrum's fixed display-scale multiplier (sp = pws_sky/pws_bl*300).
        public const double RatioDisplayScale = 300.0;
    }

    /// <summary>
    /// SKAO-style native-resolution FFT power: abs(fft/N * 2)**2, no window function.
    /// record_data.record_power_spectrum genuinely does not window its data before
    /// FFT-ing (unlike the rest of RASTA's own Hann-windowed pipeline) - this is a
    /// deliberate divergence from RASTA's house style, kept because the whole point
    /// of this class is to be an unmodified cross-check against the reference
    /// algorithm, not a RASTA-style variant of it.
    /// </summary>
    public class SkaoFftPower
    {
        /// <summary>
        /// Power spectrum of one frame of <paramref name="nativeFftSize"/> complex
        /// samples starting at byte <paramref name="offset"/> in <paramref name="rawIq"/>.
        /// Raw FFT-bin order (DC at index 0) - not yet shifted or binned down.
        /// </summary>
        public double[] ComputeNativePower(byte[] rawIq, int offset, int nativeFftSize)
        {
            var buffer = new Complex[nativeFftSize];

            for (int i = 0; i < nativeFftSize; i++)
            {
                double re = rawIq[offset + 2 * i] - 128;
                double im = rawIq[offset + 2 * i + 1] - 128;
                buffer[i] = new Complex(re, im);
            }

            // FFT (numpy's np.fft.fft has no built-in normalization, matching MathNet's
            // Matlab-style scaling here).
            Fourier.Forward(buffer, FourierOptions.Matlab);

            var pwr = new double[nativeFftSize];
            double scale = 2.0 / nativeFftSize;

            for (int i = 0; i < nativeFftSize; i++)
            {
                double re = buffer[i].Real * scale;
                double im = buffer[i].Imaginary * scale;
                pwr[i] = (re * re) + (im * im);
            }

            return pwr;
        }
    }

    /// <summary>
    /// SKAO-style HI pipeline from an already frame-accumulated, 256-channel binned
    /// power spectrum: fftshift, frequency/velocity axes, baseline division,
    /// channel-index continuum fit with RFI rejection, edge trim. See
    /// SkaoHiObservation.ProcessIq for how a raw capture is turned into the 256-bin
    /// input this expects.
    /// </summary>
    public class SkaoHiPipelineProcessor
    {
        public double[] FrequencyHz { get; private set; }
        public double[] VelocityKmPerSec { get; private set; }
        public double[] RatioSpectrum { get; private set; }
        public double[] HiSpectrum { get; private set; }

        /// <summary>
        /// baselinePower/capturePower must each be length SkaoConstants.NumIntegrationBins
        /// (256), raw FFT-bin order (DC at index 0), already summed across every native
        /// frame in the respective file - see SkaoHiObservation.ProcessIq.
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

            int n = SkaoConstants.NumIntegrationBins;
            if (baselinePower.Length != n)
                throw new InvalidOperationException($"Expected {n} bins, got {baselinePower.Length}.");

            // 1. fftshift so DC is in the middle.
            var baselineShifted = FftShift(baselinePower);
            var captureShifted = FftShift(capturePower);

            // 1a. Excise the receiver's DC/LO-leakage spike - NOT part of the actual
            //     reference algorithm (compute_hi_spectrum never touches it - see the
            //     class remarks on SkaoHiObservation), added here as a deliberate,
            //     documented divergence. The reference keeps this spike from dominating
            //     its own chart by FFT-ing at native 2**21 resolution before binning
            //     down to 256 channels, which dilutes one narrow spike bin against
            //     ~8191 "normal" neighbours per output channel; RASTA FFTs at its own,
            //     much smaller native size first (see SkaoHiObservation.ProcessIq), so
            //     the same spike is diluted by only ~15 neighbours and remains a large
            //     fraction of its output channel - RASTA has no way to replicate the
            //     reference's own dilution without capturing at that same native
            //     resolution, so this excises it directly instead, using the same
            //     baseline-only detection HiStreamingPipeline.RemoveDcSpike uses.
            RemoveDcSpike(baselineShifted, captureShifted);

            // 2. Frequency axis (fftshift-style, centred on centerFreqHz - the reference
            //    generates this from a plain 256-point fftfreq spanning the full
            //    sample rate, which is exactly the spacing binning down to 256 channels
            //    over that same bandwidth already produces).
            FrequencyHz = ComputeFrequencyAxis(n, sampleRateHz, centerFreqHz);

            // 3. Velocity axis - radio convention: v > 0 means redshifted/receding
            //    (observed frequency below the rest frequency). An earlier version of
            //    this file computed (f - HiFreqHz)/HiFreqHz here, the opposite sign.
            VelocityKmPerSec = new double[n];
            for (int i = 0; i < n; i++)
            {
                double f = FrequencyHz[i];
                VelocityKmPerSec[i] =
                    SkaoConstants.SpeedOfLightKmPerSec * ((SkaoConstants.HiFreqHz - f) / SkaoConstants.HiFreqHz);
            }

            // 4. Baseline division + the reference's own display scale
            //    (compute_hi_spectrum: sp = pws_sky / pws_bl * 300).
            RatioSpectrum = new double[n];
            for (int i = 0; i < n; i++)
            {
                double b = baselineShifted[i];
                double c = captureShifted[i];
                RatioSpectrum[i] = (b <= 0.0) ? 0.0 : (c / b) * SkaoConstants.RatioDisplayScale;
            }

            // 5. Continuum fit from two small edge windows (channel-index based, NOT a
            //    velocity-magnitude mask), with RFI outliers rejected first - mirrors
            //    compute_hi_spectrum/filter_rfi exactly.
            (double m, double b0) = FitContinuumFromEdgeWindows(VelocityKmPerSec, RatioSpectrum, n);

            // 6. Subtract continuum to isolate the HI line.
            var hiFull = new double[n];
            for (int i = 0; i < n; i++)
            {
                double v = VelocityKmPerSec[i];
                hiFull[i] = RatioSpectrum[i] - (m * v + b0);
            }

            // 7. The reference never smooths this final spectrum - savgol_filter only
            //    ever runs inside filter_rfi, over the small continuum-fit windows
            //    above, not over the returned/plotted HI spectrum. An earlier version
            //    of this file applied a final Savitzky-Golay pass here; that's removed.

            // 8. Trim CH_CUT channels off each edge before publishing, matching
            //    plot_hi_spectrum_on_axes exactly (206 of 256 channels survive).
            int cut = SkaoConstants.ChCut;
            FrequencyHz = Trim(FrequencyHz, cut);
            VelocityKmPerSec = Trim(VelocityKmPerSec, cut);
            RatioSpectrum = Trim(RatioSpectrum, cut);
            HiSpectrum = Trim(hiFull, cut);
        }

        private static double[] Trim(double[] data, int cut) =>
            data.Skip(cut).Take(data.Length - 2 * cut).ToArray();

        /// <summary>
        /// Re-orders a raw FFT-bin-order array (DC at index 0) into monotonic
        /// frequency order (most negative frequency first, DC in the middle).
        /// </summary>
        private static double[] FftShift(double[] data)
        {
            int n = data.Length;
            int half = n / 2;

            var shifted = new double[n];
            Array.Copy(data, half, shifted, 0, half);
            Array.Copy(data, 0, shifted, half, half);

            return shifted;
        }

        // How far above a robust local "normal" level (in MAD-based standard
        // deviations) counts as the spike, and the upper bound on how many bins
        // either side of center it can be judged to extend. NOT the same fixed
        // *ratio* test (4x) HiStreamingPipeline.RemoveDcSpike uses on native-
        // resolution data: measured directly against real baseline/capture FITS
        // files, this class's own 16-native-bins-per-output-channel binning (see
        // SkaoHiObservation.ProcessIq) dilutes what's a dramatic spike at native
        // resolution down to just ~2.3x the local median in linear-ratio terms - well
        // under a 4x bar - even though it's still ~144 robust standard deviations
        // above the same local baseline, since the surrounding "normal" bins vary by
        // only a fraction of a percent. A ratio test tuned for undiluted native data
        // simply misses it at this resolution; the same MAD-based sigma test
        // HiStreamingPipeline.Despike/MarkSpikeCandidates already uses for exactly
        // this "modest ratio but statistically obvious" situation catches it cleanly.
        private const double DcSpikeThresholdSigma = HiConstants.DefaultDespikeThresholdSigma;
        private const int DcSpikeMaxHalfWidthBins = 4;

        /// <summary>
        /// Excises the receiver's fixed LO/DC-leakage spike - see the "1a." remarks in
        /// Process for why this class carries it despite otherwise mirroring the
        /// reference exactly. Detected from baselinePower alone, never capturePower:
        /// the baseline is always a cold-sky (or terminator) reading with no strong
        /// narrow line of its own, so any bin that spikes there against its local
        /// surroundings is unambiguously receiver artifact, not sky signal - the
        /// decision structurally cannot excise genuine, capture-only HI emission.
        /// </summary>
        private static void RemoveDcSpike(double[] baselinePower, double[] capturePower)
        {
            int n = baselinePower.Length;
            int center = n / 2;

            var (median, sigma) = LocalRobustStatsExcluding(baselinePower, center, DcSpikeMaxHalfWidthBins);
            if (median <= 0)
                return; // no usable "normal" level to compare against - leave spectra alone

            double threshold = median + DcSpikeThresholdSigma * sigma;

            int lo = center, hi = center;
            bool anyElevated = false;

            for (int offset = 0; offset <= DcSpikeMaxHalfWidthBins; offset++)
            {
                int left = center - offset;
                int right = center + offset;
                bool leftHigh = left >= 0 && baselinePower[left] > threshold;
                bool rightHigh = right < n && baselinePower[right] > threshold;

                if (!leftHigh && !rightHigh)
                    break; // both sides back to normal - stop growing the window

                anyElevated = true;
                if (leftHigh) lo = left;
                if (rightHigh) hi = right;
            }

            if (!anyElevated)
                return; // baseline is flat at the center bin - nothing to excise

            InterpolateRange(baselinePower, lo, hi);
            InterpolateRange(capturePower, lo, hi);
        }

        /// <summary>
        /// Median and a robust (outlier-resistant) standard-deviation estimate -
        /// median absolute deviation, scaled by the usual 1.4826 factor - over a
        /// window of bins just outside +-halfWidth of centerIndex, on both sides. A
        /// "normal" reference the spike itself can't bias, as long as halfWidth
        /// generously bounds its true extent. Guarantees a small positive sigma floor
        /// (proportional to the median) rather than a literal zero, so a reference
        /// window that happens to be perfectly flat doesn't make every subsequent bin
        /// read as "infinitely many sigmas" above it. Mirrors HiStreamingPipeline.
        /// LocalRobustStatsExcluding, duplicated here to keep this class self-contained.
        /// </summary>
        private static (double median, double sigma) LocalRobustStatsExcluding(double[] data, int centerIndex, int halfWidth, int refWindow = 5)
        {
            var values = new List<double>();

            for (int i = centerIndex - halfWidth - refWindow; i < centerIndex - halfWidth; i++)
                if (i >= 0) values.Add(data[i]);

            for (int i = centerIndex + halfWidth + 1; i <= centerIndex + halfWidth + refWindow; i++)
                if (i < data.Length) values.Add(data[i]);

            if (values.Count == 0)
                return (0, 0);

            values.Sort();
            double median = values[values.Count / 2];

            var deviations = values.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToList();
            double mad = deviations[deviations.Count / 2];

            const double MadToSigma = 1.4826;
            const double MinRelativeSigma = 1e-6;
            double sigma = Math.Max(mad * MadToSigma, median * MinRelativeSigma);

            return (median, sigma);
        }

        /// <summary>
        /// Replaces data[lo..hi] with a linear interpolation between its immediate
        /// flanking bins. No-ops rather than interpolating off the end of the array if
        /// the range is right at an edge.
        /// </summary>
        private static void InterpolateRange(double[] data, int lo, int hi)
        {
            int n = data.Length;
            int leftIdx = lo - 1;
            int rightIdx = hi + 1;

            if (leftIdx < 0 || rightIdx >= n)
                return;

            double left = data[leftIdx];
            double right = data[rightIdx];
            int span = rightIdx - leftIdx;

            for (int i = lo; i <= hi; i++)
            {
                double t = (double)(i - leftIdx) / span;
                data[i] = left + (right - left) * t;
            }
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

        /// <summary>
        /// Two 15-channel windows near each edge - velocity[ChCut:ChOff] and
        /// velocity[n-ChOff:n-ChCut] - with RFI outliers rejected before the linear fit.
        /// Mirrors compute_hi_spectrum exactly (it builds the same two-window
        /// concatenation and calls filter_rfi on it before np.polyfit).
        /// </summary>
        private static (double m, double b) FitContinuumFromEdgeWindows(double[] velocity, double[] ratio, int n)
        {
            int chCut = SkaoConstants.ChCut;
            int chOff = SkaoConstants.ChOff;
            int windowLen = chOff - chCut;
            int total = windowLen * 2;

            var x = new double[total];
            var y = new double[total];

            for (int i = 0; i < windowLen; i++)
            {
                x[i] = velocity[chCut + i];
                y[i] = ratio[chCut + i];
            }
            for (int i = 0; i < windowLen; i++)
            {
                int srcIdx = n - chOff + i;
                x[windowLen + i] = velocity[srcIdx];
                y[windowLen + i] = ratio[srcIdx];
            }

            bool[] rfiMask = DetectRfiOutliers(
                y, SkaoConstants.RfiFilterWindow, SkaoConstants.RfiFilterPolyOrder, SkaoConstants.RfiFilterSigma);

            var xClean = new List<double>();
            var yClean = new List<double>();
            for (int i = 0; i < total; i++)
            {
                if (!rfiMask[i])
                {
                    xClean.Add(x[i]);
                    yClean.Add(y[i]);
                }
            }

            return FitLinearOls(xClean.ToArray(), yClean.ToArray());
        }

        /// <summary>
        /// Mirrors filter_rfi: Savitzky-Golay smooth the data, flag points whose
        /// residual from the smooth exceeds sigma * population-std(residual).
        /// </summary>
        private static bool[] DetectRfiOutliers(double[] data, int window, int polyOrder, double sigma)
        {
            double[] smooth = SavitzkyGolaySmooth(data, window, polyOrder);

            int n = data.Length;
            var residual = new double[n];
            for (int i = 0; i < n; i++)
                residual[i] = data[i] - smooth[i];

            double mean = residual.Average();
            double variance = residual.Select(r => (r - mean) * (r - mean)).Sum() / n; // population variance (ddof=0), matches np.std default
            double std = Math.Sqrt(variance);
            double threshold = sigma * std;

            var mask = new bool[n];
            for (int i = 0; i < n; i++)
                mask[i] = residual[i] > threshold || residual[i] < -threshold;

            return mask;
        }

        /// <summary>
        /// Savitzky-Golay smoothing matching scipy.signal.savgol_filter's default
        /// mode='interp': a centered local-polynomial fit for interior points, and a
        /// single polynomial fit over the first/last `window` points evaluated at the
        /// edge positions. RASTA.Processing.Dsp.SavitzkyGolay's fixed 5-point kernel
        /// looked like a match (same window/polyOrder=5/2 filter_rfi always uses) but
        /// zeroes rather than fits its two edge samples on each side - fine over a
        /// 256-channel spectrum, but filter_rfi only ever calls this over a 30-channel
        /// window, where 4 zeroed-out samples out of 30 would spuriously read as huge
        /// residuals and get wrongly flagged as RFI. Self-contained here (matching
        /// HiStreamingPipeline's own equivalent, general-purpose implementation)
        /// rather than shared, to keep this class an unmodified, standalone reference
        /// cross-check.
        /// </summary>
        private static double[] SavitzkyGolaySmooth(double[] data, int window, int polyOrder)
        {
            int n = data.Length;

            if (window > n)
                window = (n % 2 == 1) ? n : n - 1; // window must be odd and <= n
            if (window < polyOrder + 1)
                polyOrder = window - 1;

            int half = window / 2;
            var result = new double[n];

            var localX = new double[window];
            for (int k = 0; k < window; k++) localX[k] = k - half;

            for (int i = half; i < n - half; i++)
            {
                var localY = new double[window];
                for (int k = 0; k < window; k++) localY[k] = data[i - half + k];

                double[] coeffs = PolyFitLeastSquares(localX, localY, polyOrder);
                result[i] = EvalPoly(coeffs, 0.0);
            }

            if (n >= window)
            {
                var xs = new double[window];
                for (int k = 0; k < window; k++) xs[k] = k;

                var leftY = new double[window];
                Array.Copy(data, 0, leftY, 0, window);
                double[] leftCoeffs = PolyFitLeastSquares(xs, leftY, polyOrder);
                for (int i = 0; i < half; i++)
                    result[i] = EvalPoly(leftCoeffs, i);

                var rightY = new double[window];
                Array.Copy(data, n - window, rightY, 0, window);
                double[] rightCoeffs = PolyFitLeastSquares(xs, rightY, polyOrder);
                for (int i = 0; i < half; i++)
                {
                    int idx = n - half + i;
                    double localPos = window - half + i;
                    result[idx] = EvalPoly(rightCoeffs, localPos);
                }
            }
            else
            {
                var xs = new double[n];
                for (int k = 0; k < n; k++) xs[k] = k;
                double[] coeffs = PolyFitLeastSquares(xs, data, polyOrder);
                for (int i = 0; i < n; i++)
                    result[i] = EvalPoly(coeffs, i);
            }

            return result;
        }

        private static double[] PolyFitLeastSquares(double[] xs, double[] ys, int order)
        {
            int m = order + 1;
            var ata = new double[m, m];
            var aty = new double[m];

            for (int row = 0; row < m; row++)
            {
                for (int col = 0; col < m; col++)
                {
                    double sum = 0.0;
                    for (int k = 0; k < xs.Length; k++)
                        sum += Math.Pow(xs[k], row) * Math.Pow(xs[k], col);
                    ata[row, col] = sum;
                }

                double sumY = 0.0;
                for (int k = 0; k < xs.Length; k++)
                    sumY += Math.Pow(xs[k], row) * ys[k];
                aty[row] = sumY;
            }

            return SolveLinearSystem(ata, aty);
        }

        private static double[] SolveLinearSystem(double[,] a, double[] b)
        {
            int m = b.Length;
            var aug = new double[m, m + 1];
            for (int i = 0; i < m; i++)
            {
                for (int j = 0; j < m; j++) aug[i, j] = a[i, j];
                aug[i, m] = b[i];
            }

            for (int col = 0; col < m; col++)
            {
                int pivotRow = col;
                double maxAbs = Math.Abs(aug[col, col]);
                for (int row = col + 1; row < m; row++)
                {
                    if (Math.Abs(aug[row, col]) > maxAbs)
                    {
                        maxAbs = Math.Abs(aug[row, col]);
                        pivotRow = row;
                    }
                }
                if (pivotRow != col)
                {
                    for (int j = 0; j <= m; j++)
                    {
                        (aug[col, j], aug[pivotRow, j]) = (aug[pivotRow, j], aug[col, j]);
                    }
                }

                double pivot = aug[col, col];
                if (Math.Abs(pivot) < 1e-14)
                    continue; // singular / near-singular; leave row as-is rather than divide by ~0

                for (int j = col; j <= m; j++) aug[col, j] /= pivot;

                for (int row = 0; row < m; row++)
                {
                    if (row == col) continue;
                    double factor = aug[row, col];
                    if (factor == 0.0) continue;
                    for (int j = col; j <= m; j++)
                        aug[row, j] -= factor * aug[col, j];
                }
            }

            var result = new double[m];
            for (int i = 0; i < m; i++) result[i] = aug[i, m];
            return result;
        }

        private static double EvalPoly(double[] coeffs, double x)
        {
            double result = 0.0;
            double xp = 1.0;
            for (int i = 0; i < coeffs.Length; i++)
            {
                result += coeffs[i] * xp;
                xp *= x;
            }
            return result;
        }

        private static (double m, double b) FitLinearOls(double[] x, double[] y)
        {
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            int count = x.Length;

            for (int i = 0; i < count; i++)
            {
                sumX += x[i];
                sumY += y[i];
                sumXY += x[i] * y[i];
                sumX2 += x[i] * x[i];
            }

            if (count < 2)
                return (0, 0);

            double denom = count * sumX2 - sumX * sumX;
            double m = (count * sumXY - sumX * sumY) / denom;
            double b = (sumY - m * sumX) / count;

            return (m, b);
        }
    }

    /// <summary>
    /// Convenience wrapper: from raw baseline/capture IQ (at a file's own native FFT
    /// size) to the SKAO-style HI spectrum.
    ///
    /// The actual reference (record_data.record_power_spectrum) records 25 reads of
    /// 2**21 raw samples each, runs one huge FFT per read, then bins the resulting
    /// power spectrum down to 256 channels by *summing* groups of adjacent frequency
    /// bins (`pwr.reshape(256, -1).sum(axis=1)`) - full-resolution FFT first, then
    /// bin-sum, never a smaller/decimated FFT - and sums that across all 25 reads.
    /// RASTA doesn't capture in that read/reshape shape (a continuous stream, chunked
    /// at a file's own native FFT size), so this adapts the same *operation* - native
    /// FFT, then sum groups of adjacent bins down to 256, then sum across frames - to
    /// however many native-size frames a file actually contains, rather than a fixed
    /// count of 2**21-sample reads. An earlier version of this class instead ran many
    /// small, literal 256-point FFTs directly on the raw IQ and averaged their power -
    /// a structurally different operation (low native resolution before FFT, not
    /// bin-summed after one at full resolution) that doesn't match the reference at all.
    /// </summary>
    public class SkaoHiObservation
    {
        private readonly SkaoFftPower _fft = new SkaoFftPower();
        private readonly SkaoHiPipelineProcessor _pipeline = new SkaoHiPipelineProcessor();

        public SkaoHiPipelineProcessor Pipeline => _pipeline;

        /// <summary>Native fftSize-length frames actually found and summed from the baseline file.</summary>
        public int BaselineFrameCount { get; private set; }

        /// <summary>Native fftSize-length frames actually found and summed from the capture file.</summary>
        public int CaptureFrameCount { get; private set; }

        public void ProcessIq(
            byte[] baselineIq,
            byte[] captureIq,
            int nativeFftSize,
            double sampleRateHz,
            double centerFreqHz)
        {
            if (nativeFftSize < SkaoConstants.NumIntegrationBins || nativeFftSize % SkaoConstants.NumIntegrationBins != 0)
                throw new InvalidOperationException(
                    $"Native FFT size {nativeFftSize} must be a multiple of {SkaoConstants.NumIntegrationBins} " +
                    "(the reference's fixed output bin count) to bin down cleanly.");

            var baselineAccum = AccumulateBinnedPower(baselineIq, nativeFftSize, out int baselineFrames);
            var captureAccum = AccumulateBinnedPower(captureIq, nativeFftSize, out int captureFrames);

            ProcessAccumulated(baselineAccum, captureAccum, baselineFrames, captureFrames, sampleRateHz, centerFreqHz);
        }

        /// <summary>
        /// The second half of ProcessIq on its own: runs the pipeline on two spectra
        /// already produced by AccumulateBinnedPower. Split out so a caller can keep
        /// (cache) each file's accumulated spectrum and re-run the cheap reduction
        /// without reading the raw IQ again - see VisualiseViewModel/SpectrumCacheService.
        /// </summary>
        public void ProcessAccumulated(
            double[] baselineAccum,
            double[] captureAccum,
            int baselineFrames,
            int captureFrames,
            double sampleRateHz,
            double centerFreqHz)
        {
            if (baselineFrames == 0 || captureFrames == 0)
                throw new InvalidOperationException(
                    "Need at least one native FFT frame in both the baseline and capture IQ.");

            BaselineFrameCount = baselineFrames;
            CaptureFrameCount = captureFrames;

            _pipeline.Process(baselineAccum, captureAccum, sampleRateHz, centerFreqHz);
        }

        /// <summary>
        /// FFTs (unwindowed, native resolution) every whole nativeFftSize-length frame
        /// in rawIq, bins each frame's power spectrum down to
        /// SkaoConstants.NumIntegrationBins channels, and sums that across every frame -
        /// see this class's own remarks for how that maps onto the reference's "25
        /// reads of one big FFT each, summed" shape. Division in
        /// SkaoHiPipelineProcessor.Process is scale-invariant to how many frames went
        /// into each side (sum vs. mean cancels in a ratio), so baseline and capture
        /// frame counts don't need to match each other.
        /// </summary>
        public double[] AccumulateBinnedPower(byte[] rawIq, int nativeFftSize, out int frameCount)
        {
            if (nativeFftSize < SkaoConstants.NumIntegrationBins || nativeFftSize % SkaoConstants.NumIntegrationBins != 0)
                throw new InvalidOperationException(
                    $"Native FFT size {nativeFftSize} must be a multiple of {SkaoConstants.NumIntegrationBins} " +
                    "(the reference's fixed output bin count) to bin down cleanly.");

            int bytesPerFrame = nativeFftSize * 2; // IQ: 2 bytes per complex sample
            int frames = rawIq.Length / bytesPerFrame;

            var accum = new double[SkaoConstants.NumIntegrationBins];

            for (int f = 0; f < frames; f++)
            {
                var native = _fft.ComputeNativePower(rawIq, f * bytesPerFrame, nativeFftSize);
                var binned = BinSum(native, SkaoConstants.NumIntegrationBins);

                for (int i = 0; i < binned.Length; i++)
                    accum[i] += binned[i];
            }

            frameCount = frames;
            return accum;
        }

        /// <summary>
        /// Sums contiguous groups of raw (unshifted) FFT bins down to outBins channels -
        /// mirrors `pwr.reshape(outBins, -1).sum(axis=1)`. Grouping in raw FFT-bin
        /// order (rather than fftshift-ing the full native array first) is equivalent
        /// here: nativeFftSize is always a multiple of outBins, so the array's halfway
        /// point (where fftshift would cut) always lands exactly on a group boundary -
        /// grouping first and fftshift-ing the resulting outBins-length array after
        /// (SkaoHiPipelineProcessor.Process's own first step) lands on the same result.
        /// </summary>
        private static double[] BinSum(double[] nativePower, int outBins)
        {
            int n = nativePower.Length;
            int group = n / outBins;
            var binned = new double[outBins];

            for (int b = 0; b < outBins; b++)
            {
                double sum = 0.0;
                int baseIdx = b * group;
                for (int k = 0; k < group; k++)
                    sum += nativePower[baseIdx + k];
                binned[b] = sum;
            }

            return binned;
        }
    }
}
