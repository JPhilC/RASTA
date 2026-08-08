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

        /// <summary>
        /// Reads several FITS files that make up one dwell point (see
        /// FitsPathBuilder.GroupSweepFiles/BuildSweepFilePath's "{n}of{total}" convention)
        /// and concatenates their raw IQ into one buffer, reporting (status, fraction)
        /// progress as it goes - the same convention Calibrator.RunFullCalibrationAsync
        /// uses. Promoted from VisualiseViewModel.ReadCombinedCaptureRawIq so both the
        /// single-capture Visualise flow and the multi-position Mosaic flow share one
        /// implementation.
        ///
        /// Each file's IQ is trimmed to a whole number of its own native FFT frames before
        /// being appended, so a frame extracted later by the caller's chunking loop can
        /// never straddle the boundary between two separate (and physically discontinuous)
        /// captures. All files must agree on FFT size, sample rate, and center frequency;
        /// DwellTimeSec on the returned metadata is the sum across every file combined.
        /// </summary>
        public (FitsFileMetaData Meta, byte[] RawIq) ReadCombinedRawIq(
            IReadOnlyList<string> filePaths,
            Action<string, double>? progressCallback = null)
        {
            if (filePaths == null || filePaths.Count == 0)
                throw new ArgumentException("At least one file path is required.", nameof(filePaths));

            FitsFileMetaData? combinedMeta = null;
            var buffers = new List<byte[]>(filePaths.Count);

            for (int f = 0; f < filePaths.Count; f++)
            {
                string status = filePaths.Count > 1
                    ? $"Reading file {f + 1} of {filePaths.Count}…"
                    : "Reading file…";
                progressCallback?.Invoke(status, (double)f / filePaths.Count);

                var (meta, iq) = ReadRawIq(filePaths[f]);

                if (combinedMeta == null)
                {
                    combinedMeta = meta;
                }
                else
                {
                    if (meta.FftSize != combinedMeta.FftSize ||
                        meta.SampFreqHz != combinedMeta.SampFreqHz ||
                        meta.CentFreqHz != combinedMeta.CentFreqHz)
                    {
                        throw new InvalidOperationException(
                            $"Related capture file '{Path.GetFileName(filePaths[f])}' has a different FFT size, " +
                            "sample rate, or center frequency than the other files being combined.");
                    }

                    // Total integration time across all combined files, not just the first.
                    combinedMeta.DwellTimeSec += meta.DwellTimeSec;
                }

                int bytesPerNativeFrame = meta.FftSize * 2;
                int usableLength = (iq.Length / bytesPerNativeFrame) * bytesPerNativeFrame;
                if (usableLength != iq.Length)
                {
                    var trimmed = new byte[usableLength];
                    Buffer.BlockCopy(iq, 0, trimmed, 0, usableLength);
                    iq = trimmed;
                }

                buffers.Add(iq);

                progressCallback?.Invoke(status, (double)(f + 1) / filePaths.Count);
            }

            int totalLength = buffers.Sum(b => b.Length);
            var combined = new byte[totalLength];
            int offset = 0;
            foreach (var buf in buffers)
            {
                Buffer.BlockCopy(buf, 0, combined, offset, buf.Length);
                offset += buf.Length;
            }

            return (combinedMeta!, combined);
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
