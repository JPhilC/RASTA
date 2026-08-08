using RASTA.Core.Calibration;

namespace RASTA.App.Services
{
    public interface IUserPromptService
    {
        Task<bool> AskYesNoAsync(string message, string title);
        Task AskOkAsync(string message, string title);

        /// <summary>
        /// Presents the cold-sky location candidates computed by ColdSkyLocator and lets the
        /// user pick one for the calibration baseline capture. Returns null if the user
        /// cancels.
        /// </summary>
        Task<ColdSkyCandidate?> PickColdSkyLocationAsync(IReadOnlyList<ColdSkyCandidate> candidates);
    }
}
