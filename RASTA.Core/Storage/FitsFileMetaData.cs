using nom.tam.fits;

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
            if (AzDeg.HasValue) hdu.AddValue("AZ", AzDeg.Value, "Azimuth (deg)");
            if (AltDeg.HasValue) hdu.AddValue("ALT", AltDeg.Value, "Altitude (deg)");

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
            return meta;
        }
    }

}
