namespace RASTA.Processing.Dsp
{
    /// <summary>
    /// Which optional final-smoothing pass HiStreamingPipeline.Process applies to the
    /// continuum-subtracted HiSpectrum before returning it. Display-only - never affects
    /// RatioSpectrum, the continuum fit, or anything upstream. Unrelated to
    /// SkaoPipelineProcessor, which always applies its own fixed 5-point kernel
    /// (RASTA.Processing.Dsp.SavitzkyGolay) unconditionally, matching the SKAO reference
    /// algorithm exactly - that stays untouched regardless of this setting.
    /// </summary>
    public enum SmoothingKind
    {
        None,
        SavitzkyGolay,
        MovingAverage
    }
}
