using RASTA.Core.Processing;

namespace RASTA.Processing.HiPipeline
{
    /// <summary>
    /// Welch-style frame stepping for the Hann-windowed power average (issue #22, Phase 2 of
    /// SignalProcessingPlan.md): FFT frames start every half frame rather than end to end.
    /// The Hann window gives the samples near each frame's edges almost no weight, so with
    /// end-to-end frames nearly half the captured data barely contributes; a 50% overlap
    /// puts those samples near the middle of the next frame. Overlapped Hann frames are only
    /// weakly correlated, so on synthetic noise the averaged spectrum's per-bin scatter drops
    /// by about 1.35× for the same capture (OverlappedFramesTests), with the mean level
    /// unchanged.
    ///
    /// Used by every Hann-windowed averaging path - CachedSpectrumLoader (Visualise/Mosaic),
    /// Calibrator (gain trials and the cold-sky baseline) and CaptureViewModel's live
    /// spectrum - so baselines and captures are averaged identically. Not used by the SKAO
    /// reference path (SkaoHiObservation.AccumulateBinnedPower), which is unwindowed and
    /// must match the reference algorithm's end-to-end frames.
    ///
    /// Raw IQ is interleaved unsigned bytes (I0 Q0 I1 Q1 …), so a frame of fftSize samples is
    /// 2·fftSize bytes and the half-frame hop is fftSize bytes.
    /// </summary>
    public static class OverlappedFrames
    {
        /// <summary>Bytes in one frame of <paramref name="fftSize"/> IQ samples.</summary>
        public static int FrameBytes(int fftSize) => fftSize * 2;

        /// <summary>Bytes between successive frame starts: half a frame.</summary>
        public static int HopBytes(int fftSize) => fftSize;

        /// <summary>Number of whole overlapped frames in a contiguous run of <paramref name="bytes"/> bytes.</summary>
        public static int CountFrames(int bytes, int fftSize)
        {
            int frameBytes = FrameBytes(fftSize);
            return bytes < frameBytes ? 0 : (bytes - frameBytes) / HopBytes(fftSize) + 1;
        }

        /// <summary>
        /// Byte offsets of every frame start. <paramref name="segmentLengths"/> splits
        /// <paramref name="iq"/>'s length into physically separate captures (one per file of a
        /// multi-file dwell point, see FitsFileIo.ReadCombinedRawIq); the half-step sequence
        /// restarts at each segment so no frame spans two of them. Null means one segment.
        /// <paramref name="startOffset"/> skips that many bytes at the start of the first
        /// segment (e.g. a gain-switching settle period).
        /// </summary>
        public static IEnumerable<int> FrameOffsets(int totalBytes, int fftSize, IReadOnlyList<int>? segmentLengths = null, int startOffset = 0)
        {
            int frameBytes = FrameBytes(fftSize);
            int hop = HopBytes(fftSize);
            segmentLengths ??= [totalBytes];

            int segmentStart = 0;
            for (int s = 0; s < segmentLengths.Count; s++)
            {
                int segmentEnd = Math.Min(segmentStart + segmentLengths[s], totalBytes);
                int first = s == 0 ? segmentStart + Math.Max(startOffset, 0) : segmentStart;
                for (int offset = first; offset + frameBytes <= segmentEnd; offset += hop)
                    yield return offset;
                segmentStart = segmentEnd;
            }
        }

        /// <summary>
        /// Calls <paramref name="onFrame"/> with the SKAO-normalized Hann power spectrum
        /// (IFftEngine.ComputeSkAoPower, raw FFT-bin order) of every overlapped frame, and
        /// returns how many there were. <paramref name="progress"/> gets the fraction done.
        /// </summary>
        public static int ForEachPowerSpectrum(
            byte[] iq,
            int fftSize,
            IFftEngine fftEngine,
            Action<double[]> onFrame,
            IReadOnlyList<int>? segmentLengths = null,
            int startOffset = 0,
            Action<double>? progress = null,
            CancellationToken ct = default)
        {
            var offsets = FrameOffsets(iq.Length, fftSize, segmentLengths, startOffset).ToList();
            var frame = new byte[FrameBytes(fftSize)];
            for (int f = 0; f < offsets.Count; f++)
            {
                ct.ThrowIfCancellationRequested();
                Buffer.BlockCopy(iq, offsets[f], frame, 0, frame.Length);
                onFrame(fftEngine.ComputeSkAoPower(frame, fftSize));
                progress?.Invoke((double)(f + 1) / offsets.Count);
            }
            return offsets.Count;
        }

        /// <summary>
        /// Arithmetic mean of every overlapped frame's power spectrum (see
        /// <see cref="ForEachPowerSpectrum"/>); <paramref name="frames"/> is the number of
        /// overlapped frames averaged (roughly twice the number of independent ones).
        /// Returns an all-zero spectrum and frames = 0 when there isn't one whole frame.
        /// </summary>
        public static double[] AveragePower(
            byte[] iq,
            int fftSize,
            IFftEngine fftEngine,
            out int frames,
            IReadOnlyList<int>? segmentLengths = null,
            int startOffset = 0,
            Action<double>? progress = null,
            CancellationToken ct = default)
        {
            var sum = new double[fftSize];
            frames = ForEachPowerSpectrum(iq, fftSize, fftEngine, spectrum =>
            {
                for (int i = 0; i < fftSize; i++)
                    sum[i] += spectrum[i];
            }, segmentLengths, startOffset, progress, ct);

            if (frames > 0)
                for (int i = 0; i < fftSize; i++)
                    sum[i] /= frames;
            return sum;
        }
    }
}
