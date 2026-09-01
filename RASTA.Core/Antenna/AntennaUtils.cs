namespace RASTA.Core.Antenna
{
    /// <summary>
    /// Pure antenna-optics formulas - currently just the standard parabolic-dish half-power
    /// beamwidth approximation, used to suggest a sensible default sweep/region grid spacing
    /// (see PlanViewModel, which defaults TargetRange.AngularSeparationDeg to half this) instead
    /// of the unhelpful 0 a brand new plan used to start with.
    /// </summary>
    public static class AntennaUtils
    {
        private const double SpeedOfLightMetersPerSecond = 299_792_458.0;

        /// <summary>
        /// Half-power beamwidth (HPBW, degrees) for a parabolic dish of the given diameter at
        /// the given frequency, using the standard amateur-radio-astronomy approximation
        /// HPBW(deg) ~= 70 * (wavelength / diameter) - the same style of low-precision analytic
        /// approximation AstronomyUtils uses elsewhere in this app (e.g.
        /// ComputeLsrCorrectionKmPerSec). Returns 0 for a non-positive diameter or frequency
        /// rather than NaN/Infinity, so a not-yet-configured antenna reads as "no suggestion"
        /// rather than a garbage value.
        ///
        /// Deliberately diameter-only, with no focal-length parameter: the "70" constant already
        /// stands in for a *typical* edge illumination taper (roughly the -10 to -12 dB a
        /// well-matched feed gives), and that's exactly where focal length actually enters the
        /// real physics - a dish's f/D ratio sets the half-angle it subtends from the feed, and
        /// how well a given feed's own pattern matches that angle is what actually determines the
        /// taper (and so the true beamwidth constant, roughly 58 for uniform illumination up to
        /// 70+ for a heavier taper). Refining the constant from f/D alone, without the feed's own
        /// pattern, isn't something this can do rigorously, so it isn't attempted here - see
        /// SettingsViewModel.FocalRatio, which instead just surfaces f/D as context (whether a
        /// dish sits near the ~0.35-0.5 range this approximation assumes) without feeding it back
        /// into this formula.
        /// </summary>
        public static double ComputeBeamwidthDeg(double diameterM, double frequencyHz)
        {
            if (diameterM <= 0 || frequencyHz <= 0)
                return 0;

            double wavelengthM = SpeedOfLightMetersPerSecond / frequencyHz;
            return 70.0 * (wavelengthM / diameterM);
        }
    }
}
