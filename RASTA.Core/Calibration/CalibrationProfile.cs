namespace RASTA.Core.Calibration;

public class CalibrationProfile
{
    public DateTime TimestampUtc { get; set; }
    public double[] NoiseSpectrum { get; set; }
    public double GainFactor { get; set; }
    public int FftSize { get; set; }
    public double SampleRateHz { get; set; }
    public double CenterFrequencyHz { get; set; }
}
