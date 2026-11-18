using NUnit.Framework;
using RASTA.Processing.Dsp;
using RASTA.Processing.HiPipeline;
using RASTA.Tests.Synthetic;

namespace RASTA.Tests.HiPipeline
{
    [TestFixture]
    public class HiStreamingPipelineTests
    {
        private static int IndexOfMax(double[] data)
        {
            int best = 0;
            for (int i = 1; i < data.Length; i++)
                if (data[i] > data[best]) best = i;
            return best;
        }

        [Test]
        public void FftShift_MovesDcBinToCentre()
        {
            var raw = new double[8];
            raw[0] = 1.0; // DC in raw FFT order

            double[] shifted = HiStreamingPipeline.FftShift(raw);

            Assert.That(shifted[4], Is.EqualTo(1.0));
            Assert.That(shifted.Sum(), Is.EqualTo(1.0));
        }

        [Test]
        public void Process_NoiseFree_RecoversLineVelocityAndAmplitude()
        {
            var synth = new SyntheticSpectrum
            {
                Lines = new[] { new SyntheticLine(-30.0, 20.0, 0.05) }
            };
            var (baseline, capture) = synth.Build();

            var pipeline = new HiStreamingPipeline();
            pipeline.Process(baseline, capture, synth.SampleRateHz, synth.CenterFreqHz);

            int peak = IndexOfMax(pipeline.HiSpectrum);
            double channelWidthKmPerSec = Math.Abs(pipeline.VelocityKmPerSec[1] - pipeline.VelocityKmPerSec[0]);

            Assert.That(pipeline.VelocityKmPerSec[peak], Is.EqualTo(-30.0).Within(channelWidthKmPerSec));
            // Ratio is (1 + line) x RatioDisplayScale and the continuum is flat at RatioDisplayScale.
            Assert.That(pipeline.HiSpectrum[peak], Is.EqualTo(0.05 * HiConstants.RatioDisplayScale).Within(0.05));
        }

        [Test]
        public void Process_WithNoise_RecoversLineVelocityAfterSmoothing()
        {
            var synth = new SyntheticSpectrum
            {
                BaselineFrames = 20_000,
                CaptureFrames = 20_000,
                Lines = new[] { new SyntheticLine(-30.0, 20.0, 0.05) }
            };
            var (baseline, capture) = synth.Build();

            var pipeline = new HiStreamingPipeline();
            pipeline.Process(baseline, capture, synth.SampleRateHz, synth.CenterFreqHz,
                smoothing: SmoothingKind.MovingAverage, smoothingWindow: 41);

            int peak = IndexOfMax(pipeline.HiSpectrum);
            Assert.That(pipeline.VelocityKmPerSec[peak], Is.EqualTo(-30.0).Within(3.0));
        }

        [Test]
        public void Process_LsrCorrection_ShiftsVelocityAxisOnly()
        {
            var synth = new SyntheticSpectrum
            {
                Lines = new[] { new SyntheticLine(-30.0, 20.0, 0.05) }
            };
            var (baseline, capture) = synth.Build();

            var topo = new HiStreamingPipeline();
            topo.Process(baseline, capture, synth.SampleRateHz, synth.CenterFreqHz);
            var lsr = new HiStreamingPipeline();
            lsr.Process(baseline, capture, synth.SampleRateHz, synth.CenterFreqHz, lsrCorrectionKmPerSec: 12.5);

            Assert.That(lsr.VelocityKmPerSec[100] - topo.VelocityKmPerSec[100], Is.EqualTo(12.5).Within(1e-9));
            Assert.That(lsr.FrequencyHz, Is.EqualTo(topo.FrequencyHz));
            Assert.That(IndexOfMax(lsr.HiSpectrum), Is.EqualTo(IndexOfMax(topo.HiSpectrum)));
        }

        [Test]
        public void Process_VelocityAxis_IsRadioConvention()
        {
            var synth = new SyntheticSpectrum();
            var (baseline, capture) = synth.Build();

            var pipeline = new HiStreamingPipeline();
            pipeline.Process(baseline, capture, synth.SampleRateHz, synth.CenterFreqHz);

            // Frequency ascends with index; a frequency below the rest frequency is receding (v > 0).
            Assert.That(pipeline.FrequencyHz[1], Is.GreaterThan(pipeline.FrequencyHz[0]));
            int below = Array.FindIndex(pipeline.FrequencyHz, f => f < HiConstants.HiFreqHz);
            Assert.That(pipeline.VelocityKmPerSec[below], Is.GreaterThan(0.0));
            Assert.That(pipeline.FrequencyHz[synth.FftSize / 2], Is.EqualTo(synth.CenterFreqHz));
        }

        [Test]
        public void Process_ExcisesDcSpikePresentInBaseline()
        {
            var synth = new SyntheticSpectrum { DcSpikeRatio = 50.0, CaptureGain = 1.0 };
            var (baseline, capture) = synth.Build();
            // Make the spike differ between the two, so it would not simply cancel in the ratio.
            capture[0] *= 1.5;

            var pipeline = new HiStreamingPipeline();
            pipeline.Process(baseline, capture, synth.SampleRateHz, synth.CenterFreqHz);

            Assert.That(Math.Abs(pipeline.HiSpectrum[synth.FftSize / 2]), Is.LessThan(0.01));
        }

        [Test]
        public void Process_Despike_RemovesNarrowSpurButKeepsBroadLine()
        {
            var synth = new SyntheticSpectrum
            {
                BaselineFrames = 20_000,
                CaptureFrames = 20_000,
                Lines = new[] { new SyntheticLine(-30.0, 20.0, 0.05) }
            };
            var (baseline, capture) = synth.Build();

            // A one-bin spur, capture only, well away from the line and the continuum windows.
            int spurShifted = 3000;
            int spurRaw = (spurShifted + synth.FftSize / 2) % synth.FftSize;
            capture[spurRaw] *= 1.5;

            var raw = new HiStreamingPipeline();
            raw.Process(baseline, capture, synth.SampleRateHz, synth.CenterFreqHz);
            var despiked = new HiStreamingPipeline();
            despiked.Process(baseline, capture, synth.SampleRateHz, synth.CenterFreqHz, despike: true);

            Assert.That(raw.HiSpectrum[spurShifted], Is.GreaterThan(100.0));
            Assert.That(Math.Abs(despiked.HiSpectrum[spurShifted]), Is.LessThan(15.0));

            // The broad line survives: mean over its core is still close to the injected height.
            double channelWidth = Math.Abs(despiked.VelocityKmPerSec[1] - despiked.VelocityKmPerSec[0]);
            var core = Enumerable.Range(0, synth.FftSize)
                .Where(i => Math.Abs(despiked.VelocityKmPerSec[i] + 30.0) < 2.0)
                .Select(i => despiked.HiSpectrum[i])
                .ToArray();
            Assert.That(core.Length, Is.GreaterThan((int)(3.0 / channelWidth)));
            Assert.That(core.Average(), Is.EqualTo(0.05 * HiConstants.RatioDisplayScale).Within(1.5));
        }
    }
}
