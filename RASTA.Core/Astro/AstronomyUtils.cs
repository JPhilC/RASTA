using RASTA.Core.Capture;
using RASTA.Core.Telescope;
using System;
using System.Collections.Generic;
using System.Text;

namespace RASTA.Core.Astro
{
    public static class AstronomyUtils
    {

        public static (double azDeg, double elDeg) EquatorialToHorizontal(
            double raHours,
            double decDeg,
            DateTime utc,
            double latDeg,
            double lonDeg)
        {
            // 1. Convert RA hours → degrees
            double raDeg = raHours * 15.0;

            // 2. Compute Local Sidereal Time
            double lstDeg = AstronomyUtils.LocalSiderealTimeDegrees(utc, lonDeg);

            // 3. Hour angle
            double haDeg = lstDeg - raDeg;

            // 4. Convert to radians
            double ha = haDeg * Math.PI / 180.0;
            double dec = decDeg * Math.PI / 180.0;
            double lat = latDeg * Math.PI / 180.0;

            // 5. Elevation
            double sinEl = Math.Sin(dec) * Math.Sin(lat) +
                           Math.Cos(dec) * Math.Cos(lat) * Math.Cos(ha);

            double el = Math.Asin(sinEl);

            // 6. Azimuth
            double cosAz = (Math.Sin(dec) - Math.Sin(el) * Math.Sin(lat)) /
                           (Math.Cos(el) * Math.Cos(lat));

            double az = Math.Acos(cosAz);

            // Convert back to degrees
            return (az * 180.0 / Math.PI, el * 180.0 / Math.PI);
        }

        /// <summary>
        /// Inverse of EquatorialToHorizontal: recovers RA/Dec from an observed Az/Alt at
        /// a given time and site. Needed because AltAz-mode captures only store Az/Alt in
        /// FitsFileMetaData - this reconstructs the RA/Dec needed for things like the LSR
        /// correction below.
        /// </summary>
        public static (double raHours, double decDeg) HorizontalToEquatorial(
            double azDeg,
            double elDeg,
            DateTime utc,
            double latDeg,
            double lonDeg)
        {
            double az = azDeg * Math.PI / 180.0;
            double el = elDeg * Math.PI / 180.0;
            double lat = latDeg * Math.PI / 180.0;

            double sinDec = Math.Sin(el) * Math.Sin(lat) + Math.Cos(el) * Math.Cos(lat) * Math.Cos(az);
            double dec = Math.Asin(sinDec);

            // atan2 (not acos, unlike EquatorialToHorizontal above) so the hour angle
            // comes out over the full -180..+180 range instead of folding east/west.
            double sinHa = -Math.Sin(az) * Math.Cos(el);
            double cosHa = (Math.Sin(el) - Math.Sin(lat) * sinDec) / (Math.Cos(lat) * Math.Cos(dec));
            double ha = Math.Atan2(sinHa, cosHa);

            double lstDeg = LocalSiderealTimeDegrees(utc, lonDeg);
            double raDeg = lstDeg - (ha * 180.0 / Math.PI);
            raDeg %= 360.0;
            if (raDeg < 0) raDeg += 360.0;

            return (raDeg / 15.0, dec * 180.0 / Math.PI);
        }

