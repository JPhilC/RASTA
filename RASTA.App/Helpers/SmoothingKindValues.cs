using RASTA.Processing.Dsp;

namespace RASTA.App.Helpers
{
    public static class SmoothingKindValues
    {
        public static IReadOnlyList<SmoothingKindItem> All { get; } =
            new List<SmoothingKindItem>
            {
                new SmoothingKindItem(SmoothingKind.None, "None"),
                new SmoothingKindItem(SmoothingKind.SavitzkyGolay, "Savitzky-Golay"),
                new SmoothingKindItem(SmoothingKind.MovingAverage, "Moving Average")
            };
    }

    public record SmoothingKindItem(SmoothingKind Kind, string Name);
}
