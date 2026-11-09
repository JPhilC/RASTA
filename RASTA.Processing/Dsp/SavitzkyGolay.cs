using System;

namespace RASTA.Processing.Dsp
{
    /// <summary>
    /// Fixed 5-point Savitzky–Golay smoothing kernel (quadratic/cubic fit), with its
    /// two edge samples on each side left as zero rather than fitted (no mode='interp'
    /// equivalent). Originally part of the (now-removed) IfAverage chain ported from
    /// Daniel M. Kamiński's SDR AVE plugin - promoted to its own shared location at
    /// the time. No longer used by SkaoPipelineProcessor: the actual SKAO reference
    /// (compute_hi_spectrum) never smooths its final HI spectrum at all - the
    /// reference's own savgol_filter(data, 5, 2) call is only ever used internally by
    /// filter_rfi, over small (30-channel) continuum-fit windows where this kernel's
    /// zeroed edges would read as spuriously huge residuals - so SkaoPipelineProcessor
    /// now has its own self-contained, edge-correct Savitzky-Golay implementation
    /// instead. Currently unused; kept in case a future caller wants a cheap fixed
    /// kernel over a long array where a couple of zeroed edge samples don't matter.
    /// </summary>
    public class SavitzkyGolay
    {
        private static readonly double[] C = { -3.0 / 35, 12.0 / 35, 17.0 / 35, 12.0 / 35, -3.0 / 35 };

        public bool Enabled { get; set; }

        public void Process(double[] data)
        {
            if (!Enabled)
                return;

            int n = data.Length;
            double[] tmp = new double[n];

            for (int i = 2; i < n - 2; i++)
            {
                tmp[i] =
                    C[0] * data[i - 2] +
                    C[1] * data[i - 1] +
                    C[2] * data[i] +
                    C[3] * data[i + 1] +
                    C[4] * data[i + 2];
            }

            Array.Copy(tmp, data, n);
        }
    }

}
