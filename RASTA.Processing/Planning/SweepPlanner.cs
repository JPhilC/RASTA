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

        /// <summary>
        /// Rough estimate (UTC) of when the whole sweep will finish, based on the planned
        /// per-point dwell time plus the slew/settle overhead computed while ordering the
        /// sweep (see BuildSweep). This is a planning-time estimate from nominal dwell/slew
        /// figures only - callers actually executing the sweep should refine it against real
        /// measured per-point timing as points complete (see CaptureViewModel.CaptureSweepAsync,
        /// which does exactly that). Null when the plan failed to build.
        /// </summary>
        public DateTime? EstimatedCompletionUtc { get; }

        private SweepPlanResult(bool success, string? error, IReadOnlyList<TargetPoint> points, DateTime? estimatedCompletionUtc)
        {
            Success = success;
            ErrorMessage = error;
            Points = points;
            EstimatedCompletionUtc = estimatedCompletionUtc;
        }

        public static SweepPlanResult Ok(IReadOnlyList<TargetPoint> points, DateTime estimatedCompletionUtc)
            => new SweepPlanResult(true, null, points, estimatedCompletionUtc);

        public static SweepPlanResult Fail(string error)
            => new SweepPlanResult(false, error, Array.Empty<TargetPoint>(), null);
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

            // Greedily order the sweep to keep the scope as high above the horizon as
            // possible throughout: at each step, visit whichever remaining point would
            // be highest in the sky at its estimated arrival time (accounting for the
            // slew from wherever the mount currently is). For AltAz plans elevation
            // doesn't depend on time, so this simply visits the highest-elevation points
            // first; for Equatorial plans it also accounts for targets rising/setting as
            // the sweep runs. This prioritises staying high over minimising total slew
            // distance - if the plan only gets partway through before running out of
            // time or hitting the horizon limit, the best-positioned targets are captured
            // first rather than whatever came next in raster order.
            var remaining = rawPoints.ToList();
            var validated = new List<TargetPoint>();

            TargetPoint? previous = null;
            double accumulatedSlewSeconds = 0;
            int index = 0;

            while (remaining.Count > 0)
            {
                TargetPoint? best = null;
                double bestElevationDeg = double.NegativeInfinity;
                double bestSlewSeconds = 0;
                DateTime bestArrivalTime = startTimeUtc;

                foreach (var candidate in remaining)
                {
                    double slewSeconds = 0;
                    if (previous != null)
                    {
                        double slewDistanceDeg = AstronomyUtils.ComputeAngularDistance(previous, candidate);
                        slewSeconds = slewDistanceDeg / slewRateDegPerSec + settleTimeSeconds;
                    }

                    DateTime arrivalTime =
                        startTimeUtc +
                        TimeSpan.FromTicks(dwell.Ticks * index) +
                        TimeSpan.FromSeconds(accumulatedSlewSeconds + slewSeconds);

                    double elDeg = ComputeElevationDeg(candidate, arrivalTime, siteLatitudeDeg, siteLongitudeDeg);

                    if (elDeg > bestElevationDeg)
                    {
                        bestElevationDeg = elDeg;
                        best = candidate;
                        bestSlewSeconds = slewSeconds;
                        bestArrivalTime = arrivalTime;
                    }
                }

                // 'best' is the highest-elevation option available at this step - if even
                // that one is below the horizon limit, every remaining candidate is too.
                if (bestElevationDeg < minElevationDeg)
                {
                    string msg =
                        $"Sweep cancelled: point {index + 1} would be below the horizon.\n" +
                        $"Best remaining elevation = {bestElevationDeg:F1}°, limit = {minElevationDeg:F1}°.\n" +
                        $"Arrival time = {bestArrivalTime:HH:mm:ss} UTC.";

                    return SweepPlanResult.Fail(msg);
                }

                accumulatedSlewSeconds += bestSlewSeconds;
                validated.Add(best!);
                remaining.Remove(best!);
                previous = best;
                index++;
            }

            // Nominal completion estimate: every point gets the full planned dwell, plus
            // the total slew/settle overhead accumulated while ordering the sweep above.
            DateTime estimatedCompletionUtc =
                startTimeUtc +
                TimeSpan.FromTicks(dwell.Ticks * validated.Count) +
                TimeSpan.FromSeconds(accumulatedSlewSeconds);

            return SweepPlanResult.Ok(validated, estimatedCompletionUtc);
        }

        private static double ComputeElevationDeg(
            TargetPoint p,
            DateTime atUtc,
            double siteLatitudeDeg,
            double siteLongitudeDeg)
        {
            if (p.Mode == CoordinateMode.AltAz)
                return p.ElevationDeg;

            var (_, elDeg) = AstronomyUtils.EquatorialToHorizontal(
                p.RightAscensionHours,
                p.DeclinationDeg,
                atUtc,
                siteLatitudeDeg,
                siteLongitudeDeg);

            return elDeg;
        }

        private IEnumerable<TargetPoint> BuildAltAzSweep(TargetRange range)
        {
            var points = new List<TargetPoint>();
            double stepDeg = Math.Abs(range.StepDeg);

            foreach (double el in StepRange(range.AltitudeStartDeg, range.AltitudeEndDeg, stepDeg))
            {
                foreach (double az in StepRange(range.AzimuthStartDeg, range.AzimuthEndDeg, stepDeg))
                {
                    points.Add(TargetPoint.FromAzEl(az, el));
                }
            }

            return points;
        }

        private IEnumerable<TargetPoint> BuildEquatorialSweep(TargetRange range)
        {
            var points = new List<TargetPoint>();
            double stepDeg = Math.Abs(range.StepDeg);
            double stepHours = DegreesToHours(stepDeg);

            foreach (double dec in StepRange(range.DecStartDeg, range.DecEndDeg, stepDeg))
            {
                foreach (double ra in StepRange(range.RAStartHours, range.RAEndHours, stepHours))
                {
                    points.Add(TargetPoint.FromRaDec(ra, dec));
                }
            }

            return points;
        }

        /// <summary>
        /// Steps from start to end (inclusive) in increments of the given positive step
        /// magnitude, automatically stepping downward when end &lt; start - so a range's
        /// start/end can be given in either order regardless of which one is numerically
        /// larger. Uses an integer step count rather than repeated floating-point addition
        /// so accumulated rounding error can't silently drop (or duplicate) the final point.
        /// </summary>
        private static IEnumerable<double> StepRange(double start, double end, double step)
        {
            if (step <= 0)
                yield break;

            double span = end - start;
            int sign = span >= 0 ? 1 : -1;
            int count = (int)Math.Round(Math.Abs(span) / step, MidpointRounding.AwayFromZero);

            for (int i = 0; i <= count; i++)
                yield return start + sign * i * step;
        }

        private static double DegreesToHours(double degrees)
        {
            return degrees / 15.0; // 360° = 24h → 1h = 15°
        }
    }
}
