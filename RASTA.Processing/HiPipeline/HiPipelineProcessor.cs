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

        // Fixed display-scale multiplier baked into RatioSpectrum (capturePower/baselinePower
        // x this) purely to put the linear ratio in a nicer numeric range - not a calibrated
        // physical unit. Named so callers that need to recover the *raw* ratio (e.g.
        // MosaicProcessor, converting a peak to dB relative to the cold-sky baseline, where
        // baseline power == capture power should read 0 dB) can divide it back out instead of
        // duplicating the magic number.
        public const double RatioDisplayScale = 300.0;

        // Default sigma threshold for HiStreamingPipeline.Despike - how many robust local
        // standard deviations above the local median counts as narrowband RFI (see its
        // remarks). Exposed here rather than kept private to HiStreamingPipeline so every
        // caller across layers (MosaicProcessor, VisualiseViewModel/MosaicViewModel,
        // CaptureViewModel/CapturePlan) shares one canonical default instead of duplicating
        // the number - and so it can be exposed as a live, tunable UI control the same way
        // SmoothingWindow is, since the right value depends on how heavily-averaged a given
        // spectrum is (a shorter capture dwell has a noisier local floor than a long
        // baseline dwell, so may need a lower threshold to catch the same spikes).
        public const double DefaultDespikeThresholdSigma = 5.0;
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
        /// against a baseline that was already fixed earlier (e.g. during Capture, where
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
            bool despike = false, // opt-in narrowband RFI excision (comb/birdie spikes) - see Despike
            double despikeThresholdSigma = HiConstants.DefaultDespikeThresholdSigma, // only used when despike is true
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

            // 1a. Optional narrowband-RFI despike (e.g. a USB3/mount-controller comb),
            // before the always-on DC spike excision below. Uses the two-spectrum overload
            // (union-detected, excised identically in both) rather than despiking each
            // spectrum independently - see its remarks for why: independent excision can
            // remove a spike from one spectrum but not the other, turning a spike that
            // mostly canceled out through baseline division into a new, larger artifact.
            if (despike)
            {
                Despike(baselinePower, capturePower, sampleRateHz, despikeThresholdSigma);
            }

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

            for (int i = 0; i < n; i++)
                RatioSpectrum[i] *= HiConstants.RatioDisplayScale;

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
        /// long as halfWidth generously bounds its true extent. refWindow defaults to the
        /// narrow 5-bin sample RemoveDcSpike has always used; Despike passes a wider one
        /// since it's scanning arbitrary positions rather than one known bin.
        /// </summary>
        private static double LocalMedianExcluding(double[] data, int centerIndex, int halfWidth, int refWindow = 5)
        {
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

        // How many local-noise standard deviations above the local median counts as
        // narrowband RFI. A fixed multiplicative ratio (as RemoveDcSpike uses for the
        // huge, unmistakable DC/LO leakage spike) badly under-detects here: a comb spur
        // from e.g. a USB3/mount controller often sits only a couple of dB - call it
        // ~1.5-2x in linear power - above the local continuum, which a 4x-style ratio
        // test never crosses. But once a baseline/capture has been averaged over many
        // frames, the *residual* bin-to-bin noise scatter shrinks far more than that, so
        // even a "small" few-dB spur ends up many standard deviations above the genuine
        // local noise - a robust (outlier-resistant) sigma test catches it regardless of
        // how modest it looks in absolute dB terms. Callers pass this in (default
        // HiConstants.DefaultDespikeThresholdSigma) rather than it being fixed, since how
        // heavily a given spectrum has been averaged - and therefore how tight its residual
        // noise floor is - varies enough between e.g. a long baseline dwell and a shorter
        // capture dwell that one fixed value doesn't always suit both.

        // Upper bound on how far either side of a flagged peak the spike can grow, and how
        // wide a reference sample to characterise "normal" - specified in Hz, not a fixed
        // bin count, and converted to bins from the actual sampleRateHz/fftSize at each
        // call (see SpikesBinsFromHz). Unlike RemoveDcSpike's DC/LO spike - pure single-
        // tone window leakage, genuinely a fixed *bin* count regardless of FFT size,
        // hence left alone - measuring a real comb spur against a 4096-bin/2.4Msps capture
        // (see the CSV-debug workflow this was tuned against) showed skirts running wider
        // than a bare Hann main lobe alone would produce (~12 bins vs. an expected ~4),
        // consistent with genuine modulation bandwidth (e.g. USB3 spread-spectrum
        // clocking) rather than leakage alone - a physically real bandwidth is fixed in
        // Hz, so at a *larger* FFT size (narrower bins) the same feature spans *more*
        // bins, and a fixed bin-count cap would start truncating growth again exactly
        // like the too-tight original 4-bin cap did. Values below are that same ~12/~15
        // bins re-expressed in Hz at the 4096-bin/2.4Msps capture they were measured
        // against (585.94 Hz/bin: 12*585.94≈7000, 15*585.94≈8800).
        private const double SpikeMaxHalfWidthHz = 7000.0;
        private const double SpikeReferenceWindowHz = 8800.0;

        // Floors so a very small FFT size (wide bins) doesn't collapse either window to
        // 0-1 bins and lose all growth/reference capability - same magnitude as
        // RemoveDcSpike's own fixed bin counts (4 and 5 respectively).
        private const int SpikeMaxHalfWidthMinBins = 4;
        private const int SpikeReferenceWindowMinBins = 5;

        /// <summary>
        /// Converts a target width in Hz to an equivalent bin count at the given
        /// sampleRateHz/fftSize, floored at minBins - see SpikeMaxHalfWidthHz/
        /// SpikeReferenceWindowHz's remarks for why these are Hz-based rather than fixed
        /// bin counts.
        /// </summary>
        private static int SpikeBinsFromHz(double widthHz, double sampleRateHz, int fftSize, int minBins)
        {
            if (sampleRateHz <= 0 || fftSize <= 0)
                return minBins;

            double binWidthHz = sampleRateHz / fftSize;
            int bins = (int)Math.Round(widthHz / binWidthHz);
            return Math.Max(minBins, bins);
        }

        // Hysteresis: a single fixed threshold has to do two different jobs - decide
        // whether a bin is a spike at all (wants to be conservative, to avoid flagging
        // ordinary noise), and decide how far its skirt extends (wants to be permissive,
        // since a real spike's shoulder bins are elevated but individually more modest
        // than the peak). Using the caller's thresholdSigma for both meant a spike's own
        // shoulder bins often fell just short of the detection bar and were left
        // unexcised - the "flattened top, sloped sides" look. Growth instead uses
        // whichever is lower: thresholdSigma itself (so raising the detection threshold
        // never makes growth stricter than detection), or this fixed, more permissive cap
        // - chosen independent of thresholdSigma so growth doesn't become reckless if a
        // user dials thresholdSigma down for more sensitive detection.
        private const double SpikeGrowSigmaCap = 2.5;

        /// <summary>
        /// Opt-in narrowband-RFI excision for a single spectrum (SpectrumMode-independent
        /// "despiking") - used by the standalone baseline/capture views, which have no
        /// counterpart spectrum to cross-check against. See the two-spectrum overload's
        /// remarks for why Process uses that one instead: detecting and excising each
        /// spectrum independently can leave a spike removed from one but not the other,
        /// which turns a spike that mostly canceled out through baseline division into a
        /// new, larger division artifact.
        ///
        /// sampleRateHz is required (not optional/defaulted) - see SpikeMaxHalfWidthHz's
        /// remarks for why the growth/reference windows are Hz-based and therefore need it
        /// to convert to bins correctly for whatever FFT size data actually is.
        /// </summary>
        public static double[] Despike(double[] data, double sampleRateHz, double thresholdSigma = HiConstants.DefaultDespikeThresholdSigma)
        {
            int n = data.Length;
            var result = (double[])data.Clone();
            var candidate = new bool[n];
            MarkSpikeCandidates(data, candidate, thresholdSigma, sampleRateHz);
            ExciseCandidateRuns(result, candidate);
            return result;
        }

        /// <summary>
        /// Opt-in narrowband-RFI excision for a baseline/capture pair, mutating both in
        /// place (same calling convention as RemoveDcSpike) - used by Process.
        ///
        /// Candidates are flagged independently in each spectrum (their averaging depth,
        /// and therefore residual noise floor, commonly differs - e.g. a longer baseline
        /// dwell vs. a shorter capture dwell - so the same physical spur can clear the
        /// sigma threshold in one but not the other), then the UNION of flagged bins is
        /// excised identically in both. Interpolating only whichever spectrum happened to
        /// trip the threshold - leaving the other's raw, still-spiky value in place - would
        /// hand the division step a spike/smooth mismatch it didn't have before: dividing a
        /// smoothed spectrum by one that still has the spike (or vice versa) turns a spike
        /// that mostly canceled out through division already into a new, larger ratio
        /// artifact. Applying the same excision to both keeps that pre-despike cancellation
        /// intact wherever detection agreed, and only changes bins where it didn't - and as
        /// a side effect, lets whichever spectrum has the cleaner detection (typically the
        /// more heavily-averaged baseline) cover for the other's misses.
        /// </summary>
        public static void Despike(double[] baselinePower, double[] capturePower, double sampleRateHz, double thresholdSigma = HiConstants.DefaultDespikeThresholdSigma)
        {
            int n = baselinePower.Length;
            var candidate = new bool[n];
            MarkSpikeCandidates(baselinePower, candidate, thresholdSigma, sampleRateHz);
            MarkSpikeCandidates(capturePower, candidate, thresholdSigma, sampleRateHz);

            ExciseCandidateRuns(baselinePower, candidate);
            ExciseCandidateRuns(capturePower, candidate);
        }

        /// <summary>
        /// Flags (OR-in, never clears) every bin more than thresholdSigma robust standard
        /// deviations above a local median (median absolute deviation, scaled to a
        /// std-equivalent) computed from a window of nearby bins excluding a small guard
        /// region around the candidate (the same LocalMedianExcluding used by
        /// RemoveDcSpike, swept across every position instead of one fixed center bin),
        /// then grows each flagged bin outward while adjacent bins are also elevated - same
        /// style as RemoveDcSpike's window growth around the DC bin, generalized to run
        /// anywhere in the array. sampleRateHz converts the Hz-based growth/reference
        /// widths to the right bin counts for data's actual FFT size (data.Length) - see
        /// SpikeMaxHalfWidthHz's remarks.
        ///
        /// This is aimed at things like a USB3/mount-controller comb: real HI emission is
        /// always many bins wide (tens-hundreds of kHz vs ~1-2 bins for a genuine spike),
        /// so a purely local "way above its immediate neighbourhood" test structurally
        /// can't mistake a broad astrophysical feature for RFI. Unlike RemoveDcSpike this
        /// is NOT baseline-verified - it runs on whichever spectrum it's given, so it's
        /// opt-in rather than always-on.
        /// </summary>
        private static void MarkSpikeCandidates(double[] data, bool[] candidate, double thresholdSigma, double sampleRateHz)
        {
            int n = data.Length;
            int maxHalfWidthBins = SpikeBinsFromHz(SpikeMaxHalfWidthHz, sampleRateHz, n, SpikeMaxHalfWidthMinBins);
            int referenceWindowBins = SpikeBinsFromHz(SpikeReferenceWindowHz, sampleRateHz, n, SpikeReferenceWindowMinBins);

            for (int i = 0; i < n; i++)
            {
                var (median, sigma) = LocalRobustStatsExcluding(data, i, maxHalfWidthBins, referenceWindowBins);
                if (median <= 0)
                    continue; // no usable "normal" level to compare against - leave this bin alone

                double threshold = median + thresholdSigma * sigma;
                if (data[i] <= threshold)
                    continue;

                // Grow outward using the more permissive hysteresis threshold, not the
                // (possibly much stricter) detection one - see SpikeGrowSigmaCap's remarks.
                double growThreshold = median + Math.Min(thresholdSigma, SpikeGrowSigmaCap) * sigma;

                int lo = i, hi = i;
                for (int offset = 0; offset <= maxHalfWidthBins; offset++)
                {
                    int left = i - offset;
                    int right = i + offset;
                    bool leftHigh = left >= 0 && data[left] > growThreshold;
                    bool rightHigh = right < n && data[right] > growThreshold;

                    if (!leftHigh && !rightHigh)
                        break;

                    if (leftHigh) lo = left;
                    if (rightHigh) hi = right;
                }

                for (int k = lo; k <= hi; k++)
                    candidate[k] = true;
            }
        }

        /// <summary>
        /// Linearly interpolates away every contiguous run of flagged bins in data, using
        /// each run's own immediate flanking (unflagged) bins - shared by both Despike
        /// overloads so a run flagged via the union in the two-spectrum version still
        /// interpolates each spectrum from its own neighbours, not the other spectrum's.
        /// </summary>
        private static void ExciseCandidateRuns(double[] data, bool[] candidate)
        {
            int n = data.Length;
            int i = 0;
            while (i < n)
            {
                if (!candidate[i])
                {
                    i++;
                    continue;
                }

                int lo = i;
                while (i < n && candidate[i])
                    i++;
                int hi = i - 1;

                InterpolateRange(data, lo, hi);
            }
        }

        /// <summary>
        /// Median and a robust (outlier-resistant) standard-deviation estimate - median
        /// absolute deviation, scaled by the usual 1.4826 factor so it's comparable to a
        /// normal std for roughly-Gaussian noise - over the same reference window
        /// LocalMedianExcluding samples. Using MAD rather than a plain std keeps a couple
        /// of already-elevated bins in the window (e.g. the flank of a neighbouring spike)
        /// from inflating the estimate the way a mean/variance calculation would.
        /// Guarantees a small positive sigma floor (proportional to the median) rather than
        /// a literal zero, so a reference window that happens to be perfectly flat doesn't
        /// make every subsequent bin read as "infinitely many sigmas" above it.
        /// </summary>
        private static (double median, double sigma) LocalRobustStatsExcluding(double[] data, int centerIndex, int halfWidth, int refWindow)
        {
            var values = new System.Collections.Generic.List<double>();

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
            const double MinRelativeSigma = 1e-6; // floor against a perfectly flat reference window
            double sigma = Math.Max(mad * MadToSigma, median * MinRelativeSigma);

            return (median, sigma);
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
            bool despike = false,
            double despikeThresholdSigma = HiConstants.DefaultDespikeThresholdSigma,
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
                despike,
                despikeThresholdSigma,
                smoothing,
                smoothingWindow,
                smoothingPolyOrder);
        }
    }
}