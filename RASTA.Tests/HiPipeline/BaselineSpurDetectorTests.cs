using NUnit.Framework;
using RASTA.Processing.HiPipeline;
using RASTA.Tests.Synthetic;

namespace RASTA.Tests.HiPipeline
{
    [TestFixture]
    public class BaselineSpurDetectorTests
    {
        private const int N = 4096;
        private const int Centre = N / 2;

        // Shaped like the 2026-11-08 comb: three teeth per 500-bin period, 155 bins below
        // centre and 193/385 bins above that.
        private const double Period = 500.0;
        private static readonly int[] PhaseOffsets = { -155, -155 + 193, -155 + 385 };

        private static IEnumerable<int> CombTeeth(int margin = 60)
        {
            foreach (int offset in PhaseOffsets)
            {
                for (int k = -6; k <= 6; k++)
                {
                    int bin = Centre + offset + (int)(k * Period);
                    if (bin >= margin && bin < N - margin && Math.Abs(bin - Centre) > 8)
                        yield return bin;
                }
            }
        }

        /// <summary>Steep RTL-SDR-like edge roll-off on top of SyntheticSpectrum's gentle bandpass.</summary>
        private static double RollOff(int shiftedBin)
        {
            double x = (shiftedBin - Centre) / (double)Centre;
            return Math.Exp(-Math.Pow(Math.Abs(x) / 0.9, 8));
        }

        /// <summary>(baseline, capture) in fftshifted order, with the extra roll-off applied to both.</summary>
        private static (double[] baseline, double[] capture) BuildShifted(SyntheticSpectrum synth)
        {
            var (b, c) = synth.Build();
            b = HiStreamingPipeline.FftShift(b);
            c = HiStreamingPipeline.FftShift(c);
            for (int i = 0; i < N; i++)
            {
                double r = RollOff(i);
                b[i] *= r;
                c[i] *= r;
            }
            return (b, c);
        }

        private static void Inject(double[] shifted, int bin, double excessFraction)
        {
            shifted[bin] *= 1.0 + excessFraction;
            shifted[bin - 1] *= 1.0 + 0.4 * excessFraction;
            shifted[bin + 1] *= 1.0 + 0.3 * excessFraction;
        }

        [Test]
        public void Detect_CleanNoise_FlagsAlmostNothing()
        {
            int totalSpurs = 0;
            for (int seed = 1; seed <= 10; seed++)
            {
                var (baseline, _) = BuildShifted(new SyntheticSpectrum { BaselineFrames = 100_000, CaptureFrames = 100_000, Seed = seed });
                var report = BaselineSpurDetector.Detect(baseline, 2_400_000);
                totalSpurs += report.Spurs.Count;
                Assert.That(report.Comb, Is.Null);
            }

            // ~40k bins tested at 6 sigma; expect essentially none.
            Assert.That(totalSpurs, Is.LessThanOrEqualTo(2));
        }

        [Test]
        public void Detect_FindsCombTeethIncludingOnSteepBandEdges()
        {
            var synth = new SyntheticSpectrum { BaselineFrames = 100_000, CaptureFrames = 100_000 };
            var (baseline, _) = BuildShifted(synth);
            var teeth = CombTeeth().ToList();
            foreach (int t in teeth)
                Inject(baseline, t, 0.15);

            var report = BaselineSpurDetector.Detect(baseline, synth.SampleRateHz);

            foreach (int t in teeth)
                Assert.That(report.Flagged[t], Is.True, $"tooth at bin {t} ({(t - Centre) * 586.0 / 1000:F0} kHz) not flagged");
            Assert.That(report.Spurs.Count, Is.EqualTo(teeth.Count));
        }

        [Test]
        public void Detect_FitsCombAndPredictsUndetectableTeeth()
        {
            var synth = new SyntheticSpectrum { BaselineFrames = 100_000, CaptureFrames = 100_000 };
            var (baseline, _) = BuildShifted(synth);
            var teeth = CombTeeth().ToList();
            var weak = new HashSet<int>(teeth.Where((_, i) => i % 4 == 0));
            foreach (int t in teeth)
                Inject(baseline, t, weak.Contains(t) ? 0.003 : 0.15); // weak ~1 sigma: undetectable alone

            var report = BaselineSpurDetector.Detect(baseline, synth.SampleRateHz);

            Assert.That(report.Comb, Is.Not.Null);
            Assert.That(report.Comb!.PeriodBins, Is.EqualTo(Period).Within(0.2));
            Assert.That(report.Comb.PhasesBins.Count, Is.EqualTo(3));
            Assert.That(report.Comb.MaskedFraction, Is.LessThanOrEqualTo(0.05));
            foreach (int t in weak)
            {
                Assert.That(report.Flagged[t], Is.False, $"weak tooth {t} should be below the detection threshold");
                Assert.That(report.CombMask[t], Is.True, $"weak tooth {t} not predicted by the comb fit");
            }
        }

        [Test]
        public void Detect_RandomIsolatedSpurs_FitNoComb()
        {
            int combs = 0;
            for (int seed = 1; seed <= 20; seed++)
            {
                var synth = new SyntheticSpectrum { BaselineFrames = 100_000, CaptureFrames = 100_000, Seed = seed };
                var (baseline, _) = BuildShifted(synth);
                var rng = new Random(seed);
                for (int s = 0; s < 15; s++)
                    Inject(baseline, rng.Next(200, N - 200), 0.15);

                var report = BaselineSpurDetector.Detect(baseline, synth.SampleRateHz);

                Assert.That(report.Spurs.Count, Is.GreaterThanOrEqualTo(12));
                if (report.Comb != null) combs++;
            }

            Assert.That(combs, Is.EqualTo(0));
        }

