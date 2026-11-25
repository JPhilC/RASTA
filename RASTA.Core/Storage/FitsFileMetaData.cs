using nom.tam.fits;
using RASTA.Core.Astro;
using RASTA.Core.Sdr;

namespace RASTA.Core.Storage
{
    public class FitsFileMetaData
    {
        public string Origin { get; set; } = string.Empty;
        public string DataFormat { get; set; } = string.Empty;
        public double CentFreqHz { get; set; }
        public double SampFreqHz { get; set; }

        public int FftSize { get; set; }

        public double DwellTimeSec { get; set; }

        public double GainDb { get; set; }

        public DateTime ObservationDate { get; set; }

        public double? SiteLatitudeDeg { get; set; }
        public double? SiteLongitudeDeg { get; set; }
        public double? SiteElevationM { get; set; }

        public double? RaDeg { get; set; }
        public double? DecDeg { get; set; }
        public double? AzDeg { get; set; }
        public double? AltDeg { get; set; }

        /// <summary>
        /// Julian-epoch equinox of RaDeg/DecDeg (FITS EQUINOX). Everything RASTA writes is
        /// J2000, so a new instance defaults to 2000. Files written before issue #38 have no
        /// EQUINOX card and read back as null, which ComputeLsrCorrectionKmPerSec treats as
        /// J2000 - their RA/Dec is whatever the mount reported, epoch unknown.
        /// </summary>
        public double? Equinox { get; set; } = 2000.0;

        /// <summary>
        /// Receiver crystal correction (ppm) in force for this capture (issue #32): the whole
        /// part was set in the tuner (PpmHardware), the rest has to be applied to the frequency
        /// axis - see FrequencyCorrection and AxisCentFreqHz/AxisSampFreqHz. Null for files
        /// written before #32, which had no correction at all, so they reduce exactly as before.
        /// </summary>
        public double? PpmCorrection { get; set; }

        /// <summary>Whole ppm actually set in the tuner (FITS PPM_HW); null when not recorded.</summary>
        public int? PpmHardware { get; set; }

        /// <summary>
        /// Fraction of raw I/Q bytes at the ADC rail when this file was captured (FITS CLIPFRAC,
        /// issue #33; see AdcClipping). Null for files written before #33.
        /// </summary>
        public double? ClipFraction { get; set; }

        /// <summary>The part of PpmCorrection the tuner did not apply; 0 when none was recorded.</summary>
        public double ResidualPpm =>
            PpmCorrection is double ppm ? FrequencyCorrection.ResidualPpm(ppm, PpmHardware) : 0.0;

        /// <summary>True centre frequency of the data: CentFreqHz corrected for ResidualPpm.</summary>
        public double AxisCentFreqHz => FrequencyCorrection.Apply(CentFreqHz, ResidualPpm);

        /// <summary>True sample rate of the data: SampFreqHz corrected for ResidualPpm.</summary>
        public double AxisSampFreqHz => FrequencyCorrection.Apply(SampFreqHz, ResidualPpm);


        public void Write(BasicHDU hdu)
        {
            hdu.AddValue("ORIGIN", Origin, "Capture device");
            hdu.AddValue("DATAFORM", DataFormat, "IQ sample format");
            hdu.AddValue("FREQ", CentFreqHz, "Center frequency (Hz)");
            hdu.AddValue("SAMPLER", SampFreqHz, "Sample rate (Hz)");
            hdu.AddValue("GAIN", GainDb, "Gain (dB)");
            hdu.AddValue("DWELL", DwellTimeSec, "Dwell time (s)");
            hdu.AddValue("DATE-OBS", ObservationDate.ToString("yyyy-MM-ddTHH:mm:ss"), null);
            hdu.AddValue("FFT_SIZE", FftSize, "FFT size");
            if (SiteLatitudeDeg.HasValue) hdu.AddValue("SITELAT", SiteLatitudeDeg.Value, "Site latitude (deg)");
            if (SiteLongitudeDeg.HasValue) hdu.AddValue("SITELON", SiteLongitudeDeg.Value, "Site longitude (deg)");
            if (SiteElevationM.HasValue) hdu.AddValue("SITEELV", SiteElevationM.Value, "Site elevation (m)");
            if (RaDeg.HasValue) hdu.AddValue("RA", RaDeg.Value, "Right Ascension (deg)");
            if (DecDeg.HasValue) hdu.AddValue("DEC", DecDeg.Value, "Declination (deg)");
            if (RaDeg.HasValue && Equinox.HasValue)
            {
                hdu.AddValue("RADESYS", "FK5", "RA/Dec reference frame");
                hdu.AddValue("EQUINOX", Equinox.Value, "Equinox of RA/Dec (Julian epoch)");
            }
            if (AzDeg.HasValue) hdu.AddValue("AZ", AzDeg.Value, "Azimuth (deg)");
            if (AltDeg.HasValue) hdu.AddValue("ALT", AltDeg.Value, "Altitude (deg)");
            if (PpmCorrection is double ppm)
            {
                hdu.AddValue("PPM", ppm, "Receiver freq correction (ppm)");
                hdu.AddValue("PPM_HW", PpmHardware ?? FrequencyCorrection.HardwarePpm(ppm), "Part set in tuner (ppm); rest on axis");
            }
            if (ClipFraction is double clip)
                hdu.AddValue("CLIPFRAC", clip, "Fraction of I/Q bytes at the ADC rail");

        }

