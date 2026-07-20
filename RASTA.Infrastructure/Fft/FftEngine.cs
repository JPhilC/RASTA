using MathNet.Numerics.IntegralTransforms;
using RASTA.Core.Processing;
using System.Numerics;

namespace RASTA.Infrastructure.Fft
{
    public class FftEngine : IFftEngine
    {
        public double[] PowerSpectrum(Complex[] samples)
        {
            // Defensive copy because Math.NET transforms in-place
            var buffer = new Complex[samples.Length];
            Array.Copy(samples, buffer, samples.Length);

            // Apply a Hann window to reduce spectral leakage
            ApplyHannWindow(buffer);

            // Perform FFT in-place
            Fourier.Forward(buffer, FourierOptions.Matlab);

            // Compute magnitude squared (power spectrum)
            var power = new double[buffer.Length];
            for (int i = 0; i < buffer.Length; i++)
            {
                // |FFT|^2 = real^2 + imag^2
                power[i] = (buffer[i].Real * buffer[i].Real) +
                           (buffer[i].Imaginary * buffer[i].Imaginary);
            }

            return power;
        }

        private static void ApplyHannWindow(Complex[] buffer)
        {
            int n = buffer.Length;
            for (int i = 0; i < n; i++)
            {
                double w = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
                buffer[i] *= w;
            }
        }
    }
}
