using System.Globalization;
using System.Text;
using NUnit.Framework;
using RASTA.Core.Storage;
using RASTA.Infrastructure.Fft;
using RASTA.Infrastructure.Services;
using RASTA.Processing.HiPipeline;
using RASTA.Processing.Mosaic;

namespace RASTA.Tests.ReferenceData
{
    /// <summary>
    /// Not a pass/fail test: reduces the real reference capture session through
    /// MosaicProcessor (exactly what Generate Mosaic does) and writes the results to
    /// RASTA.Tests/ReferenceSnapshots/{label}.csv and {label}_spectra.csv, so the effect of a
    /// pipeline change on real data can be diffed against an earlier snapshot.
    ///
    /// Skipped unless the RASTA_SNAPSHOT_LABEL environment variable is set, e.g.
    ///
    ///   $env:RASTA_SNAPSHOT_LABEL = "phase1-after"
    ///   dotnet test RASTA.Tests/RASTA.Tests.csproj --filter "Category=ReferenceData"
    ///
    /// RASTA_REFERENCE_DATA overrides the session folder; RASTA_SNAPSHOT_DESPIKE=1 turns the
    /// pipeline's opt-in Despike on (default threshold), RASTA_SNAPSHOT_COMBMASK=1 its opt-in
    /// comb masking. {label}_baseline_spurs.csv lists what BaselineSpurDetector found in the
    /// session's baseline (and the fitted comb, if any). It goes through the app's own
    /// spectrum cache (%LOCALAPPDATA%\RASTA\SpectrumCache), so a session already viewed in
    /// the app takes seconds; an uncached one reads every FITS file and takes minutes.
    /// </summary>
    [TestFixture, Category("ReferenceData")]
    public class ReferenceSnapshotTests
    {
        private const string DefaultReferenceFolder = @"C:\Raw\RASTA\Captures\1420.7000MHz\2026-11-08";

        // Line-free region used for the noise figure: clear of Galactic HI, and inside the
        // channels the receiver's passband still covers at the default 1420.7 MHz centre.
        private const double LineFreeMinAbsKmPerSec = 150.0;
        private const double LineFreeMaxAbsKmPerSec = 250.0;

