using System;
using RASTA.Core.Telescope;

namespace RASTA.Core.Planning
{
    public class TargetRange
    {
        public CoordinateMode Mode { get; set; }

        // Alt/Az ranges
        public double StartAzimuthDeg { get; set; }
        public double EndAzimuthDeg { get; set; }
        public double StartElevationDeg { get; set; }
        public double EndElevationDeg { get; set; }

        // RA/Dec ranges
        public double StartRightAscensionHours { get; set; }
        public double EndRightAscensionHours { get; set; }
        public double StartDeclinationDeg { get; set; }
        public double EndDeclinationDeg { get; set; }

        // Step size for sweeps
        public double StepDegrees { get; set; }

        // Optional dwell time per point
        public TimeSpan DwellTime { get; set; } = TimeSpan.FromSeconds(1);

        public override string ToString()
        {
            return Mode == CoordinateMode.AltAz
                ? $"AltAz Range: Az {StartAzimuthDeg}→{EndAzimuthDeg}, El {StartElevationDeg}→{EndElevationDeg}, Step {StepDegrees}°"
                : $"Equatorial Range: RA {StartRightAscensionHours}→{EndRightAscensionHours}h, Dec {StartDeclinationDeg}→{EndDeclinationDeg}°, Step {StepDegrees}°";
        }
    }
}
