using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RASTA.Infrastructure.Services
{
    /// <summary>
    /// The expensive, parameter-independent product of reducing one file set (a baseline
    /// file, or a capture file plus its "_{n}of{total}" siblings): the native-resolution
    /// power spectrum accumulated across every FFT frame, plus the handful of FITS header
    /// values the Visualise charts need - so a chart can be rebuilt without reading the
    /// raw IQ again. Both spectra are in raw FFT-bin order (DC at index 0), exactly as
    /// the code that produced them left them.
    /// </summary>
    public sealed class SpectrumCacheEntry
    {
        public int PipelineVersion { get; set; }

        /// <summary>The "path|size|last-write" identity of every file that went in, for inspection only.</summary>
        public List<string> Files { get; set; } = new();

        public int FileCount { get; set; }
        public int FftSize { get; set; }
        public double CentFreqHz { get; set; }
        public double SampFreqHz { get; set; }
        public double GainDb { get; set; }
        public double LsrCorrectionKmPerSec { get; set; }

        // Recorded pointing, exactly as FitsFileMetaData holds it (whichever pair the
        // capture's CoordinateMode didn't produce is null) - what MosaicProcessor needs to
        // place a position without reopening its FITS file.
        public double? RaDeg { get; set; }
        public double? DecDeg { get; set; }
        public double? AzDeg { get; set; }
        public double? AltDeg { get; set; }

        /// <summary>Hann-windowed IFftEngine.ComputeSkAoPower, averaged over AveragedFrames native frames.</summary>
        public double[]? AveragedPower { get; set; }
        public int AveragedFrames { get; set; }

        /// <summary>SkaoHiObservation.AccumulateBinnedPower: unwindowed, bin-summed to 256 channels, summed over SkaoFrames frames.</summary>
        public double[]? SkaoBinnedPower { get; set; }
        public int SkaoFrames { get; set; }
    }

    /// <summary>
    /// Disk cache of <see cref="SpectrumCacheEntry"/> under %LOCALAPPDATA%\RASTA\SpectrumCache
    /// (issue #19). Keyed on the identity of the source files (full path, size, last-write
    /// time) plus <see cref="PipelineVersion"/> - deliberately NOT on spectrum mode, Target
    /// FFT Size, smoothing, despike or dB: none of those affect the cached product, they are
    /// all re-applied (cheaply) to it on every chart generation, so a change to any of them
    /// can never be served a stale result.
    /// </summary>
    public sealed class SpectrumCacheService
    {
        /// <summary>
        /// Bump whenever anything that feeds a cached value changes: IFftEngine.ComputeSkAoPower,
        /// SkaoFftPower/SkaoHiObservation.AccumulateBinnedPower, FitsFileIo's raw IQ read/combine,
        /// FitsFileMetaData.ComputeLsrCorrectionKmPerSec, or which header values
        /// SpectrumCacheEntry carries. Changes to anything downstream of
        /// the averaged spectrum (binning, despike, DC-spike excision, continuum fit, smoothing)
        /// do NOT need a bump - those always run fresh.
        ///
        /// 2: added the recorded pointing (RaDeg/DecDeg/AzDeg/AltDeg) for the Mosaic tab.
        /// </summary>
        public const int PipelineVersion = 2;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        private readonly string _folder;

        public SpectrumCacheService()
        {
            _folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RASTA",
                "SpectrumCache");
        }

        /// <summary>One "full path|size|last-write ticks" line per file, in the order given.</summary>
        public static List<string> DescribeFiles(IReadOnlyList<string> filePaths)
        {
            var result = new List<string>(filePaths.Count);
            foreach (var path in filePaths)
            {
                var info = new FileInfo(path);
                result.Add($"{info.FullName.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
            }
            return result;
        }

        private string PathFor(IReadOnlyList<string> fileDescriptions)
        {
            string identity = $"v{PipelineVersion}\n{string.Join("\n", fileDescriptions)}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
            return Path.Combine(_folder, Convert.ToHexString(hash, 0, 16) + ".json");
        }

        /// <summary>
        /// Returns the entry for exactly this file set, or null if there is none (or it is
        /// unreadable/from another pipeline version). Never throws - a broken cache must
        /// only ever cost a regeneration.
        /// </summary>
        public SpectrumCacheEntry? TryLoad(IReadOnlyList<string> filePaths)
        {
            try
            {
                var description = DescribeFiles(filePaths);
                string path = PathFor(description);
                if (!File.Exists(path))
                    return null;

                var entry = JsonSerializer.Deserialize<SpectrumCacheEntry>(File.ReadAllBytes(path), JsonOptions);
                if (entry is null || entry.PipelineVersion != PipelineVersion || !entry.Files.SequenceEqual(description))
                    return null;

                return entry;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Writes (or replaces) the entry for this file set. Never throws.</summary>
        public void Save(IReadOnlyList<string> filePaths, SpectrumCacheEntry entry)
        {
            try
            {
                var description = DescribeFiles(filePaths);
                entry.PipelineVersion = PipelineVersion;
                entry.Files = description;

                Directory.CreateDirectory(_folder);
                string path = PathFor(description);
                string temp = path + ".tmp";
                File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions));
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                // A cache that can't be written just means the next run regenerates.
            }
        }

        /// <summary>Deletes every cached entry; returns how many were removed.</summary>
        public int Clear()
        {
            int removed = 0;
            try
            {
                if (!Directory.Exists(_folder))
                    return 0;

                foreach (var file in Directory.EnumerateFiles(_folder))
                {
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch
                    {
                        // skip anything locked
                    }
                }
            }
            catch
            {
            }
            return removed;
        }
    }
}
