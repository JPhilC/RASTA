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
            if (range.StepDeg <= 0)
                throw new ArgumentException("StepSize must be > 0.");

            if (range.Mode == CoordinateMode.AltAz)
                return BuildAltAzSweep(range);

            return BuildEquatorialSweep(range);
        }

        private IEnumerable<TargetPoint> BuildAltAzSweep(TargetRange range)
        {
            var points = new List<TargetPoint>();

            for (double el = range.AltitudeStartDeg; el <= range.AltitudeEndDeg; el += range.StepDeg)
            {
                for (double az = range.AzimuthStartDeg; az <= range.AzimuthEndDeg; az += range.StepDeg)
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

            for (double dec = range.DecStartDeg; dec <= range.DecEndDeg; dec += range.StepDeg)
            {
                for (double ra = range.RAStartHours; ra <= range.RAEndHours; ra += DegreesToHours(range.StepDeg))
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
