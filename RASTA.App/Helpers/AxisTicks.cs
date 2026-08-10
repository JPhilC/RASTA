using System;
using System.Collections.Generic;

namespace RASTA.App.Helpers
{
    /// <summary>
    /// One faint reference gridline across a 2D plot area, in that plot's own pixel-coordinate
    /// space (X1/Y1 to X2/Y2) - see MosaicHeatmapDisplay.GridLines/MosaicView.xaml's overlay.
    /// </summary>
    public record AxisGridLine(double X1, double Y1, double X2, double Y2);

    /// <summary>
    /// One axis tick's label and where it belongs - a pixel coordinate for a 2D plot overlay
    /// (MosaicHeatmapDisplay.XTickLabels/YTickLabels), or a real RA/Dec(/Az/El) axis value for
    /// MosaicSurfaceView's 3D floor grid/labels (which does its own value-to-3D-space mapping,
    /// since it already owns the Norm* normalization the mesh itself uses). Deliberately a
    /// Helpers type rather than living on MosaicViewModel, so MosaicSurfaceView (a View) can
    /// consume it without depending on a ViewModel type - consistent with its other bound
    /// properties (IntensityGrid/XValues/YValues) all being plain primitives.
    /// </summary>
    public record AxisTick(string Label, double Position);

    /// <summary>
    /// "Nice numbers for graph labels" (Heckbert) - shared by the Sky Mosaic 2D heatmap and the
    /// 3D surface's axis overlays, so both pick the same kind of round-looking tick values (e.g.
    /// whole/5/10-step RA hours or Dec degrees) rather than raw grid-cell-center values, which
    /// for a fine cell size would produce ticks like "13.333h" that don't read naturally.
    /// </summary>
    public static class AxisTicks
    {
        /// <summary>
        /// Picks up to targetCount evenly-spaced, round tick values covering [min, max] (a tick
        /// slightly outside the range by rounding is dropped, not clamped - clamping would break
        /// the even spacing). Falls back to a single tick at min if the range is degenerate.
        /// </summary>
        public static double[] ComputeNiceTicks(double min, double max, int targetCount = 6)
        {
            if (!(max > min) || targetCount < 2)
                return new[] { min };

            double range = NiceNum(max - min, round: false);
            double step = NiceNum(range / (targetCount - 1), round: true);
            if (!(step > 0))
                return new[] { min };

            double niceMin = Math.Floor(min / step) * step;
            double niceMax = Math.Ceiling(max / step) * step;

            var ticks = new List<double>();
            for (double v = niceMin; v <= niceMax + step * 0.5; v += step)
            {
                if (v >= min - step * 1e-6 && v <= max + step * 1e-6)
                    ticks.Add(Math.Round(v, 6));
            }
            return ticks.Count > 0 ? ticks.ToArray() : new[] { min };
        }

        private static double NiceNum(double range, bool round)
        {
            if (!(range > 0))
                return 0;

            double exponent = Math.Floor(Math.Log10(range));
            double fraction = range / Math.Pow(10, exponent);
            double niceFraction = round
                ? (fraction < 1.5 ? 1 : fraction < 3 ? 2 : fraction < 7 ? 5 : 10)
                : (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10);

            return niceFraction * Math.Pow(10, exponent);
        }
    }
}