        /// <summary>
        /// Approximate correction (km/s) to ADD to a topocentric radio-convention radial
        /// velocity to convert it to the Local Standard of Rest (LSR) frame:
        /// <c>v_LSR = v_topocentric + ComputeLsrCorrectionKmPerSec(...)</c>.
        ///
        /// Sums the projection, onto the line of sight to the target, of three velocity
        /// vectors:
        ///   1. Earth's orbital motion around the Sun (topocentric -> heliocentric) -
        ///      by far the largest term, up to ~30 km/s.
        ///   2. Earth's rotation / the observer's diurnal motion - usually the smallest,
        ///      under ~0.5 km/s.
        ///   3. The Sun's own "standard solar motion" relative to the LSR (heliocentric
        ///      -> LSR) - a fixed 20 km/s toward a fixed apex, not time-dependent.
        ///
        /// This is the same style of low-precision analytic formula used throughout
        /// amateur radio astronomy (Earth's orbit treated as circular and coplanar with
        /// the ecliptic, no nutation/aberration) - good to a few tenths of a km/s, well
        /// within a single FFT channel's width for typical HI setups. It is not
        /// JPL-ephemeris precision, and it is the conventional "kinematic" LSR (fixed
        /// standard solar motion), not the newer IAU "dynamical" LSR definition.
        /// </summary>
        public static double ComputeLsrCorrectionKmPerSec(
            double raHours,
            double decDeg,
            DateTime utc,
            double latDeg,
            double lonDeg)
        {
            if (utc.Kind != DateTimeKind.Utc)
                utc = utc.ToUniversalTime();

            double raDeg = raHours * 15.0;
            double ra = raDeg * Math.PI / 180.0;
            double dec = decDeg * Math.PI / 180.0;

            // Unit vector toward the target, equatorial Cartesian (J2000-ish; precession
            // over a human lifetime is well under the precision this formula targets).
            double ux = Math.Cos(dec) * Math.Cos(ra);
            double uy = Math.Cos(dec) * Math.Sin(ra);
            double uz = Math.Sin(dec);

            // --- 1. Earth's heliocentric orbital velocity ---------------------------
            double d = ToJulianDate(utc) - 2451545.0; // days since J2000.0

            double meanLongitudeDeg = NormalizeDegrees(280.460 + 0.9856474 * d);
            double meanAnomalyDeg = NormalizeDegrees(357.528 + 0.9856003 * d);
            double meanAnomaly = meanAnomalyDeg * Math.PI / 180.0;

            // Low-precision apparent ecliptic longitude of the Sun.
            double sunLambdaDeg = NormalizeDegrees(
                meanLongitudeDeg + 1.915 * Math.Sin(meanAnomaly) + 0.020 * Math.Sin(2 * meanAnomaly));
            double sunLambda = sunLambdaDeg * Math.PI / 180.0;

            const double obliquityDeg = 23.4393;
            double obliquity = obliquityDeg * Math.PI / 180.0;
            const double earthOrbitalSpeedKmPerSec = 29.7859; // mean orbital speed

            // Earth's velocity direction leads the Sun's ecliptic longitude by 90 deg
            // (circular-orbit approximation): vDirection = sunLambda - 90 deg.
            double vEclX = earthOrbitalSpeedKmPerSec * Math.Sin(sunLambda);
            double vEclY = -earthOrbitalSpeedKmPerSec * Math.Cos(sunLambda);

            // Ecliptic -> equatorial (orbit defines the ecliptic plane, so vEclZ = 0).
            double vOrbX = vEclX;
            double vOrbY = vEclY * Math.Cos(obliquity);
            double vOrbZ = vEclY * Math.Sin(obliquity);

            double vOrbital = vOrbX * ux + vOrbY * uy + vOrbZ * uz;

            // --- 2. Earth's rotation (diurnal motion) --------------------------------
            double lstDeg = LocalSiderealTimeDegrees(utc, lonDeg);
            double haDeg = lstDeg - raDeg;
            double ha = haDeg * Math.PI / 180.0;
            double lat = latDeg * Math.PI / 180.0;

            const double earthEquatorialSpeedKmPerSec = 0.4651;
            double vRotation = earthEquatorialSpeedKmPerSec * Math.Cos(lat) * Math.Cos(dec) * Math.Sin(ha);

            // --- 3. Sun's standard motion relative to the LSR ------------------------
            // Classical/"standard" solar apex (B1900): 20.0 km/s toward RA 18h03m50.2s,
            // Dec +30d00'17" - the conventional definition of the kinematic LSR.
            const double solarMotionKmPerSec = 20.0;
            const double solarApexRaDeg = 270.9592; // 18h 03m 50.2s
            const double solarApexDecDeg = 30.0047; // +30d 00' 17"

            double apexRa = solarApexRaDeg * Math.PI / 180.0;
            double apexDec = solarApexDecDeg * Math.PI / 180.0;

            double vSunX = solarMotionKmPerSec * Math.Cos(apexDec) * Math.Cos(apexRa);
            double vSunY = solarMotionKmPerSec * Math.Cos(apexDec) * Math.Sin(apexRa);
            double vSunZ = solarMotionKmPerSec * Math.Sin(apexDec);

            double vSolarMotion = vSunX * ux + vSunY * uy + vSunZ * uz;

            return vOrbital + vRotation + vSolarMotion;
        }

        private static double NormalizeDegrees(double deg)
        {
            deg %= 360.0;
            if (deg < 0) deg += 360.0;
            return deg;
        }

