using RASTA.Core.Capture;
using RASTA.Core.Sdr;

namespace RASTA.Core.Calibration;

/// <summary>How much a calibration/plan difference matters.</summary>
public enum CalibrationMismatchSeverity
{
    /// <summary>The sweep's spectra would be wrong or inconsistent; ask before running.</summary>
    Warning,

    /// <summary>Worth knowing, but harmless: the sweep uses the calibration's value.</summary>
    Note
}

/// <summary>One difference between the loaded calibration and a plan (or the current setup).</summary>
public sealed record CalibrationMismatch(string Parameter, CalibrationMismatchSeverity Severity, string Message);

/// <summary>
/// Compares the loaded calibration with a plan about to be swept (issue #3).
///
/// A sweep tunes to the plan's centre frequency and sample rate but takes gain, FFT size and
/// ppm correction from the calibration, and its spectra are divided bin for bin by the
/// calibration's baseline. So the baseline only fits if it was captured at the same centre
/// frequency and sample rate, on the same receiver. The ppm correction is always the
/// calibration's (issue #32), so a different current setting means the new value is not used
/// until a new baseline is captured.
/// </summary>
public static class CalibrationPlanCheck
{
    /// <summary>Frequencies closer than this are treated as equal (absorbs JSON/float round trips).</summary>
    public const double FrequencyToleranceHz = 1.0;

    /// <param name="calibration">The loaded calibration profile.</param>
    /// <param name="plan">The plan to be swept.</param>
    /// <param name="currentPpmSetting">The ppm correction currently set in options.</param>
    /// <param name="currentDeviceId">The open SDR's DeviceId, or null if unknown.</param>
    public static IReadOnlyList<CalibrationMismatch> Check(
        CalibrationProfile calibration,
        CapturePlan plan,
        double currentPpmSetting,
        string? currentDeviceId)
    {
        var result = new List<CalibrationMismatch>();

        if (Math.Abs(plan.CenterFrequency - calibration.CenterFrequencyHz) > FrequencyToleranceHz)
        {
            result.Add(new CalibrationMismatch("Centre frequency", CalibrationMismatchSeverity.Warning,
                $"Centre frequency: plan {plan.CenterFrequency / 1e6:0.0000} MHz, baseline {calibration.CenterFrequencyHz / 1e6:0.0000} MHz. " +
                "The baseline's bandpass would be divided into the wrong frequencies."));
        }

        if (Math.Abs(plan.SampleRate - calibration.SampleRateHz) > FrequencyToleranceHz)
        {
            result.Add(new CalibrationMismatch("Sample rate", CalibrationMismatchSeverity.Warning,
                $"Sample rate: plan {plan.SampleRate / 1e6:0.###} Msps, baseline {calibration.SampleRateHz / 1e6:0.###} Msps. " +
                "The baseline's channels would not line up with the capture's."));
        }

        if (!string.IsNullOrEmpty(calibration.DeviceId)
            && !string.IsNullOrEmpty(currentDeviceId)
            && !string.Equals(calibration.DeviceId, currentDeviceId, StringComparison.Ordinal))
        {
            result.Add(new CalibrationMismatch("Receiver", CalibrationMismatchSeverity.Warning,
                $"Receiver: calibrated on {calibration.DeviceId}, now using {currentDeviceId}. " +
                "Its gain and bandpass may not suit this receiver."));
        }

        if (!FrequencyCorrection.AreEqual(calibration.PpmCorrection, currentPpmSetting))
        {
            result.Add(new CalibrationMismatch("ppm correction", CalibrationMismatchSeverity.Warning,
                $"ppm correction: the sweep uses the calibration's {FrequencyCorrection.Describe(calibration.PpmCorrection ?? 0.0)}, " +
                $"not the current setting ({currentPpmSetting:+0.00;-0.00;0} ppm). Capture a new baseline to use the new value."));
        }

        if (plan.FftBins > 0 && plan.FftBins != calibration.FftSize)
        {
            result.Add(new CalibrationMismatch("FFT size", CalibrationMismatchSeverity.Note,
                $"FFT size: the sweep records with the calibration's {calibration.FftSize}, not the plan's {plan.FftBins}."));
        }

        return result;
    }

    /// <summary>True if any difference should be confirmed before sweeping.</summary>
    public static bool HasWarnings(IEnumerable<CalibrationMismatch> mismatches) =>
        mismatches.Any(m => m.Severity == CalibrationMismatchSeverity.Warning);
}
