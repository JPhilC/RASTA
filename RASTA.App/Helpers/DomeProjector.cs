using System;
using System.Collections.Generic;

namespace RASTA.App.Helpers
{
    /// <summary>One concentric altitude ring on a dome, precomputed to a Canvas-friendly bounding box.</summary>
    public record DomeRingGeometry(double Left, double Top, double Diameter);

    /// <summary>One compass-rose label (N/NE/E/...) on a dome, at its own pixel position.</summary>
    public record DomeCompassLabel(string Text, double X, double Y);

    /// <summary>
    /// Az/El &lt;-&gt; pixel projection for a zenith-centered dome view, plus the standard
    /// altitude-ring/azimuth-spoke/compass-label reference geometry every dome tab in the app
    /// draws. Extracted from MosaicViewModel.RenderDome's own Project/ring/spoke/label math (see
    /// its remarks for the "N up, S down, E left, W right" convention - looking up at the sky's
    /// dome mirrors east/west relative to looking down at a map) so PlanViewModel's sky map can
    /// use the exact same look without depending on MosaicViewModel. Deliberately standalone
    /// rather than a refactor of the existing Mosaic code, to keep this change off a working,
    /// already-documented feature.
    ///
    /// Unlike Mosaic's dome, this also needs the inverse projection (Unproject) - translating a
    /// mouse click/hover back to sky coordinates - which Mosaic's read-only dome never required.
    /// </summary>
    public sealed class DomeProjector
    {
        public double CenterX { get; }
        public double CenterY { get; }
        public double Radius { get; }

        public DomeProjector(double canvasSize, double marginPx)
        {
            CenterX = canvasSize / 2.0;
            CenterY = canvasSize / 2.0;
            Radius = Math.Max(canvasSize / 2.0 - marginPx, 1.0);
        }

        public (double x, double y) Project(double azDeg, double elDeg)
        {
            double r = Math.Clamp((90.0 - elDeg) / 90.0, 0.0, 1.0) * Radius;
            double azRad = azDeg * Math.PI / 180.0;
            return (CenterX - r * Math.Sin(azRad), CenterY - r * Math.Cos(azRad));
        }

        /// <summary>
        /// Inverse of Project. Returns null when (x, y) falls outside the dome circle - either
        /// off-canvas or, more usefully, below the horizon (el &lt; 0), since Project clamps
        /// el to [0, 90] and so can never itself produce a point out that far.
        /// </summary>
        public (double azDeg, double elDeg)? Unproject(double x, double y)
        {
            double dx = CenterX - x;
            double dy = CenterY - y;
            double r = Math.Sqrt(dx * dx + dy * dy);
            if (r > Radius)
                return null;

            double elDeg = 90.0 - (r / Radius) * 90.0;

            // Project's (x, y) = (cx - r*sin(az), cy - r*cos(az)), so dx = r*sin(az), dy = r*cos(az)
            // => az = atan2(dx, dy) - the same "swap the usual atan2 argument order" trick Project's
            // own -sin/-cos convention needs inverted.
            double azDeg = Math.Atan2(dx, dy) * 180.0 / Math.PI;
            if (azDeg < 0) azDeg += 360.0;

            return (azDeg, elDeg);
        }

        public IReadOnlyList<DomeRingGeometry> BuildAltitudeRings(double stepDeg = 15)
        {
            var rings = new List<DomeRingGeometry>();
            for (double el = 0; el < 90; el += stepDeg)
            {
                double r = (90.0 - el) / 90.0 * Radius;
                rings.Add(new DomeRingGeometry(CenterX - r, CenterY - r, r * 2));
            }
            return rings;
        }

        public IReadOnlyList<AxisGridLine> BuildAzimuthSpokes(double stepDeg = 30)
        {
            var spokes = new List<AxisGridLine>();
            for (double az = 0; az < 360; az += stepDeg)
            {
                var (ex, ey) = Project(az, 0.0);
                spokes.Add(new AxisGridLine(CenterX, CenterY, ex, ey));
            }
            return spokes;
        }

        public IReadOnlyList<DomeCompassLabel> BuildCompassLabels()
        {
            string[] compassPoints = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            var labels = new List<DomeCompassLabel>();
            for (int i = 0; i < compassPoints.Length; i++)
            {
                double az = i * 45.0;
                double azRad = az * Math.PI / 180.0;
                double labelR = Radius + 20;
                labels.Add(new DomeCompassLabel(compassPoints[i], CenterX - labelR * Math.Sin(azRad), CenterY - labelR * Math.Cos(azRad)));
            }
            return labels;
        }
    }
}