        /// <summary>
        /// Computes the LSR correction (km/s) to add to a topocentric radio-convention
        /// velocity axis for this capture's recorded pointing/time/site (see
        /// AstronomyUtils.ComputeLsrCorrectionKmPerSec), or 0 if not enough was recorded to
        /// compute it (e.g. older files, or a capture with no site configured). Reconstructs
        /// RA/Dec from Az/Alt if the capture was made in AltAz mode rather than Equatorial.
        /// Promoted from VisualiseViewModel.TryComputeLsrCorrectionKmPerSec so both the
        /// single-capture Visualise flow and the multi-position Mosaic flow (which needs a
        /// per-position correction, one pointing per file) share one implementation.
        /// </summary>
        public double ComputeLsrCorrectionKmPerSec()
        {
            if (SiteLatitudeDeg is not double lat || SiteLongitudeDeg is not double lon)
                return 0.0;
            if (ObservationDate == DateTime.MinValue)
                return 0.0;

            double raHours, decDeg;

            if (RaDeg is double raDeg && DecDeg is double dec)
            {
                raHours = raDeg / 15.0;
                decDeg = dec;

                // No EQUINOX (pre-#38 file): assume J2000, which is what this did before.
                if (Equinox is double equinox && equinox != 2000.0)
                    (raHours, decDeg) = AstronomyUtils.Precess(raHours, decDeg,
                        AstronomyUtils.JulianEpochToJd(equinox), AstronomyUtils.JdJ2000);
            }
            else if (AzDeg is double az && AltDeg is double alt)
            {
                // Returns J2000 (precessed from of-date since issue #38).
                (raHours, decDeg) = AstronomyUtils.HorizontalToEquatorial(az, alt, ObservationDate, lat, lon);
            }
            else
            {
                return 0.0; // no pointing recorded at all
            }

            return AstronomyUtils.ComputeLsrCorrectionKmPerSec(raHours, decDeg, ObservationDate, lat, lon);
        }

        public static FitsFileMetaData Read(BasicHDU[] hdus)
        {
            var meta = new FitsFileMetaData();
            Header header = hdus[0].Header;
            meta.Origin = header.GetStringValue("ORIGIN");
            meta.DataFormat = header.GetStringValue("DATAFORM");
            meta.CentFreqHz = header.GetDoubleValue("FREQ");
            meta.SampFreqHz = header.GetDoubleValue("SAMPLER");
            meta.GainDb = header.GetDoubleValue("GAIN");
            meta.DwellTimeSec = header.GetDoubleValue("DWELL");
            meta.FftSize = header.GetIntValue("FFT_SIZE");
            var dateObsStr = header.GetStringValue("DATE-OBS");
            if (DateTime.TryParse(dateObsStr, out DateTime obsDate))
                meta.ObservationDate = obsDate;
            else
                meta.ObservationDate = DateTime.MinValue;
            meta.SiteLatitudeDeg = header.FindCard("SITELAT") != null ? header.GetDoubleValue("SITELAT") : null;
            meta.SiteLongitudeDeg = header.FindCard("SITELON") != null ? header.GetDoubleValue("SITELON") : null;
            meta.SiteElevationM = header.FindCard("SITEELV") != null ? header.GetDoubleValue("SITEELV") : null;
            meta.RaDeg = header.FindCard("RA") != null ? header.GetDoubleValue("RA") : null;
            meta.DecDeg = header.FindCard("DEC") != null ? header.GetDoubleValue("DEC") : null;
            meta.AzDeg = header.FindCard("AZ") != null ? header.GetDoubleValue("AZ") : null;
            meta.AltDeg = header.FindCard("ALT") != null ? header.GetDoubleValue("ALT") : null;
            meta.Equinox = header.FindCard("EQUINOX") != null ? header.GetDoubleValue("EQUINOX") : null;
            meta.PpmCorrection = header.FindCard("PPM") != null ? header.GetDoubleValue("PPM") : null;
            meta.PpmHardware = header.FindCard("PPM_HW") != null ? header.GetIntValue("PPM_HW") : null;
            meta.ClipFraction = header.FindCard("CLIPFRAC") != null ? header.GetDoubleValue("CLIPFRAC") : null;
            return meta;
        }
    }

}
