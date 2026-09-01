using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RASTA.Core.Astro;

namespace RASTA.App.Helpers
{
    /// <summary>
    /// Renders an analytic Milky Way "glow band" as a dome background BitmapSource for
    /// PlanViewModel's sky map. RASTA has no real star/sky catalog (see the parked
    /// "overlay a lightweight star/constellation map" goal noted elsewhere) - this approximates
    /// the Galactic plane's location and rough brightness from AstronomyUtils.EquatorialToGalactic
    /// alone (Gaussian falloff from Galactic latitude b=0, gently brighter toward the Galactic
    /// center l=0 than the anticenter, echoing how a real radio-continuum sky brightens toward
    /// Sgr A), coloured with the same visible-spectrum ramp used throughout the app
    /// (HeatmapImageBuilder.Ramp). It is explicitly not real sky data - just enough visual
    /// context to orient the map, in the same spirit as the rest of the app's low-precision
    /// analytic astronomy helpers (e.g. AstronomyUtils.ComputeLsrCorrectionKmPerSec).
    ///
    /// Builds its own DomeProjector sized to pixelSize rather than taking one in, so the caller
    /// can render this at a lower internal resolution than the on-screen canvas (several trig
    /// conversions per pixel add up) and let the Image control's bilinear scaling stretch it -
    /// only depends on time and site, so it only needs rebuilding when either changes, not on
    /// every animation tick.
    /// </summary>
    public static class MilkyWayBackgroundBuilder
    {
        public static BitmapSource Build(
            int pixelSize,
            double marginFraction,
            DateTime utcTime,
            double siteLatitudeDeg,
            double siteLongitudeDeg)
        {
            if (utcTime.Kind != DateTimeKind.Utc)
                utcTime = DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);

            var projector = new DomeProjector(pixelSize, pixelSize * marginFraction);
            var pixels = new byte[pixelSize * pixelSize * 4];

            for (int py = 0; py < pixelSize; py++)
            {
                for (int px = 0; px < pixelSize; px++)
                {
                    int idx = (py * pixelSize + px) * 4;

                    var azel = projector.Unproject(px + 0.5, py + 0.5);
                    if (azel is null)
                    {
                        pixels[idx + 3] = 0; // below the horizon / off the dome circle - transparent
                        continue;
                    }

                    var (azDeg, elDeg) = azel.Value;
                    var (raHours, decDeg) = AstronomyUtils.HorizontalToEquatorial(
                        azDeg, elDeg, utcTime, siteLatitudeDeg, siteLongitudeDeg);
                    var (lDeg, bDeg) = AstronomyUtils.EquatorialToGalactic(raHours, decDeg);

                    double brightness = MilkyWayBrightness(lDeg, bDeg);
                    var (r, g, b) = HeatmapImageBuilder.Ramp(brightness);

                    pixels[idx + 0] = b;
                    pixels[idx + 1] = g;
                    pixels[idx + 2] = r;
                    // Alpha follows brightness too (slightly boosted) so faint sky fades toward
                    // transparent rather than reading as a uniform wall of colour - only the
                    // Milky Way band itself should stand out.
                    pixels[idx + 3] = (byte)Math.Clamp(brightness * 255.0 * 1.15, 0, 255);
                }
            }

            var bmp = BitmapSource.Create(pixelSize, pixelSize, 96, 96, PixelFormats.Bgra32, null, pixels, pixelSize * 4);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>
        /// Analytic brightness in [0, 1] for a given Galactic longitude/latitude. Not derived
        /// from any survey - a Gaussian falloff from the plane (b=0) with a gentle longitude
        /// boost toward the Galactic center (l=0), purely to give the dome map a recognisable
        /// Milky Way band rather than a blank sky.
        /// </summary>
        private static double MilkyWayBrightness(double lDeg, double bDeg)
        {
            const double bSigmaDeg = 8.0;
            double planeGlow = Math.Exp(-(bDeg * bDeg) / (2 * bSigmaDeg * bSigmaDeg));

            double lRad = lDeg * Math.PI / 180.0;
            double centerBoost = 0.5 + 0.5 * Math.Cos(lRad); // 1.0 at l=0 (center), 0.0 at l=180 (anticenter)

            return Math.Clamp(planeGlow * (0.4 + 0.6 * centerBoost), 0.0, 1.0);
        }
    }
}
