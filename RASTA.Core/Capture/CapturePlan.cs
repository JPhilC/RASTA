using System;
using System.Collections.Generic;
using RASTA.Core.Planning;
using RASTA.Core.Capture;
using RASTA.Core.Telescope;
using System.Text.Json.Serialization;

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
    /// and consumed by ObserveViewModel / CaptureRunner.
    /// </summary>
    public class CapturePlan
    {
        public string FriendlyName { get; init; } = "Untitled Plan";

        public PlanType PlanType { get; init; }

        public string SdrDeviceId { get; set; } = string.Empty;

        // Sweep definition
        public TargetRange Range { get; set; } = new TargetRange();

        public TimeSpan DwellTime { get; init; } = TimeSpan.FromSeconds(1);

        public double SampleRate { get; init; }
        public double CenterFrequency { get; init; }
        public int FftBins { get; init; }
        public int Integrations { get; init; }
        public double Gain { get; init; }

        public double SettleTimeSeconds { get; init; }
        public bool TrackingEnabled { get; init; }

        public string OutputFolder { get; init; } = "Captures";
        public string FilePrefix { get; init; } = "rasta_";

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
