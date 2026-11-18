using NUnit.Framework;
using RASTA.Core.Astro;

namespace RASTA.Tests.Astro
{
    [TestFixture]
    public class AstronomyUtilsTests
    {
        private static readonly DateTime Utc = new(2026, 11, 8, 15, 30, 0, DateTimeKind.Utc);

        [TestCase(21.0, 40.0, 52.0, -1.5)]
        [TestCase(3.5, -10.0, 52.0, -1.5)]
        [TestCase(12.0, 75.0, 52.0, -1.5)]
        [TestCase(18.0, 20.0, -33.9, 151.2)]
        public void EquatorialToHorizontal_RoundTripsThroughHorizontalToEquatorial(
            double raHours, double decDeg, double latDeg, double lonDeg)
        {
            var (az, el) = AstronomyUtils.EquatorialToHorizontal(raHours, decDeg, Utc, latDeg, lonDeg);
            var (ra2, dec2) = AstronomyUtils.HorizontalToEquatorial(az, el, Utc, latDeg, lonDeg);

            double raDiffHours = Math.Abs(ra2 - raHours);
            raDiffHours = Math.Min(raDiffHours, 24.0 - raDiffHours);

            Assert.That(dec2, Is.EqualTo(decDeg).Within(1e-6));
            Assert.That(raDiffHours, Is.LessThan(1e-6));
        }

        [Test]
        public void EquatorialToGalactic_GalacticCentre_IsNearOrigin()
        {
            // Sgr A*: RA 17h45m40s, Dec -29d00'28" (J2000).
            var (l, b) = AstronomyUtils.EquatorialToGalactic(17.7611, -29.0078);

            double lFromZero = Math.Min(l, 360.0 - l);
            Assert.That(lFromZero, Is.LessThan(0.2));
            Assert.That(Math.Abs(b), Is.LessThan(0.2));
        }

        [Test]
        public void EquatorialToGalactic_NorthGalacticPole_IsLatitude90()
        {
            // NGP: RA 12h51m26s, Dec +27d07'42" (J2000).
            var (_, b) = AstronomyUtils.EquatorialToGalactic(12.8572, 27.1283);

            Assert.That(b, Is.GreaterThan(89.8));
        }
    }
}
