using NUnit.Framework;
using RASTA.Core.Astro;
using RASTA.Core.Storage;
using RASTA.Core.Telescope;

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
            // 1900. Precess that to J2000 with AstronomyUtils.Precess (itself checked against
            // Meeus below) rather than trusting any quoted J2000 figure.
            const double jdB1900 = 2415020.3135;
            var (raHours, decDeg) = AstronomyUtils.Precess(18.0, 30.0, jdB1900, AstronomyUtils.JdJ2000);
            double raDeg = raHours * 15.0;

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

        // ---- Precession / mount epoch (issue #38) -----------------------------------------

        [Test]
        public void Precess_MatchesMeeusExample21b()
        {
            // Meeus, Astronomical Algorithms (2nd ed.), example 21.b: theta Persei, J2000 mean
            // position with proper motion applied, precessed to 2028 Nov 13.19 TD
            // (JD 2462088.69): RA 2h46m11.331s, Dec +49d20'54.54".
            var (raHours, decDeg) = AstronomyUtils.Precess(
                41.054063 / 15.0, 49.227750, AstronomyUtils.JdJ2000, 2462088.69);

            double expectedRaDeg = (2.0 + 46.0 / 60.0 + 11.331 / 3600.0) * 15.0;
            double expectedDecDeg = 49.0 + 20.0 / 60.0 + 54.54 / 3600.0;

            Assert.That(raHours * 15.0, Is.EqualTo(expectedRaDeg).Within(2e-5));
            Assert.That(decDeg, Is.EqualTo(expectedDecDeg).Within(2e-5));
        }

        [TestCase(EquatorialCoordinateSystem.Topocentric)]
        [TestCase(EquatorialCoordinateSystem.J2050)]
        [TestCase(EquatorialCoordinateSystem.B1950)]
        public void FromJ2000_ThenToJ2000_RoundTrips(EquatorialCoordinateSystem system)
        {
            foreach (var (ra, dec) in new[] { (0.5, 10.0), (12.0, -45.0), (20.0, 88.5), (23.9, -89.0) })
            {
                var (mountRa, mountDec) = AstronomyUtils.FromJ2000(ra, dec, system, Utc);
                var (ra2, dec2) = AstronomyUtils.ToJ2000(mountRa, mountDec, system, Utc);

                double raDiffHours = Math.Abs(ra2 - ra);
                raDiffHours = Math.Min(raDiffHours, 24.0 - raDiffHours);

                Assert.That(dec2, Is.EqualTo(dec).Within(1e-7));
                Assert.That(raDiffHours * 15.0 * Math.Cos(dec * Math.PI / 180.0), Is.LessThan(1e-7));
            }
        }

        [Test]
        public void ToJ2000_J2000System_IsIdentity()
        {
            var (ra, dec) = AstronomyUtils.ToJ2000(5.5, 22.0, EquatorialCoordinateSystem.J2000, Utc);
            Assert.That(ra, Is.EqualTo(5.5));
            Assert.That(dec, Is.EqualTo(22.0));
        }

        [Test]
        public void FromJ2000_Topocentric_In2026_MovesAboutPointThreeSevenDegrees()
        {
            // On the equinoctial colure (RA 0h, Dec 0) general precession is almost all in RA:
            // 26.85 years * ~46.1"/yr (m + n*tan(dec) at dec 0, RA 0) = ~0.344 deg in RA, plus
            // ~20"/yr * 26.85 = ~0.149 deg in Dec.
            var (ra, dec) = AstronomyUtils.FromJ2000(0.0, 0.0, EquatorialCoordinateSystem.Topocentric, Utc);
            double sepDeg = Math.Sqrt(Math.Pow(ra * 15.0, 2) + dec * dec);
            Assert.That(sepDeg, Is.EqualTo(0.375).Within(0.01));
        }

        [Test]
        public void ToJ2000_Other_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                AstronomyUtils.ToJ2000(1.0, 1.0, EquatorialCoordinateSystem.Other, Utc));
        }

        [Test]
        public void FitsLsrCorrection_OfDateEquinox_MatchesJ2000()
        {
            // On the ecliptic 90 deg from the Earth's November velocity, where precession (along the
            // ecliptic) moves the target most nearly along that velocity.
            const double raHoursJ2000 = 3.0, decJ2000 = 17.0;
            var (raOfDate, decOfDate) = AstronomyUtils.PrecessJ2000ToDate(raHoursJ2000, decJ2000, Utc);

            var j2000 = new FitsFileMetaData
            {
                ObservationDate = Utc, SiteLatitudeDeg = 52.0, SiteLongitudeDeg = -1.5,
                RaDeg = raHoursJ2000 * 15.0, DecDeg = decJ2000, Equinox = 2000.0,
            };
            var ofDate = new FitsFileMetaData
            {
                ObservationDate = Utc, SiteLatitudeDeg = 52.0, SiteLongitudeDeg = -1.5,
                RaDeg = raOfDate * 15.0, DecDeg = decOfDate, Equinox = AstronomyUtils.JulianEpoch(Utc),
            };
            var legacy = new FitsFileMetaData
            {
                ObservationDate = Utc, SiteLatitudeDeg = 52.0, SiteLongitudeDeg = -1.5,
                RaDeg = raHoursJ2000 * 15.0, DecDeg = decJ2000, Equinox = null,
            };

            double expected = j2000.ComputeLsrCorrectionKmPerSec();
            Assert.That(ofDate.ComputeLsrCorrectionKmPerSec(), Is.EqualTo(expected).Within(1e-4));
            Assert.That(legacy.ComputeLsrCorrectionKmPerSec(), Is.EqualTo(expected).Within(1e-12));

            // ...and treating the of-date position as J2000 is the error issue #38 is about.
            var wrong = new FitsFileMetaData
            {
                ObservationDate = Utc, SiteLatitudeDeg = 52.0, SiteLongitudeDeg = -1.5,
                RaDeg = raOfDate * 15.0, DecDeg = decOfDate, Equinox = 2000.0,
            };
            Assert.That(Math.Abs(wrong.ComputeLsrCorrectionKmPerSec() - expected), Is.GreaterThan(0.1));
        }

        [Test]
        public void FitsLsrCorrection_AltAzCapture_UsesJ2000()
        {
            const double raHoursJ2000 = 3.5, decJ2000 = -10.0;
            var (az, alt) = AstronomyUtils.EquatorialToHorizontal(raHoursJ2000, decJ2000, Utc, 52.0, -1.5);

            var altAz = new FitsFileMetaData
            {
                ObservationDate = Utc, SiteLatitudeDeg = 52.0, SiteLongitudeDeg = -1.5,
                AzDeg = az, AltDeg = alt,
            };

            double expected = AstronomyUtils.ComputeLsrCorrectionKmPerSec(raHoursJ2000, decJ2000, Utc, 52.0, -1.5);
            Assert.That(altAz.ComputeLsrCorrectionKmPerSec(), Is.EqualTo(expected).Within(1e-6));
        }

        private static (double x, double y, double z) UnitVector(double raDeg, double decDeg)
        {
            double ra = raDeg * Math.PI / 180.0;
            double dec = decDeg * Math.PI / 180.0;
            return (Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec));
        }
    }
}
