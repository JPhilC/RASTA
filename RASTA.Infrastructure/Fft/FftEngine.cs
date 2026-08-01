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

            ApplyHannWindow(buffer);
            // ApplyBlackmanHarris(buffer);

            Fourier.Forward(buffer, FourierOptions.Matlab);

            var power = new double[buffer.Length];
            for (int i = 0; i < buffer.Length; i++)
            {
                power[i] = (buffer[i].Real * buffer[i].Real) +
                           (buffer[i].Imaginary * buffer[i].Imaginary);
            }

            return power;
        }

        public double[] ComputeSpectrum(byte[] rawIq, int fftSize)
        {
            int n = Math.Min(rawIq.Length / 2, fftSize);

            var complex = new Complex[fftSize];

            // Convert raw IQ bytes → Complex samples
            for (int i = 0; i < n; i++)
            {
                double re = rawIq[2 * i] - 128;
                double im = rawIq[2 * i + 1] - 128;
                complex[i] = new Complex(re, im);
            }

            // Zero-pad if needed
            for (int i = n; i < fftSize; i++)
                complex[i] = Complex.Zero;

            return PowerSpectrum(complex);
        }

        private void ApplyHannWindow(Complex[] buffer)
        {
            int n = buffer.Length;
            for (int i = 0; i < n; i++)
            {
                double w = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
                buffer[i] *= w;
            }
        }

        private void ApplyBlackmanHarris(Complex[] buffer)
        {
            int n = buffer.Length;
            const double a0 = 0.35875;
            const double a1 = 0.48829;
            const double a2 = 0.14128;
            const double a3 = 0.01168;

            for (int i = 0; i < n; i++)
            {
                double w =
                    a0
                    - a1 * Math.Cos(2 * Math.PI * i / (n - 1))
                    + a2 * Math.Cos(4 * Math.PI * i / (n - 1))
                    - a3 * Math.Cos(6 * Math.PI * i / (n - 1));

                buffer[i] *= w;
            }
        }

    }
}
