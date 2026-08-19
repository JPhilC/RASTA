namespace RASTA.Processing.HiPipeline
{
    /// <summary>
    /// Reduces an already-averaged power spectrum from its native FFT size down to a smaller
    /// "Target FFT Size" by averaging groups of physically-adjacent bins together - the
    /// correct way to trade frequency resolution for lower per-bin noise while covering the
    /// same total bandwidth.
    ///
    /// Supersedes the old IqDownscaler, which shrank the raw IQ *before* the FFT by
    /// block-averaging groups of consecutive time-domain samples. That looked plausible but
    /// was a real bug: block-averaging D consecutive time samples and then FFT-ing the
    /// shorter result is mathematically decimation, and decimating in time aliases the
    /// spectrum - each output bin becomes a sum of D bins spaced nativeSize/D apart in the
    /// *original* spectrum (e.g. for a 4096→2048 downscale, output bin k combines original
    /// bin k with bin k+2048 - the middle of the band folded together with content from the
    /// far edge), not an average of nearby frequencies. That's exactly why reducing Target
    /// FFT Size on a single baseline/capture file made the displayed spectrum noisier and
    /// erased its smooth bandpass shape instead of smoothing it: neighbouring points in the
    /// downscaled display were no longer physically neighbouring frequencies. It was mostly
    /// invisible in the combined baseline/capture ratio view only because the same aliasing
    /// pattern hits both spectra identically and roughly cancels in the division - not
    /// because the underlying math was actually correct there either.
    ///
    /// BinAverage instead runs after the FFT and after frame-averaging: average groups of
    /// `factor` consecutive bins of the *native-resolution* averaged power spectrum. Grouping
    /// consecutive array indices is a true local-frequency average in either bin ordering a
    /// caller might have - raw FFT-bin order (DC at index 0) or already-fftshifted monotonic
    /// frequency order - since a block boundary only ever needs to land on a real adjacency,
    /// and consecutive indices are adjacent frequencies in both orderings (fftshift is just a
    /// circular rotation, and DC sits exactly on a block boundary - index 0 - in raw order).
    /// </summary>
    public static class SpectrumBinner
    {
        /// <summary>
        /// Averages groups of nativeSize/targetSize consecutive bins of an already-averaged
        /// power spectrum together. Returns the input unchanged if targetSize equals the
        /// spectrum's own length (no re-binning needed).
        /// </summary>
        public static double[] BinAverage(double[] spectrum, int targetSize)
        {
            if (spectrum == null) throw new ArgumentNullException(nameof(spectrum));
            int nativeSize = spectrum.Length;
            if (targetSize <= 0 || targetSize > nativeSize)
                throw new ArgumentOutOfRangeException(nameof(targetSize),
                    $"targetSize ({targetSize}) must be > 0 and <= the spectrum's own length ({nativeSize}).");
            if (targetSize == nativeSize)
                return spectrum;
            if (nativeSize % targetSize != 0)
                throw new InvalidOperationException(
                    $"Spectrum bin averaging must be an integer ratio: {nativeSize} → {targetSize}");

            int factor = nativeSize / targetSize;
            var result = new double[targetSize];
            for (int i = 0; i < targetSize; i++)
            {
                double sum = 0;
                int start = i * factor;
                for (int j = 0; j < factor; j++)
                    sum += spectrum[start + j];
                result[i] = sum / factor;
            }
            return result;
        }
    }
}
