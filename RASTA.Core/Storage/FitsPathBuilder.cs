using System.IO;
using System.Text.RegularExpressions;

namespace RASTA.Core.Storage
{
    public static class FitsPathBuilder
    {
        public static string BuildCalibrationFilePath(
            string baseFolder,
            string prefix,
            DateTime startTime,
            double frequencyHz,
            double gainDb)
        {
            string timestamp = startTime.ToString("HHmmss");

            string baseDir = Path.Combine(baseFolder,
                $"{frequencyHz / 1_000_000:F4}MHz",
                startTime.ToString("yyyy-MM-dd"));

            Directory.CreateDirectory(baseDir);

            return Path.Combine(baseDir,
                $"{prefix}_{timestamp}_{frequencyHz:F0}Hz_{gainDb:F1}dB.fits");
        }

        public static string BuildSweepFilePath(
            string baseFolder,
            string prefix,
            DateTime startTime,
            double frequencyHz,
            string coordString,
            int fileIndex,
            int totalFiles)
        {
            string timestamp = startTime.ToString("HHmmss");

            string baseDir = Path.Combine(baseFolder,
                $"{frequencyHz / 1_000_000:F4}MHz",
                startTime.ToString("yyyy-MM-dd"));

            Directory.CreateDirectory(baseDir);

            return Path.Combine(baseDir,
                $"{prefix}_{coordString}_{timestamp}_{fileIndex}of{totalFiles}.fits");
        }

        // Matches the "..._{index}of{total}" suffix BuildSweepFilePath writes onto every
        // sweep capture file (even single-file dwell points, where total == 1) - shared by
        // VisualiseViewModel's single-file "resolve my siblings" lookup and
        // FitsPathBuilder.GroupSweepFiles' whole-folder grouping below.
        private static readonly Regex SweepFilePattern =
            new(@"^(?<base>.+)_(?<index>\d+)of(?<total>\d+)$", RegexOptions.Compiled);

        /// <summary>
        /// Parses a sweep capture file's name (without extension) for the
        /// "{base}_{index}of{total}" dwell-point convention BuildSweepFilePath writes.
        /// Returns false (with all out params zeroed/empty) if the name doesn't match -
        /// e.g. a baseline file, or anything not produced by BuildSweepFilePath.
        /// </summary>
        public static bool TryParseSweepFileName(string fileNameWithoutExtension, out string baseKey, out int index, out int total)
        {
            var match = SweepFilePattern.Match(fileNameWithoutExtension);
            if (!match.Success)
            {
                baseKey = string.Empty;
                index = 0;
                total = 0;
                return false;
            }

            baseKey = match.Groups["base"].Value;
            index = int.Parse(match.Groups["index"].Value);
            total = int.Parse(match.Groups["total"].Value);
            return true;
        }

        /// <summary>
        /// True if the file matches the "base_..." naming convention BuildCalibrationFilePath
        /// writes for a baseline capture (see Calibrator, which always passes prefix "base").
        /// </summary>
        public static bool IsBaselineFile(string filePath) =>
            Path.GetFileNameWithoutExtension(filePath).StartsWith("base_", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Groups a flat list of FITS file paths (e.g. every *.fits file found under one
        /// session folder) into per-dwell-point capture groups, using the
        /// "{base}_{index}of{total}.fits" naming convention BuildSweepFilePath writes. Each
        /// group is ordered by index. Files whose name doesn't match the convention (or that
        /// only ever appear as a lone "1of1") are returned as their own single-file group.
        /// Callers should filter out baseline files (see IsBaselineFile) before calling this -
        /// baseline files never carry the sweep suffix, but it costs nothing to be defensive.
        /// </summary>
        public static List<List<string>> GroupSweepFiles(IEnumerable<string> filePaths)
        {
            var groups = new Dictionary<string, SortedList<int, string>>();
            var ungrouped = new List<string>();

            foreach (var path in filePaths)
            {
                string nameNoExt = Path.GetFileNameWithoutExtension(path);
                if (TryParseSweepFileName(nameNoExt, out var baseKey, out var index, out var total))
                {
                    string key = $"{baseKey}|{total}";
                    if (!groups.TryGetValue(key, out var files))
                        groups[key] = files = new SortedList<int, string>();
                    files[index] = path;
                }
                else
                {
                    ungrouped.Add(path);
                }
            }

            var result = groups.Values.Select(files => files.Values.ToList()).ToList();
            foreach (var single in ungrouped)
                result.Add(new List<string> { single });

            return result;
        }
    }
}
