using System;
using System.Linq;
using RASTA.Processing.Dsp;

namespace RASTA.Processing.HiPipeline
{
    public static class HiConstants
    {
        public const double SpeedOfLightKmPerSec = 299_792.458;
        public const double HiFreqHz = 1_420_405_751.77; // 1420.40575177 MHz

        // Reference implementation (SKAO TTRT) uses fixed channel *counts* tuned
        // for a 256-bin spectrum: CH_CUT = 25, CH_OFF = 40. These mark two small
        // windows near each edge of the array used ONLY for the continuum fit -
        // they are not velocity thresholds and not a "most of the array" mask.
        // To stay FFT-size-agnostic we keep them as fractions of the spectrum
        // length instead of hardcoded channel counts. Revisit this if you'd
        // rather keep the channel counts fixed regardless of FFT size.
        public const double ChCutFraction = 25.0 / 256.0;
        public const double ChOffFraction = 40.0 / 256.0;

        // RFI rejection (applied only to the continuum-fit window, per reference)
        public const int RfiFilterWindow = 5;
        public const int RfiFilterPolyOrder = 2;
        public const double RfiFilterSigma = 3.0;
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

        /// <summary>
        /// Returns the averaged baseline spectrum only. Used during calibration,
        /// where a baseline is captured and averaged well before any observation
        /// capture frames exist to pair it with.
        /// </summary>
        public double[] GetBaselineAverage()
        {
            if (_baselineFrames == 0)
                throw new InvalidOperationException("Need at least one baseline frame.");

            var baselineAvg = new double[_fftSize];
            for (int i = 0; i < _fftSize; i++)
                baselineAvg[i] = _baselineSum[i] / _baselineFrames;

            return baselineAvg;
        }

