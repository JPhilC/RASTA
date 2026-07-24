namespace RASTA.Core.Calibration;

public sealed class CalibrationProfile
{
    public DateTime TimestampUtc { get; init; }
    public double CenterFrequencyHz { get; init; }
    public double SampleRateHz { get; init; } 
    public uint FftSize { get; init; }
    public double GainDb { get; init; }
    public double GainFactor { get; init; }
    public double[]? NoiseSpectrum { get; init; } = null;
}

