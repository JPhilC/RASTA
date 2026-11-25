using RASTA.Core.Sdr;

namespace RASTA.Processing.Calibration
{
    /// <summary>
    /// Thrown by Calibrator.CaptureColdSkyBaselineAsync when the baseline's raw I/Q has more
    /// than AdcClipping.MaxAcceptableFraction of its bytes at the ADC rail (issue #33). The
    /// baseline is refused - nothing is written and the calibration keeps its previous state.
    /// </summary>
    public sealed class BaselineClippedException : InvalidOperationException
    {
        public double ClipFraction { get; }
        public double GainDb { get; }

        public BaselineClippedException(double clipFraction, double gainDb)
            : base($"The baseline was refused: {AdcClipping.Percent(clipFraction)} of raw I/Q samples were at the " +
                   $"ADC rail at {gainDb:0.0} dB gain (limit {AdcClipping.Percent(AdcClipping.MaxAcceptableFraction)}), " +
                   "so the receiver was overloading on the antenna signal. Nothing was saved. Try a different " +
                   "cold-sky position, or look for a nearby source of interference; if it keeps happening, the " +
                   "gain chosen against the terminator is too high for the sky.")
        {
            ClipFraction = clipFraction;
            GainDb = gainDb;
        }
    }
}
