using RASTA.Core.Processing;
using RASTA.Core.Storage;
using RASTA.Core.Telescope;
using RASTA.Processing.HiPipeline;
using System.IO;

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
    /// </summary>
    public class MosaicProcessor
    {
        private readonly FitsFileIo _fitsFileIo;
        private readonly IFftEngine _fftEngine;

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
            CancellationToken ct = default)
        {
            return Task.Run(
                () => ProcessFolder(folder, baselineFilePath, targetFftSize, integratedWindowKmPerSec, progressCallback, despike, despikeThresholdSigma, ct),
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
            int bytesPerFrame = fftSize * 2;

            byte[] baselineIq = fftSize < scanFftSize
                ? IqDownscaler.Downscale(baselineIqRaw, scanFftSize, fftSize)
                : baselineIqRaw;

            var baselineAcc = new HiStreamingAccumulator(fftSize);
            ForEachChunk(baselineIq, bytesPerFrame, chunk =>
                baselineAcc.AddBaselineFrame(_fftEngine.ComputeSkAoPower(chunk, fftSize)));
            double[] baselinePower = baselineAcc.GetBaselineAverage();

            // --- One dwell-point capture group per sky/AltAz position ---
            var positions = new List<MosaicPosition>(groups.Count);
            double[]? frequencyAxis = null;
            double[]? velocityAxis = null;

            for (int g = 0; g < groups.Count; g++)
            {
                ct.ThrowIfCancellationRequested();

                var files = groups[g];
                string label = Path.GetFileNameWithoutExtension(files[0]);
                string status = $"Processing position {g + 1} of {groups.Count} ({label})…";
                progressCallback?.Invoke(status, (double)g / groups.Count);

                var (captureMeta, captureIqRaw) = _fitsFileIo.ReadCombinedRawIq(files);

                if (captureMeta.FftSize != scanFftSize ||
                    captureMeta.SampFreqHz != baselineMeta.SampFreqHz ||
                    captureMeta.CentFreqHz != baselineMeta.CentFreqHz)
                {
                    throw new InvalidOperationException(
                        $"Capture group '{label}' has a different FFT size, sample rate, or center " +
                        "frequency than the baseline.");
                }

                byte[] captureIq = fftSize < scanFftSize
                    ? IqDownscaler.Downscale(captureIqRaw, scanFftSize, fftSize)
                    : captureIqRaw;

                var captureAcc = new HiStreamingAccumulator(fftSize);
                ForEachChunk(captureIq, bytesPerFrame, chunk =>
                    captureAcc.AddCaptureFrame(_fftEngine.ComputeSkAoPower(chunk, fftSize)));
                double[] capturePower = captureAcc.GetCaptureAverage();

                // Each position has its own pointing, so its own LSR correction - unlike
                // ProcessHiCore, this can't be hoisted out of the loop.
                double lsrCorrectionKmPerSec = captureMeta.ComputeLsrCorrectionKmPerSec();

                var pipeline = new HiStreamingPipeline();
                pipeline.Process(baselinePower, capturePower, captureMeta.SampFreqHz, captureMeta.CentFreqHz, lsrCorrectionKmPerSec, despike: despike, despikeThresholdSigma: despikeThresholdSigma);

                frequencyAxis ??= pipeline.FrequencyHz;
                velocityAxis ??= pipeline.VelocityKmPerSec;

                double lineStrengthDb = ComputeLineStrengthDb(
                    pipeline.VelocityKmPerSec, pipeline.RatioSpectrum, integratedWindowKmPerSec);

                CoordinateMode mode = captureMeta.RaDeg.HasValue && captureMeta.DecDeg.HasValue
                    ? CoordinateMode.Equatorial
                    : captureMeta.AzDeg.HasValue && captureMeta.AltDeg.HasValue
                        ? CoordinateMode.AltAz
                        : CoordinateMode.Unknown;

                positions.Add(new MosaicPosition(
                    label,
                    mode,
                    captureMeta.RaDeg / 15.0,
                    captureMeta.DecDeg,
                    captureMeta.AzDeg,
                    captureMeta.AltDeg,
                    pipeline.HiSpectrum,
                    lineStrengthDb,
                    files));

                progressCallback?.Invoke(status, (double)(g + 1) / groups.Count);
            }

            return new MosaicResult(
                frequencyAxis ?? Array.Empty<double>(),
                velocityAxis ?? Array.Empty<double>(),
                positions);
        }

        /// <summary>
        /// Finds the strongest RatioSpectrum channel within +-windowKmPerSec of 0 (the
        /// LSR-corrected line center) and reports it as dB *relative to the cold-sky baseline*
        /// - "how much brighter is the sky here than the cold-sky reference", not "how much
        /// brighter than this same pointing's own local continuum". This is deliberately
        /// single-differenced: RatioSpectrum (capturePower/baselinePower x
        /// HiConstants.RatioDisplayScale) already carries the only division against a
        /// reference this metric applies - unlike the earlier local-continuum-referenced
        /// version (which additionally divided out a per-position linear fit from
        /// HiStreamingPipeline's own continuum subtraction), so a position with no HI signal
        /// at all reads close to 0 dB here rather than a fraction of a dB. That also means this
        /// map isn't HI-line-only - broad continuum brightness differences across the sky (e.g.
        /// toward the Galactic plane) show up too, same as the raw calibrated power a receiver
        /// would show relative to a cold-sky zero point. Returns NaN if no channel falls in the
        /// window, or if the peak ratio comes out non-positive (shouldn't happen with real
        /// data, but the log guard stays regardless) - callers should skip NaN positions rather
        /// than treat them as "0 dB".
        /// </summary>
        private static double ComputeLineStrengthDb(
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
                return double.NaN;

            return 10.0 * Math.Log10(peakRatio / HiConstants.RatioDisplayScale);
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
