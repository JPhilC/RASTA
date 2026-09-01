using System;
using System.Collections.Generic;

namespace RASTA.Processing.Planning
{
    /// <summary>
    /// Gnomonic (tangent-plane) projection centered on a chosen RA/Dec - the standard
    /// "standard coordinates" astrometric projection (the same math as a FITS WCS TAN
    /// projection). Used by SweepPlanner.BuildRegionGrid so a drawn region's point-in-polygon
    /// test and grid generation happen in a genuine local Cartesian plane instead of raw
    /// RA-hours x Dec-degrees, which becomes wildly distorted - and can even become
    /// self-intersecting - near the celestial pole (see BuildRegionGrid's own remarks for why
    /// that matters in practice: a region traced near due-North at a mid/high-latitude site,
    /// a very natural thing to want to draw, sits close to the pole). Exact at the tangent
    /// point and a good approximation for a region up to a few tens of degrees across;
    /// distortion grows - and the projection eventually breaks down entirely 90 degrees from
    /// center - for a very large region, but that's still a vastly better regime than the
    /// previous approach's complete failure near the pole for even a modest region.
    /// </summary>
    public readonly struct GnomonicProjection
    {
        private readonly double _ra0Rad;
        private readonly double _sinDec0;
        private readonly double _cosDec0;

        public GnomonicProjection(double centerRaHours, double centerDecDeg)
        {
            _ra0Rad = centerRaHours * 15.0 * Math.PI / 180.0;
            double dec0Rad = centerDecDeg * Math.PI / 180.0;
            _sinDec0 = Math.Sin(dec0Rad);
            _cosDec0 = Math.Cos(dec0Rad);
        }

        /// <summary>RA/Dec (hours, degrees) -&gt; tangent-plane (xi, eta), in radians.</summary>
        public (double xi, double eta) Project(double raHours, double decDeg)
        {
            double dec = decDeg * Math.PI / 180.0;
            double dRa = raHours * 15.0 * Math.PI / 180.0 - _ra0Rad;

            double sinDec = Math.Sin(dec);
            double cosDec = Math.Cos(dec);
            double cosDRa = Math.Cos(dRa);
            double sinDRa = Math.Sin(dRa);

            double denom = _sinDec0 * sinDec + _cosDec0 * cosDec * cosDRa;
            double xi = cosDec * sinDRa / denom;
            double eta = (_cosDec0 * sinDec - _sinDec0 * cosDec * cosDRa) / denom;
            return (xi, eta);
        }

        /// <summary>Tangent-plane (xi, eta), in radians -&gt; RA/Dec (hours, degrees).</summary>
        public (double raHours, double decDeg) Unproject(double xi, double eta)
        {
            double rho = Math.Sqrt(xi * xi + eta * eta);
            if (rho < 1e-14)
                return (NormalizeRaHours(_ra0Rad * 180.0 / Math.PI / 15.0), Math.Asin(_sinDec0) * 180.0 / Math.PI);

            double c = Math.Atan(rho);
            double sinC = Math.Sin(c);
            double cosC = Math.Cos(c);

            double dec = Math.Asin(cosC * _sinDec0 + eta * sinC * _cosDec0 / rho);
            double ra = _ra0Rad + Math.Atan2(xi * sinC, rho * _cosDec0 * cosC - eta * _sinDec0 * sinC);

            return (NormalizeRaHours(ra * 180.0 / Math.PI / 15.0), dec * 180.0 / Math.PI);
        }

        private static double NormalizeRaHours(double raHours)
        {
            raHours %= 24.0;
            if (raHours < 0) raHours += 24.0;
            return raHours;
        }

        /// <summary>
        /// A pole-safe centroid for a set of RA/Dec points - the mean of their unit vectors,
        /// re-normalized, rather than a naive mean of RA-hours (meaningless near the pole, and
        /// undefined across the 24h/0h seam). Used to choose this projection's own tangent
        /// point from a region's vertices, so the tangent point sits somewhere reasonable in
        /// the middle of the drawn shape regardless of where on the sky it was drawn.
        /// </summary>
        public static (double raHours, double decDeg) ComputeCentroid(IEnumerable<(double raHours, double decDeg)> points)
        {
            double x = 0, y = 0, z = 0;
            foreach (var (raHours, decDeg) in points)
            {
                double ra = raHours * 15.0 * Math.PI / 180.0;
                double dec = decDeg * Math.PI / 180.0;
                double cosDec = Math.Cos(dec);
                x += cosDec * Math.Cos(ra);
                y += cosDec * Math.Sin(ra);
                z += Math.Sin(dec);
            }

            double raC = Math.Atan2(y, x);
            double decC = Math.Atan2(z, Math.Sqrt(x * x + y * y));

            return (NormalizeRaHours(raC * 180.0 / Math.PI / 15.0), decC * 180.0 / Math.PI);
        }
    }
}
