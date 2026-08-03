using RASTA.Core.Sdr;

namespace RASTA.Core.Processing;

public interface IFftEngine
{
    double[] ComputeSkAoPower(byte[] rawIq, int fftSize);

    double[] PowerSpectrum(System.Numerics.Complex[] samples);

    double[] ComputeSpectrum(byte[] rawIq, int fftSize);
}


