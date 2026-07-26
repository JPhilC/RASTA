using RASTA.Core.Capture;

namespace RASTA.App.Helpers
{
    public static class PlanTypeEnum
    {
        /// <summary>
        /// Returns all values of the PlanType enum.
        /// Useful for binding to ComboBoxes in WPF.
        /// </summary>
        public static IReadOnlyList<PlanType> Values { get; } =
            Array.AsReadOnly((PlanType[])Enum.GetValues(typeof(PlanType)));
    }
}
