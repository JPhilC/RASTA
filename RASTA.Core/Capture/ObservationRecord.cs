namespace RASTA.Core.Capture;




public class ObservationRecord
{
    public ObservationMetadata Metadata { get; set; }
    public double[] AveragedSpectrum { get; set; }
}