        [Test]
        public async Task WriteSnapshot()
        {
            string? label = Environment.GetEnvironmentVariable("RASTA_SNAPSHOT_LABEL");
            if (string.IsNullOrWhiteSpace(label))
                Assert.Ignore("Set RASTA_SNAPSHOT_LABEL to write a reference snapshot.");

            string folder = Environment.GetEnvironmentVariable("RASTA_REFERENCE_DATA") ?? DefaultReferenceFolder;
            if (!Directory.Exists(folder))
                Assert.Ignore($"Reference data folder not found: {folder}");

            string baseline = Directory.GetFiles(folder, "*.fits").Single(FitsPathBuilder.IsBaselineFile);

            var loader = new CachedSpectrumLoader(new FitsFileIo(), new FftEngine(), new SpectrumCacheService());
            var processor = new MosaicProcessor(loader);

            MosaicResult result = await processor.ProcessFolderAsync(
                folder,
                baseline,
                targetFftSize: 0,
                integratedWindowKmPerSec: MosaicProcessor.DefaultIntegratedWindowKmPerSec,
                progressCallback: null,
                despike: Environment.GetEnvironmentVariable("RASTA_SNAPSHOT_DESPIKE") == "1",
                combMask: Environment.GetEnvironmentVariable("RASTA_SNAPSHOT_COMBMASK") == "1");

            var positions = result.Positions.OrderBy(p => p.Label, StringComparer.Ordinal).ToList();
            double[] velocity = result.VelocityKmPerSec;

            var inv = CultureInfo.InvariantCulture;
            var summary = new StringBuilder();
            summary.AppendLine("Label,RaHours,DecDeg,LineStrengthDb,PeakVelocityKmPerSec,HiPeak,HiPeakVelocityKmPerSec,HiLineFreeRms");
            foreach (var p in positions)
            {
                // Note: the shared velocity axis carries position 0's LSR correction (see
                // MosaicResult), so HiPeakVelocity/the line-free mask are approximate to within
                // the spread of LSR corrections across the session. PeakVelocityKmPerSec is
                // each position's own.
                int peak = -1;
                var lineFree = new List<double>();
                for (int i = 0; i < velocity.Length; i++)
                {
                    double absV = Math.Abs(velocity[i]);
                    if (absV <= MosaicProcessor.DefaultIntegratedWindowKmPerSec &&
                        (peak < 0 || p.HiSpectrum[i] > p.HiSpectrum[peak]))
                        peak = i;
                    if (absV >= LineFreeMinAbsKmPerSec && absV <= LineFreeMaxAbsKmPerSec)
                        lineFree.Add(p.HiSpectrum[i]);
                }

                double mean = lineFree.Average();
                double rms = Math.Sqrt(lineFree.Sum(x => (x - mean) * (x - mean)) / lineFree.Count);

                summary.AppendLine(string.Join(",",
                    p.Label,
                    p.RaHours?.ToString("F4", inv),
                    p.DecDeg?.ToString("F3", inv),
                    p.LineStrengthDb.ToString("F4", inv),
                    p.PeakVelocityKmPerSec.ToString("F2", inv),
                    p.HiSpectrum[peak].ToString("F3", inv),
                    velocity[peak].ToString("F2", inv),
                    rms.ToString("F3", inv)));
            }

            var spectra = new StringBuilder();
            spectra.Append("VelocityKmPerSec,FrequencyHz");
            foreach (var p in positions)
                spectra.Append(',').Append(p.Label);
            spectra.AppendLine();
            for (int i = 0; i < velocity.Length; i++)
            {
                spectra.Append(velocity[i].ToString("F3", inv)).Append(',').Append(result.FrequencyHz[i].ToString("F1", inv));
                foreach (var p in positions)
                    spectra.Append(',').Append(p.HiSpectrum[i].ToString("F3", inv));
                spectra.AppendLine();
            }

            string outDir = Path.Combine(FindRepoRoot(), "RASTA.Tests", "ReferenceSnapshots");
            Directory.CreateDirectory(outDir);
            string summaryPath = Path.Combine(outDir, $"{label}.csv");
            File.WriteAllText(summaryPath, summary.ToString());
            File.WriteAllText(Path.Combine(outDir, $"{label}_spectra.csv"), spectra.ToString());

            File.WriteAllText(Path.Combine(outDir, $"{label}_baseline_spurs.csv"), DescribeBaselineSpurs(loader, baseline));

            TestContext.Out.WriteLine($"{positions.Count} positions ({result.PositionsFromCache} from cache) -> {summaryPath}");
            Assert.That(positions, Is.Not.Empty);
        }

        private static string DescribeBaselineSpurs(CachedSpectrumLoader loader, string baselineFile)
        {
            var entry = loader.Load(new[] { baselineFile }, skao: false, forceRegenerate: false, "baseline", null, CancellationToken.None, out _);
            double[] shifted = HiStreamingPipeline.FftShift(entry.AveragedPower!);
            var report = BaselineSpurDetector.Detect(shifted, entry.SampFreqHz);

            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("PeakBin,LoBin,HiBin,OffsetKHz,Sigma,ExcessPercent");
            foreach (var s in report.Spurs)
                sb.AppendLine(string.Join(",", s.PeakBin, s.LoBin, s.HiBin,
                    (s.OffsetHz / 1000).ToString("F1", inv), s.Sigma.ToString("F1", inv), (100 * s.ExcessFraction).ToString("F2", inv)));

            if (report.Comb is { } comb)
            {
                sb.AppendLine();
                sb.AppendLine($"# Comb: period {comb.PeriodBins.ToString("F3", inv)} bins ({(comb.PeriodHz / 1000).ToString("F3", inv)} kHz), " +
                    $"{comb.PhasesBins.Count} phases at bins {string.Join(" ", comb.PhasesBins.Select(p => p.ToString("F1", inv)))}, " +
                    $"{comb.TeethExplained} spurs explained, {comb.PredictedToothBins.Count} teeth predicted, " +
                    $"half-width {comb.ToothHalfWidthBins}, masks {(100 * comb.MaskedFraction).ToString("F2", inv)}% of the band");
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("# Comb: none fitted");
            }
            return sb.ToString();
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RASTA.slnx")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Could not locate RASTA.slnx above the test directory.");
        }
    }
}
