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
        IReadOnlyList<MosaicPosition> Positions);

    /// <summary>
    /// Folder-wide counterpart to VisualiseViewModel.ProcessHiCore: walks every dwell-point
    /// capture group in a session folder through the same HiStreamingPipeline, against one
    /// shared baseline, producing one MosaicPosition per sky/AltAz position. Pure algorithm -
    /// no UI/hardware deps - matching the RASTA.Processing layering convention.
    ///
    /// The baseline is read/averaged once up front, then every position is processed
    /// concurrently via Parallel.For (see ProcessFolder's remarks) - positions are otherwise
    /// completely independent of each other, so this scales with core count rather than being
    /// stuck at one position's worth of FFT/pipeline work at a time.
    /// </summary>
    public class MosaicProcessor
    {
        private readonly FitsFileIo _fitsFileIo;
        private readonly IFftEngine _fftEngine;

        // FitsFileIo itself holds no state, but the nom.tam.fits/nom.tam.util library it reads
        // through underneath is an old Java port never written with concurrent access in mind -
        // calling ReadRawIq/ReadCombinedRawIq from multiple positions at once hung the whole
        // process (observed stalled mid-FFT, with total CPU/memory activity flatlining - a
        // deadlock signature, not a crash). IFftEngine and everything after the read genuinely
        // is stateless/thread-safe (see ProcessFolder's remarks), so only the file-reading step
        // itself is serialized here, not the CPU-bound work that follows it.
        private readonly object _fitsReadLock = new();

        public MosaicProcessor(FitsFileIo fitsFileIo, IFftEngine fftEngine)
        {
            _fitsFileIo = fitsFileIo;
            _fftEngine = fftEngine;
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
            CancellationToken ct = default)
        {
            return Task.Run(
                () => ProcessFolder(folder, baselineFilePath, targetFftSize, integratedWindowKmPerSec, progressCallback, despike, despikeThresholdSigma, smoothing, smoothingWindow, ct),
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
            CancellationToken ct)
        {
            string baselineFullPath = Path.GetFullPath(baselineFilePath);

            var captureFiles = Directory.GetFiles(folder, "*.fits")
                .Where(f => !string.Equals(Path.GetFullPath(f), baselineFullPath, StringComparison.OrdinalIgnoreCase))
                .Where(f => !FitsPathBuilder.IsBaselineFile(f))
                .ToList();

            var groups = FitsPathBuilder.GroupSweepFiles(captureFiles);
            if (groups.Count == 0)
                throw new InvalidOperationException("No capture files found in the selected folder.");

            // --- Baseline: read once, average once, reuse for every position ---
            progressCallback?.Invoke("Reading baseline…", 0.0);
            var (baselineMeta, baselineIqRaw) = _fitsFileIo.ReadRawIq(baselineFilePath);

            int scanFftSize = baselineMeta.FftSize;
            int fftSize = (targetFftSize > 0 && targetFftSize <= scanFftSize) ? targetFftSize : scanFftSize;
            int bytesPerFrame = scanFftSize * 2;

            // Always FFT/accumulate at native resolution; Target FFT Size re-binning
            // happens after averaging, via SpectrumBinner - not by shrinking the raw IQ
            // beforehand, which aliases distant frequency bins together instead of
            // averaging local ones (see SpectrumBinner's own remarks for why).
            var baselineAcc = new HiStreamingAccumulator(scanFftSize);
            ForEachChunk(baselineIqRaw, bytesPerFrame, chunk =>
                baselineAcc.AddBaselineFrame(_fftEngine.ComputeSkAoPower(chunk, scanFftSize)));
            double[] baselinePower = baselineAcc.GetBaselineAverage();
            if (fftSize < scanFftSize)
                baselinePower = SpectrumBinner.BinAverage(baselinePower, fftSize);

            // --- One dwell-point capture group per sky/AltAz position, processed in parallel.
            // Each position reads its own file(s) and runs its own FFT/accumulate/pipeline work
            // completely independently of every other position - the only thing they share is
            // the already-computed, read-only baselinePower array above. IFftEngine (FftEngine)
            // is genuinely stateless and safe to call concurrently; HiStreamingAccumulator/
            // HiStreamingPipeline are freshly `new`'d per position anyway, same as the old
            // sequential loop. The FITS read itself is deliberately still serialized (see
            // _fitsReadLock) - only the FFT/accumulate/pipeline/peak-search work that follows it
            // runs concurrently across positions.
            var positionsArray = new MosaicPosition[groups.Count];
            double[]? frequencyAxis = null;
            double[]? velocityAxis = null;
            int completedCount = 0;

            try
            {
                Parallel.For(0, groups.Count, new ParallelOptions { CancellationToken = ct }, g =>
                {
                    var files = groups[g];
                    string label = Path.GetFileNameWithoutExtension(files[0]);

                    (FitsFileMetaData captureMeta, byte[] captureIqRaw) read;
                    lock (_fitsReadLock)
                    {
                        read = _fitsFileIo.ReadCombinedRawIq(files);
                    }
                    var (captureMeta, captureIqRaw) = read;

                    if (captureMeta.FftSize != scanFftSize ||
                        captureMeta.SampFreqHz != baselineMeta.SampFreqHz ||
                        captureMeta.CentFreqHz != baselineMeta.CentFreqHz)
                    {
                        throw new InvalidOperationException(
                            $"Capture group '{label}' has a different FFT size, sample rate, or center " +
                            "frequency than the baseline.");
                    }

                    var captureAcc = new HiStreamingAccumulator(scanFftSize);
                    ForEachChunk(captureIqRaw, bytesPerFrame, chunk =>
                        captureAcc.AddCaptureFrame(_fftEngine.ComputeSkAoPower(chunk, scanFftSize)));
                    double[] capturePower = captureAcc.GetCaptureAverage();
                    if (fftSize < scanFftSize)
                        capturePower = SpectrumBinner.BinAverage(capturePower, fftSize);

                    // Each position has its own pointing, so its own LSR correction - unlike
                    // ProcessHiCore, this can't be hoisted out of the loop.
                    double lsrCorrectionKmPerSec = captureMeta.ComputeLsrCorrectionKmPerSec();

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
                    progressCallback?.Invoke($"Processing position {done} of {groups.Count} ({label})…", (double)done / groups.Count);
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
                positions);
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

        private static void ForEachChunk(byte[] iq, int bytesPerChunk, Action<byte[]> processChunk)
        {
            for (int offset = 0; offset + bytesPerChunk <= iq.Length; offset += bytesPerChunk)
            {
                var chunk = new byte[bytesPerChunk];
                Buffer.BlockCopy(iq, offset, chunk, 0, bytesPerChunk);
                processChunk(chunk);
            }
        }
    }
}
