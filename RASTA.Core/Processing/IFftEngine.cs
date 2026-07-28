using RASTA.Core.Sdr;

namespace RASTA.Core.Processing;

public interface IFftEngine
{
    double[] PowerSpectrum(System.Numerics.Complex[] samples);

    double[] ComputeSpectrum(byte[] rawIq, int fftSize);
}


