using NUnit.Framework;
using RASTA.Core.Sdr;
using RASTA.Core.Storage;
using RASTA.Processing.HiPipeline;
using RASTA.Tests.Synthetic;

namespace RASTA.Tests.Sdr
{
    /// <summary>Receiver ppm correction (issue #32).</summary>
    [TestFixture]
    public class FrequencyCorrectionTests
    {
        [TestCase(0.0, 0, 0.0)]
        [TestCase(1.3, 1, 0.3)]
        [TestCase(1.5, 2, -0.5)]
        [TestCase(-1.5, -2, 0.5)]
        [TestCase(-0.4, 0, -0.4)]
        [TestCase(42.0, 42, 0.0)]
        public void Split_WholePartToTuner_RemainderToAxis(double ppm, int hardware, double residual)
        {
            Assert.That(FrequencyCorrection.HardwarePpm(ppm), Is.EqualTo(hardware));
            Assert.That(FrequencyCorrection.ResidualPpm(ppm), Is.EqualTo(residual).Within(1e-12));
        }

        [Test]
        public void ResidualPpm_UsesRecordedHardwarePart()
        {
            // Whatever rounding wrote the file, the residual is what the tuner didn't apply.
            Assert.That(FrequencyCorrection.ResidualPpm(1.5, hardwarePpm: 1), Is.EqualTo(0.5).Within(1e-12));
        }

        [Test]
        public void Apply_OnePpmAt1420MHz_Is1420Hz()
        {
            Assert.That(FrequencyCorrection.Apply(1_420_000_000, 1.0) - 1_420_000_000, Is.EqualTo(1420.0).Within(1e-6));
            Assert.That(FrequencyCorrection.Apply(1_420_000_000, 0.0), Is.EqualTo(1_420_000_000));
        }

        [Test]
        public void AreEqual_TreatsNullAsZero()
        {
            Assert.That(FrequencyCorrection.AreEqual(null, 0.0), Is.True);
            Assert.That(FrequencyCorrection.AreEqual(null, null), Is.True);
            Assert.That(FrequencyCorrection.AreEqual(1.3, 1.3), Is.True);
            Assert.That(FrequencyCorrection.AreEqual(null, 1.0), Is.False);
        }

        private static int IndexOfMax(double[] data)
        {
            int best = 0;
            for (int i = 1; i < data.Length; i++)
                if (data[i] > data[best]) best = i;
            return best;
        }

        /// <summary>
        /// A receiver whose crystal runs fast by a residual the tuner didn't apply puts every
        /// sky frequency at a bin whose nominal-axis frequency is too low by that factor. Built
        /// here by synthesising on the true axis (centre and sample rate both scaled, since they
        /// share the crystal); reducing with the nominal values misplaces the line, reducing with
        /// the corrected ones recovers it.
        /// </summary>
        [Test]
        public void ResidualCorrection_RecoversLineVelocity()
        {
            const double nominalCentreHz = 1_420_700_000;
            const double nominalRateHz = 2_400_000;
            const double ppm = 3.4; // tuner +3, residual +0.4 ppm, ~0.57 kHz, ~0.12 km/s
            const double lineVelocity = -30.0;
            double residual = FrequencyCorrection.ResidualPpm(ppm);

            var synth = new SyntheticSpectrum
            {
                FftSize = 65536, // ~37 Hz channels, so a 0.12 km/s shift is ~15 channels
                CenterFreqHz = FrequencyCorrection.Apply(nominalCentreHz, residual),
                SampleRateHz = FrequencyCorrection.Apply(nominalRateHz, residual),
                Lines = new[] { new SyntheticLine(lineVelocity, 2.0, 0.05) }
            };
            var (baseline, capture) = synth.Build();

            var nominal = new HiStreamingPipeline();
            nominal.Process(baseline, capture, nominalRateHz, nominalCentreHz);
            var corrected = new HiStreamingPipeline();
            corrected.Process(baseline, capture,
                FrequencyCorrection.Apply(nominalRateHz, residual),
                FrequencyCorrection.Apply(nominalCentreHz, residual));

            double channel = Math.Abs(corrected.VelocityKmPerSec[1] - corrected.VelocityKmPerSec[0]);
            double recovered = corrected.VelocityKmPerSec[IndexOfMax(corrected.HiSpectrum)];
            double uncorrected = nominal.VelocityKmPerSec[IndexOfMax(nominal.HiSpectrum)];

            Assert.That(recovered, Is.EqualTo(lineVelocity).Within(channel));
            // Uncorrected, the line reads ~ +c * residual * 1e-6 * (f/f0) too high (radio convention: lower frequency, higher velocity).
            double expectedError = HiConstants.SpeedOfLightKmPerSec * residual * 1e-6;
            Assert.That(uncorrected - lineVelocity, Is.EqualTo(expectedError).Within(2 * channel));
        }

        [Test]
        public void Fits_RoundTripsPpm_AndOldFilesReadAsUncorrected()
        {
            string dir = Path.Combine(Path.GetTempPath(), "rasta-ppm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var io = new FitsFileIo();
                var iq = new byte[4096 * 2];

                FitsFileMetaData Meta(double? ppm) => new()
                {
                    Origin = "test",
                    DataFormat = "UINT8_IQ",
                    CentFreqHz = 1_420_700_000,
                    SampFreqHz = 2_400_000,
                    FftSize = 4096,
                    ObservationDate = new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc),
                    PpmCorrection = ppm
                };

                string withPpm = Path.Combine(dir, "with.fits");
                io.WriteRawIq(withPpm, iq, Meta(-1.7));
                var read = io.ReadRawIq(withPpm).Meta;
                Assert.That(read.PpmCorrection, Is.EqualTo(-1.7).Within(1e-12));
                Assert.That(read.PpmHardware, Is.EqualTo(-2));
                Assert.That(read.ResidualPpm, Is.EqualTo(0.3).Within(1e-12));
                Assert.That(read.AxisCentFreqHz, Is.EqualTo(1_420_700_000 * (1 + 0.3e-6)).Within(1e-3));

                string without = Path.Combine(dir, "without.fits");
                io.WriteRawIq(without, iq, Meta(null));
                var old = io.ReadRawIq(without).Meta;
                Assert.That(old.PpmCorrection, Is.Null);
                Assert.That(old.PpmHardware, Is.Null);
                Assert.That(old.AxisCentFreqHz, Is.EqualTo(old.CentFreqHz));
                Assert.That(old.AxisSampFreqHz, Is.EqualTo(old.SampFreqHz));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
