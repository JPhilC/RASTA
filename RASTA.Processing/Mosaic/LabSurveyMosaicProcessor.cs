using System.IO;
using RASTA.Core.Telescope;
using RASTA.Processing.Dsp;
using RASTA.Processing.HiPipeline;

namespace RASTA.Processing.Mosaic
{
    /// <summary>
    /// LAB-Survey-text counterpart to MosaicProcessor: walks every LAB Survey profile .txt
    /// file in a folder (see LabSurveyProfileParser) into the same MosaicResult/MosaicPosition
    /// shape MosaicProcessor produces from real RASTA FITS captures, so a folder of downloaded
    /// LAB profiles exercises exactly the same downstream code - GridBuilder, HeatmapImageBuilder,
    /// MosaicSurfaceView - as a real capture session, without needing real hardware/observing
    /// time to validate the Sky Mosaic/3D Surface pipelines. See MosaicFolderFormatDetector for
    /// how MosaicViewModel decides which of the two processors a selected folder should use.
    ///
    /// Two structural differences from a real RASTA session, both consequences of the LAB
    /// Survey's own data already being a finished scientific product rather than raw IQ:
    ///
    ///  - There's no baseline file/division step. LAB Survey brightness temperatures (T_B, in
    ///    Kelvin) are already background/stray-radiation corrected by the survey itself, unlike
    ///    RASTA's own RatioSpectrum (capturePower/baselinePower) which only exists because a raw
    ///    RTL-SDR capture isn't independently calibrated. So MosaicPosition.LineStrengthDb here
    ///    is a reused field, not a true dB-relative-to-baseline figure - it holds the peak T_B
    ///    in Kelvin directly. MosaicViewModel.StrengthUnitLabel is what tells the UI to render
    ///    "K" instead of "dB" for a LAB-sourced session, so this doesn't quietly present GK-scale
    ///    Kelvin readings as if they were dB.
    ///  - Processing is a plain sequential loop, not MosaicProcessor's Parallel.For. Parsing a
    ///    ~800-point text file is milliseconds - there's no FFT/accumulate work per position to
    ///    parallelise, and no equivalent of MosaicProcessor's serialized-FITS-read complexity
    ///    (nom.tam.fits isn't involved at all here) to work around.
    /// </summary>
    public class LabSurveyMosaicProcessor
    {
        public const double DefaultIntegratedWindowKmPerSec = MosaicProcessor.DefaultIntegratedWindowKmPerSec;

        public Task<MosaicResult> ProcessFolderAsync(
            string folder,
            double integratedWindowKmPerSec,
            Action<string, double>? progressCallback,
            SmoothingKind smoothing = SmoothingKind.None,
            int smoothingWindow = 21,
            CancellationToken ct = default)
        {
            return Task.Run(
                () => ProcessFolder(folder, integratedWindowKmPerSec, progressCallback, smoothing, smoothingWindow, ct),
                ct);
        }

        private static MosaicResult ProcessFolder(
            string folder,
            double integratedWindowKmPerSec,
            Action<string, double>? progressCallback,
            SmoothingKind smoothing,
            int smoothingWindow,
            CancellationToken ct)
        {
            var files = Directory.GetFiles(folder, "*.txt")
                .Where(LabSurveyProfileParser.LooksLikeLabProfile)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0)
                throw new InvalidOperationException("No LAB Survey profile files found in the selected folder.");

            var positions = new MosaicPosition[files.Count];
            double[]? velocityAxis = null;
            double[]? frequencyAxis = null;

            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                string file = files[i];
                var profile = LabSurveyProfileParser.Parse(file);

                // Every LAB profile query returns the same 777-point grid, but not necessarily
                // bit-identical velocities across positions (observed a +/-0.01 km/s drift
                // between two real downloads) - same "any one position is representative"
                // convention MosaicProcessor uses for its own shared axis (see its remarks),
                // captured from index 0 specifically for determinism.
                if (i == 0)
                {
                    velocityAxis = profile.VelocityKmPerSec;
                    frequencyAxis = profile.FrequencyMHz.Select(mhz => mhz * 1e6).ToArray();
                }

                double[] peakSearchSpectrum = smoothing == SmoothingKind.None
                    ? profile.BrightnessTempK
                    : HiStreamingPipeline.ApplySmoothing(profile.BrightnessTempK, smoothing, smoothingWindow);

                var (peakTempK, peakVelocityKmPerSec) = FindPeak(profile.VelocityKmPerSec, peakSearchSpectrum, integratedWindowKmPerSec);

                positions[i] = new MosaicPosition(
                    Path.GetFileNameWithoutExtension(file),
                    CoordinateMode.Equatorial,
                    profile.RaDeg / 15.0,
                    profile.DecDeg,
                    null,
                    null,
                    profile.BrightnessTempK,
                    peakTempK,
                    peakVelocityKmPerSec,
                    new[] { file });

                progressCallback?.Invoke($"Processing position {i + 1} of {files.Count} ({positions[i].Label})…", (double)(i + 1) / files.Count);
            }

            return new MosaicResult(
                frequencyAxis ?? Array.Empty<double>(),
                velocityAxis ?? Array.Empty<double>(),
                positions.ToList());
        }

        /// <summary>
        /// Strongest T_B channel within +-windowKmPerSec of 0 (the profile's own v_lsr=0
        /// reference). Unlike MosaicProcessor.FindLinePeak there's no log10/positivity guard
        /// needed - T_B is a plain linear brightness temperature, not a ratio being converted
        /// to dB, so a near-zero or slightly negative reading (real noise on genuinely cold,
        /// line-free sky) is legitimate data, not a value to hide as NaN.
        /// </summary>
        private static (double peakTempK, double peakVelocityKmPerSec) FindPeak(
            double[] velocityKmPerSec, double[] brightnessTempK, double windowKmPerSec)
        {
            int peakIndex = -1;
            double peakValue = double.NegativeInfinity;
            for (int i = 0; i < velocityKmPerSec.Length; i++)
            {
                if (Math.Abs(velocityKmPerSec[i]) <= windowKmPerSec && brightnessTempK[i] > peakValue)
                {
                    peakValue = brightnessTempK[i];
                    peakIndex = i;
                }
            }
            return peakIndex < 0 ? (double.NaN, double.NaN) : (peakValue, velocityKmPerSec[peakIndex]);
        }
    }
}
