using RASTA.Core.Astro;
using RASTA.Core.Capture;
using RASTA.Core.Planning;
using RASTA.Core.Telescope;

namespace RASTA.Processing.Planning
{
    public class SweepPlanResult
    {
        public bool Success { get; }
        public string? ErrorMessage { get; }
        public IReadOnlyList<TargetPoint> Points { get; }

        private SweepPlanResult(bool success, string? error, IReadOnlyList<TargetPoint> points)
        {
            Success = success;
            ErrorMessage = error;
            Points = points;
        }

        public static SweepPlanResult Ok(IReadOnlyList<TargetPoint> points)
            => new SweepPlanResult(true, null, points);

        public static SweepPlanResult Fail(string error)
            => new SweepPlanResult(false, error, Array.Empty<TargetPoint>());
    }

    public class SweepPlanner
    {
        public SweepPlanResult BuildSweep(
            CapturePlan plan,
            DateTime startTimeUtc,
            TimeSpan dwell,
            double settleTimeSeconds,
            double siteLatitudeDeg,
            double siteLongitudeDeg,
            double minElevationDeg,
            double slewRateDegPerSec)
        {
            var range = plan.Range;
            if (plan.PlanType == PlanType.Drift)
                return SweepPlanResult.Fail("Drift plans are not supported for sweeps.");
            if (range.StepDeg <= 0)
                return SweepPlanResult.Fail("Step size must be greater than zero.");

            var rawPoints = (plan.PlanType == PlanType.AltAz)
                ? BuildAltAzSweep(range)
                : BuildEquatorialSweep(range);

            var validated = new List<TargetPoint>();

            TargetPoint? previous = null;
            double accumulatedSlewSeconds = 0;
            int index = 0;

            foreach (var p in rawPoints)
            {
                // Compute slew time from previous point
                if (previous != null)
                {
                    double slewDistanceDeg = AstronomyUtils.ComputeAngularDistance(previous, p);
                    double slewSeconds = slewDistanceDeg / slewRateDegPerSec + settleTimeSeconds;
                    accumulatedSlewSeconds += slewSeconds;
                }

                // Compute arrival time
                DateTime arrivalTime =
                    startTimeUtc +
                    TimeSpan.FromTicks(dwell.Ticks * index) +
                    TimeSpan.FromSeconds(accumulatedSlewSeconds);

                // Compute elevation at arrival time
                double elDeg;

                if (p.Mode == CoordinateMode.AltAz)
                {
                    elDeg = p.ElevationDeg;
                }
                else
                {
                    var (az, el) = AstronomyUtils.EquatorialToHorizontal(
                        p.RightAscensionHours,
                        p.DeclinationDeg,
                        arrivalTime,
                        siteLatitudeDeg,
                        siteLongitudeDeg);

                    elDeg = el;
                }

                // FAIL EARLY if below horizon
                if (elDeg < minElevationDeg)
                {
                    string msg =
                        $"Sweep cancelled: point {index + 1} would be below the horizon.\n" +
                        $"Elevation = {elDeg:F1}°, limit = {minElevationDeg:F1}°.\n" +
                        $"Arrival time = {arrivalTime:HH:mm:ss} UTC.";

                    return SweepPlanResult.Fail(msg);
                }

                validated.Add(p);
                previous = p;
                index++;
            }

            return SweepPlanResult.Ok(validated);
        }


        private IEnumerable<TargetPoint> BuildAltAzSweep(TargetRange range)
        {
            var points = new List<TargetPoint>();

            for (double el = range.AltitudeStartDeg; el <= range.AltitudeEndDeg; el += range.StepDeg)
            {
                for (double az = range.AzimuthStartDeg; az <= range.AzimuthEndDeg; az += range.StepDeg)
                {
                    points.Add(TargetPoint.FromAzEl(az, el));
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
                    points.Add(TargetPoint.FromRaDec(ra, dec));
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
