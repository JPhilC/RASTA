using nom.tam.fits;
using nom.tam.util;
using System.IO;

namespace RASTA.Core.Storage
{

    public sealed class FitsFileIo
    {

        public void WriteRawIq(
            string filePath,
            byte[] rawIq,
            FitsFileMetaData meta)
        {
            if (rawIq == null || rawIq.Length == 0)
                throw new InvalidOperationException("RAW IQ buffer is empty — capture failed.");

            // Reshape into byte[2][samples] — I-plane and Q-plane as two large arrays.
            // This avoids allocating millions of tiny byte[2] objects (one per sample)
            // which causes severe GC pressure and prevents memory recovery after the write.
            // NOTE: data is stored as planar (I0..IN, Q0..QN), not interleaved. ReadRawIq re-interleaves on load.
            int numSamples = rawIq.Length / 2;
            var iSamples = new byte[numSamples];
            var qSamples = new byte[numSamples];
            for (int s = 0; s < numSamples; s++)
            {
                iSamples[s] = rawIq[s * 2];
                qSamples[s] = rawIq[s * 2 + 1];
            }
            var jaggedIq = new byte[][] { iSamples, qSamples };

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

                // Read the data — stored as planar byte[2][N] (I-plane, Q-plane), re-interleave to I0Q0I1Q1...
                var flat = (byte[])ArrayFuncs.Flatten(hdus[0].Data.Kernel);
                int numSamples = flat.Length / 2;
                var rawIq = new byte[flat.Length];
                for (int s = 0; s < numSamples; s++)
                {
                    rawIq[s * 2]     = flat[s];
                    rawIq[s * 2 + 1] = flat[s + numSamples];
                }
                return (meta, rawIq);
            }
            finally
            {
                fitsIn.Close();
            }
        }
    }
}
