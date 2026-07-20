using System;
using System.Linq;

namespace RASTA.Processing.Spectral
{
    public class SpectrumMath
    {
        // -----------------------------
        // Public API
        // -----------------------------

        public double[] SubtractBaseline(double[] spectrum, int windowSize = 257)
        {
            // Running median baseline subtraction
            var baseline = RunningMedian(spectrum, windowSize);

            var result = new double[spectrum.Length];
            for (int i = 0; i < spectrum.Length; i++)
                result[i] = spectrum[i] - baseline[i];

            return result;
        }

        public double[] Smooth(double[] spectrum, int windowSize = 31)
        {
            return SavitzkyGolaySmooth(spectrum, windowSize, polynomialOrder: 3);
        }

        public double[] Normalise(double[] spectrum)
        {
            double max = spectrum.Max();
            if (max == 0) return spectrum;

            var result = new double[spectrum.Length];
            for (int i = 0; i < spectrum.Length; i++)
                result[i] = spectrum[i] / max;

            return result;
        }

        public double[] EnhancePeak(double[] spectrum, double factor = 1.5)
        {
            var result = new double[spectrum.Length];
            for (int i = 0; i < spectrum.Length; i++)
                result[i] = Math.Pow(spectrum[i], factor);

            return result;
        }

        public double[] BuildFrequencyAxis(
            double centerFreqHz,
            double sampleRateHz,
            int fftSize)
        {
            double binWidth = sampleRateHz / fftSize;
            double startFreq = centerFreqHz - (sampleRateHz / 2);

            var freqs = new double[fftSize];
            for (int i = 0; i < fftSize; i++)
                freqs[i] = startFreq + i * binWidth;

            return freqs;
        }


        // -----------------------------
        // Internal DSP helpers
        // -----------------------------

        private static double[] RunningMedian(double[] data, int windowSize)
        {
            int n = data.Length;
            int half = windowSize / 2;

            var result = new double[n];
            var window = new double[windowSize];

            for (int i = 0; i < n; i++)
            {
                int start = Math.Max(0, i - half);
                int end = Math.Min(n - 1, i + half);
                int count = end - start + 1;

                Array.Copy(data, start, window, 0, count);
                Array.Sort(window, 0, count);

                result[i] = window[count / 2];
            }

            return result;
        }

        private static double[] SavitzkyGolaySmooth(
            double[] data,
            int windowSize,
            int polynomialOrder)
        {
            if (windowSize % 2 == 0)
                throw new ArgumentException("Window size must be odd.");

            int half = windowSize / 2;
            int n = data.Length;

            var result = new double[n];

            // Precompute convolution coefficients
            double[] coeffs = SavitzkyGolayCoefficients(windowSize, polynomialOrder);

            for (int i = 0; i < n; i++)
            {
                double sum = 0;

                for (int k = -half; k <= half; k++)
                {
                    int idx = i + k;
                    if (idx < 0) idx = 0;
                    if (idx >= n) idx = n - 1;

                    sum += coeffs[k + half] * data[idx];
                }

                result[i] = sum;
            }

            return result;
        }

        private static double[] SavitzkyGolayCoefficients(int windowSize, int polyOrder)
        {
            // For radio astronomy, polyOrder=3 is ideal.
            // This implementation uses the standard least-squares formulation.

            int half = windowSize / 2;
            int m = polyOrder;

            double[,] a = new double[windowSize, m + 1];

            for (int i = -half; i <= half; i++)
            {
                for (int j = 0; j <= m; j++)
                    a[i + half, j] = Math.Pow(i, j);
            }

            // Compute pseudoinverse: (A^T A)^-1 A^T
            double[,] ata = MultiplyTranspose(a);
            double[,] inv = Invert(ata);
            double[,] pseudo = Multiply(inv, Transpose(a));

            // First row gives smoothing coefficients
            double[] coeffs = new double[windowSize];
            for (int i = 0; i < windowSize; i++)
                coeffs[i] = pseudo[0, i];

            return coeffs;
        }


        // -----------------------------
        // Matrix helpers (small matrices only)
        // -----------------------------

        private static double[,] MultiplyTranspose(double[,] a)
        {
            int rows = a.GetLength(0);
            int cols = a.GetLength(1);

            var result = new double[cols, cols];

            for (int i = 0; i < cols; i++)
            {
                for (int j = 0; j < cols; j++)
                {
                    double sum = 0;
                    for (int k = 0; k < rows; k++)
                        sum += a[k, i] * a[k, j];

                    result[i, j] = sum;
                }
            }

            return result;
        }

        private static double[,] Transpose(double[,] a)
        {
            int rows = a.GetLength(0);
            int cols = a.GetLength(1);

            var result = new double[cols, rows];

            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++)
                    result[j, i] = a[i, j];

            return result;
        }

        private static double[,] Multiply(double[,] a, double[,] b)
        {
            int aRows = a.GetLength(0);
            int aCols = a.GetLength(1);
            int bCols = b.GetLength(1);

            var result = new double[aRows, bCols];

            for (int i = 0; i < aRows; i++)
            {
                for (int j = 0; j < bCols; j++)
                {
                    double sum = 0;
                    for (int k = 0; k < aCols; k++)
                        sum += a[i, k] * b[k, j];

                    result[i, j] = sum;
                }
            }

            return result;
        }

        private static double[,] Invert(double[,] m)
        {
            // Small matrix inversion (polyOrder <= 5)
            int n = m.GetLength(0);
            var result = new double[n, n];
            var temp = new double[n, n];

            Array.Copy(m, temp, m.Length);

            // Identity matrix
            for (int i = 0; i < n; i++)
                result[i, i] = 1;

            // Gauss-Jordan elimination
            for (int i = 0; i < n; i++)
            {
                double diag = temp[i, i];
                for (int j = 0; j < n; j++)
                {
                    temp[i, j] /= diag;
                    result[i, j] /= diag;
                }

                for (int k = 0; k < n; k++)
                {
                    if (k == i) continue;

                    double factor = temp[k, i];
                    for (int j = 0; j < n; j++)
                    {
                        temp[k, j] -= factor * temp[i, j];
                        result[k, j] -= factor * result[i, j];
                    }
                }
            }

            return result;
        }
    }
}
