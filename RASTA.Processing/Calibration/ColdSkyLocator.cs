using RASTA.Core.Astro;
using RASTA.Core.Calibration;

namespace RASTA.Processing.Calibration
{
    /// <summary>
    /// Computes candidate "cold sky" pointings for a new calibration's baseline capture -
    /// positions clear of the Galactic plane (where HI emission is strong, i.e. not "cold")
    /// and above the horizon, spread widely apart so the user has a good chance of one being
    /// conveniently unobstructed. Pure/static, like AstronomyUtils, so it carries no
    /// UI/hardware dependency (see CLAUDE.md's RASTA.Processing layering rule) - the caller
    /// (PrepareViewModel) is responsible for presenting the choice and driving the slew.
    /// </summary>
    public static class ColdSkyLocator
    {
        private const double AzimuthStepDeg = 15.0;
        private static readonly double[] ElevationOffsetsDeg = { 15.0, 35.0, 55.0 };
        private const double MaxElevationDeg = 80.0;

        // Tried in order; the first threshold whose surviving pool has enough candidates
        // wins. 0 is always tried last as a guaranteed fallback so this never comes up empty
        // for a sane horizon limit, even at a site/time where nothing is very "cold".
        private static readonly double[] GalacticLatitudeThresholdsDeg = { 40.0, 30.0, 20.0, 10.0, 0.0 };

        // How close (in azimuth) a fresh candidate has to steer clear of a direction the
        // caller wants excluded (see excludeAzimuthsDeg below) - wide, since a real-world
        // obstruction like a building or tree blocks a range of azimuths, not one exact
        // degree.
        private const double ExclusionRadiusDeg = 20.0;

        private sealed record Candidate(ColdSkyCandidate Point, double AbsGalacticLatitudeDeg);

        /// <param name="excludeAzimuthsDeg">
        /// Azimuths to steer clear of (within ExclusionRadiusDeg) - directions already
        /// offered/rejected in this calibration attempt (e.g. blocked by a building), so a
        /// "Recalculate" or "try another position" request doesn't just re-suggest the same
        /// spot. Silently ignored if honouring it would leave fewer than <paramref
        /// name="count"/> candidates, same "never come up empty" fallback philosophy as the
        /// Galactic latitude threshold search below.
        /// </param>
        public static IReadOnlyList<ColdSkyCandidate> FindCandidates(
            double siteLatDeg,
            double siteLonDeg,
            DateTime utcNow,
            double horizonLimitDeg,
            int count = 4,
            IReadOnlyCollection<double>? excludeAzimuthsDeg = null)
        {
            var elevations = ElevationOffsetsDeg
                .Select(offset => Math.Min(horizonLimitDeg + offset, MaxElevationDeg))
                .Where(el => el > horizonLimitDeg && el <= 85.0)
                .Distinct()
                .ToList();

            var pool = new List<Candidate>();
            for (double az = 0; az < 360.0; az += AzimuthStepDeg)
            {
                foreach (var el in elevations)
                {
                    var (raHours, decDeg) = AstronomyUtils.HorizontalToEquatorial(az, el, utcNow, siteLatDeg, siteLonDeg);
                    var (lDeg, bDeg) = AstronomyUtils.EquatorialToGalactic(raHours, decDeg);

                    pool.Add(new Candidate(
                        new ColdSkyCandidate(az, el, raHours, decDeg, lDeg, bDeg),
                        Math.Abs(bDeg)));
                }
            }

            if (excludeAzimuthsDeg is { Count: > 0 })
            {
                var withExclusions = pool
                    .Where(c => excludeAzimuthsDeg.All(az => CircularAzimuthDistanceDeg(c.Point.AzimuthDeg, az) >= ExclusionRadiusDeg))
                    .ToList();

                if (withExclusions.Count >= count)
                    pool = withExclusions;
            }

            // Pick the coldest threshold that still leaves enough candidates to choose from.
            List<Candidate> filtered = pool;
            foreach (var threshold in GalacticLatitudeThresholdsDeg)
            {
                var candidates = pool.Where(c => c.AbsGalacticLatitudeDeg >= threshold).ToList();
                if (candidates.Count >= count || threshold == GalacticLatitudeThresholdsDeg[^1])
                {
                    filtered = candidates;
                    break;
                }
            }

            return GreedySelectWidelySpaced(filtered, count)
                .OrderBy(c => c.Point.AzimuthDeg)
                .Select(c => c.Point)
                .ToList();
        }

        /// <summary>
        /// Greedily selects up to <paramref name="count"/> candidates: starts with the
        /// coldest (highest |b|), then repeatedly adds whichever remaining candidate
        /// maximizes the minimum circular-azimuth separation from what's already picked
        /// (ties broken by higher |b|) - this is what makes the final set "widely spaced".
        /// </summary>
        private static List<Candidate> GreedySelectWidelySpaced(List<Candidate> pool, int count)
        {
            var remaining = pool.OrderByDescending(c => c.AbsGalacticLatitudeDeg).ToList();
            var selected = new List<Candidate>();

            if (remaining.Count == 0)
                return selected;

            selected.Add(remaining[0]);
            remaining.RemoveAt(0);

            while (selected.Count < count && remaining.Count > 0)
            {
                var best = remaining
                    .Select(c => (Candidate: c, MinSeparation: selected.Min(s => CircularAzimuthDistanceDeg(c.Point.AzimuthDeg, s.Point.AzimuthDeg))))
                    .OrderByDescending(t => t.MinSeparation)
                    .ThenByDescending(t => t.Candidate.AbsGalacticLatitudeDeg)
                    .First();

                selected.Add(best.Candidate);
                remaining.Remove(best.Candidate);
            }

            return selected;
        }

        private static double CircularAzimuthDistanceDeg(double a, double b)
        {
            double diff = Math.Abs(a - b) % 360.0;
            return diff > 180.0 ? 360.0 - diff : diff;
        }
    }
}
