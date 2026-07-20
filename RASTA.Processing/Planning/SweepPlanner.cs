using RASTA.Core.Capture;
using RASTA.Core.Planning;
using RASTA.Core.Telescope;
using System;
using System.Collections.Generic;

namespace RASTA.Processing.Planning
{
    public class SweepPlanner
    {
        public IEnumerable<TargetPoint> BuildSweep(TargetRange range)
        {
            if (range.StepDegrees <= 0)
                throw new ArgumentException("StepDegrees must be > 0.");

            if (range.Mode == CoordinateMode.AltAz)
                return BuildAltAzSweep(range);

            return BuildEquatorialSweep(range);
        }

        private IEnumerable<TargetPoint> BuildAltAzSweep(TargetRange range)
        {
            var points = new List<TargetPoint>();

            for (double el = range.StartElevationDeg; el <= range.EndElevationDeg; el += range.StepDegrees)
            {
                for (double az = range.StartAzimuthDeg; az <= range.EndAzimuthDeg; az += range.StepDegrees)
                {
                    points.Add(new TargetPoint
                    {
                        Mode = CoordinateMode.AltAz,
                        AzimuthDeg = az,
                        ElevationDeg = el
                    });
                }
            }

            return points;
        }

        private IEnumerable<TargetPoint> BuildEquatorialSweep(TargetRange range)
        {
            var points = new List<TargetPoint>();

            for (double dec = range.StartDeclinationDeg; dec <= range.EndDeclinationDeg; dec += range.StepDegrees)
            {
                for (double ra = range.StartRightAscensionHours; ra <= range.EndRightAscensionHours; ra += DegreesToHours(range.StepDegrees))
                {
                    points.Add(new TargetPoint
                    {
                        Mode = CoordinateMode.Equatorial,
                        RightAscensionHours = ra,
                        DeclinationDeg = dec
                    });
                }
            }

            return points;
        }

        private static double DegreesToHours(double degrees)
        {
            return degrees / 15.0; // 360° = 24h → 1h = 15°
        }
    }
}
