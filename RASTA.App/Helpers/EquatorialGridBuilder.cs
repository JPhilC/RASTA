using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using RASTA.Core.Astro;

namespace RASTA.App.Helpers
{
    /// <summary>
    /// Builds RA/Dec meridian/parallel gridlines projected onto a zenith-centered Alt/Az dome
    /// (see DomeProjector) at a given moment - the equatorial-coordinate alternative to the
    /// dome's usual fixed altitude-ring/azimuth-spoke reference geometry, for
    /// PlanViewModel.MapGridMode. A physical dome position is inherently Az/El, so an RA/Dec
    /// grid line only has one shape at one instant - each curve is re-projected fresh from
    /// AstronomyUtils.EquatorialToHorizontal at the given time/site, same as everything else in
    /// PlanViewModel's map that depends on MapTimeUtc.
    ///
    /// Each meridian (constant RA, sampled across Dec) or parallel (constant Dec, sampled across
    /// RA) can dip below the horizon and re-emerge, so a single RA/Dec line can produce more than
    /// one on-screen polyline segment - see SampleCurve, which breaks the line at every
    /// below-horizon sample rather than drawing a distorted line through the clamped edge of the
    /// dome.
    /// </summary>
    public static class EquatorialGridBuilder
    {
        private const double RaStepHours = 2.0;
        private const double DecStepDeg = 15.0;

        public static (IReadOnlyList<PointCollection> Lines, IReadOnlyList<DomeCompassLabel> Labels) Build(
            DomeProjector projector, DateTime utcTime, double siteLatitudeDeg, double siteLongitudeDeg)
        {
            if (utcTime.Kind != DateTimeKind.Utc)
                utcTime = DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);

            var lines = new List<PointCollection>();
            var labels = new List<DomeCompassLabel>();

            // RA meridians - constant RA, sampled across declination.
            for (double ra = 0; ra < 24; ra += RaStepHours)
            {
                double raLocal = ra;
                var segments = SampleCurve(
                    dec => AstronomyUtils.EquatorialToHorizontal(raLocal, dec, utcTime, siteLatitudeDeg, siteLongitudeDeg),
                    -85, 85, 2.0, projector, out var anchor);
                lines.AddRange(segments);
                if (anchor is { } a)
                    labels.Add(new DomeCompassLabel($"{ra:0}h", a.x, a.y));
            }

            // Dec parallels - constant declination, sampled across RA. Stops short of the poles
            // (+/-90), which are single points rather than a line worth drawing.
            for (double dec = -75; dec <= 75; dec += DecStepDeg)
            {
                double decLocal = dec;
                var segments = SampleCurve(
                    raHours => AstronomyUtils.EquatorialToHorizontal(raHours, decLocal, utcTime, siteLatitudeDeg, siteLongitudeDeg),
                    0, 24, 0.25, projector, out var anchor);
                lines.AddRange(segments);
                if (anchor is { } a)
                    labels.Add(new DomeCompassLabel($"{(decLocal > 0 ? "+" : "")}{decLocal:0}°", a.x, a.y));
            }

            return (lines, labels);
        }

        /// <summary>
        /// Walks <paramref name="tStart"/>..<paramref name="tEnd"/> in steps of
        /// <paramref name="tStep"/>, projecting azElAt(t) onto the dome and breaking into a new
        /// polyline segment every time a sample dips below the horizon (el &lt; 0) - so a line
        /// that sets and later rises again on the dome draws as separate arcs rather than one
        /// distorted line cutting across the disk. The returned label anchor is the projected
        /// position of whichever sample had the highest elevation overall (a line's own highest,
        /// most prominent point on the dome - simple and adequate without needing per-line
        /// custom placement logic).
        /// </summary>
        private static List<PointCollection> SampleCurve(
            Func<double, (double azDeg, double elDeg)> azElAt,
            double tStart, double tEnd, double tStep,
            DomeProjector projector,
            out (double x, double y)? labelAnchor)
        {
            var segments = new List<PointCollection>();
            PointCollection? current = null;
            double bestElDeg = double.NegativeInfinity;
            (double x, double y)? bestPoint = null;

            for (double t = tStart; t <= tEnd + 1e-9; t += tStep)
            {
                var (azDeg, elDeg) = azElAt(t);

                if (elDeg < 0)
                {
                    if (current is { Count: > 1 })
                        segments.Add(Freeze(current));
                    current = null;
                    continue;
                }

                var (x, y) = projector.Project(azDeg, elDeg);
                current ??= new PointCollection();
                current.Add(new Point(x, y));

                if (elDeg > bestElDeg)
                {
                    bestElDeg = elDeg;
                    bestPoint = (x, y);
                }
            }

            if (current is { Count: > 1 })
                segments.Add(Freeze(current));

            labelAnchor = bestPoint;
            return segments;
        }

        private static PointCollection Freeze(PointCollection points)
        {
            points.Freeze();
            return points;
        }
    }
}
