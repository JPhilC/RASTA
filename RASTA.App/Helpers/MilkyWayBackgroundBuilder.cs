using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RASTA.Core.Astro;

namespace RASTA.App.Helpers
{
    /// <summary>
    /// Renders a Milky Way dome background BitmapSource for PlanViewModel's sky map from real
    /// HI4PI all-sky HI column-density data (see Hi4PiSkyMap for the survey/attribution/downgrade
    /// details) - Galactic longitude/latitude from AstronomyUtils.EquatorialToGalactic, N_HI
    /// bilinearly sampled from the embedded grid and normalized to [0,1], coloured with the same
    /// visible-spectrum ramp used throughout the app (HeatmapImageBuilder.Ramp). RASTA has no
    /// real star/constellation catalog on top of this (see the parked "overlay a lightweight
    /// star/constellation map" goal noted elsewhere) - this is still just orientation context for
    /// the sky map, now driven by a real survey rather than the earlier Gaussian-glow
    /// approximation it replaced.
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

                    double brightness = Hi4PiSkyMap.SampleBrightness(lDeg, bDeg);
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
    }
}
