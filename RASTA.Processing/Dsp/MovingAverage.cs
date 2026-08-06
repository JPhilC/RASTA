using System;

namespace RASTA.Processing.Dsp
{
    /// <summary>
    /// Plain centered boxcar average - a simpler, blunter alternative to Savitzky-Golay for
    /// HiStreamingPipeline's optional final smoothing pass. Unlike SG (which fits a local
    /// polynomial and so preserves peak height/curvature better for the same window size),
    /// a moving average just flattens everything in the window equally - it smooths harder
    /// but broadens/flattens real features more. Offered as an alternative so the two can be
    /// compared directly rather than assuming SG is always the right choice.
    /// </summary>
    public static class MovingAverage
    {
        /// <summary>
        /// Averages each point over a centered window, shrinking the window naturally at
        /// the array edges rather than padding/mirroring - simple and never reads out of
        /// bounds. window &lt;= 1 is a no-op (returns a copy of the input unchanged).
        /// </summary>
        public static double[] Smooth(double[] data, int window)
        {
            int n = data.Length;
            var result = new double[n];

            if (window <= 1)
            {
                Array.Copy(data, result, n);
                return result;
            }

            int half = window / 2;
            for (int i = 0; i < n; i++)
            {
                int lo = Math.Max(0, i - half);
                int hi = Math.Min(n - 1, i + half);

                double sum = 0.0;
                for (int k = lo; k <= hi; k++)
                    sum += data[k];

                result[i] = sum / (hi - lo + 1);
            }

            return result;
        }
    }
}
