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
            if (plan.PlanType == PlanType.Drift)
                return SweepPlanResult.Fail("Drift plans are not supported for sweeps.");

            var (rawPoints, error) = BuildRawPoints(plan);
            if (error != null)
                return SweepPlanResult.Fail(error);

            return BuildSweepFromPoints(rawPoints, startTimeUtc, dwell, settleTimeSeconds,
                siteLatitudeDeg, siteLongitudeDeg, minElevationDeg, slewRateDegPerSec);
        }

        /// <summary>
        /// The "turn a plan's geometry into an unordered/unvalidated candidate point list" half
        /// of BuildSweep, extracted so PlanViewModel's map preview can get the same raw points
        /// BuildSweep itself would use even when the plan fails BuildSweepFromPoints' horizon
        /// check - showing the raw grid (colour-coded per point by the caller) is more useful for
        /// diagnosing a failing plan than just an error message. Drift plans are not handled here
        /// - callers that care (BuildSweep) check PlanType == Drift themselves first.
        /// </summary>
        public (IReadOnlyList<TargetPoint> Points, string? Error) BuildRawPoints(CapturePlan plan)
        {
            var range = plan.Range;

            if (plan.PlanType == PlanType.AltAz)
            {
                if (range.AngularSeparationDeg <= 0)
                    return (Array.Empty<TargetPoint>(), "Angular separation must be greater than zero.");
                return (BuildAltAzSweep(range).ToList(), null);
            }

            if (range.GeometryMode == SweepGeometryMode.Region)
            {
                // A region-mode plan's grid spacing/point count comes entirely from the
                // drawn polygon - see BuildRegionGrid, which already validates
                // AngularSeparationDeg and vertex count internally.
                var regionPoints = BuildRegionGrid(range.RegionVertices, range.AngularSeparationDeg);
                if (regionPoints.Count == 0)
                    return (regionPoints, "Region has too few vertices, or no grid points fell inside it - check the drawn region and angular separation.");
                return (regionPoints, null);
            }

            if (range.AngularSeparationDeg <= 0)
                return (Array.Empty<TargetPoint>(), "Angular separation must be greater than zero.");
            return (BuildEquatorialSweep(range).ToList(), null);
        }

        /// <summary>
        /// The ordering/horizon-validation half of BuildSweep, extracted so a raw point list
        /// from any source (a Range's Start/End boxes, a drawn Region's grid - see
        /// BuildRegionGrid - or, for PlanViewModel's map preview, either of those computed
        /// ahead of time) goes through the exact same greedy-elevation ordering and horizon-
        /// limit check that an actual sweep uses. Behaviourally identical to the loop that used
        /// to live directly in BuildSweep.
        /// </summary>
        public SweepPlanResult BuildSweepFromPoints(
            IReadOnlyList<TargetPoint> rawPoints,
            DateTime startTimeUtc,
            TimeSpan dwell,
            double settleTimeSeconds,
            double siteLatitudeDeg,
            double siteLongitudeDeg,
            double minElevationDeg,
            double slewRateDegPerSec)
        {
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

        /// <summary>
        /// Turns a closed loop of RA/Dec vertices (traced on the Plan view's sky map - see
        /// TargetRange.RegionVertices) into a coverage grid, the region-mode counterpart to
        /// BuildEquatorialSweep's Start/End-box grid.
        ///
        /// Unlike BuildEquatorialSweep, this does its point-in-polygon test and grid generation
        /// in a local GnomonicProjection tangent-plane centered on the vertices' own pole-safe
        /// centroid, not raw RA-hours x Dec-degrees - see GnomonicProjection's own remarks for
        /// why: near the celestial pole (very natural to trace a region near, drawing close to
        /// due-North from a mid/high-latitude site), a compact patch of real sky maps to a
        /// wildly distorted, sometimes self-intersecting shape in plain RA/Dec, which used to
        /// scatter grid points far outside the actually-drawn region and blow the bounding box
        /// out to nearly the whole 24h RA range. The tangent plane's xi/eta axes are already
        /// locally equal-scale near the tangent point, so - unlike BuildEquatorialSweep's RA
        /// axis - neither needs a separate cos(Dec)-style row correction here.
        ///
        /// Equatorial only - a region drawn on the map is captured as fixed RA/Dec (see
        /// PlanViewModel), the same way any other Equatorial plan point is.
        /// </summary>
        public static IReadOnlyList<TargetPoint> BuildRegionGrid(
            IReadOnlyList<RegionVertex> vertices,
            double angularSeparationDeg)
        {
            if (vertices.Count < 3)
                return Array.Empty<TargetPoint>();

            double separationDeg = Math.Abs(angularSeparationDeg);
            if (separationDeg <= 0)
                return Array.Empty<TargetPoint>();

            var (centerRa, centerDec) = GnomonicProjection.ComputeCentroid(
                vertices.Select(v => (v.RaHours, v.DecDeg)));
            var projection = new GnomonicProjection(centerRa, centerDec);

            var polygon = vertices.Select(v => projection.Project(v.RaHours, v.DecDeg)).ToList();

            double minXi = polygon.Min(p => p.xi);
            double maxXi = polygon.Max(p => p.xi);
            double minEta = polygon.Min(p => p.eta);
            double maxEta = polygon.Max(p => p.eta);
            double stepRad = separationDeg * Math.PI / 180.0;

            var points = new List<TargetPoint>();
            foreach (double eta in StepRange(minEta, maxEta, stepRad))
            {
                foreach (double xi in StepRange(minXi, maxXi, stepRad))
                {
                    if (IsPointInPolygon(xi, eta, polygon))
                    {
                        var (ra, dec) = projection.Unproject(xi, eta);
                        points.Add(TargetPoint.FromRaDec(ra, dec));
                    }
                }
            }

            return points;
        }

        /// <summary>
        /// Standard ray-casting point-in-polygon test: counts how many polygon edges a
        /// horizontal ray from (x, y) toward +infinity in x crosses - odd means inside.
        /// Operates directly on RA-hours/Dec-degrees; only a topological test, so the mismatched
        /// axis units don't matter.
        /// </summary>
        private static bool IsPointInPolygon(double x, double y, IReadOnlyList<(double x, double y)> polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var pi = polygon[i];
                var pj = polygon[j];

                if (((pi.y > y) != (pj.y > y)) &&
                    (x < (pj.x - pi.x) * (y - pi.y) / (pj.y - pi.y) + pi.x))
                {
                    inside = !inside;
                }
            }
            return inside;
        }
    }
}
