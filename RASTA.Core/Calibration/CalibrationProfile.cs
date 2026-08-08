namespace RASTA.Core.Calibration
{
    public sealed class CalibrationProfile
    {
        /// <summary>
        /// When the calibration was performed (UTC).
        /// </summary>
        public DateTime TimestampUtc { get; init; }

        /// <summary>
        /// The center frequency used during calibration.
        /// </summary>
        public double CenterFrequencyHz { get; init; }

        /// <summary>
        /// The sample rate used during calibration.
        /// </summary>
        public double SampleRateHz { get; init; }

        /// <summary>
        /// FFT size used for all calibration spectra.
        /// </summary>
        public int FftSize { get; init; }

        /// <summary>
        /// The gain chosen by the gain-sweep algorithm.
        /// This MUST be used for all subsequent observations.
        /// </summary>
        public double GainDb { get; init; }

        /// <summary>
        /// The baseline spectrum captured at the chosen gain.
        /// This is the averaged noise spectrum used for calibration.
        /// </summary>
        public double[] BaselineSpectrum { get; init; } = Array.Empty<double>();

        /// <summary>
        /// Mean of the baseline spectrum.
        /// Useful for checking stability and for normalisation.
        /// </summary>
        public double BaselineMean { get; init; }

        /// <summary>
        /// Standard deviation of the baseline spectrum.
        /// Used to detect instability or RFI contamination.
        /// </summary>
        public double BaselineStdDev { get; init; }

        /// <summary>
        /// The SDR device ID (tuner type or serial).
        /// Ensures calibration is tied to the correct hardware.
        /// </summary>
        public string DeviceId { get; init; } = string.Empty;

        // -----------------------------------------------------------------
        // Cold-sky baseline pointing (null for older profiles captured
        // against a terminator, before this was tracked).
        // -----------------------------------------------------------------

        /// <summary>Azimuth (deg) the mount was pointed at for the baseline capture.</summary>
        public double? BaselineAzimuthDeg { get; init; }

        /// <summary>Elevation (deg) the mount was pointed at for the baseline capture.</summary>
        public double? BaselineElevationDeg { get; init; }

        /// <summary>Right Ascension (deg) of the baseline capture's pointing.</summary>
        public double? BaselineRaDeg { get; init; }

        /// <summary>Declination (deg) of the baseline capture's pointing.</summary>
        public double? BaselineDecDeg { get; init; }

        /// <summary>Galactic latitude (deg) of the baseline capture's pointing - higher |b| means further from the HI-rich Galactic plane, i.e. "colder".</summary>
        public double? BaselineGalacticLatitudeDeg { get; init; }
    }
}


