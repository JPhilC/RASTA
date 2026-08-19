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
            if (range.AngularSeparationDeg <= 0)
                return SweepPlanResult.Fail("Angular separation must be greater than zero.");

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
            double separationDeg = Math.Abs(range.AngularSeparationDeg);

            foreach (double el in StepRange(range.AltitudeStartDeg, range.AltitudeEndDeg, separationDeg))
            {
                // Azimuth circles shrink toward the zenith (el=90deg) exactly like RA circles
                // shrink toward the celestial pole - correct by cos(elevation) per row so
                // azimuth points stay the same real angular distance apart everywhere in the
                // sweep, rather than bunching closer together in real sky-angle the higher the
                // row sits.
                double azStepDeg = RowStepDeg(separationDeg, el);

                foreach (double az in StepRange(range.AzimuthStartDeg, range.AzimuthEndDeg, azStepDeg))
                {
                    points.Add(TargetPoint.FromAzEl(az, el));
                }
            }

            return points;
        }

        private IEnumerable<TargetPoint> BuildEquatorialSweep(TargetRange range)
        {
            var points = new List<TargetPoint>();
            double separationDeg = Math.Abs(range.AngularSeparationDeg);

            foreach (double dec in StepRange(range.DecStartDeg, range.DecEndDeg, separationDeg))
            {
                // RA circles shrink toward the celestial poles by cos(dec) - correct the RA
                // step per row so points stay the same real angular distance apart everywhere
                // in the sweep, rather than the naive "1h = 15deg" conversion (only exact at
                // dec=0) leaving RA points crowded closer together in real sky-angle the
                // higher |dec| gets.
                double raStepHours = DegreesToHours(RowStepDeg(separationDeg, dec));

                foreach (double ra in StepRange(range.RAStartHours, range.RAEndHours, raStepHours))
                {
                    points.Add(TargetPoint.FromRaDec(ra, dec));
                }
            }

            return points;
        }

        /// <summary>
        /// The coordinate-space step (same units as <paramref name="separationDeg"/>) needed
        /// along a row at <paramref name="rowAngleDeg"/> degrees from the equator/horizon so
        /// adjacent points stay <paramref name="separationDeg"/> apart in real angle on the
        /// sphere, rather than in raw coordinate units. Floors cos(rowAngle) instead of
        /// dividing by (near) zero right at the pole/zenith - at that floor the resulting step
        /// comfortably exceeds any real sweep range, so StepRange naturally collapses that row
        /// to a single point, which is physically correct there (the row itself has shrunk to
        /// essentially a point).
        /// </summary>
        private static double RowStepDeg(double separationDeg, double rowAngleDeg)
        {
            const double minCos = 0.01; // ~89.4 deg from the equator/horizon
            double cosRow = Math.Max(Math.Abs(Math.Cos(rowAngleDeg * Math.PI / 180.0)), minCos);
            return separationDeg / cosRow;
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
