using NUnit.Framework;
using RASTA.Core.Sdr;
using RASTA.Core.Storage;

namespace RASTA.Tests.Sdr
{
    /// <summary>ADC clipping check on raw I/Q bytes (issue #33).</summary>
    [TestFixture]
    public class AdcClippingTests
    {
        /// <summary>Unsigned 8-bit I/Q of Gaussian noise centred on 127.5 with the given rms in ADC counts.</summary>
        private static byte[] NoiseIq(int bytes, double rmsCounts, int seed = 1)
        {
            var rng = new Random(seed);
            var iq = new byte[bytes];
            for (int i = 0; i < bytes; i++)
            {
                double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
                double g = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                iq[i] = (byte)Math.Clamp(Math.Round(127.5 + g * rmsCounts), 0, 255);
            }
            return iq;
        }

        [Test]
        public void Fraction_CountsOnlyRailBytes()
        {
            var iq = new byte[1000];
            Array.Fill(iq, (byte)128);
            iq[0] = 0;
            iq[1] = 255;
            iq[2] = 1;   // near the rail, not on it
            iq[3] = 254;
            Assert.That(AdcClipping.Fraction(iq), Is.EqualTo(2.0 / 1000));
            Assert.That(AdcClipping.Fraction(Array.Empty<byte>()), Is.EqualTo(0.0));
        }

        [Test]
        public void NormalNoise_IsNearZero_AndNotClipped()
        {
            // A typical well-set gain: rms ~ 20 counts, rail is >6 sigma away.
            double f = AdcClipping.Fraction(NoiseIq(1_000_000, 20));
            Assert.That(f, Is.LessThan(1e-5));
            Assert.That(AdcClipping.IsClipped(f), Is.False);
        }

        [Test]
        public void GainTooHigh_IsFlagged()
        {
            // Rms ~ 60 counts: the rail is ~2 sigma away, so several percent of samples clip.
            double f = AdcClipping.Fraction(NoiseIq(1_000_000, 60));
            Assert.That(f, Is.GreaterThan(0.01));
            Assert.That(AdcClipping.IsClipped(f), Is.True);
            Assert.That(AdcClipping.Describe(f), Does.EndWith("- clipped"));
        }

        [Test]
        public void Threshold_AndNotMeasured()
        {
            Assert.That(AdcClipping.IsClipped(AdcClipping.MaxAcceptableFraction), Is.False);
            Assert.That(AdcClipping.IsClipped(AdcClipping.MaxAcceptableFraction * 1.01), Is.True);
            Assert.That(AdcClipping.IsClipped(null), Is.False);
            Assert.That(AdcClipping.Describe(null), Is.EqualTo("not measured"));
            Assert.That(AdcClipping.Percent(0.00012), Is.EqualTo("0.012%"));
            Assert.That(AdcClipping.Percent(0.0), Is.EqualTo("0.000%"));
        }

        [Test]
        public void Fits_RoundTripsClipFraction_AndOldFilesReadAsNull()
        {
            string dir = Path.Combine(Path.GetTempPath(), "rasta-clip-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var io = new FitsFileIo();
                var iq = new byte[4096 * 2];

                FitsFileMetaData Meta(double? clip) => new()
                {
                    Origin = "test",
                    DataFormat = "UINT8_IQ",
                    CentFreqHz = 1_420_700_000,
                    SampFreqHz = 2_400_000,
                    FftSize = 4096,
                    ObservationDate = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc),
                    ClipFraction = clip
                };

                string with = Path.Combine(dir, "with.fits");
                io.WriteRawIq(with, iq, Meta(0.0031));
                Assert.That(io.ReadRawIq(with).Meta.ClipFraction, Is.EqualTo(0.0031).Within(1e-12));

                string without = Path.Combine(dir, "without.fits");
                io.WriteRawIq(without, iq, Meta(null));
                Assert.That(io.ReadRawIq(without).Meta.ClipFraction, Is.Null);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
