using RASTA.Core.Processing;
using RASTA.Core.Storage;
using RASTA.Core.Telescope;
using RASTA.Processing.Dsp;
using RASTA.Processing.HiPipeline;
using System.IO;
using System.Runtime.ExceptionServices;

namespace RASTA.Processing.Mosaic
{
    /// <summary>
    /// One sky/AltAz position within a mosaic session: the HiStreamingPipeline output for
    /// that position's dwell-point capture group, divided by the session's one shared
    /// baseline, plus the recorded pointing (whichever pair FitsFileMetaData actually has -
    /// Equatorial and AltAz are mutually exclusive per file, same ambiguity GridBuilder has
    /// always assumed is uniform across one session) and the source files it came from.
    /// </summary>
    public record MosaicPosition(
        string Label,
        CoordinateMode Mode,
        double? RaHours,
        double? DecDeg,
        double? AzDeg,
        double? AltDeg,
        double[] HiSpectrum,
        double LineStrengthDb,
        double PeakVelocityKmPerSec,
        IReadOnlyList<string> SourceFiles);

    /// <summary>
    /// Every position in a mosaic session shares the same frequency/velocity axis (baseline
    /// and every capture group are validated to agree on FFT size/sample rate/center
    /// frequency before being processed), so it's hoisted out of MosaicPosition rather than
    /// repeated per position.
    /// </summary>
    public record MosaicResult(
        double[] FrequencyHz,
        double[] VelocityKmPerSec,
        IReadOnlyList<MosaicPosition> Positions)
    {
        /// <summary>
        /// How many of Positions had their averaged spectrum served from SpectrumCacheService
        /// rather than re-read from raw IQ (see CachedSpectrumLoader). Always 0 for a LAB
        /// Survey session, which has no FFT step to cache.
        /// </summary>
        public int PositionsFromCache { get; init; }
    }

    /// <summary>
    /// Folder-wide counterpart to VisualiseViewModel.ProcessHiCore: walks every dwell-point
    /// capture group in a session folder through the same HiStreamingPipeline, against one
    /// shared baseline, producing one MosaicPosition per sky/AltAz position. Pure algorithm -
    /// no UI/hardware deps - matching the RASTA.Processing layering convention.
    ///
    /// The baseline is loaded once up front, then every position is processed
    /// concurrently via Parallel.For (see ProcessFolder's remarks) - positions are otherwise
    /// completely independent of each other, so this scales with core count rather than being
    /// stuck at one position's worth of FFT/pipeline work at a time.
    ///
    /// The slow part of each file set - reading the raw IQ and averaging its FFT frames - goes
    /// through CachedSpectrumLoader, shared with Single Capture (issue #19): a position
    /// reduced before, here or there, is loaded from the cache without opening its FITS
    /// file at all. Everything from Target FFT Size binning onward always runs fresh.
    /// </summary>
    public class MosaicProcessor
    {
        private readonly CachedSpectrumLoader _spectrumLoader;

        public MosaicProcessor(CachedSpectrumLoader spectrumLoader)
        {
            _spectrumLoader = spectrumLoader;
        }

        /// <summary>
        /// Default half-width of the velocity window searched for the line peak. HI lines
        /// from Galactic rotation typically fall within this range of the LSR-corrected
        /// zero point; wide enough to catch most sightlines without dragging in unrelated
        /// baseline ripple far from the line.
        /// </summary>
        public const double DefaultIntegratedWindowKmPerSec = 100.0;

        public Task<MosaicResult> ProcessFolderAsync(
            string folder,
            string baselineFilePath,
            int targetFftSize,
            double integratedWindowKmPerSec,
            Action<string, double>? progressCallback,
            bool despike = false,
            double despikeThresholdSigma = HiConstants.DefaultDespikeThresholdSigma,
            SmoothingKind smoothing = SmoothingKind.None,
            int smoothingWindow = 21,
            CancellationToken ct = default,
            bool forceRegenerate = false)
        {
            return Task.Run(
                () => ProcessFolder(folder, baselineFilePath, targetFftSize, integratedWindowKmPerSec, progressCallback, despike, despikeThresholdSigma, smoothing, smoothingWindow, ct, forceRegenerate),
                ct);
        }

