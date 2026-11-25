using NUnit.Framework;
using RASTA.Core.Astro;
using RASTA.Processing.Calibration;

namespace RASTA.Tests.Calibration
{
    /// <summary>Cold-sky baseline picker favouring higher elevations (issue #16).</summary>
    [TestFixture]
    public class ColdSkyLocatorTests
    {
        private const double Lat = 52.5;
        private const double Lon = -1.9;
        private const double Horizon = 20.0;

        private static IEnumerable<DateTime> HoursOfDay()
        {
            var start = new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc);
            for (int h = 0; h < 24; h++)
                yield return start.AddHours(h);
        }

        [Test]
        public void EveryHour_ReturnsFourDistinctDirectionsAboveHorizon_HighestFirst()
        {
            foreach (var utc in HoursOfDay())
            {
                var result = ColdSkyLocator.FindCandidates(Lat, Lon, utc, Horizon);

                Assert.That(result, Has.Count.EqualTo(4), utc.ToString("HH:mm"));
                Assert.That(result.Select(c => c.AzimuthDeg).Distinct().Count(), Is.EqualTo(4), utc.ToString("HH:mm"));
                Assert.That(result.All(c => c.ElevationDeg > Horizon), utc.ToString("HH:mm"));
                Assert.That(result.Select(c => c.ElevationDeg), Is.Ordered.Descending, utc.ToString("HH:mm"));
            }
        }

        /// <summary>
        /// Whenever four directions with |b| ≥ 30° exist at Alt ≥ 50°, every candidate offered
        /// must be one of them - cold enough, so the lower ones lose (the 2026-11-08 failure was
        /// a cold but low Alt 45° baseline).
        /// </summary>
        [Test]
        public void ColdHighSkyAvailable_AllCandidatesAreHighAndCold()
        {
            int hoursChecked = 0;
            foreach (var utc in HoursOfDay())
            {
                int highColdDirections = Enumerable.Range(0, 24)
                    .Count(i => Enumerable.Range(0, 7).Any(j =>
                    {
                        double az = i * 15.0, el = 50.0 + j * 5.0;
                        var (ra, dec) = AstronomyUtils.HorizontalToEquatorial(az, el, utc, Lat, Lon);
                        return Math.Abs(AstronomyUtils.EquatorialToGalactic(ra, dec).bDeg) >= 30.0;
                    }));
                if (highColdDirections < 4)
                    continue;

                hoursChecked++;
                var result = ColdSkyLocator.FindCandidates(Lat, Lon, utc, Horizon);
                foreach (var c in result)
                {
                    Assert.That(c.ElevationDeg, Is.GreaterThanOrEqualTo(50.0), utc.ToString("HH:mm"));
                    Assert.That(Math.Abs(c.GalacticLatitudeDeg), Is.GreaterThanOrEqualTo(30.0), utc.ToString("HH:mm"));
                }
            }

            Assert.That(hoursChecked, Is.GreaterThan(0), "No hour had cold sky above 50° - test site/date needs changing.");
        }

        [Test]
        public void ExcludedAzimuths_AreAvoided()
        {
            var utc = new DateTime(2026, 11, 8, 22, 0, 0, DateTimeKind.Utc);
            var first = ColdSkyLocator.FindCandidates(Lat, Lon, utc, Horizon);
            var excluded = first.Select(c => c.AzimuthDeg).ToList();

            var second = ColdSkyLocator.FindCandidates(Lat, Lon, utc, Horizon, excludeAzimuthsDeg: excluded);

            Assert.That(second, Has.Count.EqualTo(4));
            foreach (var c in second)
                foreach (var az in excluded)
                {
                    double diff = Math.Abs(c.AzimuthDeg - az) % 360.0;
                    Assert.That(Math.Min(diff, 360.0 - diff), Is.GreaterThanOrEqualTo(20.0));
                }
        }

        [Test]
        public void EverythingExcluded_FallsBackRatherThanComingUpEmpty()
        {
            var utc = new DateTime(2026, 11, 8, 22, 0, 0, DateTimeKind.Utc);
            var all = Enumerable.Range(0, 36).Select(i => i * 10.0).ToList();

            var result = ColdSkyLocator.FindCandidates(Lat, Lon, utc, Horizon, excludeAzimuthsDeg: all);

            Assert.That(result, Has.Count.EqualTo(4));
        }
    }
}
