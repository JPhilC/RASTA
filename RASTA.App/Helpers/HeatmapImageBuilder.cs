using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RASTA.App.Helpers
{
    /// <summary>
    /// Renders a 2D grid of values (as produced by GridBuilder) to a BitmapSource, replacing
    /// LiveChartsCore.SkiaSharpView's HeatSeries for MosaicViewModel's heatmaps - HeatSeries
    /// produced a blank chart against real, well-spread mosaic data (38 positions with valid
    /// RA/Dec), and rather than keep debugging an opaque third-party series type blind, this
    /// draws pixels directly, the same technique already proven elsewhere in RASTA
    /// (MosaicSurfaceView's own gradient-texture builder; the historic, since-deleted
    /// HeatmapBuilder). One solid-colour block per grid cell (nearest-neighbour, not
    /// interpolated) - each cell is a real independent measurement, blending between them
    /// would imply data that isn't there.
    /// </summary>
    public static class HeatmapImageBuilder
    {
        // Diverging blue-gray-red ramp (dataviz skill's diverging formula: two hues + a
        // neutral gray midpoint) - kept in one place, reused by MosaicSurfaceView's 3D
        // texture too so the sky heatmap and the 3D surface colour identically.
        public static readonly (byte r, byte g, byte b)[] DivergingStops =
        {
            (0x10, 0x42, 0x81), // deep blue
            (0x39, 0x87, 0xE5), // blue
            (0xF0, 0xEF, 0xEC), // neutral gray midpoint
            (0xE3, 0x49, 0x48), // red
            (0xA5, 0x30, 0x2F), // deep red
        };

        // Flat, desaturated fill for "no data" cells - visually distinct from the ramp's own
        // (very slightly warm) gray midpoint so "nothing measured here" doesn't read as
        // "measured, and it was zero".
        private static readonly (byte r, byte g, byte b) NoDataColor = (0xC8, 0xC8, 0xC8);

        /// <summary>
        /// grid[x, y] with x=0 at the image's left edge. y=0 is placed at the image's BOTTOM
        /// edge when flipY is true (the default - matches "Dec/Alt increases upward" chart
        /// convention) or at the TOP edge when false (matches top-to-bottom list/table order,
        /// used for the position-velocity diagram's position axis).
        /// </summary>
        public static BitmapSource Build(double[,] grid, int pixelWidth, int pixelHeight, bool flipY = true)
        {
            int gridWidth = grid.GetLength(0);
            int gridHeight = grid.GetLength(1);

            double min = double.MaxValue, max = double.MinValue;
            for (int gx = 0; gx < gridWidth; gx++)
            {
                for (int gy = 0; gy < gridHeight; gy++)
                {
                    double v = grid[gx, gy];
                    if (double.IsNaN(v)) continue;
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }

            bool hasData = min <= max;
            double range = hasData ? Math.Max(max - min, 1e-9) : 1;

            var pixels = new byte[pixelWidth * pixelHeight * 4];
            for (int py = 0; py < pixelHeight; py++)
            {
                int gyFromTop = gridHeight > 1 ? Math.Clamp(py * gridHeight / pixelHeight, 0, gridHeight - 1) : 0;
                int gy = flipY ? gridHeight - 1 - gyFromTop : gyFromTop;

                for (int px = 0; px < pixelWidth; px++)
                {
                    int gx = gridWidth > 1 ? Math.Clamp(px * gridWidth / pixelWidth, 0, gridWidth - 1) : 0;

                    double v = grid[gx, gy];
                    var color = !hasData || double.IsNaN(v) ? NoDataColor : Ramp((v - min) / range);

                    int idx = (py * pixelWidth + px) * 4;
                    pixels[idx + 0] = color.b;
                    pixels[idx + 1] = color.g;
                    pixels[idx + 2] = color.r;
                    pixels[idx + 3] = 255;
                }
            }

            var bmp = BitmapSource.Create(pixelWidth, pixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, pixelWidth * 4);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>A thin horizontal strip of the same ramp, for a colour-scale legend.</summary>
        public static BitmapSource BuildLegendStrip(int pixelWidth, int pixelHeight = 16)
        {
            var pixels = new byte[pixelWidth * pixelHeight * 4];
            for (int px = 0; px < pixelWidth; px++)
            {
                var color = Ramp(px / (double)(pixelWidth - 1));
                for (int py = 0; py < pixelHeight; py++)
                {
                    int idx = (py * pixelWidth + px) * 4;
                    pixels[idx + 0] = color.b;
                    pixels[idx + 1] = color.g;
                    pixels[idx + 2] = color.r;
                    pixels[idx + 3] = 255;
                }
            }

            var bmp = BitmapSource.Create(pixelWidth, pixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, pixelWidth * 4);
            bmp.Freeze();
            return bmp;
        }

        public static (byte r, byte g, byte b) Ramp(double t)
        {
            t = Math.Clamp(t, 0, 1) * (DivergingStops.Length - 1);
            int i0 = (int)Math.Floor(t);
            int i1 = Math.Min(i0 + 1, DivergingStops.Length - 1);
            double frac = t - i0;

            byte Lerp(byte a, byte b) => (byte)(a + (b - a) * frac);
            var s0 = DivergingStops[i0];
            var s1 = DivergingStops[i1];
            return (Lerp(s0.r, s1.r), Lerp(s0.g, s1.g), Lerp(s0.b, s1.b));
        }
    }
}