        private MosaicResult ProcessFolder(
            string folder,
            string baselineFilePath,
            int targetFftSize,
            double integratedWindowKmPerSec,
            Action<string, double>? progressCallback,
            bool despike,
            double despikeThresholdSigma,
            SmoothingKind smoothing,
            int smoothingWindow,
            CancellationToken ct,
            bool forceRegenerate)
        {
            string baselineFullPath = Path.GetFullPath(baselineFilePath);

            var captureFiles = Directory.GetFiles(folder, "*.fits")
                .Where(f => !string.Equals(Path.GetFullPath(f), baselineFullPath, StringComparison.OrdinalIgnoreCase))
                .Where(f => !FitsPathBuilder.IsBaselineFile(f))
                .ToList();

            var groups = FitsPathBuilder.GroupSweepFiles(captureFiles);
            if (groups.Count == 0)
                throw new InvalidOperationException("No capture files found in the selected folder.");

            // --- Baseline: loaded (cache or raw IQ) once, reused for every position ---
            // Always averaged at native resolution; Target FFT Size re-binning happens after
            // averaging, via SpectrumBinner - not by shrinking the raw IQ beforehand, which
            // aliases distant frequency bins together instead of averaging local ones (see
            // SpectrumBinner's own remarks for why).
            progressCallback?.Invoke("Loading baseline…", 0.0);
            var baseline = _spectrumLoader.Load(
                new[] { baselineFilePath }, skao: false, forceRegenerate, "baseline",
                progressCallback, ct, out _);

            int scanFftSize = baseline.FftSize;
            int fftSize = (targetFftSize > 0 && targetFftSize <= scanFftSize) ? targetFftSize : scanFftSize;

            double[] baselinePower = baseline.AveragedPower!;
            if (fftSize < scanFftSize)
                baselinePower = SpectrumBinner.BinAverage(baselinePower, fftSize);

            // --- One dwell-point capture group per sky/AltAz position, processed in parallel.
            // Each position loads its own file set and runs its own pipeline work completely
            // independently of every other position - the only thing they share is the
            // already-computed, read-only baselinePower array above. HiStreamingPipeline is
            // freshly `new`'d per position. On a cache miss the FITS read itself is still
            // serialized (see CachedSpectrumLoader's FitsReadLock - the underlying FITS library
            // hangs under concurrent reads); the FFT/averaging after it, and everything on a
            // cache hit, runs concurrently across positions.
            var positionsArray = new MosaicPosition[groups.Count];
            double[]? frequencyAxis = null;
            double[]? velocityAxis = null;
            int completedCount = 0;
            int fromCacheCount = 0;

            // The bar is one continuous 0->1 run across all positions, built from each
            // position's own measured fraction (0 = not started, 1 = done) rather than only
            // from completed positions: on a cache miss a single position is a slow serialized
            // FITS read plus thousands of FFT frames, so counting whole positions alone leaves
            // the bar frozen for a long time - and, since the baseline phase above has just
            // driven it to 100%, frozen at "finished" while the first positions are still being
            // read. Reset to 0 here so the positions phase visibly starts.
            var positionFractions = new double[groups.Count];
            var progressLock = new object();
            void ReportOverall(int g, double fraction, string? status = null)
            {
                if (progressCallback is null)
                    return;
                double overall;
                lock (progressLock)
                {
                    positionFractions[g] = fraction;
                    overall = positionFractions.Sum() / groups.Count;
                }
                progressCallback(
                    status ?? $"Processing positions ({Volatile.Read(ref completedCount)} of {groups.Count} done)…",
                    overall);
            }
            progressCallback?.Invoke($"Processing positions (0 of {groups.Count} done)…", 0.0);

            try
            {
                Parallel.For(0, groups.Count, new ParallelOptions { CancellationToken = ct }, g =>
                {
                    var files = groups[g];
                    string label = Path.GetFileNameWithoutExtension(files[0]);

                    // On a cache miss the loader reports two 0->1 runs for this position - the
                    // file read, then the frame averaging. Map them onto the first and second
                    // half of this position's own fraction (a drop back toward 0 marks the
                    // switch from one run to the next), and only pass on changes of 1% or more:
                    // the averaging run reports once per FFT frame, from several positions at
                    // once. A cache hit reports nothing here and jumps straight to "done" below.
                    int phase = 0;
                    double lastPhaseFraction = 0.0;
                    double lastReported = 0.0;
                    void OnPositionProgress(string _, double fraction)
                    {
                        if (fraction < lastPhaseFraction && phase == 0)
                            phase = 1;
                        lastPhaseFraction = fraction;

                        double positionFraction = Math.Min((phase + fraction) / 2.0, 0.99);
                        if (positionFraction - lastReported < 0.01)
                            return;
                        lastReported = positionFraction;
                        ReportOverall(g, positionFraction);
                    }

                    var captureMeta = _spectrumLoader.Load(
                        files, skao: false, forceRegenerate, label, OnPositionProgress, ct, out bool fromCache);
                    if (fromCache)
                        Interlocked.Increment(ref fromCacheCount);

                    if (captureMeta.FftSize != scanFftSize ||
                        captureMeta.SampFreqHz != baseline.SampFreqHz ||
                        captureMeta.CentFreqHz != baseline.CentFreqHz)
                    {
                        throw new InvalidOperationException(
                            $"Capture group '{label}' has a different FFT size, sample rate, or center " +
                            "frequency than the baseline.");
                    }

                    double[] capturePower = captureMeta.AveragedPower!;
                    if (fftSize < scanFftSize)
                        capturePower = SpectrumBinner.BinAverage(capturePower, fftSize);

                    // Each position has its own pointing, so its own LSR correction - unlike
                    // ProcessHiCore, this can't be hoisted out of the loop.
                    double lsrCorrectionKmPerSec = captureMeta.LsrCorrectionKmPerSec;

                    var pipeline = new HiStreamingPipeline();
                    pipeline.Process(baselinePower, capturePower, captureMeta.SampFreqHz, captureMeta.CentFreqHz, lsrCorrectionKmPerSec, despike: despike, despikeThresholdSigma: despikeThresholdSigma);

                    // Every position's FrequencyHz/VelocityKmPerSec axis agrees on FFT size/
                    // sample rate/center frequency (validated above), so any one of them is a
                    // representative "the" axis for MosaicResult - deliberately captured from
                    // index 0 specifically (not "whichever position happens to finish first",
                    // which parallel execution makes non-deterministic) so this matches what the
                    // old sequential loop always did. Only iteration g==0 ever writes these, so
                    // there's no race despite running alongside every other iteration.
                    if (g == 0)
                    {
                        frequencyAxis = pipeline.FrequencyHz;
                        velocityAxis = pipeline.VelocityKmPerSec;
                    }

                    // Smoothing is applied here, to a copy of RatioSpectrum used only for the peak
                    // search - not to pipeline.RatioSpectrum itself, and not via Process's own
                    // smoothing parameters (which only ever touch HiSpectrum - see
                    // HiStreamingPipeline.Process's remarks). FindLinePeak picks the single
                    // strongest bin in a window, so on an unsmoothed spectrum that pick can be
                    // driven by one noisy bin; smoothing first makes it representative of the
                    // line's actual shape instead, the same reasoning Single Capture's own Smooth
                    // control exists for.
                    double[] peakSearchSpectrum = smoothing == SmoothingKind.None
                        ? pipeline.RatioSpectrum
                        : HiStreamingPipeline.ApplySmoothing(pipeline.RatioSpectrum, smoothing, smoothingWindow);

                    var (lineStrengthDb, peakVelocityKmPerSec) = FindLinePeak(
                        pipeline.VelocityKmPerSec, peakSearchSpectrum, integratedWindowKmPerSec);

                    CoordinateMode mode = captureMeta.RaDeg.HasValue && captureMeta.DecDeg.HasValue
                        ? CoordinateMode.Equatorial
                        : captureMeta.AzDeg.HasValue && captureMeta.AltDeg.HasValue
                            ? CoordinateMode.AltAz
                            : CoordinateMode.Unknown;

                    // Written to this position's own array slot, never appended - safe for
                    // concurrent writes from different iterations (distinct indices) without a
                    // lock, and keeps MosaicResult.Positions in the same group order the old
                    // sequential loop produced regardless of which position actually finished
                    // first.
                    positionsArray[g] = new MosaicPosition(
                        label,
                        mode,
                        captureMeta.RaDeg / 15.0,
                        captureMeta.DecDeg,
                        captureMeta.AzDeg,
                        captureMeta.AltDeg,
                        pipeline.HiSpectrum,
                        lineStrengthDb,
                        peakVelocityKmPerSec,
                        files);

                    // Interlocked, not g-based: completion order is whatever the scheduler
                    // happens to finish, not necessarily 0,1,2... - a plain (g+1)/groups.Count
                    // could report progress out of order or even go "backwards" on screen.
                    int done = Interlocked.Increment(ref completedCount);
                    ReportOverall(g, 1.0, $"Processing positions ({done} of {groups.Count} done, last: {label})…");
                });
            }
            catch (AggregateException ex)
            {
                // Unwrap back to a single exception rather than leaking Parallel.For's own
                // AggregateException shape - callers (MosaicViewModel.GenerateMosaicAsync) only
                // ever distinguished OperationCanceledException from "everything else" against
                // the old sequential loop's single direct exception, and ExceptionDispatchInfo
                // preserves the original type/message/stack trace rather than just rethrowing a
                // new exception with a truncated trace.
                var inner = ex.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException) ?? ex.InnerExceptions[0];
                ExceptionDispatchInfo.Capture(inner).Throw();
                throw; // unreachable - ExceptionDispatchInfo.Throw() always throws
            }

