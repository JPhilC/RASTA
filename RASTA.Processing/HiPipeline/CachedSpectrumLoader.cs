using RASTA.Core.Processing;
using RASTA.Core.Storage;
using RASTA.Infrastructure.Services;

namespace RASTA.Processing.HiPipeline
{
    /// <summary>
    /// Returns the parameter-independent, expensive part of reducing one file set - the
    /// native-resolution power spectrum accumulated over every FFT frame, plus the FITS
    /// header values the charts/mosaic need - from <see cref="SpectrumCacheService"/> when
    /// the same files (path, size, last-write time) have been reduced before, and otherwise
    /// by reading the raw IQ and caching the result (issue #19).
    ///
    /// Shared by VisualiseViewModel (Single Capture) and MosaicProcessor, so a position
    /// reduced by either is a cache hit for the other. Everything downstream (Target FFT
    /// Size binning, despike, baseline division, continuum fit, smoothing, dB, peak search)
    /// is cheap and always re-run by the caller, so no chart parameter is part of the cache
    /// key and a parameter change can never be served a stale result.
    ///
    /// Safe to call concurrently (MosaicProcessor does, once per position): the FITS read is
    /// serialized internally, everything else is per-call state.
    /// </summary>
    public sealed class CachedSpectrumLoader
    {
        private readonly FitsFileIo _fitsFileIo;
        private readonly IFftEngine _fftEngine;
        private readonly SpectrumCacheService _cache;

        // FitsFileIo itself holds no state, but the nom.tam.fits/nom.tam.util library it reads
        // through underneath is an old Java port never written with concurrent access in mind -
        // calling ReadRawIq/ReadCombinedRawIq from multiple threads at once hung the whole
        // process (observed stalled mid-FFT, with total CPU/memory activity flatlining - a
        // deadlock signature, not a crash). IFftEngine and everything after the read genuinely
        // is stateless/thread-safe, so only the file-reading step itself is serialized here,
        // not the CPU-bound work that follows it. Static so it also covers a Single Capture
        // chart being generated while a mosaic is processing.
        private static readonly object FitsReadLock = new();

        public CachedSpectrumLoader(FitsFileIo fitsFileIo, IFftEngine fftEngine, SpectrumCacheService cache)
        {
            _fitsFileIo = fitsFileIo;
            _fftEngine = fftEngine;
            _cache = cache;
        }

        /// <summary>
        /// <paramref name="files"/> is a single baseline file, or a capture file plus any
        /// "_{n}of{total}" siblings; FitsFileIo.ReadCombinedRawIq does the read/validate/trim/
        /// concatenate. <paramref name="skao"/> selects which product is required: false for
        /// the Hann-windowed ComputeSkAoPower average (<see cref="SpectrumCacheEntry.AveragedPower"/>),
        /// true for the SKAO reference's unwindowed 256-channel bin-sum
        /// (<see cref="SpectrumCacheEntry.SkaoBinnedPower"/>). Only the requested product is
        /// guaranteed non-null on return. Both arrays are in raw FFT-bin order (DC at index 0)
        /// and must not be modified in place by callers.
        ///
        /// <paramref name="forceRegenerate"/> ignores (and then overwrites) whatever is cached.
        /// <paramref name="progress"/> receives (status, fraction) while reading/processing on
        /// a cache miss - real, measured progress (files read, frames processed).
        /// </summary>
        public SpectrumCacheEntry Load(
            IReadOnlyList<string> files,
            bool skao,
            bool forceRegenerate,
            string label,
            Action<string, double>? progress,
            CancellationToken ct,
            out bool fromCache)
        {
            SpectrumCacheEntry? cached = forceRegenerate ? null : _cache.TryLoad(files);
            if (cached is not null && (skao ? cached.SkaoBinnedPower is not null : cached.AveragedPower is not null))
            {
                fromCache = true;
                return cached;
            }

            fromCache = false;

            FitsFileMetaData meta;
            byte[] iq;
            lock (FitsReadLock)
            {
                ct.ThrowIfCancellationRequested();
                (meta, iq) = _fitsFileIo.ReadCombinedRawIq(files, progress);
            }

            // Keep whichever product an existing entry already holds (e.g. the Hann average
            // when SKAO TTRT is requested for the first time) rather than discarding it.
            var entry = cached ?? new SpectrumCacheEntry();
            entry.FileCount = files.Count;
            entry.FftSize = meta.FftSize;
            entry.CentFreqHz = meta.CentFreqHz;
            entry.SampFreqHz = meta.SampFreqHz;
            entry.GainDb = meta.GainDb;
            entry.RaDeg = meta.RaDeg;
            entry.DecDeg = meta.DecDeg;
            entry.AzDeg = meta.AzDeg;
            entry.AltDeg = meta.AltDeg;
            entry.LsrCorrectionKmPerSec = meta.ComputeLsrCorrectionKmPerSec();

            string status = $"Processing {label}…";
            progress?.Invoke(status, 0.0);

            if (skao)
            {
                // Unwindowed native FFT per frame, bin-summed to 256 channels, summed across
                // frames - see SkaoHiObservation's own remarks.
                entry.SkaoBinnedPower = new SkaoHiObservation().AccumulateBinnedPower(iq, meta.FftSize, out int skaoFrames);
                entry.SkaoFrames = skaoFrames;
                ct.ThrowIfCancellationRequested();
            }
            else
            {
                // Average the same way HiStreamingAccumulator/Calibrator do: SKAO-normalized
                // power per fftSize frame, arithmetic mean. Always at the file's native
                // resolution - Target FFT Size re-binning happens later, on the averaged
                // spectrum, via SpectrumBinner (see its remarks for why shrinking the raw IQ
                // before the FFT - the old approach - was actually a bug, not a downscale).
                int fftSize = meta.FftSize;
                int bytesPerFrame = fftSize * 2;
                int totalFrames = bytesPerFrame > 0 ? iq.Length / bytesPerFrame : 0;
                if (totalFrames == 0)
                    throw new InvalidOperationException($"The {label} file contains no complete FFT frames.");

                var sum = new double[fftSize];
                var chunk = new byte[bytesPerFrame];
                for (int f = 0; f < totalFrames; f++)
                {
                    ct.ThrowIfCancellationRequested();

                    Buffer.BlockCopy(iq, f * bytesPerFrame, chunk, 0, bytesPerFrame);
                    var spectrum = _fftEngine.ComputeSkAoPower(chunk, fftSize);
                    for (int i = 0; i < fftSize; i++)
                        sum[i] += spectrum[i];

                    progress?.Invoke(status, (double)(f + 1) / totalFrames);
                }

                for (int i = 0; i < fftSize; i++)
                    sum[i] /= totalFrames;

                entry.AveragedPower = sum;
                entry.AveragedFrames = totalFrames;
            }

            progress?.Invoke(status, 1.0);
            _cache.Save(files, entry);
            return entry;
        }
    }
}