        [Test]
        public void Process_BaselineComb_RemovedFromHiSpectrumWithLineIntact()
        {
            var synth = new SyntheticSpectrum
            {
                BaselineFrames = 100_000,
                CaptureFrames = 100_000,
                Lines = new[] { new SyntheticLine(-30.0, 20.0, 0.05) }
            };
            var (baseline, capture) = BuildShifted(synth);
            var teeth = CombTeeth().ToList();
            foreach (int t in teeth)
                Inject(baseline, t, 0.15);

            var pipeline = new HiStreamingPipeline();
            pipeline.Process(HiStreamingPipeline.FftShift(baseline), HiStreamingPipeline.FftShift(capture),
                synth.SampleRateHz, synth.CenterFreqHz);

            double[] hi = pipeline.HiSpectrum;
            foreach (int t in teeth)
            {
                // Local robust level/scatter of the surrounding HI spectrum.
                var around = Enumerable.Range(t - 40, 81).Where(i => Math.Abs(i - t) > 5).Select(i => hi[i]).OrderBy(v => v).ToList();
                double median = around[around.Count / 2];
                var dev = around.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToList();
                double sigma = dev[dev.Count / 2] * 1.4826;
                for (int b = t - 1; b <= t + 1; b++)
                    Assert.That(Math.Abs(hi[b] - median), Is.LessThan(6 * sigma), $"dip left at bin {b}");
            }

            // The broad line is untouched.
            var core = Enumerable.Range(0, N)
                .Where(i => Math.Abs(pipeline.VelocityKmPerSec[i] + 30.0) < 2.0)
                .Select(i => hi[i])
                .ToArray();
            Assert.That(core.Average(), Is.EqualTo(0.05 * HiConstants.RatioDisplayScale).Within(1.5));
        }

        [Test]
        public void Process_CombMask_RemovesCaptureOnlyTeethOnPredictedPositions()
        {
            var synth = new SyntheticSpectrum { BaselineFrames = 100_000, CaptureFrames = 100_000 };
            var (baseline, capture) = BuildShifted(synth);
            var teeth = CombTeeth().ToList();
            // Most teeth in the baseline; one tooth present ONLY in the capture.
            int captureOnly = teeth[5];
            foreach (int t in teeth.Where(t => t != captureOnly))
                Inject(baseline, t, 0.15);
            Inject(capture, captureOnly, 0.15);

            var plain = new HiStreamingPipeline();
            plain.Process(HiStreamingPipeline.FftShift(baseline), HiStreamingPipeline.FftShift(capture),
                synth.SampleRateHz, synth.CenterFreqHz);
            var masked = new HiStreamingPipeline();
            masked.Process(HiStreamingPipeline.FftShift(baseline), HiStreamingPipeline.FftShift(capture),
                synth.SampleRateHz, synth.CenterFreqHz, combMask: true);

            Assert.That(plain.HiSpectrum[captureOnly], Is.GreaterThan(30.0)); // 15% x 300
            Assert.That(Math.Abs(masked.HiSpectrum[captureOnly]), Is.LessThan(5.0));
        }

        [Test]
        public void IfftShift_InvertsFftShift()
        {
            foreach (int n in new[] { 8, 9 })
            {
                var data = Enumerable.Range(0, n).Select(i => (double)i).ToArray();
                Assert.That(HiStreamingPipeline.IfftShift(HiStreamingPipeline.FftShift(data)), Is.EqualTo(data));
            }
        }

        // Target FFT Size path (Visualise/Mosaic): clean at native resolution, then bin. Binning
        // first smears each tooth across a coarse channel, and the comb can't be fitted there.
        [Test]
        public void NativeCleaningThenBinning_FitsCombAndRemovesCaptureOnlyTooth()
        {
            var synth = new SyntheticSpectrum { BaselineFrames = 100_000, CaptureFrames = 100_000 };
            var (baseline, capture) = BuildShifted(synth);
            var teeth = CombTeeth().ToList();
            int captureOnly = teeth[5];
            foreach (int t in teeth.Where(t => t != captureOnly))
                Inject(baseline, t, 0.15);
            Inject(capture, captureOnly, 0.15);

            double[] rawBaseline = HiStreamingPipeline.FftShift(baseline);
            double[] rawCapture = HiStreamingPipeline.FftShift(capture);

            // Binning first (the old behaviour) loses the comb.
            var binnedFirst = new HiStreamingPipeline();
            binnedFirst.Process(SpectrumBinner.BinAverage(rawBaseline, 512), SpectrumBinner.BinAverage(rawCapture, 512),
                synth.SampleRateHz, synth.CenterFreqHz, combMask: true);
            Assert.That(binnedFirst.BaselineSpurs!.Comb, Is.Null);

            var report = HiStreamingPipeline.DetectBaselineSpurs(rawBaseline, synth.SampleRateHz, combMask: true);
            Assert.That(report.Comb, Is.Not.Null);

            double[] cleanBaseline = SpectrumBinner.BinAverage(
                HiStreamingPipeline.ApplyBaselineSpurCleaning(rawBaseline, report, combMask: true), 512);
            double[] cleanCapture = SpectrumBinner.BinAverage(
                HiStreamingPipeline.ApplyBaselineSpurCleaning(rawCapture, report, combMask: true), 512);
            var pipeline = new HiStreamingPipeline();
            pipeline.Process(cleanBaseline, cleanCapture, synth.SampleRateHz, synth.CenterFreqHz, cleanBaselineSpurs: false);

            int coarse = captureOnly / 8; // shifted order is preserved by binning (n/2 is a multiple of 8)
            Assert.That(Math.Abs(pipeline.HiSpectrum[coarse]), Is.LessThan(2.0));
        }
    }
}
