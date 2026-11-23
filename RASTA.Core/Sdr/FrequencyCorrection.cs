namespace RASTA.Core.Sdr;

/// <summary>
/// The receiver's crystal frequency correction, in ppm (issue #32).
///
/// librtlsdr only accepts a whole number of ppm, so a correction is split in two: the
/// nearest whole ppm (<see cref="HardwarePpm"/>) goes to the tuner, which then tunes the
/// LO and sample clock correctly to within that; the remainder (<see cref="ResidualPpm"/>)
/// is left in the data and corrected on the frequency axis instead. The LO and the sample
/// clock share the crystal, so with a residual of r ppm every frequency on the nominal
/// axis (centre + k * sampleRate / N) is really nominal * (1 + r * 1e-6) - scaling both the
/// centre frequency and the sample rate by that factor gives the true axis exactly.
///
/// Sign: the ppm value is how fast the crystal runs (librtlsdr's convention, as used by
/// rtl_test -p and every RTL-SDR tool). 1 ppm is 1.42 kHz at 1420 MHz, about 0.3 km/s.
/// </summary>
public static class FrequencyCorrection
{
    /// <summary>Largest correction accepted in settings; real dongles are within ~100 ppm, TCXO ones within ~1.</summary>
    public const double MaxAbsPpm = 200.0;

    /// <summary>The whole-ppm part to set in the tuner (rounded half away from zero).</summary>
    public static int HardwarePpm(double ppm) => (int)Math.Round(ppm, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The part the tuner did not apply. <paramref name="hardwarePpm"/> is what was actually
    /// set when known (e.g. read from a FITS header); otherwise it is derived from
    /// <paramref name="ppm"/> the same way the capture code derives it.
    /// </summary>
    public static double ResidualPpm(double ppm, int? hardwarePpm = null) =>
        ppm - (hardwarePpm ?? HardwarePpm(ppm));

    /// <summary>
    /// A nominal frequency (a tuned centre frequency or a sample rate) corrected for a
    /// residual of <paramref name="residualPpm"/>. Returns the input unchanged for 0.
    /// </summary>
    public static double Apply(double nominalHz, double residualPpm) =>
        residualPpm == 0.0 ? nominalHz : nominalHz * (1.0 + residualPpm * 1e-6);

    /// <summary>
    /// True when two recorded corrections are the same. Null (a file or profile from before
    /// issue #32) means no correction was applied, i.e. 0.
    /// </summary>
    public static bool AreEqual(double? a, double? b) => Math.Abs((a ?? 0.0) - (b ?? 0.0)) < 1e-6;

    /// <summary>Short display form, e.g. "+1.30 ppm (tuner +1, axis +0.30)".</summary>
    public static string Describe(double? ppm, int? hardwarePpm = null)
    {
        if (ppm is not double p)
            return "not recorded (0)";
        int hw = hardwarePpm ?? HardwarePpm(p);
        double residual = ResidualPpm(p, hw);
        return residual == 0.0
            ? $"{p:+0.00;-0.00;0} ppm"
            : $"{p:+0.00;-0.00;0} ppm (tuner {hw:+0;-0;0}, axis {residual:+0.00;-0.00;0})";
    }
}
