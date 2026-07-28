using RASTA.Core.Sdr;
using RASTA.Core.Storage;
using System.IO;

namespace RASTA.App.Services
{
    public sealed class SdrRawCaptureService
    {
        private readonly ISdrDevice _sdr;
        private readonly FitsFileIo _fits;

        public SdrRawCaptureService(ISdrDevice sdr, FitsFileIo fits)
        {
            _sdr = sdr;
            _fits = fits;
        }

        public async Task<string> CaptureRawIqToFitsAsync(
            double frequencyHz,
            double sampleRateHz,
            double gainDb,
            TimeSpan dwell,
            CancellationToken ct)
        {

            var timestamp = DateTime.UtcNow;

            // -----------------------------
            // 2. Compute sample count
            // -----------------------------
            uint sampleCount = (uint)(sampleRateHz * dwell.TotalSeconds);

            // -----------------------------
            // 3. Capture RAW IQ
            // -----------------------------
            var rawIq = await _sdr.CaptureRawIqAsync(frequencyHz, sampleRateHz, gainDb, sampleCount, ct);

            if (IsMalformedRawIq(rawIq, sampleCount))
                throw new InvalidOperationException("RAW IQ buffer is malformed — capture failed.");

            // -----------------------------
            // 4. Build output directory
            // -----------------------------
            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RASTA",
                "RawIqData",
                $"{frequencyHz / 1_000_000:F4}MHz",
                timestamp.ToString("yyyy-MM-dd"));

            Directory.CreateDirectory(baseDir);

            string filePath = Path.Combine(baseDir, timestamp.ToString("HH-mm-ss") + ".fits");

            // -----------------------------
            // 5. Write FITS file
            // -----------------------------
            var meta = new FitsFileMetaData
            {
                Origin = "RTL-SDR",
                DataFormat = "UINT8_IQ",
                CentFreqHz = frequencyHz,
                SampFreqHz = sampleRateHz,
                GainDb = gainDb,
                ObservationDate = timestamp,
                DwellTimeSec = dwell.TotalSeconds
            };

            _fits.WriteRawIq(
                filePath,
                rawIq,
                meta);

            return filePath;
        }

        private bool IsMalformedRawIq(byte[] rawIq, uint sampleCount)
        {
            if (rawIq == null)
                return true;

            // 1. Must not be empty
            if (rawIq.Length == 0)
                return true;

            // 2. Must be even length (I/Q pairs)
            if ((rawIq.Length % 2) != 0)
                return true;

            // 3. Must match expected size
            ulong expectedBytes = (ulong)sampleCount * 2UL;
            if ((ulong)rawIq.Length != expectedBytes)
                return true;

            // 4. Must not be all zeros
            if (rawIq.All(b => b == 0))
                return true;

            // 5. Must not be all 255
            if (rawIq.All(b => b == 255))
                return true;

            // 6. Must not be constant
            byte first = rawIq[0];
            if (rawIq.All(b => b == first))
                return true;

            // 7. Must contain plausible ADC values
            if (rawIq.Any(b => b > 255))
                return true;

            return false; // Looks valid
        }
    }
}
