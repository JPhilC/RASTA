using RASTA.Core.Astro;
using RASTA.Core.Calibration;

namespace RASTA.Processing.Calibration
{
    /// <summary>
    /// Computes candidate "cold sky" pointings for a new calibration's baseline capture -
    /// positions clear of the Galactic plane (where HI emission is strong, i.e. not "cold"),
    /// as high above the horizon as the sky allows, and spread apart in azimuth so the user
    /// has a good chance of one being conveniently unobstructed. Pure/static, like
    /// AstronomyUtils, so it carries no UI/hardware dependency (see CLAUDE.md's
    /// RASTA.Processing layering rule) - the caller (PrepareViewModel) is responsible for
    /// presenting the choice and driving the slew.
    /// </summary>
    /// <remarks>
    /// Elevation matters as well as Galactic latitude (issue #16): on 2026-11-08 a baseline at
    /// Alt 45° carried a ~30-spur RFI comb and extra ground spillover while pointings at
    /// Alt 48-70° elsewhere were almost clean. Higher pointings pick up less of both, so
    /// candidates are chosen through <see cref="SelectionTiers"/>, which demands a high
    /// minimum elevation first and relaxes it only when too few directions qualify.
    /// </remarks>
    public static class ColdSkyLocator
    {
        private const double AzimuthStepDeg = 15.0;
        private const double ElevationStepDeg = 5.0;
        private const double MaxElevationDeg = 80.0;

        /// <summary>
        /// (minimum |b|, minimum elevation) pairs, tried in order; the first whose surviving
        /// pool covers at least <c>count</c> distinct azimuths wins. |b| ≥ 30° counts as cold
        /// enough that elevation takes priority (50° → 40° → 30° → anything above the horizon
        /// limit are all tried before accepting |b| below 30°); below that, |b| takes priority
        /// again. The last tier (0, 0) accepts everything above the horizon limit, so this never
        /// comes up empty for a sane horizon limit, even at a site/time where nothing is very
        /// "cold". A minimum elevation at or below the horizon limit just means "no extra
        /// constraint".
        /// </summary>
        public static readonly IReadOnlyList<(double MinAbsGalacticLatitudeDeg, double MinElevationDeg)> SelectionTiers =
        [
            (40, 50), (30, 50),
            (40, 40), (30, 40),
            (40, 30), (30, 30),
            (40, 0), (30, 0),
            (20, 50), (20, 40), (20, 30), (20, 0),
            (10, 50), (10, 40), (10, 30), (10, 0),
            (0, 50), (0, 40), (0, 30), (0, 0),
        ];

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
        /// tier search below.
        /// </param>
        /// <returns>The chosen candidates, highest elevation first.</returns>
        public static IReadOnlyList<ColdSkyCandidate> FindCandidates(
            double siteLatDeg,
            double siteLonDeg,
            DateTime utcNow,
            double horizonLimitDeg,
            int count = 4,
            IReadOnlyCollection<double>? excludeAzimuthsDeg = null)
        {
            var elevations = new List<double>();
            for (double el = horizonLimitDeg + ElevationStepDeg; el <= MaxElevationDeg + 1e-9; el += ElevationStepDeg)
                elevations.Add(el);
            if (elevations.Count == 0 && horizonLimitDeg < 85.0)
                elevations.Add((horizonLimitDeg + 85.0) / 2.0);

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

                if (DistinctAzimuths(withExclusions) >= count)
                    pool = withExclusions;
            }

            // The first tier that still leaves enough to choose from. Distinct azimuths, not
            // raw points: several elevations share each azimuth, and the user needs genuinely
            // different directions to choose between.
            List<Candidate> filtered = pool;
            foreach (var (minB, minEl) in SelectionTiers)
            {
                var candidates = pool
                    .Where(c => c.AbsGalacticLatitudeDeg >= minB && c.Point.ElevationDeg >= minEl)
                    .ToList();
                if (DistinctAzimuths(candidates) >= count)
                {
                    filtered = candidates;
                    break;
                }
            }

            return GreedySelectWidelySpaced(filtered, count)
                .OrderByDescending(c => c.Point.ElevationDeg)
                .ThenBy(c => c.Point.AzimuthDeg)
                .Select(c => c.Point)
                .ToList();
        }

        private static int DistinctAzimuths(IEnumerable<Candidate> candidates) =>
            candidates.Select(c => c.Point.AzimuthDeg).Distinct().Count();

        /// <summary>
        /// Greedily selects up to <paramref name="count"/> candidates: starts with the highest
        /// (ties broken by higher |b|), then repeatedly adds whichever remaining candidate
        /// maximizes the minimum circular-azimuth separation from what's already picked (ties
        /// broken by higher elevation, then higher |b|) - widely spaced in direction, but the
        /// highest qualifying point in each.
        /// </summary>
        private static List<Candidate> GreedySelectWidelySpaced(List<Candidate> pool, int count)
        {
            var remaining = pool
                .OrderByDescending(c => c.Point.ElevationDeg)
                .ThenByDescending(c => c.AbsGalacticLatitudeDeg)
                .ToList();
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
                    .ThenByDescending(t => t.Candidate.Point.ElevationDeg)
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
