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

        // ---- LSR correction (issue #21) ----------------------------------------------------
        //
        // Reference values. The last three arguments of each case are the observer's velocity
        // (km/s, ICRF/J2000 equatorial axes) relative to the Solar System barycentre, from JPL
        // Horizons (DE441): a geometric VECTORS ephemeris of the surface point
        // "g: lon, lat, 0 @ 399" with centre 500@0, fetched 2026-11-23 by
        // scripts/Get-LsrReferenceVectors.ps1 (re-run it to regenerate these lines). That
        // vector already contains the Earth's orbital motion and the observer's rotation, so
        // the expected correction needs no astronomy of RASTA's own:
        //
        //     expected = v_observer . u_target + 20 km/s * (u_apex . u_target)
        //
        // i.e. the kinematic LSR (standard solar motion, 20 km/s toward 18h, +30 deg at
        // B1900), which is what astropy gives when told to use that solar motion explicitly.
        // The sites are arbitrary round numbers. Cases cover the whole year (perihelion,
        // aphelion and the quadratures where the eccentricity term peaks), 2000-2040, both
        // hemispheres, east and west longitudes, and hour angles either side of the meridian.
        [TestCase(45.0, 10.0, "2026-01-03T22:00:00Z", 5.5, 20.0, -29.79002, -6.29241, -2.74303)]
        [TestCase(45.0, 10.0, "2026-02-14T04:30:00Z", 12.5, 60.0, -17.29970, -22.77353, -9.76624)]
        [TestCase(45.0, 10.0, "2026-04-04T20:00:00Z", 6.0, -10.0, 6.87637, -26.79281, -11.49975)]
        [TestCase(-35.0, 150.0, "2026-05-20T12:00:00Z", 17.76, -29.0, 25.28679, -14.45789, -6.12101)]
        [TestCase(20.0, -155.0, "2026-07-05T08:00:00Z", 20.0, 40.0, 28.95939, 5.86009, 2.60863)]
        [TestCase(45.0, 10.0, "2026-08-15T23:15:00Z", 20.65, 45.0, 17.74912, 21.93640, 9.39549)]
        [TestCase(65.0, 25.0, "2026-10-05T18:00:00Z", 0.7, 41.0, -6.57695, 26.75234, 11.54334)]
        [TestCase(45.0, 10.0, "2026-11-08T15:30:00Z", 21.0, 40.0, -21.55191, 19.05396, 8.21012)]
        [TestCase(-35.0, 150.0, "2026-12-21T14:00:00Z", 8.0, -45.0, -30.63171, 0.21344, 0.09305)]
        [TestCase(20.0, -155.0, "2027-03-20T10:00:00Z", 3.0, 0.0, -0.94918, -27.84899, -11.88286)]
        [TestCase(65.0, 25.0, "2027-06-10T01:00:00Z", 14.0, 75.0, 28.90366, -5.40207, -2.38117)]
        [TestCase(45.0, 10.0, "2015-09-23T03:00:00Z", 23.0, 5.0, -0.54259, 27.41553, 11.80686)]
        [TestCase(-35.0, 150.0, "2040-02-29T09:45:00Z", 10.5, -60.0, -11.11814, -25.76985, -11.15478)]
        [TestCase(45.0, 10.0, "2000-01-01T12:00:00Z", 18.064, 30.0, -29.47627, -4.91486, -2.18080)]
        public void ComputeLsrCorrectionKmPerSec_MatchesJplHorizons(
            double latDeg, double lonDeg, string utcText, double raHours, double decDeg,
            double vx, double vy, double vz)
        {
            DateTime utc = DateTime.Parse(utcText, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal);

            var u = UnitVector(raHours * 15.0, decDeg);

            // Standard solar apex precessed to J2000, as usually quoted: 18h03m50.29s, +30d00'16.8".
            var apex = UnitVector((18.0 + 3.0 / 60.0 + 50.29 / 3600.0) * 15.0, 30.0 + 16.8 / 3600.0);

            double expected = vx * u.x + vy * u.y + vz * u.z
                            + 20.0 * (apex.x * u.x + apex.y * u.y + apex.z * u.z);

            double actual = AstronomyUtils.ComputeLsrCorrectionKmPerSec(raHours, decDeg, utc, latDeg, lonDeg);

            double haDeg = AstronomyUtils.LocalSiderealTimeDegrees(utc, lonDeg) - raHours * 15.0;
            haDeg = ((haDeg + 540.0) % 360.0) - 180.0;
            TestContext.Out.WriteLine(
                $"HA {haDeg,7:F1} deg  expected {expected,8:F3}  actual {actual,8:F3}  diff {actual - expected,7:F3} km/s");

            Assert.That(actual, Is.EqualTo(expected).Within(0.1));
        }

        [Test]
        public void SolarApex_IsStandardB1900ApexPrecessedToJ2000()
        {
            // The kinematic LSR is defined by 20 km/s toward RA 18h, Dec +30 deg at equinox
            // 1900. Precess that to J2000 here (IAU 1976 angles, Meeus ch. 21) rather than
            // trusting any quoted J2000 figure.
            const double jdB1900 = 2415020.3135;
            const double jdJ2000 = 2451545.0;
            double T = (jdB1900 - jdJ2000) / 36525.0;
            double t = (jdJ2000 - jdB1900) / 36525.0;

            double arcsec = Math.PI / (180.0 * 3600.0);
            double zeta = ((2306.2181 + 1.39656 * T - 0.000139 * T * T) * t
                          + (0.30188 - 0.000344 * T) * t * t + 0.017998 * t * t * t) * arcsec;
            double z = ((2306.2181 + 1.39656 * T - 0.000139 * T * T) * t
                       + (1.09468 + 0.000066 * T) * t * t + 0.018203 * t * t * t) * arcsec;
            double theta = ((2004.3109 - 0.85330 * T - 0.000217 * T * T) * t
                           - (0.42665 + 0.000217 * T) * t * t - 0.041833 * t * t * t) * arcsec;

            double ra0 = 270.0 * Math.PI / 180.0;
            double dec0 = 30.0 * Math.PI / 180.0;

            double a = Math.Cos(dec0) * Math.Sin(ra0 + zeta);
            double b = Math.Cos(theta) * Math.Cos(dec0) * Math.Cos(ra0 + zeta) - Math.Sin(theta) * Math.Sin(dec0);
            double c = Math.Sin(theta) * Math.Cos(dec0) * Math.Cos(ra0 + zeta) + Math.Cos(theta) * Math.Sin(dec0);

            double raDeg = (Math.Atan2(a, b) + z) * 180.0 / Math.PI;
            if (raDeg < 0) raDeg += 360.0;
            double decDeg = Math.Asin(c) * 180.0 / Math.PI;

            TestContext.Out.WriteLine($"B1900 18h/+30 precessed to J2000: RA {raDeg:F5} deg, Dec {decDeg:F5} deg");

            // 0.002 deg of apex error is 0.0007 km/s.
            Assert.That(AstronomyUtils.SolarApexRaDegJ2000, Is.EqualTo(raDeg).Within(0.002));
            Assert.That(AstronomyUtils.SolarApexDecDegJ2000, Is.EqualTo(decDeg).Within(0.002));
        }

        [Test]
        public void ComputeLsrCorrectionKmPerSec_DiurnalTerm_IsPositiveTowardTheEast()
        {
            // The observer is carried east by the Earth's rotation, i.e. toward a target that
            // is still rising (hour angle < 0), so the correction for a target on the celestial
            // equator 6h east of the meridian must exceed the one for the same target 6h west
            // of it by twice the rotation speed at this latitude - once the orbital and
            // solar-motion terms, which don't depend on the site, are differenced away by
            // moving the site rather than the target or the time.
            const double latDeg = 40.0;
            const double raHours = 6.0;
            DateTime utc = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

            // Longitudes that put this RA at hour angle -6h and +6h at this instant.
            double gmstDeg = AstronomyUtils.LocalSiderealTimeDegrees(utc, 0.0);
            double lonEastOfMeridian = raHours * 15.0 - 90.0 - gmstDeg;
            double lonWestOfMeridian = raHours * 15.0 + 90.0 - gmstDeg;

            double rising = AstronomyUtils.ComputeLsrCorrectionKmPerSec(raHours, 0.0, utc, latDeg, lonEastOfMeridian);
            double setting = AstronomyUtils.ComputeLsrCorrectionKmPerSec(raHours, 0.0, utc, latDeg, lonWestOfMeridian);

            double expected = 2.0 * 0.4651 * Math.Cos(latDeg * Math.PI / 180.0);
            Assert.That(rising - setting, Is.EqualTo(expected).Within(0.005));
        }

        private static (double x, double y, double z) UnitVector(double raDeg, double decDeg)
        {
            double ra = raDeg * Math.PI / 180.0;
            double dec = decDeg * Math.PI / 180.0;
            return (Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec));
        }
    }
}