            var positions = positionsArray.ToList();

            return new MosaicResult(
                frequencyAxis ?? Array.Empty<double>(),
                velocityAxis ?? Array.Empty<double>(),
                positions)
            {
                PositionsFromCache = fromCacheCount
            };
        }

        /// <summary>
        /// Finds the strongest channel of <paramref name="ratioSpectrum"/> (RatioSpectrum, or a
        /// smoothed copy of it - see the ProcessFolder call site) within +-windowKmPerSec of 0
        /// (the LSR-corrected line center) and reports both its strength (dB, *relative to the
        /// cold-sky baseline* - "how much brighter is the sky here than the cold-sky reference",
        /// not "how much brighter than this same pointing's own local continuum") and its
        /// velocity (km/s, signed - positive/receding-away vs negative/approaching-toward the
        /// LSR, per the radio-convention velocity axis HiStreamingPipeline already produces -
        /// see HiStreamingPipeline.Process's own "v > 0 means redshifted/receding" remark). The
        /// dB figure is deliberately single-differenced: RatioSpectrum (capturePower/baselinePower x
        /// HiConstants.RatioDisplayScale) already carries the only division against a
        /// reference this metric applies - unlike the earlier local-continuum-referenced
        /// version (which additionally divided out a per-position linear fit from
        /// HiStreamingPipeline's own continuum subtraction), so a position with no HI signal
        /// at all reads close to 0 dB here rather than a fraction of a dB. That also means this
        /// map isn't HI-line-only - broad continuum brightness differences across the sky (e.g.
        /// toward the Galactic plane) show up too, same as the raw calibrated power a receiver
        /// would show relative to a cold-sky zero point. Both values come back NaN if no channel
        /// falls in the window, or if the peak ratio comes out non-positive (shouldn't happen
        /// with real data, but the log guard stays regardless) - callers should skip NaN
        /// positions rather than treat them as "0 dB"/"0 km/s".
        /// </summary>
        private static (double lineStrengthDb, double peakVelocityKmPerSec) FindLinePeak(
            double[] velocityKmPerSec, double[] ratioSpectrum, double windowKmPerSec)
        {
            int peakIndex = -1;
            double peakRatio = double.NegativeInfinity;
            for (int i = 0; i < velocityKmPerSec.Length; i++)
            {
                if (Math.Abs(velocityKmPerSec[i]) <= windowKmPerSec && ratioSpectrum[i] > peakRatio)
                {
                    peakRatio = ratioSpectrum[i];
                    peakIndex = i;
                }
            }
            if (peakIndex < 0 || peakRatio <= 0)
                return (double.NaN, double.NaN);

            double lineStrengthDb = 10.0 * Math.Log10(peakRatio / HiConstants.RatioDisplayScale);
            return (lineStrengthDb, velocityKmPerSec[peakIndex]);
        }

    }
}
