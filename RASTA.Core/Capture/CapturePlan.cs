using RASTA.Core.Planning;

namespace RASTA.Core.Capture
{
    public enum PlanType
    {
        Equatorial,
        AltAz,
        Drift
    }

    /// <summary>
    /// A fully-defined capture plan produced by the PlanViewModel
    /// and consumed by CaptureViewModel / CaptureRunner.
    /// </summary>
    public class CapturePlan
    {
        public string FriendlyName { get; init; } = "Untitled Plan";

        public PlanType PlanType { get; init; }

        public string SdrDeviceId { get; set; } = string.Empty;

        // Sweep definition
        public TargetRange Range { get; set; } = new TargetRange();

        public TimeSpan DwellTime { get; init; } = TimeSpan.FromSeconds(1);

        public int FilesPerPoint { get; init; } = 1;

        public double SampleRate { get; init; }
        public double CenterFrequency { get; init; }
        public int FftBins { get; init; }

        public double SettleTimeSeconds { get; init; }
        public bool TrackingEnabled { get; init; }

        public bool GoToHomeAfterCapture { get; init; } = true;

        // Drift-specific
        public double DriftDeclinationDeg { get; init; }
        public double DriftDurationMinutes { get; init; }
        public double DriftCadenceSeconds { get; init; }

        public override string ToString()
        {
            return $"{FriendlyName} ({PlanType})";
        }
    }
}
