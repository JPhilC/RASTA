using RASTA.App.ViewModels;

namespace RASTA.App.Helpers
{
    public static class SpectrumModeValues
    {
        public static IReadOnlyList<SpectrumModeItem> All { get; } =
            new List<SpectrumModeItem>
            {
                new SpectrumModeItem(SpectrumMode.IF, "IF Spectrum"),
                new SpectrumModeItem(SpectrumMode.HiFrequency, "HI (Frequency)"),
                new SpectrumModeItem(SpectrumMode.HiVelocity, "HI (Velocity)"),
                new SpectrumModeItem(SpectrumMode.TTRT, "SKAO TTRT")
            };
    }

    public record SpectrumModeItem(SpectrumMode Mode, string Name);
}

