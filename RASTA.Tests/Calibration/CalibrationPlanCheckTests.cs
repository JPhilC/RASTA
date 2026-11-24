using NUnit.Framework;
using RASTA.Core.Calibration;
using RASTA.Core.Capture;

namespace RASTA.Tests.Calibration
{
    /// <summary>Calibration/plan mismatch warning (issue #3).</summary>
    [TestFixture]
    public class CalibrationPlanCheckTests
    {
        private static CalibrationProfile Calibration(double? ppm = 0.0, string deviceId = "R820T (Index 0)") => new()
        {
            CenterFrequencyHz = 1420.7e6,
            SampleRateHz = 2.4e6,
            FftSize = 4096,
            GainDb = 40,
            PpmCorrection = ppm,
            DeviceId = deviceId,
            BaselineSpectrum = new double[4096]
        };

        private static CapturePlan Plan(double centre = 1420.7e6, double rate = 2.4e6, int fft = 4096) => new()
        {
            CenterFrequency = centre,
            SampleRate = rate,
            FftBins = fft
        };

        [Test]
        public void Matching_NoMismatches()
        {
            var result = CalibrationPlanCheck.Check(Calibration(), Plan(), 0.0, "R820T (Index 0)");
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void SubHertzDifference_Ignored()
        {
            var result = CalibrationPlanCheck.Check(Calibration(), Plan(centre: 1420.7e6 + 0.4), 0.0, null);
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void CentreFrequency_Warns()
        {
            var result = CalibrationPlanCheck.Check(Calibration(), Plan(centre: 1420.4e6), 0.0, null);
            Assert.That(result.Select(m => m.Parameter), Is.EqualTo(new[] { "Centre frequency" }));
            Assert.That(CalibrationPlanCheck.HasWarnings(result), Is.True);
        }

        [Test]
        public void SampleRate_Warns()
        {
            var result = CalibrationPlanCheck.Check(Calibration(), Plan(rate: 2.048e6), 0.0, null);
            Assert.That(result.Single().Parameter, Is.EqualTo("Sample rate"));
            Assert.That(CalibrationPlanCheck.HasWarnings(result), Is.True);
        }

        [Test]
        public void FftSize_IsOnlyANote()
        {
            var result = CalibrationPlanCheck.Check(Calibration(), Plan(fft: 2048), 0.0, null);
            Assert.That(result.Single().Severity, Is.EqualTo(CalibrationMismatchSeverity.Note));
            Assert.That(CalibrationPlanCheck.HasWarnings(result), Is.False);
        }

        [Test]
        public void PlanWithoutFftSize_NoNote()
        {
            var result = CalibrationPlanCheck.Check(Calibration(), Plan(fft: 0), 0.0, null);
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void PpmSettingChangedSinceCalibration_Warns()
        {
            var result = CalibrationPlanCheck.Check(Calibration(ppm: 1.3), Plan(), 2.0, null);
            Assert.That(result.Single().Parameter, Is.EqualTo("ppm correction"));
            Assert.That(CalibrationPlanCheck.HasWarnings(result), Is.True);
        }

        [Test]
        public void PrePpmProfile_MatchesZeroSetting()
        {
            var result = CalibrationPlanCheck.Check(Calibration(ppm: null), Plan(), 0.0, null);
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void PrePpmProfile_WarnsWhenSettingNonZero()
        {
            var result = CalibrationPlanCheck.Check(Calibration(ppm: null), Plan(), 1.0, null);
            Assert.That(result.Single().Parameter, Is.EqualTo("ppm correction"));
        }

        [Test]
        public void DifferentReceiver_Warns()
        {
            var result = CalibrationPlanCheck.Check(Calibration(), Plan(), 0.0, "R828D (Index 0)");
            Assert.That(result.Single().Parameter, Is.EqualTo("Receiver"));
        }

        [Test]
        public void UnknownDevice_NotCompared()
        {
            Assert.That(CalibrationPlanCheck.Check(Calibration(deviceId: ""), Plan(), 0.0, "R828D (Index 0)"), Is.Empty);
            Assert.That(CalibrationPlanCheck.Check(Calibration(), Plan(), 0.0, null), Is.Empty);
        }
    }
}
