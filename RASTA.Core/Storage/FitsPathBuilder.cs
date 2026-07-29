using System.IO;

namespace RASTA.Core.Storage
{
    public static class FitsPathBuilder
    {
        public static string BuildCalibrationFilePath(
            string prefix,
            DateTime startTime,
            double frequencyHz,
            double gainDb)
        {
            string timestamp = startTime.ToString("HHmmss");

            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RASTA",
                "Data",
                $"{frequencyHz / 1_000_000:F4}MHz",
                startTime.ToString("yyyy-MM-dd"));

            Directory.CreateDirectory(baseDir);

            return Path.Combine(baseDir,
                $"{prefix}_{timestamp}_{frequencyHz:F0}Hz_{gainDb:F1}dB.fits");
        }

        public static string BuildSweepFilePath(
            string prefix,
            DateTime startTime,
            double frequencyHz,
            string coordString,
            int fileIndex,
            int totalFiles)
        {
            string timestamp = startTime.ToString("HHmmss");

            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RASTA",
                "Data",
                $"{frequencyHz / 1_000_000:F4}MHz",
                startTime.ToString("yyyy-MM-dd"));

            Directory.CreateDirectory(baseDir);

            return Path.Combine(baseDir,
                $"{prefix}_{coordString}_{timestamp}_{fileIndex}of{totalFiles}.fits");
        }
    }
}
