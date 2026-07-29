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
