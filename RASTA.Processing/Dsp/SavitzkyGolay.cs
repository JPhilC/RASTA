using System;

namespace RASTA.Processing.Dsp
{
    /// <summary>
    /// Fixed 5-point Savitzky–Golay smoothing kernel (quadratic/cubic fit).
    /// Used by both HiStreamingPipeline and SkaoPipelineProcessor for the optional
    /// final smoothing pass. Originally part of the (now-removed) IfAverage chain
    /// ported from Daniel M. Kamiński's SDR AVE plugin - promoted to its own shared
    /// location since it's still a real dependency of the active HI pipelines.
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