        /// <summary>
        /// Returns the averaged capture spectrum only. Used for a live running average
        /// against a baseline that was already fixed earlier (e.g. during Observe, where
        /// the calibration baseline doesn't change frame to frame).
        /// </summary>
        public double[] GetCaptureAverage()
        {
            if (_captureFrames == 0)
                throw new InvalidOperationException("Need at least one capture frame.");

            var captureAvg = new double[_fftSize];
            for (int i = 0; i < _fftSize; i++)
                captureAvg[i] = _captureSum[i] / _captureFrames;

            return captureAvg;
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
            double lsrCorrectionKmPerSec = 0.0, // add AstronomyUtils.ComputeLsrCorrectionKmPerSec(...) here to report LSR velocity instead of raw topocentric
            SmoothingKind smoothing = SmoothingKind.None, // reference pipeline never smooths the final output
            int smoothingWindow = 5,
            int smoothingPolyOrder = 2) // only used when smoothing == SavitzkyGolay
        {
            if (baselinePower == null) throw new ArgumentNullException(nameof(baselinePower));
            if (capturePower == null) throw new ArgumentNullException(nameof(capturePower));
            if (baselinePower.Length != capturePower.Length)
                throw new ArgumentException("Baseline and capture spectra must have the same length.");

            int n = baselinePower.Length;

            // 1. fftshift spectra
            baselinePower = FftShift(baselinePower);
            capturePower = FftShift(capturePower);

            // 1b. Excise the receiver's fixed LO/DC-leakage spike (every zero-IF SDR,
            // including the RTL-SDR this app targets, has one at exactly the tuned
            // center frequency - see RemoveDcSpike for why the window is detected from
            // the baseline rather than hardcoded).
            RemoveDcSpike(baselinePower, capturePower);

            // 2. Frequency axis
            FrequencyHz = ComputeFrequencyAxis(n, sampleRateHz, centerFreqHz);

            // 3. Velocity axis (topocentric, then shifted to LSR by a single scalar
            // offset - the correction depends only on pointing/time/site, not on
            // frequency, so it's the same for every channel).
            VelocityKmPerSec = new double[n];
            for (int i = 0; i < n; i++)
            {
                double f = FrequencyHz[i];
                // Radio velocity convention: v > 0 means redshifted / receding (f < f0).
                // (Previously this was (f - HiFreqHz)/HiFreqHz, which inverted the sign.)
                VelocityKmPerSec[i] =
                    HiConstants.SpeedOfLightKmPerSec * ((HiConstants.HiFreqHz - f) / HiConstants.HiFreqHz)
                    + lsrCorrectionKmPerSec;
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

            // 5. Continuum fit input: two small edge windows (channel-index based,
            //    NOT a velocity-magnitude mask), with RFI outliers removed before
            //    fitting - matching compute_hi_spectrum / filter_rfi in the reference.
            (double m, double b0) = FitContinuumFromEdgeWindows(VelocityKmPerSec, RatioSpectrum, n);

            // 6. Subtract continuum
            HiSpectrum = new double[n];
            for (int i = 0; i < n; i++)
            {
                double v = VelocityKmPerSec[i];
                double continuum = m * v + b0;
                HiSpectrum[i] = RatioSpectrum[i] - continuum;
            }

            // 7. Optional final smoothing (display-only - never feeds back into the continuum
            //    fit or RatioSpectrum, both already computed above). SkaoPipelineProcessor is
            //    unaffected either way; it always applies its own fixed 5-point kernel to match
            //    the SKAO reference algorithm exactly.
            if (smoothing != SmoothingKind.None)
            {
                HiSpectrum = ApplyFinalSmoothing(HiSpectrum, smoothing, smoothingWindow, smoothingPolyOrder);
            }
        }

        /// <summary>
        /// Dispatches to whichever final-smoothing kernel the caller asked for. SavitzkyGolay
        /// reuses the same general-purpose, arbitrary-window/order implementation already used
        /// internally for RFI outlier detection (SavitzkyGolaySmooth below) rather than the
        /// crude fixed-5-point RASTA.Processing.Dsp.SavitzkyGolay class, which is reserved for
        /// SkaoPipelineProcessor's unmodified reference kernel.
        /// </summary>
        private static double[] ApplyFinalSmoothing(double[] data, SmoothingKind kind, int window, int polyOrder)
        {
            if (window < 1) window = 1;
            if (window % 2 == 0) window++; // SG and the centered moving average both expect an odd window

            return kind switch
            {
                SmoothingKind.SavitzkyGolay => SavitzkyGolaySmooth(data, window, polyOrder),
                SmoothingKind.MovingAverage => MovingAverage.Smooth(data, window),
                _ => data
            };
        }

        /// <summary>
        /// Re-orders a raw FFT-bin-order power spectrum (DC at index 0) into monotonic
        /// frequency order (most negative frequency first, DC in the middle) - public so
        /// callers displaying a single averaged spectrum directly (without running the
        /// rest of the pipeline) can shift it the same way before plotting against a
        /// monotonic frequency axis.
        /// </summary>
        public static double[] FftShift(double[] data)
        {
            int n = data.Length;
            int half = n / 2;

            var shifted = new double[n];
            Array.Copy(data, half, shifted, 0, n - half);
            Array.Copy(data, 0, shifted, n - half, half);

            return shifted;
        }

        // How far above a robust local "normal" level counts as the DC/LO spike.
        private const double DcSpikeThresholdRatio = 4.0;

        // Upper bound on how many bins either side of center the spike can be judged to
        // extend - generous versus the ~1-2 bins actually observed in practice (a Hann
        // window's main lobe, which is what a pure DC offset turns into after windowing,
        // is only about 4 bins wide regardless of FFT size).
        private const int DcSpikeMaxHalfWidthBins = 4;

        /// <summary>
        /// Excises the fixed LO/DC-leakage spike every zero-IF SDR (including the
        /// RTL-SDR this app targets) produces at exactly the tuned center frequency -
        /// after FftShift that's always the middle bin of the array, regardless of
        /// pointing or tuning choice.
        ///
        /// The window to excise is detected from baselinePower alone, never from
        /// capturePower: the baseline is a terminator reading with zero sky signal, so
        /// any bin that spikes there is unambiguously receiver artifact. Because the
        /// decision never inspects the capture spectrum, this cannot excise genuine,
        /// capture-only HI signal - if the tuned center frequency happens to coincide
        /// with a target's actual line frequency but the baseline is flat there, nothing
        /// gets touched. Only a spike that's present in the artifact-only baseline gets
        /// interpolated away, identically, in both spectra.
        /// </summary>
        private static void RemoveDcSpike(double[] baselinePower, double[] capturePower)
        {
            int n = baselinePower.Length;
            int center = n / 2;

            double reference = LocalMedianExcluding(baselinePower, center, DcSpikeMaxHalfWidthBins);
            if (reference <= 0)
                return; // no usable "normal" level to compare against - leave spectra alone

            int lo = center, hi = center;
            bool anyElevated = false;

            for (int offset = 0; offset <= DcSpikeMaxHalfWidthBins; offset++)
            {
                int left = center - offset;
                int right = center + offset;
                bool leftHigh = left >= 0 && baselinePower[left] > DcSpikeThresholdRatio * reference;
                bool rightHigh = right < n && baselinePower[right] > DcSpikeThresholdRatio * reference;

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
        /// Median of a small window of bins just outside +-halfWidth of centerIndex, on
        /// both sides - a "normal" reference level that the spike itself can't bias, as
        /// long as halfWidth generously bounds its true extent.
        /// </summary>
        private static double LocalMedianExcluding(double[] data, int centerIndex, int halfWidth)
        {
            const int refWindow = 5;
            var values = new System.Collections.Generic.List<double>();

            for (int i = centerIndex - halfWidth - refWindow; i < centerIndex - halfWidth; i++)
                if (i >= 0) values.Add(data[i]);

            for (int i = centerIndex + halfWidth + 1; i <= centerIndex + halfWidth + refWindow; i++)
                if (i < data.Length) values.Add(data[i]);

            if (values.Count == 0)
                return 0;

            values.Sort();
            return values[values.Count / 2];
        }

        /// <summary>
        /// Replaces data[lo..hi] with a linear interpolation between its immediate
        /// flanking bins. No-ops rather than interpolating off the end of the array if
        /// the range is right at an edge (shouldn't happen in practice - the spike is
        /// always near the center bin, far from either edge - but safe regardless).
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
        /// Builds the continuum-fit input the way compute_hi_spectrum does: two small
        /// channel-index windows near each edge of the array (NOT a velocity-magnitude
        /// mask over most of the spectrum), with RFI outliers removed before fitting.
        /// chCut/chOff are scaled from HiConstants.ChCutFraction/ChOffFraction so this
        /// works for FFT sizes other than the reference's fixed 256 bins.
        /// </summary>
        private static (double m, double b) FitContinuumFromEdgeWindows(double[] velocity, double[] ratio, int n)
        {
            int chCut = (int)Math.Round(HiConstants.ChCutFraction * n);
            int chOff = (int)Math.Round(HiConstants.ChOffFraction * n);

            if (chCut < 0) chCut = 0;
            if (chOff <= chCut || chOff > n / 2)
                throw new InvalidOperationException(
                    $"FFT size {n} is too small for the scaled edge windows (chCut={chCut}, chOff={chOff}).");

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
                y, HiConstants.RfiFilterWindow, HiConstants.RfiFilterPolyOrder, HiConstants.RfiFilterSigma);

            var xClean = new System.Collections.Generic.List<double>();
            var yClean = new System.Collections.Generic.List<double>();
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
        /// edge positions (rather than a fixed convolution kernel at the boundary).
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

            // Interior points: fit a local polynomial to the centered window and
            // evaluate it at the center. (Equivalent to the fixed SG convolution
            // kernel, computed directly since these windows are small in practice.)
            var localX = new double[window];
            for (int k = 0; k < window; k++) localX[k] = k - half;

            for (int i = half; i < n - half; i++)
            {
                var localY = new double[window];
                for (int k = 0; k < window; k++) localY[k] = data[i - half + k];

                double[] coeffs = PolyFitLeastSquares(localX, localY, polyOrder);
                result[i] = EvalPoly(coeffs, 0.0);
            }

            // Edges ("interp" mode): one polynomial fit to the first `window` points,
            // evaluated at each leading edge position; likewise for the trailing edge.
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

        /// <summary>
        /// Least-squares polynomial fit y = c0 + c1*x + c2*x^2 + ... via normal
        /// equations, solved by Gaussian elimination. order+1 is small in every
        /// call site here (RFI window sizes are tiny), so this is plenty fast.
        /// </summary>
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
            double lsrCorrectionKmPerSec = 0.0,
            SmoothingKind smoothing = SmoothingKind.None, // reference pipeline never smooths the final output
            int smoothingWindow = 5,
            int smoothingPolyOrder = 2)
        {
            var (baselineAvg, captureAvg) = _acc.GetAveragedSpectra();
            _pipe.Process(
                baselineAvg,
                captureAvg,
                sampleRateHz,
                centerFreqHz,
                lsrCorrectionKmPerSec,
                smoothing,
                smoothingWindow,
                smoothingPolyOrder);
        }
    }
}