        /// <summary>
        /// Computes Local Sidereal Time (degrees) for a given UTC time and longitude.
        /// Longitude is positive East, negative West.
        /// </summary>
        public static double LocalSiderealTimeDegrees(DateTime utc, double longitudeDeg)
        {
            if (utc.Kind != DateTimeKind.Utc)
                utc = utc.ToUniversalTime();

            // 1. Julian Date
            double jd = ToJulianDate(utc);

            // 2. Julian centuries since J2000.0
            double t = (jd - 2451545.0) / 36525.0;

            // 3. Greenwich Mean Sidereal Time (GMST) in seconds
            double gmstSec =
                67310.54841 +
                (876600.0 * 3600.0 + 8640184.812866) * t +
                0.093104 * t * t -
                6.2e-6 * t * t * t;

            // Normalize to [0, 86400)
            gmstSec = gmstSec % 86400.0;
            if (gmstSec < 0)
                gmstSec += 86400.0;

            // Convert to degrees (360° per sidereal day)
            double gmstDeg = gmstSec * (360.0 / 86400.0);

            // 4. Local Sidereal Time = GMST + longitude
            double lstDeg = gmstDeg + longitudeDeg;

            // Normalize to [0, 360)
            lstDeg = lstDeg % 360.0;
            if (lstDeg < 0)
                lstDeg += 360.0;

            return lstDeg;
        }

        /// <summary>
        /// Converts a UTC DateTime to Julian Date.
        /// </summary>
        private static double ToJulianDate(DateTime utc)
        {
            if (utc.Kind != DateTimeKind.Utc)
                utc = utc.ToUniversalTime();

            int year = utc.Year;
            int month = utc.Month;
            double day = utc.Day +
                         utc.Hour / 24.0 +
                         utc.Minute / 1440.0 +
                         utc.Second / 86400.0 +
                         utc.Millisecond / 86400000.0;

            if (month <= 2)
            {
                year -= 1;
                month += 12;
            }

            int A = year / 100;
            int B = 2 - A + (A / 4);

            double jd = Math.Floor(365.25 * (year + 4716))
                      + Math.Floor(30.6001 * (month + 1))
                      + day + B - 1524.5;

            return jd;
        }

        // North Galactic Pole (J2000-ish, IAU 1958 definition) and the galactic longitude
        // of the North Celestial Pole - the standard constants for a direct equatorial ->
        // galactic conversion, same low-precision-analytic style as the rest of this file.
        private const double NgpRaDeg = 192.85948;
        private const double NgpDecDeg = 27.12825;
        private const double GalacticLongitudeOfNcpDeg = 122.93192;

        /// <summary>
        /// Converts equatorial (RA/Dec) coordinates to Galactic longitude/latitude (l, b),
        /// both in degrees. Used to steer clear of the Galactic plane (strong HI emission)
        /// when picking a "cold sky" calibration baseline position - see ColdSkyLocator.
        /// </summary>
        public static (double lDeg, double bDeg) EquatorialToGalactic(double raHours, double decDeg)
        {
            double ra = raHours * 15.0 * Math.PI / 180.0;
            double dec = decDeg * Math.PI / 180.0;
            double ngpRa = NgpRaDeg * Math.PI / 180.0;
            double ngpDec = NgpDecDeg * Math.PI / 180.0;

            double sinB = Math.Sin(dec) * Math.Sin(ngpDec) + Math.Cos(dec) * Math.Cos(ngpDec) * Math.Cos(ra - ngpRa);
            double b = Math.Asin(sinB);

            double y = Math.Cos(dec) * Math.Sin(ra - ngpRa);
            double x = Math.Sin(dec) * Math.Cos(ngpDec) - Math.Cos(dec) * Math.Sin(ngpDec) * Math.Cos(ra - ngpRa);
            double l = GalacticLongitudeOfNcpDeg - Math.Atan2(y, x) * 180.0 / Math.PI;

            l = NormalizeDegrees(l);

            return (l, b * 180.0 / Math.PI);
        }

        public static double ComputeAngularDistance(TargetPoint a, TargetPoint b)
        {
            if (a.Mode == CoordinateMode.AltAz && b.Mode == CoordinateMode.AltAz)
            {
                double dAz = b.AzimuthDeg - a.AzimuthDeg;
                double dEl = b.ElevationDeg - a.ElevationDeg;
                return Math.Sqrt(dAz * dAz + dEl * dEl);
            }

            // RA/Dec version
            double raAdeg = a.RightAscensionHours * 15.0;
            double raBdeg = b.RightAscensionHours * 15.0;

            double dRa = raBdeg - raAdeg;
            double dDec = b.DeclinationDeg - a.DeclinationDeg;

            return Math.Sqrt(dRa * dRa + dDec * dDec);
        }

    }


}
