using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RASTA.Core.Capture;

    public class ObservationMetadata
{
    public DateTime TimestampUtc { get; set; }
    public TargetPoint Pointing { get; set; }
    public TimeSpan IntegrationTime { get; set; }
    public double CenterFrequencyHz { get; set; }
    public double SampleRateHz { get; set; }
}


