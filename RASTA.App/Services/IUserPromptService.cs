using RASTA.Core.Calibration;

namespace RASTA.App.Services
{
    public interface IUserPromptService
    {
        Task<bool> AskYesNoAsync(string message, string title);
        Task AskOkAsync(string message, string title);

        /// <summary>
        /// Presents the cold-sky location candidates computed by ColdSkyLocator and lets the
        /// user pick one for the calibration baseline capture. Returns null if "Cancel
        /// Calibration" is chosen. <paramref name="recalculate"/> is called (without closing
        /// the dialog) when "Recalculate" is clicked, given the candidates currently on
        /// screen, and should return a fresh set (typically excluding those azimuths - see
        /// ColdSkyLocator.FindCandidates' excludeAzimuthsDeg) for the dialog to redisplay.
        /// </summary>
        Task<ColdSkyCandidate?> PickColdSkyLocationAsync(
            IReadOnlyList<ColdSkyCandidate> candidates,
            Func<IReadOnlyList<ColdSkyCandidate>, IReadOnlyList<ColdSkyCandidate>> recalculate);
    }
}
