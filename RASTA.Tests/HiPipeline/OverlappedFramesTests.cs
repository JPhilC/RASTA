using NUnit.Framework;
using RASTA.Core.Storage;
using RASTA.Infrastructure.Fft;
using RASTA.Processing.HiPipeline;

namespace RASTA.Tests.HiPipeline
{
    /// <summary>Half-overlapped (Welch) frame averaging, issue #22.</summary>
    [TestFixture]
    public class OverlappedFramesTests
    {
        private const int FftSize = 256;

        /// <summary>Complex Gaussian noise as 8-bit offset IQ, well inside the ADC range.</summary>
        private static byte[] Noise(int samples, int seed, double sigma = 12.0)
        {
            var rng = new Random(seed);
            var iq = new byte[samples * 2];
            for (int i = 0; i < iq.Length; i++)
            {
                double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
                double g = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                iq[i] = (byte)Math.Clamp(Math.Round(128 + sigma * g), 0, 255);
            }
            return iq;
        }

        private static double[] EndToEndAverage(byte[] iq, FftEngine fft)
        {
            int frameBytes = FftSize * 2, frames = iq.Length / frameBytes;
            var sum = new double[FftSize];
            var frame = new byte[frameBytes];
            for (int f = 0; f < frames; f++)
            {
                Buffer.BlockCopy(iq, f * frameBytes, frame, 0, frameBytes);
                var p = fft.ComputeSkAoPower(frame, FftSize);
                for (int i = 0; i < FftSize; i++) sum[i] += p[i];
            }
            return sum.Select(v => v / frames).ToArray();
        }

        /// <summary>Per-bin scatter relative to the mean, skipping DC (the 128 offset's rounding bias lands there).</summary>
        private static (double Mean, double RelScatter) Stats(double[] spectrum)
        {
            var bins = spectrum.Skip(1).ToArray();
            double mean = bins.Average();
            double sd = Math.Sqrt(bins.Sum(v => (v - mean) * (v - mean)) / (bins.Length - 1));
            return (mean, sd / mean);
        }

        [Test]
        public void SyntheticNoise_ScatterDrops_MeanUnchanged()
        {
            var fft = new FftEngine();
            double ratioSum = 0, meanRatioSum = 0;
            const int trials = 8;
            for (int t = 0; t < trials; t++)
            {
                var iq = Noise(FftSize * 64, seed: 100 + t);

                var (meanPlain, scatterPlain) = Stats(EndToEndAverage(iq, fft));
                var (meanOverlap, scatterOverlap) = Stats(OverlappedFrames.AveragePower(iq, FftSize, fft, out int frames));

                Assert.That(frames, Is.EqualTo(2 * 64 - 1));
                ratioSum += scatterPlain / scatterOverlap;
                meanRatioSum += meanOverlap / meanPlain;
            }

            double improvement = ratioSum / trials;
            TestContext.Out.WriteLine($"Scatter improvement {improvement:F3}x, mean ratio {meanRatioSum / trials:F4}");

            // Issue #22 expects about 1.37x; this measures ~1.35x.
            Assert.That(improvement, Is.InRange(1.25, 1.45));
            Assert.That(meanRatioSum / trials, Is.EqualTo(1.0).Within(0.01));
        }

        [Test]
        public void FrameOffsets_RestartAtEachSegment_NoFrameSpansTwo()
        {
            int frameBytes = OverlappedFrames.FrameBytes(FftSize);
            // Three files: 3, 1 and 5 whole frames.
            int[] segments = [3 * frameBytes, 1 * frameBytes, 5 * frameBytes];
            int total = segments.Sum();

            var offsets = OverlappedFrames.FrameOffsets(total, FftSize, segments).ToList();

            var starts = new List<int> { 0, segments[0], segments[0] + segments[1] };
            for (int s = 0; s < segments.Length; s++)
            {
                int start = starts[s], end = start + segments[s];
                var inSegment = offsets.Where(o => o >= start && o < end).ToList();
                Assert.That(inSegment, Is.Not.Empty);
                Assert.That(inSegment[0], Is.EqualTo(start), "sequence restarts at the segment start");
                Assert.That(inSegment.All(o => o + frameBytes <= end), "no frame spans a boundary");
                Assert.That(inSegment, Has.Count.EqualTo(2 * (segments[s] / frameBytes) - 1));
            }
            Assert.That(offsets, Has.Count.EqualTo(5 + 1 + 9));
        }

        [Test]
        public void MultiFileDwellPoint_SegmentLengthsMatchFiles()
        {
            var dir = Path.Combine(Path.GetTempPath(), "RASTA.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var io = new FitsFileIo();
                var meta = new FitsFileMetaData
                {
                    Origin = "test",
                    DataFormat = "UINT8_IQ",
                    CentFreqHz = 1_420_700_000,
                    SampFreqHz = 2_400_000,
                    FftSize = FftSize,
                    ObservationDate = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc)
                };
                // Second file has a part-frame tail, which ReadCombinedRawIq trims.
                var a = Path.Combine(dir, "cap_1of2.fits");
                var b = Path.Combine(dir, "cap_2of2.fits");
                io.WriteRawIq(a, Noise(FftSize * 3, 1), meta);
                io.WriteRawIq(b, Noise(FftSize * 2 + 10, 2), meta);

                var (_, iq, segments) = io.ReadCombinedRawIq([a, b]);

                Assert.That(segments, Is.EqualTo(new[] { FftSize * 3 * 2, FftSize * 2 * 2 }));
                Assert.That(iq.Length, Is.EqualTo(segments.Sum()));
                Assert.That(OverlappedFrames.FrameOffsets(iq.Length, FftSize, segments).Count(), Is.EqualTo(5 + 3));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void StartOffset_SkipsSettlePeriod()
        {
            int frameBytes = OverlappedFrames.FrameBytes(FftSize);
            var offsets = OverlappedFrames.FrameOffsets(4 * frameBytes, FftSize, startOffset: frameBytes).ToList();

            Assert.That(offsets[0], Is.EqualTo(frameBytes));
            Assert.That(offsets, Has.Count.EqualTo(5));
        }

        [Test]
        public void ShorterThanOneFrame_NoFrames()
        {
            Assert.That(OverlappedFrames.CountFrames(FftSize * 2 - 1, FftSize), Is.EqualTo(0));
            Assert.That(OverlappedFrames.FrameOffsets(FftSize * 2 - 1, FftSize), Is.Empty);
        }
    }
}
