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

        /// <summary>
        /// Non-fatal, success-path message - currently only set when one or more points at the
        /// tail of the sweep were dropped because they (and every other point still remaining at
        /// that point in the schedule - see BuildSweepFromPoints' horizon-skip handling) never
        /// clear the horizon limit for their own dwell. Null whenever nothing was skipped.
        /// </summary>
        public string? Warning { get; }

        /// <summary>How many of the plan's points were dropped for the reason described by
        /// Warning. 0 when nothing was skipped.</summary>
        public int SkippedPointCount { get; }

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

        private SweepPlanResult(bool success, string? error, string? warning, int skippedPointCount,
            IReadOnlyList<TargetPoint> points, DateTime? estimatedCompletionUtc)
        {
            Success = success;
            ErrorMessage = error;
            Warning = warning;
            SkippedPointCount = skippedPointCount;
            Points = points;
            EstimatedCompletionUtc = estimatedCompletionUtc;
        }

        public static SweepPlanResult Ok(IReadOnlyList<TargetPoint> points, DateTime estimatedCompletionUtc,
            int skippedPointCount = 0, string? warning = null)
            => new SweepPlanResult(true, null, warning, skippedPointCount, points, estimatedCompletionUtc);

        public static SweepPlanResult Fail(string error)
            => new SweepPlanResult(false, error, null, 0, Array.Empty<TargetPoint>(), null);
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
            // Greedily order the sweep by urgency, not raw elevation: at each step, visit
            // whichever remaining point is closest to dropping below the horizon limit at its
            // estimated arrival time (accounting for slew from wherever the mount currently is),
            // working through the sweep in that order - effectively chasing the setting wave
            // from most-imminent target to least, against the sky's own apparent east-to-west
            // drift, rather than simply visiting whatever happens to be highest right now. A
            // target sitting comfortably near culmination can safely wait; one about to set
            // can't, even if something else is currently a few degrees higher in the sky - the
            // old "highest elevation first" rule could starve a slowly-setting target behind a
            // string of higher-but-not-urgent ones and lose it before its turn came round.
            // AstronomyUtils.SecondsUntilElevationDropsBelow does the actual urgency estimate
            // for an Equatorial point (closed-form from the elevation identity, hemisphere-
            // agnostic - a southern site's negative latitude needs no special-casing); an AltAz
            // point's elevation is time-invariant (no setting to estimate), and a circumpolar
            // Equatorial point never crosses the limit at this latitude/declination either, so
            // both are treated as infinitely non-urgent and only then broken by elevation - the
            // same "stay as high as possible" tie-break the old rule always applied.
            var remaining = rawPoints.ToList();
            var validated = new List<TargetPoint>();
            int skippedCount = 0;

            TargetPoint? previous = null;
            double accumulatedSlewSeconds = 0;
            int index = 0;

            while (remaining.Count > 0)
            {
                TargetPoint? best = null;
                double bestUrgencySeconds = double.PositiveInfinity;
                double bestElevationDeg = double.NegativeInfinity;
                double bestSlewSeconds = 0;
                bool anyValid = false;

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

                    double elAtArrival = ComputeElevationDeg(candidate, arrivalTime, siteLatitudeDeg, siteLongitudeDeg);
                    if (elAtArrival < minElevationDeg)
                        continue; // already below the limit at this arrival time - not a candidate this round

                    double urgencySeconds = candidate.Mode == CoordinateMode.AltAz
                        ? double.PositiveInfinity
                        : AstronomyUtils.SecondsUntilElevationDropsBelow(
                            candidate.RightAscensionHours, candidate.DeclinationDeg, arrivalTime,
                            siteLatitudeDeg, siteLongitudeDeg, minElevationDeg);

                    // Must stay above the limit for the candidate's whole dwell, not just the
                    // instant of arrival - otherwise a setting target could start a capture that
                    // runs (partly) below the limit. Elevation only ever declines monotonically
                    // from here to the next horizon crossing, so this single comparison is
                    // exactly equivalent to separately checking the elevation at dwell's end.
                    if (urgencySeconds < dwell.TotalSeconds)
                        continue;

                    anyValid = true;

                    bool better = urgencySeconds < bestUrgencySeconds ||
                        (urgencySeconds == bestUrgencySeconds && elAtArrival > bestElevationDeg);

                    if (better)
                    {
                        bestUrgencySeconds = urgencySeconds;
                        bestElevationDeg = elAtArrival;
                        best = candidate;
                        bestSlewSeconds = slewSeconds;
                    }
                }

                // No remaining candidate can be captured next (each was individually checked
                // above) - nothing further can be scheduled from here regardless of order.
                // Rather than cancelling the whole plan, stop here and report the remainder as
                // skipped - whatever was already validated above still runs.
                if (!anyValid)
                {
                    skippedCount = remaining.Count;
                    break;
                }

                accumulatedSlewSeconds += bestSlewSeconds;
                validated.Add(best!);
                remaining.Remove(best!);
                previous = best;
                index++;
            }

            if (validated.Count == 0)
            {
                return SweepPlanResult.Fail(
                    $"No points are above the horizon limit ({minElevationDeg:F1}°) at the selected start time " +
                    $"({startTimeUtc:HH:mm:ss} UTC).");
            }

            // Nominal completion estimate: every point gets the full planned dwell, plus
            // the total slew/settle overhead accumulated while ordering the sweep above.
            DateTime estimatedCompletionUtc =
                startTimeUtc +
                TimeSpan.FromTicks(dwell.Ticks * validated.Count) +
                TimeSpan.FromSeconds(accumulatedSlewSeconds);

            string? warning = skippedCount > 0
                ? $"{skippedCount} point(s) will be skipped - below the horizon limit ({minElevationDeg:F1}°) for their whole dwell."
                : null;

            return SweepPlanResult.Ok(validated, estimatedCompletionUtc, skippedCount, warning);
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
