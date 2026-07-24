using nom.tam.fits;
using nom.tam.util;
using System.IO;

namespace RASTA.Infrastructure.Storage
{
    public class FitsFileMetaData
    {
        public string Origin { get; set; } = string.Empty;
        public string DataFormat { get; set; } = string.Empty;
        public double CentFreqHz { get; set; }
        public double SampFreqHz { get; set; }

        public double DwellTimeSec { get; set; }

        public double GainDb { get; set; }

        public DateTime ObservationDate { get; set; }

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
            var dateObsStr = header.GetStringValue("DATE-OBS");
            if (DateTime.TryParse(dateObsStr, out DateTime obsDate))
                meta.ObservationDate = obsDate;
            else
                meta.ObservationDate = DateTime.MinValue;
            meta.RaDeg = header.FindCard("RA") != null ? header.GetDoubleValue("RA") : null;
            meta.DecDeg = header.FindCard("DEC") != null ? header.GetDoubleValue("DEC") : null;
            meta.AzDeg = header.FindCard("AZ") != null ? header.GetDoubleValue("AZ") : null;
            meta.AltDeg = header.FindCard("ALT") != null ? header.GetDoubleValue("ALT") : null;
            return meta;
        }
    }

    public sealed class FitsFileIo
    {

        public void WriteRawIq(
            string filePath,
            byte[] rawIq,
            FitsFileMetaData meta)
        {
            if (rawIq == null || rawIq.Length == 0)
                throw new InvalidOperationException("RAW IQ buffer is empty — capture failed.");

            // Reshape into byte[samples][2]
            int NumSamples = rawIq.Length / 2;
            var jaggedIq = new byte[NumSamples][];
            for (int s = 0; s < NumSamples; s++)
            {
                jaggedIq[s] = new byte[] { rawIq[s * 2], rawIq[s * 2 + 1] };
            }

            // --- Write -------------------------------------------------------
            Fits fitsOut = new Fits();
            BasicHDU hdu = Fits.MakeHDU(jaggedIq);
            meta.Write(hdu);

            fitsOut.AddHDU(hdu);

            WriteFitsToFile(fitsOut, filePath);

        }

        private static string WriteFitsToFile(Fits fits, string path)
        {
            using (FileStream fs = File.Create(path))
            using (BufferedDataStream bds = new BufferedDataStream(fs))
            {
                fits.Write(bds);
            }
            return path;
        }

        public (FitsFileMetaData Meta, byte[] RawIq) ReadRawIq(string filePath)
        {
            Fits fitsIn = new Fits(filePath);
            try
            {
                BasicHDU[] hdus = fitsIn.Read();
                // Get the metadata from the header
                var meta = FitsFileMetaData.Read(hdus);
                // Validate format
                if (meta.DataFormat == null || meta.DataFormat != "UINT8_IQ")
                    throw new InvalidOperationException($"Invalid FITS file format: {meta.DataFormat}. Expected 'UINT8_IQ'.");

                // Read the data
                var rawIq = (byte[])ArrayFuncs.Flatten(hdus[0].Data.Kernel);
                return (meta, rawIq);
            }
            finally
            {
                fitsIn.Close();
            }
        }
    }
}
